using System.Buffers;
using System.Security.Cryptography;

namespace MultiDriveCloud.Core.Storage;

public sealed record FragmentRange(long Offset, long Length);

public sealed record FragmentHash(string Sha256, string Md5);

public sealed record FileHashResult(string FileSha256, IReadOnlyList<FragmentHash> Fragments);

public static class IntegrityService
{
    public const int BufferSize = 1024 * 1024;

    public static FileStream OpenSequentialRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });

    public static async Task<string> Sha256FileAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await using var fs = OpenSequentialRead(path);
        return await Sha256StreamAsync(fs, progress, ct).ConfigureAwait(false);
    }

    public static async Task<string> Sha256StreamAsync(Stream stream, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task<string> Sha256RangeAsync(string path, long offset, long length, CancellationToken ct = default)
    {
        await using var fs = OpenSequentialRead(path);
        fs.Seek(offset, SeekOrigin.Begin);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                var want = (int)Math.Min(BufferSize, remaining);
                var read = await fs.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Le fichier est plus court que prévu.");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task<string> Sha256RangeAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, long length,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            while (done < length)
            {
                var want = (int)Math.Min(BufferSize, length - done);
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, want), offset + done, ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Le fichier est plus court que prévu.");
                hash.AppendData(buffer, 0, read);
                done += read;
                progress?.Report(done);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task<string> Md5FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = OpenSequentialRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task<FileHashResult> HashFileWithFragmentsAsync(
        string path, IReadOnlyList<FragmentRange> fragments, IProgress<long>? progress, CancellationToken ct)
    {
        await using var fs = OpenSequentialRead(path);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var results = new List<FragmentHash>(fragments.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            foreach (var frag in fragments)
            {
                if (fs.Position != frag.Offset)
                    throw new IOException("Plan de fragments incohérent avec le fichier.");
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                long remaining = frag.Length;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(BufferSize, remaining);
                    var read = await fs.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (read == 0) throw new IOException("Le fichier a été tronqué pendant la lecture.");
                    sha.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    fileHash.AppendData(buffer, 0, read);
                    remaining -= read;
                    done += read;
                    progress?.Report(done);
                }
                results.Add(new FragmentHash(Convert.ToHexStringLower(sha.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset())));
            }
            if (fs.ReadByte() != -1)
                throw new IOException("Le fichier a grandi pendant la lecture.");
            return new FileHashResult(Convert.ToHexStringLower(fileHash.GetHashAndReset()), results);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static bool HashEquals(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
