using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.GoogleDrive.Authentication;

public sealed record GoogleOAuthConfig(string ClientId, string? ClientSecret);

public interface IGoogleOAuthConfigProvider
{
    Task<GoogleOAuthConfig?> GetAsync(CancellationToken ct = default);
}

public interface IBrowserLauncher
{
    void Open(string url);
}

public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        if (!url.StartsWith("https://accounts.google.com/", StringComparison.Ordinal))
            throw new InvalidOperationException("URL d'autorisation inattendue.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

public sealed class GoogleOAuthConfigProvider : IGoogleOAuthConfigProvider
{
    public const string SecretName = "google-client-secret";
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly string[] _jsonSearchPaths;

    public GoogleOAuthConfigProvider(ISettingsService settings, ISecretStore secrets, params string[] jsonSearchDirectories)
    {
        _settings = settings;
        _secrets = secrets;
        _jsonSearchPaths = jsonSearchDirectories.Select(d => Path.Combine(d, "google-oauth.json")).ToArray();
    }

    public async Task<GoogleOAuthConfig?> GetAsync(CancellationToken ct = default)
    {
        var id = _settings.Current.GoogleClientId;
        if (!string.IsNullOrWhiteSpace(id))
            return new GoogleOAuthConfig(id.Trim(), await _secrets.GetAsync(SecretName, ct).ConfigureAwait(false));

        var envId = Environment.GetEnvironmentVariable("MULTIDRIVE_GOOGLE_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(envId))
            return new GoogleOAuthConfig(envId.Trim(), Environment.GetEnvironmentVariable("MULTIDRIVE_GOOGLE_CLIENT_SECRET"));

        foreach (var path in _jsonSearchPaths.Where(File.Exists))
        {
            var parsed = ParseJson(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            if (parsed is not null) return parsed;
        }
        return null;
    }

    public async Task SaveAsync(string clientId, string? clientSecret, CancellationToken ct = default)
    {
        var s = _settings.Current;
        s.GoogleClientId = clientId.Trim();
        await _settings.SaveAsync(s, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientSecret)) await _secrets.DeleteAsync(SecretName, ct).ConfigureAwait(false);
        else await _secrets.SetAsync(SecretName, clientSecret.Trim(), ct).ConfigureAwait(false);
    }

    public async Task ImportJsonAsync(string path, CancellationToken ct = default)
    {
        var parsed = ParseJson(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("Ce fichier ne contient pas d'identifiant client OAuth Google (client_id).");
        if (ValidateClientId(parsed.ClientId) is { } problem) throw new InvalidDataException(problem);
        await SaveAsync(parsed.ClientId, parsed.ClientSecret, ct).ConfigureAwait(false);
    }

    public static string? ValidateClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return "L'identifiant client est vide.";
        var id = clientId.Trim();
        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || id.Length < 40 || id.Contains(' '))
            return "Un identifiant client Google ressemble à 123456789-abcdef.apps.googleusercontent.com.";
        return null;
    }

    public static IReadOnlyList<string> FindDownloadedJsonFiles(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();
            return new DirectoryInfo(directory).EnumerateFiles("client_secret*.json")
                .Where(f => f.Length < 64 * 1024)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Where(f =>
                {
                    try { var c = ParseJson(File.ReadAllText(f.FullName)); return c is not null && ValidateClientId(c.ClientId) is null; }
                    catch (IOException) { return false; }
                })
                .Select(f => f.FullName)
                .Take(5)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static GoogleOAuthConfig? ParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var wrapper in new[] { "installed", "web" })
                if (root.TryGetProperty(wrapper, out var inner)) { root = inner; break; }
            var id = root.TryGetProperty("client_id", out var i) ? i.GetString() : null;
            var secret = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
            return string.IsNullOrWhiteSpace(id) ? null : new GoogleOAuthConfig(id, secret);
        }
        catch (JsonException) { return null; }
    }
}

public sealed class TokenSet
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_at")] public DateTime ExpiresAtUtc { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
}

public sealed class GoogleOAuthClient
{
    public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly IBrowserLauncher _browser;
    private readonly TimeSpan _loginTimeout;

