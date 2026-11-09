using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MultiDriveCloud.Core.Storage;

public static class AesGcmStreamCipher
{
    public const int SegmentSize = 1 << 20;
    public const int TagSize = 16;
    public const int SaltSize = 16;
    public const int HeaderSize = 4 + SaltSize + 4;
    private static readonly byte[] Magic = "MDC1"u8.ToArray();
    private static readonly byte[] KeyInfo = "MultiDriveCloud fragment key v1"u8.ToArray();

    public static long EncryptedLength(long plainLength)
    {
        var segments = Math.Max(1, (plainLength + SegmentSize - 1) / SegmentSize);
        return HeaderSize + plainLength + segments * TagSize;
    }

    public static long MaxPlainForEncrypted(long storedBytes)
    {
        var payload = storedBytes - HeaderSize;
        if (payload < TagSize) return 0;
        var full = payload / (SegmentSize + TagSize);
        var rem = payload % (SegmentSize + TagSize);
        return full * SegmentSize + (rem > TagSize ? rem - TagSize : 0);
    }

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);

    public static async Task EncryptAsync(Stream input, Stream output, byte[] masterKey, IProgress<long>? progress, CancellationToken ct)
    {
        if (masterKey.Length != 32) throw new ArgumentException("La clé doit faire 256 bits.", nameof(masterKey));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        salt.CopyTo(header, 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + SaltSize), SegmentSize);
        await output.WriteAsync(header, ct).ConfigureAwait(false);

        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt, KeyInfo);
        using var aes = new AesGcm(key, TagSize);
        var a = ArrayPool<byte>.Shared.Rent(SegmentSize);
        var b = ArrayPool<byte>.Shared.Rent(SegmentSize);
        var cipher = ArrayPool<byte>.Shared.Rent(SegmentSize);
        try
        {
            var nonce = new byte[12];
            var tag = new byte[TagSize];
            var aad = new byte[HeaderSize + 1];
            header.CopyTo(aad, 0);
            long index = 0, done = 0;
            var cur = await ReadFullAsync(input, a, SegmentSize, ct).ConfigureAwait(false);
            while (true)
            {
                var next = cur == SegmentSize ? await ReadFullAsync(input, b, SegmentSize, ct).ConfigureAwait(false) : 0;
                var last = next == 0;
                aad[HeaderSize] = last ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt64BigEndian(nonce, index);
                aes.Encrypt(nonce, a.AsSpan(0, cur), cipher.AsSpan(0, cur), tag, aad);
                await output.WriteAsync(cipher.AsMemory(0, cur), ct).ConfigureAwait(false);
                await output.WriteAsync(tag, ct).ConfigureAwait(false);
                done += cur;
                progress?.Report(done);
                if (last) break;
                (a, b) = (b, a);
                cur = next;
                index++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
            ArrayPool<byte>.Shared.Return(cipher);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task DecryptAsync(Stream input, Stream output, byte[] masterKey, IProgress<long>? progress, CancellationToken ct)
    {
        if (masterKey.Length != 32) throw new ArgumentException("La clé doit faire 256 bits.", nameof(masterKey));
        var header = new byte[HeaderSize];
        if (await ReadFullAsync(input, header, HeaderSize, ct).ConfigureAwait(false) != HeaderSize
            || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Format de fragment chiffré invalide.");
        var segSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4 + SaltSize));
        if (segSize != SegmentSize) throw new CryptographicException("Taille de segment non prise en charge.");

        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, header.AsSpan(4, SaltSize).ToArray(), KeyInfo);
        using var aes = new AesGcm(key, TagSize);
        var full = SegmentSize + TagSize;
        var a = ArrayPool<byte>.Shared.Rent(full);
        var b = ArrayPool<byte>.Shared.Rent(full);
        var plain = ArrayPool<byte>.Shared.Rent(SegmentSize);
        try
        {
            var nonce = new byte[12];
            var aad = new byte[HeaderSize + 1];
            header.CopyTo(aad, 0);
            long index = 0, done = 0;
            var cur = await ReadFullAsync(input, a, full, ct).ConfigureAwait(false);
            while (true)
            {
                if (cur < TagSize) throw new CryptographicException("Fragment chiffré tronqué.");
                var next = cur == full ? await ReadFullAsync(input, b, full, ct).ConfigureAwait(false) : 0;
                var last = next == 0;
                aad[HeaderSize] = last ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt64BigEndian(nonce, index);
                var dataLen = cur - TagSize;
                try
                {
                    aes.Decrypt(nonce, a.AsSpan(0, dataLen), a.AsSpan(dataLen, TagSize), plain.AsSpan(0, dataLen), aad);
                }
                catch (CryptographicException ex)
                {
                    throw new CryptographicException("Fragment chiffré corrompu, tronqué ou clé incorrecte.", ex);
                }
                await output.WriteAsync(plain.AsMemory(0, dataLen), ct).ConfigureAwait(false);
                done += dataLen;
                progress?.Report(done);
                if (last) break;
                (a, b) = (b, a);
                cur = next;
                index++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
            ArrayPool<byte>.Shared.Return(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
