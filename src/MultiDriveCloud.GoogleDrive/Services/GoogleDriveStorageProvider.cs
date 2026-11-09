using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Google;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.GoogleDrive.Authentication;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace MultiDriveCloud.GoogleDrive.Services;

internal sealed class BearerTokenInitializer : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor, IHttpUnsuccessfulResponseHandler
{
    private readonly Func<bool, CancellationToken, Task<string>> _getToken;
    private long _lastForcedRefreshTicks;

    public BearerTokenInitializer(Func<bool, CancellationToken, Task<string>> getToken) => _getToken = getToken;

    public void Initialize(ConfigurableHttpClient httpClient)
    {
        httpClient.MessageHandler.AddExecuteInterceptor(this);
        httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
    }

    public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _getToken(false, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
    {
        if (args.Response.StatusCode != HttpStatusCode.Unauthorized) return false;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastForcedRefreshTicks) < 5000) return false;        Interlocked.Exchange(ref _lastForcedRefreshTicks, now);
        await _getToken(true, args.CancellationToken).ConfigureAwait(false);
        return true;
    }
}

public sealed class GoogleDriveStorageProvider : IStorageProvider, IDisposable
{
    public const string Id = "googledrive";
    public const string AppFolderName = "MultiDrive Cloud";
    public const long UnlimitedAssumedBytes = 1024L * 1024 * 1024 * 1024;
    private const int UploadChunkSize = 16 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string ApiBase { get; init; } = "https://www.googleapis.com";

    private readonly GoogleOAuthClient _oauth;
    private readonly GoogleTokenManager _tokens;
    private readonly IGoogleOAuthConfigProvider _config;
    private readonly Dictionary<string, DriveService> _services = new();
    private readonly Dictionary<string, string> _appFolders = new();
    private readonly SemaphoreSlim _folderLock = new(1, 1);

    public GoogleDriveStorageProvider(GoogleOAuthClient oauth, GoogleTokenManager tokens, IGoogleOAuthConfigProvider config)
    {
        _oauth = oauth;
        _tokens = tokens;
        _config = config;
    }

    public string ProviderId => Id;


    public async Task<AuthorizedAccount> AuthorizeAsync(string? loginHint, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct).ConfigureAwait(false)
            ?? throw new StorageProviderException(ProviderErrorKind.Fatal,
                "L'identifiant client OAuth Google n'est pas configuré. Ouvrez Paramètres › Google OAuth (voir docs/GOOGLE_OAUTH.md).");

        var tokens = await _oauth.AuthorizeInteractiveAsync(config, loginHint, ct).ConfigureAwait(false);

        using var temp = CreateService((_, _) => Task.FromResult(tokens.AccessToken));
        var about = temp.About.Get();
        about.Fields = "user(emailAddress,displayName,permissionId)";
        Google.Apis.Drive.v3.Data.About info;
        try { info = await about.ExecuteAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Map(ex, ct); }

