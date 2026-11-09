using System.Security.Cryptography;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.Core.Util;
using MultiDriveCloud.GoogleDrive.Authentication;
using MultiDriveCloud.GoogleDrive.Services;
using MultiDriveCloud.Infrastructure.Security;
using MultiDriveCloud.Tests.Support;
using static MultiDriveCloud.Tests.Support.TestEnv;

namespace MultiDriveCloud.Tests;

public class EncryptionTests
{
    public static IEnumerable<object[]> Sizes() => new[]
    {
        new object[] { 0 }, new object[] { 1 }, new object[] { 1000 },
        new object[] { AesGcmStreamCipher.SegmentSize }, new object[] { AesGcmStreamCipher.SegmentSize + 1 },
        new object[] { 3 * AesGcmStreamCipher.SegmentSize + 77 },
    };

    private static async Task<byte[]> EncryptAsync(byte[] plain, byte[] key)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        await AesGcmStreamCipher.EncryptAsync(input, output, key, null, default);
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task Roundtrip_and_exact_length_prediction(int size)
    {
        var key = AesGcmStreamCipher.GenerateKey();
        var plain = RandomNumberGenerator.GetBytes(size);
        var cipher = await EncryptAsync(plain, key);
        Assert.Equal(AesGcmStreamCipher.EncryptedLength(size), cipher.Length);
        Assert.True(AesGcmStreamCipher.MaxPlainForEncrypted(cipher.Length) >= size);

        using var output = new MemoryStream();
        await AesGcmStreamCipher.DecryptAsync(new MemoryStream(cipher), output, key, null, default);
        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task Wrong_key_tampering_and_truncation_are_detected()
    {
        var key = AesGcmStreamCipher.GenerateKey();
        var plain = RandomNumberGenerator.GetBytes(2 * AesGcmStreamCipher.SegmentSize + 10);
        var cipher = await EncryptAsync(plain, key);

        async Task Decrypt(byte[] data, byte[] k) =>
            await AesGcmStreamCipher.DecryptAsync(new MemoryStream(data), new MemoryStream(), k, null, default);

        await Assert.ThrowsAsync<CryptographicException>(() => Decrypt(cipher, AesGcmStreamCipher.GenerateKey()));

        var flipped = (byte[])cipher.Clone();
        flipped[flipped.Length / 2] ^= 1;
        await Assert.ThrowsAsync<CryptographicException>(() => Decrypt(flipped, key));

        var segment = AesGcmStreamCipher.SegmentSize + AesGcmStreamCipher.TagSize;
        var truncated = cipher[..(AesGcmStreamCipher.HeaderSize + 2 * segment)];
        await Assert.ThrowsAsync<CryptographicException>(() => Decrypt(truncated, key));
        await Assert.ThrowsAsync<CryptographicException>(() => Decrypt(cipher[..20], key));
    }

    [Fact]
    public async Task Ciphertext_differs_every_time_and_hides_the_content()
    {
        var key = AesGcmStreamCipher.GenerateKey();
        var plain = System.Text.Encoding.UTF8.GetBytes(new string('A', 5000) + "TOP-SECRET-MARKER");
        var c1 = await EncryptAsync(plain, key);
        var c2 = await EncryptAsync(plain, key);
        Assert.NotEqual(c1, c2);
        Assert.False(c1.AsSpan().IndexOf("TOP-SECRET-MARKER"u8) >= 0);
    }

    [Fact]
    public async Task Encrypted_files_are_distributed_stored_unreadable_and_restored()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 8 * MB);
        await env.AddAccountAsync("b@test", 8 * MB);
        await env.UpdateSettingsAsync(s => s.EncryptionEnabled = true);
        var source = env.CreateFile("secret.txt", 9 * MB, 77);
        var marker = System.Text.Encoding.ASCII.GetBytes("HELLO-MARKER-HELLO-MARKER");
        using (var fs = new FileStream(source, FileMode.Open, FileAccess.Write)) { fs.Seek(1000, SeekOrigin.Begin); fs.Write(marker); }

        await env.UploadAndWaitAsync(source);