    public GoogleOAuthClient(IBrowserLauncher browser, TimeSpan? loginTimeout = null)
    {
        _browser = browser;
        _loginTimeout = loginTimeout ?? TimeSpan.FromMinutes(5);
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task<TokenSet> AuthorizeInteractiveAsync(GoogleOAuthConfig config, string? loginHint, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        using var listener = StartListener(out var redirectUri);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveFileScope,
            ["code_challenge"] = CodeChallenge(verifier),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint;
        var url = AuthEndpoint + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        _browser.Open(url);

        var code = await WaitForCallbackAsync(listener, state, ct).ConfigureAwait(false);
        return await ExchangeCodeAsync(config, code, verifier, redirectUri, ct).ConfigureAwait(false);
    }

    private static HttpListener StartListener(out string redirectUri)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                redirectUri = $"http://127.0.0.1:{port}/oauth2callback";
                return listener;
            }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new StorageProviderException(ProviderErrorKind.Fatal, "Impossible d'ouvrir un port local pour recevoir la réponse de Google.");
    }

    private async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_loginTimeout);
        using var reg = timeout.Token.Register(() => { try { listener.Stop(); } catch { } });

        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new StorageProviderException(ProviderErrorKind.Fatal, "Délai dépassé : aucune réponse de Google reçue dans le navigateur.");
            }

            var request = context.Request;
            if (request.Url?.AbsolutePath != "/oauth2callback")
            {
                Respond(context, 404, "Introuvable.");
                continue;
            }

            var q = request.QueryString;
            var error = q["error"];
            var returnedState = q["state"];
            var code = q["code"];

            if (!FixedTimeEquals(returnedState, expectedState))
            {
                Respond(context, 400, "Requête rejetée (paramètre state invalide). Fermez cet onglet et recommencez.");
                throw new StorageProviderException(ProviderErrorKind.Fatal, "Réponse OAuth rejetée : le paramètre state ne correspond pas (protection CSRF).");
            }
            if (!string.IsNullOrEmpty(error))
            {
                Respond(context, 200, "Autorisation refusée ou annulée. Vous pouvez fermer cet onglet.");
                throw new StorageProviderException(ProviderErrorKind.Fatal,
                    error == "access_denied" ? "Autorisation refusée dans le navigateur." : "Google a renvoyé une erreur : " + error);
            }
            if (string.IsNullOrEmpty(code))
            {
                Respond(context, 400, "Réponse incomplète.");
                throw new StorageProviderException(ProviderErrorKind.Fatal, "Réponse OAuth sans code d'autorisation.");
            }
            Respond(context, 200, "Compte autorisé. Vous pouvez fermer cet onglet et retourner dans MultiDrive Cloud.");
            return code;
        }
    }

    private static bool FixedTimeEquals(string? a, string? b) =>
        a is not null && b is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static void Respond(HttpListenerContext ctx, int status, string message)
    {
        var html = $"<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>MultiDrive Cloud</title>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;background:#12151c;color:#e8ebf2;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
                   $"<div style=\"text-align:center\"><h2>MultiDrive Cloud</h2><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private async Task<TokenSet> ExchangeCodeAsync(GoogleOAuthConfig config, string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = config.ClientId,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        };
        if (!string.IsNullOrEmpty(config.ClientSecret)) form["client_secret"] = config.ClientSecret;
        var tokens = await PostTokenAsync(form, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new StorageProviderException(ProviderErrorKind.Fatal,
                "Google n'a pas fourni de jeton de renouvellement. Révoquez l'accès de l'application sur myaccount.google.com/permissions puis recommencez.");
        if (tokens.Scope is not null && !tokens.Scope.Split(' ').Contains(DriveFileScope))
            throw new StorageProviderException(ProviderErrorKind.Fatal, "La permission d'accès à Google Drive n'a pas été accordée. Cochez-la sur l'écran d'autorisation.");
        return tokens;
    }

    public async Task<TokenSet> RefreshAsync(GoogleOAuthConfig config, string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
        };
        if (!string.IsNullOrEmpty(config.ClientSecret)) form["client_secret"] = config.ClientSecret;
        var tokens = await PostTokenAsync(form, ct).ConfigureAwait(false);
        tokens.RefreshToken ??= refreshToken;
        return tokens;
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await Http.PostAsync(RevokeEndpoint, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    private static async Task<TokenSet> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        string body;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            response = await Http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            throw new StorageProviderException(ProviderErrorKind.Transient, "Impossible de joindre Google (réseau).", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string err = "", desc = "";
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                    desc = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
                }
                catch (JsonException) { }

                throw err switch
                {
                    "invalid_grant" => new StorageProviderException(ProviderErrorKind.AuthRequired,
                        "L'autorisation Google a expiré ou a été révoquée (invalid_grant). Les jetons des applications en mode « Test » expirent après 7 jours."),
                    "invalid_client" or "unauthorized_client" => new StorageProviderException(ProviderErrorKind.Fatal,
                        "Identifiants OAuth refusés par Google (client_id/client_secret). Vérifiez la configuration OAuth dans les Paramètres."),
                    _ when (int)response.StatusCode >= 500 => new StorageProviderException(ProviderErrorKind.Transient, "Service d'authentification Google indisponible."),
                    _ => new StorageProviderException(ProviderErrorKind.Fatal, Redactor.Redact($"Échec de l'authentification Google : {err} {desc}".Trim())),
                };
            }

            var parsed = JsonSerializer.Deserialize<TokenResponse>(body)
                ?? throw new StorageProviderException(ProviderErrorKind.Fatal, "Réponse d'authentification illisible.");
            if (string.IsNullOrEmpty(parsed.AccessToken))
                throw new StorageProviderException(ProviderErrorKind.Fatal, "Réponse d'authentification sans jeton d'accès.");
            return new TokenSet
            {
                AccessToken = parsed.AccessToken,
                RefreshToken = parsed.RefreshToken,
                Scope = parsed.Scope,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, parsed.ExpiresIn)),
            };
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }
}