        var user = info.User ?? throw new StorageProviderException(ProviderErrorKind.Fatal, "Google n'a pas renvoyé l'identité du compte.");
        var accountId = "google:" + (user.PermissionId ?? user.EmailAddress);
        await _tokens.SaveAsync(accountId, tokens, ct).ConfigureAwait(false);
        lock (_services)
        {
            if (_services.Remove(accountId, out var old)) old.Dispose();
        }
        lock (_appFolders) _appFolders.Remove(accountId);
        return new AuthorizedAccount(accountId, user.EmailAddress ?? accountId, user.DisplayName ?? user.EmailAddress ?? accountId);
    }

    public async Task DisconnectAsync(string accountId, CancellationToken ct)
    {
        try
        {
            var refresh = await _tokens.GetRefreshTokenAsync(accountId, ct).ConfigureAwait(false);
            if (refresh is not null) await _tokens.RevokeAsync(refresh, ct).ConfigureAwait(false);
        }
        finally
        {
            await _tokens.RemoveAsync(accountId, CancellationToken.None).ConfigureAwait(false);
            lock (_services)
            {
                if (_services.Remove(accountId, out var old)) old.Dispose();
            }
            lock (_appFolders) _appFolders.Remove(accountId);
        }
    }


    private DriveService CreateService(Func<bool, CancellationToken, Task<string>> tokenSource)
    {
        var svc = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new BearerTokenInitializer(tokenSource),
            ApplicationName = "MultiDriveCloud/1.0",
            GZipEnabled = false,
        });
        svc.HttpClient.Timeout = TimeSpan.FromMinutes(30);
        return svc;
    }

    private DriveService Service(string accountId)
    {
        lock (_services)
        {
            if (!_services.TryGetValue(accountId, out var svc))
                _services[accountId] = svc = CreateService((force, ct) => _tokens.GetAccessTokenAsync(accountId, force, ct));
            return svc;
        }
    }

    private async Task<string> AppFolderIdAsync(string accountId, DriveService svc, CancellationToken ct)
    {
        lock (_appFolders) if (_appFolders.TryGetValue(accountId, out var cached)) return cached;
        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_appFolders) if (_appFolders.TryGetValue(accountId, out var cached)) return cached;
            var list = svc.Files.List();
            list.Q = $"mimeType='application/vnd.google-apps.folder' and name='{AppFolderName}' and trashed=false and 'root' in parents";
            list.Fields = "files(id,name)";
            list.PageSize = 1;
            var found = await list.ExecuteAsync(ct).ConfigureAwait(false);
            var id = found.Files?.FirstOrDefault()?.Id;
            if (id is null)
            {
                var create = svc.Files.Create(new DriveFile { Name = AppFolderName, MimeType = "application/vnd.google-apps.folder" });
                create.Fields = "id";
                id = (await create.ExecuteAsync(ct).ConfigureAwait(false)).Id;
            }
            lock (_appFolders) _appFolders[accountId] = id;
            return id;
        }
        finally { _folderLock.Release(); }
    }


    public async Task<AccountQuota> GetQuotaAsync(string accountId, CancellationToken ct)
    {
        try
        {
            var req = Service(accountId).About.Get();
            req.Fields = "storageQuota(limit,usage)";
            var about = await req.ExecuteAsync(ct).ConfigureAwait(false);
            var usage = about.StorageQuota?.Usage ?? 0;
            if (about.StorageQuota?.Limit is long limit) return new AccountQuota(limit, usage, false);
            return new AccountQuota(usage + UnlimitedAssumedBytes, usage, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { throw Map(ex, ct); }
    }

    public async Task<RemoteFileInfo> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        try
        {
            var svc = Service(request.AccountId);
            var folderId = await AppFolderIdAsync(request.AccountId, svc, ct).ConfigureAwait(false);
            var props = new Dictionary<string, string>(request.Properties) { ["mdcChunk"] = request.FragmentKey };
            var meta = new DriveFile
            {
                Name = request.RemoteName,
                Parents = new List<string> { folderId },
                MimeType = "application/octet-stream",
                AppProperties = props,
            };

            if (request.Length == 0)
            {
                var empty = svc.Files.Create(meta);
                empty.Fields = "id,size,md5Checksum";
                return ToInfo(await empty.ExecuteAsync(ct).ConfigureAwait(false));
            }

            var upload = svc.Files.Create(meta, request.Content, "application/octet-stream");
            upload.Fields = "id,size,md5Checksum";
            upload.ChunkSize = UploadChunkSize;
            upload.ProgressChanged += p => request.Progress?.Report(p.BytesSent);
            upload.UploadSessionData += d => request.OnSessionCreated?.Invoke(d.UploadUri.ToString());

            IUploadProgress result;
            if (request.ResumeSessionUri is { } uri)
                result = await upload.ResumeAsync(new Uri(uri), ct).ConfigureAwait(false);
            else
                result = await upload.UploadAsync(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (result.Status != UploadStatus.Completed)
                throw result.Exception ?? new IOException("Envoi interrompu.");
            return ToInfo(upload.ResponseBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { throw Map(ex, ct); }
    }

    public async Task DownloadAsync(DownloadRequest request, CancellationToken ct)
    {
        var url = $"{ApiBase}/drive/v3/files/{Uri.EscapeDataString(request.RemoteId)}?alt=media";
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                var token = await _tokens.GetAccessTokenAsync(request.AccountId, attempt > 0, ct).ConfigureAwait(false);
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { throw Map(ex, ct); }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;                if (!response.IsSuccessStatusCode) throw await ErrorFromResponseAsync(response, ct).ConfigureAwait(false);

                Stream body;
                try { body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { throw Map(ex, ct); }

                await using (body)
                {
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    try
                    {
                        long total = 0;
                        while (true)
                        {
                            int read;
                            try { read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { throw Map(ex, ct); }
                            if (read == 0) break;
                            await request.Destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            total += read;
                            request.Progress?.Report(total);
                        }
                    }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
                }
                return;
            }
        }
    }

    private static async Task<StorageProviderException> ErrorFromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string reason = "", message = "";
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                if (err.TryGetProperty("errors", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array && list.GetArrayLength() > 0
                    && list[0].TryGetProperty("reason", out var r))
                    reason = r.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException or IOException) { }
        return MapHttp((int)response.StatusCode, reason, message, null);
    }

    public async Task<RemoteFileInfo?> GetFileInfoAsync(string accountId, string remoteId, CancellationToken ct)
    {
        try
        {
            var get = Service(accountId).Files.Get(remoteId);
            get.Fields = "id,size,md5Checksum";
            return ToInfo(await get.ExecuteAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var mapped = Map(ex, ct);
            if (mapped.Kind == ProviderErrorKind.NotFound) return null;
            throw mapped;
        }
    }

    public async Task<RemoteFileInfo?> FindFragmentAsync(string accountId, string fragmentKey, CancellationToken ct)
    {
        try
        {
            var list = Service(accountId).Files.List();
            list.Q = $"appProperties has {{ key='mdcChunk' and value='{fragmentKey.Replace("'", "")}' }} and trashed=false";
            list.Fields = "files(id,size,md5Checksum)";
            list.PageSize = 1;
            var found = await list.ExecuteAsync(ct).ConfigureAwait(false);
            var f = found.Files?.FirstOrDefault();
            return f is null ? null : ToInfo(f);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { throw Map(ex, ct); }
    }

    public async Task DeleteAsync(string accountId, string remoteId, CancellationToken ct)
    {
        try
        {
            await Service(accountId).Files.Delete(remoteId).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var mapped = Map(ex, ct);
            if (mapped.Kind != ProviderErrorKind.NotFound) throw mapped;
        }
    }

    private static RemoteFileInfo ToInfo(DriveFile f) => new(f.Id, f.Size ?? 0, f.Md5Checksum);


    public static StorageProviderException Map(Exception ex, CancellationToken ct = default)
    {
        switch (ex)
        {
            case StorageProviderException spe:
                return spe;

            case GoogleApiException g:
                return MapHttp((int)g.HttpStatusCode, g.Error?.Errors?.FirstOrDefault()?.Reason ?? "", g.Error?.Message ?? "", ex);

            case OperationCanceledException when ct.IsCancellationRequested:
                throw ex;

            case HttpRequestException or SocketException or TimeoutException or OperationCanceledException:
                return new StorageProviderException(ProviderErrorKind.Transient, "Erreur réseau ou délai dépassé.", ex);

            case IOException io when io.InnerException is SocketException or HttpRequestException || IsNetworkIo(io):
                return new StorageProviderException(ProviderErrorKind.Transient, "Connexion interrompue.", ex);

            default:
                return new StorageProviderException(ProviderErrorKind.Fatal,
                    $"Erreur inattendue ({ex.GetType().Name}) : {MultiDriveCloud.Core.Util.Redactor.Redact(ex.Message)}", ex);
        }
    }

    public static StorageProviderException MapHttp(int status, string reason, string apiMessage, Exception? inner)
    {
        var msg = $"Google Drive a répondu {status}" + (reason.Length > 0 ? $" ({reason})" : "") + ".";
        if (status == 401) return new StorageProviderException(ProviderErrorKind.AuthRequired, "Autorisation Google refusée (401).", inner);
        if (status == 404 || status == 410) return new StorageProviderException(ProviderErrorKind.NotFound, "Ressource Google Drive introuvable ou session expirée.", inner);
        if (status == 429) return new StorageProviderException(ProviderErrorKind.RateLimited, "Trop de requêtes vers Google Drive (429).", inner);
        if (status == 403)
        {
            return reason switch
            {
                "storageQuotaExceeded" => new StorageProviderException(ProviderErrorKind.StorageFull, "Le Google Drive est plein.", inner),
                "userRateLimitExceeded" or "rateLimitExceeded" or "sharingRateLimitExceeded" =>
                    new StorageProviderException(ProviderErrorKind.RateLimited, "Limite de débit Google Drive atteinte.", inner),
                "dailyLimitExceeded" or "quotaExceeded" or "downloadQuotaExceeded" or "dailyLimitExceededUnreg" =>
                    new StorageProviderException(ProviderErrorKind.DailyQuota, "Quota quotidien Google Drive atteint.", inner),
                "authError" or "forbidden" when apiMessage.Contains("scope", StringComparison.OrdinalIgnoreCase) =>
                    new StorageProviderException(ProviderErrorKind.AuthRequired, "Permission Drive insuffisante : réautorisez le compte.", inner),
                _ => new StorageProviderException(ProviderErrorKind.Fatal, msg, inner),
            };
        }
        if (status == 408 || status >= 500) return new StorageProviderException(ProviderErrorKind.Transient, msg, inner);
        return new StorageProviderException(ProviderErrorKind.Fatal, msg, inner);
    }

    private static bool IsNetworkIo(IOException io) =>
        io.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
        io.Message.Contains("connexion", StringComparison.OrdinalIgnoreCase) ||
        io.Message.Contains("transport", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_services)
        {
            foreach (var s in _services.Values) s.Dispose();
            _services.Clear();
        }
    }
}