        var up = env.Snapshots().Single();
        Assert.True(up.State == TransferState.Completed, up.ErrorMessage);
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        Assert.True(file.IsEncrypted);
        foreach (var obj in env.Fake.Drives.Values.SelectMany(d => d.Objects.Values))
            Assert.False(obj.Data.AsSpan().IndexOf(marker) >= 0, "le contenu en clair ne doit jamais quitter la machine");
        Assert.True(await env.Keys.HasKeyAsync());

        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });
        var down = env.Snapshots(TransferDirection.Download).Single();
        Assert.True(down.State == TransferState.Completed, down.ErrorMessage);
        Assert.True(FilesEqual(source, Path.Combine(env.DownloadDir, "secret.txt")));
        Assert.Empty(Directory.GetFiles(env.Paths.Temp));        env.DeleteRoot();
    }

    [Fact]
    public async Task Lost_key_makes_encrypted_download_fail_with_a_clear_message()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 20 * MB);
        await env.UpdateSettingsAsync(s => s.EncryptionEnabled = true);
        await env.UploadAndWaitAsync(env.CreateFile("s.bin", 2 * MB));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();

        await env.Secrets.DeleteAsync("encryption-master-key");        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });

        var snap = env.Snapshots(TransferDirection.Download).Single();
        Assert.Equal(TransferState.Failed, snap.State);
        Assert.Contains("clé", snap.ErrorMessage);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Exported_key_restores_access_and_needs_the_right_passphrase()
    {
        var secrets = new MemorySecretStore();
        var keys = new EncryptionKeyManager(secrets);
        Assert.True(await keys.EnsureKeyAsync());
        Assert.False(await keys.EnsureKeyAsync());
        var original = await keys.GetKeyAsync();
        var path = Path.Combine(Path.GetTempPath(), "mdc-key-" + Guid.NewGuid().ToString("N") + ".json");

        await Assert.ThrowsAsync<ArgumentException>(() => keys.ExportKeyAsync(path, "short"));
        await keys.ExportKeyAsync(path, "correct horse battery staple");
        Assert.DoesNotContain(Convert.ToBase64String(original!), await File.ReadAllTextAsync(path));

        var fresh = new EncryptionKeyManager(new MemorySecretStore());
        await Assert.ThrowsAsync<KeyImportException>(() => fresh.ImportKeyAsync(path, "wrong passphrase!!", false));
        await fresh.ImportKeyAsync(path, "correct horse battery staple", false);
        Assert.Equal(original, await fresh.GetKeyAsync());

        var other = new EncryptionKeyManager(new MemorySecretStore());
        await other.EnsureKeyAsync();
        await Assert.ThrowsAsync<KeyImportException>(() => other.ImportKeyAsync(path, "correct horse battery staple", false));
        File.Delete(path);
    }
}

public class SecretHandlingTests
{
    [Fact]
    public void Redactor_removes_tokens_and_secrets()
    {
        var text = "Authorization: Bearer ya29.A0AfH6SMBx-1234567890abcdef refresh_token=1//0gAbCdEfGhIjKlMnOpQrStUv client_secret: GOCSPX-abcdefghijklmnop " +
                   "https://x/upload?upload_id=AEnB2Ur-abcdefgh12345 eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk";
        var clean = Redactor.Redact(text);
        foreach (var secret in new[] { "ya29.A0AfH6SMBx", "1//0gAbCdEf", "GOCSPX-abcdef", "AEnB2Ur-abcdefgh12345", "eyJhbGciOiJSUzI1NiJ9" })
            Assert.DoesNotContain(secret, clean);
        Assert.Contains("Authorization", clean);
    }