public sealed class GoogleTokenManager
{
    private readonly ISecretStore _secrets;
    private readonly GoogleOAuthClient _oauth;
    private readonly IGoogleOAuthConfigProvider _config;
    private readonly Dictionary<string, TokenSet> _cache = new();
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();

    public GoogleTokenManager(ISecretStore secrets, GoogleOAuthClient oauth, IGoogleOAuthConfigProvider config)
    {
        _secrets = secrets;
        _oauth = oauth;
        _config = config;
    }

    private static string SecretKey(string accountId) => "google-tokens:" + accountId;

    private SemaphoreSlim LockFor(string accountId)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out var l)) _locks[accountId] = l = new SemaphoreSlim(1, 1);
            return l;
        }
    }

    public async Task SaveAsync(string accountId, TokenSet tokens, CancellationToken ct = default)
    {
        await _secrets.SetAsync(SecretKey(accountId), JsonSerializer.Serialize(tokens), ct).ConfigureAwait(false);
        lock (_cache) _cache[accountId] = tokens;
    }

    public async Task<string> GetAccessTokenAsync(string accountId, bool forceRefresh, CancellationToken ct)
    {
        var gate = LockFor(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TokenSet? tokens;
            lock (_cache) _cache.TryGetValue(accountId, out tokens);
            if (tokens is null)
            {
                var json = await _secrets.GetAsync(SecretKey(accountId), ct).ConfigureAwait(false);
                if (json is null)
                    throw new StorageProviderException(ProviderErrorKind.AuthRequired, "Aucun jeton enregistré pour ce compte : il faut le réautoriser.");
                tokens = JsonSerializer.Deserialize<TokenSet>(json)!;
            }

            if (!forceRefresh && tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
            {
                lock (_cache) _cache[accountId] = tokens;
                return tokens.AccessToken;
            }

            if (string.IsNullOrEmpty(tokens.RefreshToken))
                throw new StorageProviderException(ProviderErrorKind.AuthRequired, "Jeton de renouvellement absent : il faut réautoriser le compte.");
            var config = await _config.GetAsync(ct).ConfigureAwait(false)
                ?? throw new StorageProviderException(ProviderErrorKind.Fatal, "L'identifiant client OAuth Google n'est pas configuré (Paramètres › Google OAuth).");

            var refreshed = await _oauth.RefreshAsync(config, tokens.RefreshToken, ct).ConfigureAwait(false);
            await SaveAsync(accountId, refreshed, ct).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally { gate.Release(); }
    }

    public async Task<string?> GetRefreshTokenAsync(string accountId, CancellationToken ct)
    {
        var json = await _secrets.GetAsync(SecretKey(accountId), ct).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<TokenSet>(json)?.RefreshToken;
    }

    public async Task RemoveAsync(string accountId, CancellationToken ct = default)
    {
        lock (_cache) _cache.Remove(accountId);
        await _secrets.DeleteAsync(SecretKey(accountId), ct).ConfigureAwait(false);
    }

    public Task RevokeAsync(string token, CancellationToken ct) => _oauth.RevokeAsync(token, ct);
}
