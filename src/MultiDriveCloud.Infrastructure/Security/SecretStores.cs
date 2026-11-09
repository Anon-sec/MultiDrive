using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Storage;

namespace MultiDriveCloud.Infrastructure.Security;

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedBase64);
}

public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "MultiDriveCloud.v1"u8.ToArray();

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedBase64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser));
}

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DpapiSecretStore(string directory, ISecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string name) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))) + ".bin");

    public async Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathFor(name);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, _protector.Protect(value), ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            try { return _protector.Unprotect(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)); }
            catch (CryptographicException) { return null; }        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _lock.Release(); }
    }
}

public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();

    public Task SetAsync(string name, string value, CancellationToken ct = default) { lock (_values) _values[name] = value; return Task.CompletedTask; }
    public Task<string?> GetAsync(string name, CancellationToken ct = default) { lock (_values) return Task.FromResult(_values.GetValueOrDefault(name)); }
    public Task DeleteAsync(string name, CancellationToken ct = default) { lock (_values) _values.Remove(name); return Task.CompletedTask; }
}

public sealed class PassthroughProtector : ISecretProtector
{
    public string Protect(string plain) => plain;
    public string Unprotect(string protectedBase64) => protectedBase64;
}

public sealed class KeyImportException : Exception
{
    public KeyImportException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class EncryptionKeyManager : IEncryptionKeyProvider
{
    private const string SecretName = "encryption-master-key";
    private const int Iterations = 600_000;
    private readonly ISecretStore _secrets;

    public EncryptionKeyManager(ISecretStore secrets) => _secrets = secrets;

    public async Task<byte[]?> GetKeyAsync(CancellationToken ct = default)
    {
        var b64 = await _secrets.GetAsync(SecretName, ct).ConfigureAwait(false);
        return b64 is null ? null : Convert.FromBase64String(b64);
    }

    public async Task<bool> EnsureKeyAsync(CancellationToken ct = default)
    {
        if (await _secrets.GetAsync(SecretName, ct).ConfigureAwait(false) is not null) return false;
        var key = AesGcmStreamCipher.GenerateKey();
        await _secrets.SetAsync(SecretName, Convert.ToBase64String(key), ct).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
        return true;
    }

    public async Task<bool> HasKeyAsync(CancellationToken ct = default) =>
        await _secrets.GetAsync(SecretName, ct).ConfigureAwait(false) is not null;

    private sealed record KeyFile(int Version, string Kdf, int Iterations, string Salt, string Nonce, string Ciphertext, string Tag);

    public async Task ExportKeyAsync(string path, string passphrase, CancellationToken ct = default)
    {
        if (passphrase.Length < 10) throw new ArgumentException("La phrase secrète doit contenir au moins 10 caractères.");
        var key = await GetKeyAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Aucune clé de chiffrement n'existe.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(kek, 16)) aes.Encrypt(nonce, key, cipher, tag, "MultiDriveCloud key export v1"u8);
        CryptographicOperations.ZeroMemory(kek);
        CryptographicOperations.ZeroMemory(key);
        var doc = new KeyFile(1, "PBKDF2-SHA256", Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
    }

    public async Task ImportKeyAsync(string path, string passphrase, bool overwrite, CancellationToken ct = default)
    {
        KeyFile doc;
        try
        {
            doc = JsonSerializer.Deserialize<KeyFile>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new KeyImportException("Fichier de clé vide.");
        }
        catch (JsonException ex) { throw new KeyImportException("Fichier de clé invalide.", ex); }

        byte[] key;
        try
        {
            var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase, Convert.FromBase64String(doc.Salt), doc.Iterations, HashAlgorithmName.SHA256, 32);
            key = new byte[32];
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(Convert.FromBase64String(doc.Nonce), Convert.FromBase64String(doc.Ciphertext), Convert.FromBase64String(doc.Tag), key, "MultiDriveCloud key export v1"u8);
            CryptographicOperations.ZeroMemory(kek);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new KeyImportException("Phrase secrète incorrecte ou fichier de clé altéré.", ex);
        }

        var existing = await GetKeyAsync(ct).ConfigureAwait(false);
        if (existing is not null && !existing.AsSpan().SequenceEqual(key) && !overwrite)
            throw new KeyImportException("Une autre clé existe déjà. Les fichiers déjà chiffrés avec elle deviendraient illisibles : confirmez explicitement le remplacement.");
        await _secrets.SetAsync(SecretName, Convert.ToBase64String(key), ct).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
    }
}