    [Fact]
    public async Task Dpapi_store_roundtrips_and_leaves_no_plaintext_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdc-secrets-" + Guid.NewGuid().ToString("N"));
        var store = new DpapiSecretStore(dir, new DpapiProtector());
        await store.SetAsync("google-tokens:acct", "{\"refresh_token\":\"1//SUPERSECRETVALUE\"}");
        Assert.Equal("{\"refresh_token\":\"1//SUPERSECRETVALUE\"}", await store.GetAsync("google-tokens:acct"));
        foreach (var f in Directory.GetFiles(dir))
        {
            Assert.DoesNotContain("SUPERSECRETVALUE", await File.ReadAllTextAsync(f));
            Assert.DoesNotContain("acct", Path.GetFileName(f));
        }
        await store.DeleteAsync("google-tokens:acct");
        Assert.Null(await store.GetAsync("google-tokens:acct"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Upload_session_uri_is_stored_protected_in_the_database()
    {
        await using var env = new TestEnv();
        var repo = new MultiDriveCloud.Infrastructure.Database.IndexRepository(env.Db, new DpapiProtector());
        await env.AddAccountAsync("a@test", 20 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("s.bin", 1000));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        var chunk = (await env.Index.GetChunksAsync(file.FileId)).Single();
        await repo.UpsertSessionAsync(new TransferSession { ChunkId = chunk.ChunkId, TransferId = Guid.NewGuid(), SessionUri = "https://x/upload?upload_id=VERYSECRET", ExpiresAt = DateTime.UtcNow.AddDays(1) });

        using var conn = env.Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SessionUri FROM TransferSessions";
        var raw = (string)cmd.ExecuteScalar()!;
        Assert.DoesNotContain("VERYSECRET", raw);
        Assert.Equal("https://x/upload?upload_id=VERYSECRET", (await repo.GetSessionAsync(chunk.ChunkId))!.SessionUri);
        env.DeleteRoot();
    }

    [Theory]
    [InlineData("report.txt", true)]
    [InlineData("a b (1).pdf", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("trailing.", false)]
    [InlineData(" lead", false)]
    [InlineData("", false)]
    public void Virtual_names_are_validated(string name, bool valid) =>
        Assert.Equal(valid, PathGuard.ValidateVirtualName(name) is null);

    [Fact]
    public void Destination_paths_cannot_escape_the_target_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mdc-root");
        Assert.StartsWith(Path.GetFullPath(root), PathGuard.CombineInside(root, "..", "evil.txt"), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root), PathGuard.CombineInside(root, "sub", "a:b?.txt"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("_CON.txt", PathGuard.SanitizeFileName("CON.txt"));
    }
}

public class GoogleTests
{
    [Fact]
    public void Pkce_challenge_matches_rfc7636_test_vector() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            GoogleOAuthClient.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Oauth_json_downloaded_from_google_console_is_understood()
    {
        var parsed = GoogleOAuthConfigProvider.ParseJson("""{"installed":{"client_id":"123.apps.googleusercontent.com","client_secret":"GOCSPX-x","redirect_uris":["http://localhost"]}}""");
        Assert.Equal("123.apps.googleusercontent.com", parsed!.ClientId);
        Assert.Equal("GOCSPX-x", parsed.ClientSecret);
        Assert.Null(GoogleOAuthConfigProvider.ParseJson("{}"));
        Assert.Null(GoogleOAuthConfigProvider.ParseJson("not json"));
    }

    [Fact]
    public async Task Identity_shipped_next_to_the_exe_is_used_without_any_user_setup()
    {
        await using var env = new TestEnv();
        var appDir = Path.Combine(env.Root, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "google-oauth.json"),
            """{"client_id":"123456789012-abcdefghijklmnop.apps.googleusercontent.com","client_secret":"GOCSPX-shipped"}""");

        var provider = new GoogleOAuthConfigProvider(env.Settings, env.Secrets, Path.Combine(env.Root, "nothing"), appDir);
        var cfg = await provider.GetAsync();

        Assert.NotNull(cfg);
        Assert.Equal("123456789012-abcdefghijklmnop.apps.googleusercontent.com", cfg!.ClientId);
        Assert.Equal("GOCSPX-shipped", cfg.ClientSecret);
        env.DeleteRoot();
    }

    [Fact]
    public void Client_id_format_is_checked()
    {
        Assert.Null(GoogleOAuthConfigProvider.ValidateClientId("123456789012-abcdefghijklmnop.apps.googleusercontent.com"));
        Assert.NotNull(GoogleOAuthConfigProvider.ValidateClientId(""));
        Assert.NotNull(GoogleOAuthConfigProvider.ValidateClientId("monmotdepasse"));
        Assert.NotNull(GoogleOAuthConfigProvider.ValidateClientId("a b.apps.googleusercontent.com"));
    }

    [Fact]
    public void Downloaded_google_json_is_found_newest_first_and_junk_is_ignored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdc-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string good = """{"installed":{"client_id":"123456789012-abcdefghijklmnop.apps.googleusercontent.com","client_secret":"GOCSPX-x"}}""";
        File.WriteAllText(Path.Combine(dir, "client_secret_old.json"), good);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "client_secret_old.json"), DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(dir, "client_secret_new.json"), good);
        File.WriteAllText(Path.Combine(dir, "client_secret_broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir, "client_secret_wrongid.json"), """{"installed":{"client_id":"abc"}}""");
        File.WriteAllText(Path.Combine(dir, "other.json"), good);

        var found = GoogleOAuthConfigProvider.FindDownloadedJsonFiles(dir);

        Assert.Equal(new[] { "client_secret_new.json", "client_secret_old.json" }, found.Select(Path.GetFileName).ToArray());
        Assert.Empty(GoogleOAuthConfigProvider.FindDownloadedJsonFiles(Path.Combine(dir, "missing")));
        Directory.Delete(dir, true);
    }

    private sealed class CallbackBrowser : IBrowserLauncher
    {
        public Func<Uri, string> Respond = _ => "";
        public Task? Pending;
        public void Open(string url)
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var redirect = query["redirect_uri"]!;
            var state = query["state"]!;
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(GoogleOAuthClient.DriveFileScope, query["scope"]);            Pending = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetAsync(Respond(new Uri(redirect)).Replace("{state}", state));
            });
        }
    }

    [Fact]
    public async Task Callback_with_wrong_state_is_rejected()
    {
        var browser = new CallbackBrowser { Respond = r => r + "?code=abc&state=FORGED" };
        var client = new GoogleOAuthClient(browser, TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<StorageProviderException>(() =>
            client.AuthorizeInteractiveAsync(new GoogleOAuthConfig("id", null), null, default));
        Assert.Contains("state", ex.Message);
        await browser.Pending!;
    }

    [Fact]
    public async Task User_denying_access_is_reported_cleanly()
    {
        var browser = new CallbackBrowser { Respond = r => r + "?error=access_denied&state={state}" };
        var client = new GoogleOAuthClient(browser, TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<StorageProviderException>(() =>
            client.AuthorizeInteractiveAsync(new GoogleOAuthConfig("id", null), "user@example.com", default));
        Assert.Contains("refusée", ex.Message);
        await browser.Pending!;
    }

    [Fact]
    public async Task Login_times_out_when_nothing_comes_back()
    {
        var client = new GoogleOAuthClient(new CallbackBrowser { Respond = _ => "http://127.0.0.1:1/never" }, TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<StorageProviderException>(() => client.AuthorizeInteractiveAsync(new GoogleOAuthConfig("id", null), null, default));
    }

    [Theory]
    [InlineData(401, "", ProviderErrorKind.AuthRequired)]
    [InlineData(404, "notFound", ProviderErrorKind.NotFound)]
    [InlineData(429, "", ProviderErrorKind.RateLimited)]
    [InlineData(503, "backendError", ProviderErrorKind.Transient)]
    [InlineData(403, "storageQuotaExceeded", ProviderErrorKind.StorageFull)]
    [InlineData(403, "userRateLimitExceeded", ProviderErrorKind.RateLimited)]
    [InlineData(403, "dailyLimitExceeded", ProviderErrorKind.DailyQuota)]
    [InlineData(400, "badRequest", ProviderErrorKind.Fatal)]
    public void Google_errors_map_to_actionable_kinds(int status, string reason, ProviderErrorKind expected)
    {
        var error = new Google.Apis.Requests.RequestError
        {
            Code = status,
            Message = "x",
            Errors = new List<Google.Apis.Requests.SingleError> { new() { Reason = reason } },
        };
        var ex = new Google.GoogleApiException("Drive", "x") { Error = error, HttpStatusCode = (System.Net.HttpStatusCode)status };
        Assert.Equal(expected, GoogleDriveStorageProvider.Map(ex).Kind);
    }

    [Fact]
    public void Network_exceptions_are_transient() =>
        Assert.Equal(ProviderErrorKind.Transient, GoogleDriveStorageProvider.Map(new HttpRequestException("boom")).Kind);
}

public class GoogleDownloadTests
{
    private sealed class DriveStub : IDisposable
    {
        public readonly System.Net.HttpListener Listener = new();
        public string Base { get; }
        public Func<System.Net.HttpListenerRequest, (int Status, byte[] Body)> Handler = _ => (404, Array.Empty<byte>());
        public string? LastAuthorization;
        public string? LastPath;

        public DriveStub()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = $"http://127.0.0.1:{port}";
            Listener.Prefixes.Add(Base + "/");
            Listener.Start();
            _ = Task.Run(async () =>
            {
                while (Listener.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = await Listener.GetContextAsync(); } catch { return; }
                    LastAuthorization = ctx.Request.Headers["Authorization"];
                    LastPath = ctx.Request.Url!.PathAndQuery;
                    var (status, body) = Handler(ctx.Request);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => Listener.Close();
    }

    private static async Task<GoogleDriveStorageProvider> ProviderAsync(DriveStub stub, MemorySecretStore secrets, string? refreshToken = null)
    {
        var cfg = new FixedConfig();
        var oauth = new GoogleOAuthClient(new ShellBrowserLauncher());
        var tokens = new GoogleTokenManager(secrets, oauth, cfg);
        await tokens.SaveAsync("acct", new TokenSet { AccessToken = "tok123", RefreshToken = refreshToken, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        return new GoogleDriveStorageProvider(oauth, tokens, cfg) { ApiBase = stub.Base };
    }

    private sealed class FixedConfig : IGoogleOAuthConfigProvider
    {
        public Task<GoogleOAuthConfig?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<GoogleOAuthConfig?>(new GoogleOAuthConfig("id.apps.googleusercontent.com", null));
    }

    [Fact]
    public async Task Download_streams_the_fragment_at_the_right_offset_with_the_bearer_token()
    {
        using var stub = new DriveStub();
        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        stub.Handler = _ => (200, payload);
        var provider = await ProviderAsync(stub, new MemorySecretStore());
        var path = Path.Combine(Path.GetTempPath(), "mdc-dl-" + Guid.NewGuid().ToString("N"));
        using (var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
        {
            RandomAccess.SetLength(handle, 100 + payload.Length);
            var ws = new OffsetWriteStream(handle, 100, payload.Length);
            long reported = 0;
            await provider.DownloadAsync(new DownloadRequest { AccountId = "acct", RemoteId = "file/id 1", Destination = ws, Progress = new SyncProgress<long>(b => reported = b) }, default);
            Assert.Equal(payload.Length, ws.Written);
            Assert.Equal(payload.Length, reported);
        }
        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(payload, written[100..]);
        Assert.Equal("Bearer tok123", stub.LastAuthorization);
        Assert.Contains("alt=media", stub.LastPath);
        Assert.Contains("file%2Fid%201", stub.LastPath);
        File.Delete(path);
    }

    [Theory]
    [InlineData(404, """{"error":{"code":404,"message":"File not found","errors":[{"reason":"notFound"}]}}""", ProviderErrorKind.NotFound)]
    [InlineData(403, """{"error":{"code":403,"message":"x","errors":[{"reason":"downloadQuotaExceeded"}]}}""", ProviderErrorKind.DailyQuota)]
    [InlineData(500, "oops not json", ProviderErrorKind.Transient)]
    public async Task Download_errors_are_mapped(int status, string body, ProviderErrorKind expected)
    {
        using var stub = new DriveStub();
        stub.Handler = _ => (status, System.Text.Encoding.UTF8.GetBytes(body));
        var provider = await ProviderAsync(stub, new MemorySecretStore());
        var ex = await Assert.ThrowsAsync<StorageProviderException>(() =>
            provider.DownloadAsync(new DownloadRequest { AccountId = "acct", RemoteId = "x", Destination = new MemoryStream() }, default));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Rejected_token_without_refresh_token_asks_for_reauthorisation()
    {
        using var stub = new DriveStub();
        stub.Handler = _ => (401, Array.Empty<byte>());
        var provider = await ProviderAsync(stub, new MemorySecretStore(), refreshToken: null);
        var ex = await Assert.ThrowsAsync<StorageProviderException>(() =>
            provider.DownloadAsync(new DownloadRequest { AccountId = "acct", RemoteId = "x", Destination = new MemoryStream() }, default));
        Assert.Equal(ProviderErrorKind.AuthRequired, ex.Kind);
    }

    [Fact]
    public async Task Local_write_problems_are_not_reported_as_google_errors()
    {
        using var stub = new DriveStub();
        stub.Handler = _ => (200, new byte[5000]);
        var provider = await ProviderAsync(stub, new MemorySecretStore());
        var tiny = new MemoryStream();
        var path = Path.Combine(Path.GetTempPath(), "mdc-dl-" + Guid.NewGuid().ToString("N"));
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        var ws = new OffsetWriteStream(handle, 0, 1000);        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DownloadAsync(new DownloadRequest { AccountId = "acct", RemoteId = "x", Destination = ws }, default));
        handle.Dispose();
        File.Delete(path);
    }
}

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MDC_INTEGRATION") != "1")
            Skip = "Test réel avec comptes Google : définir MDC_INTEGRATION=1 (voir docs/TESTING.md).";
    }
}

public class GoogleIntegrationTests
{
    [IntegrationFact]
    public async Task Two_real_accounts_distribute_and_restore_a_file()
    {
        var appData = new MultiDriveCloud.Infrastructure.AppPaths();        var liveDb = new MultiDriveCloud.Infrastructure.Database.SqliteDatabase(appData.Database);
        var liveAccounts = (await new MultiDriveCloud.Infrastructure.Database.AccountRepository(liveDb).GetAllAsync())
            .Where(a => a.Provider == GoogleDriveStorageProvider.Id && a.Status == AccountStatus.Connected).ToList();
        Assert.True(liveAccounts.Count >= 2, "Connectez au moins deux comptes Google dans l'application avant ce test.");

        var secrets = new DpapiSecretStore(appData.Secrets, new DpapiProtector());
        await using var env = new TestEnv(chunkSize: 5 * MB);
        var settings = env.Settings;
        var cfg = new GoogleOAuthConfigProvider(settings, secrets, appData.Root);
        var oauth = new GoogleOAuthClient(new ShellBrowserLauncher());
        var provider = new GoogleDriveStorageProvider(oauth, new GoogleTokenManager(secrets, oauth, cfg), cfg);

        var registry = new ProviderRegistry(new IStorageProvider[] { provider });
        var accountSvc = new AccountService(registry, env.Accounts, env.Index, env.Allocator, Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountService>.Instance);
        foreach (var a in liveAccounts.Take(2))
        {
            a.Status = AccountStatus.Connected;
            await env.Accounts.UpsertAsync(a);
            await accountSvc.RefreshAsync(a.AccountId);
        }
        var cleaner = new FragmentCleaner(registry, env.Accounts, env.Index, env.Allocator, Microsoft.Extensions.Logging.Abstractions.NullLogger<FragmentCleaner>.Instance);
        await using var manager = new TransferManager(env.Index, env.Accounts, registry, env.Allocator, env.Chunks, cleaner, settings, env.Keys, accountSvc,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TransferManager>.Instance);
        var vfs = new VirtualFileSystemService(env.Index, env.Accounts, cleaner, manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<VirtualFileSystemService>.Instance);

        var source = env.CreateFile("integration.bin", 12 * MB, 99);
        await manager.EnqueueUploadAsync(new[] { source }, VirtualFolder.RootId);
        await manager.WaitForIdleAsync(TimeSpan.FromMinutes(10));
        var up = manager.GetSnapshots().Single();
        Assert.True(up.State == TransferState.Completed, up.ErrorMessage);

        var file = (await vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        await manager.EnqueueDownloadAsync(new[] { new ItemRef(false, file.FileId) }, env.DownloadDir);
        await manager.WaitForIdleAsync(TimeSpan.FromMinutes(10));
        Assert.True(FilesEqual(source, Path.Combine(env.DownloadDir, "integration.bin")));

        var report = await vfs.DeleteAsync(new[] { new ItemRef(false, file.FileId) });
        Assert.True(report.Success, string.Join("; ", report.Problems));
        env.DeleteRoot();
    }
}
