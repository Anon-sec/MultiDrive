using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MultiDriveCloud.Core.Util;

public static partial class Redactor
{
    [GeneratedRegex(@"ya29\.[A-Za-z0-9_\-\.]+", RegexOptions.Compiled)]
    private static partial Regex AccessToken();

    [GeneratedRegex(@"1//[A-Za-z0-9_\-\.]{20,}", RegexOptions.Compiled)]
    private static partial Regex RefreshToken();

    [GeneratedRegex(@"(?i)(access_token|refresh_token|client_secret|code_verifier|id_token|upload_id|authorization|bearer|password|code)([""'=:\s]+)([A-Za-z0-9_\-\.~/+]{8,})", RegexOptions.Compiled)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]*", RegexOptions.Compiled)]
    private static partial Regex Jwt();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = AccessToken().Replace(text, "[token]");
        text = RefreshToken().Replace(text, "[token]");
        text = Jwt().Replace(text, "[jwt]");
        text = KeyValue().Replace(text, "$1$2[masqué]");
        return text;
    }
}

public static class FileUtil
{
    public static async Task MoveWithRetryAsync(string source, string destination, bool overwrite, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException) when (attempt < 8 && File.Exists(source))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 8 && File.Exists(source))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }
}

public static class PathGuard
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
    };

    private static readonly char[] InvalidNameChars = "\\/:*?\"<>|".ToCharArray();

    public static string? ValidateVirtualName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Le nom ne peut pas être vide.";
        if (name.Length > 255) return "Le nom est trop long (255 caractères maximum).";
        if (name is "." or "..") return "Nom réservé.";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
            return "Le nom ne peut pas contenir \\ / : * ? \" < > |";
        if (name != name.Trim() || name.EndsWith('.')) return "Le nom ne peut pas commencer/finir par un espace ni finir par un point.";
        return null;
    }

    public static string SanitizeFileName(string name)
    {
        var chars = name.Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = "_";
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(stem)) s = "_" + s;
        return s.Length > 200 ? s[..200] : s;
    }

    public static string CombineInside(string root, params string[] segments)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = fullRoot;
        foreach (var seg in segments) combined = Path.Combine(combined, SanitizeFileName(seg));
        var full = Path.GetFullPath(combined);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chemin en dehors du dossier de destination refusé.");
        return full;
    }

    public static string? ValidateSourceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return "Chemin non valide.";
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return "Les chemins de périphériques sont refusés.";
        var info = new FileInfo(path);
        if (!info.Exists) return "Fichier introuvable.";
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "Les liens symboliques ne sont pas suivis.";
        return null;
    }

    public static string UniqueLocalPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Impossible de trouver un nom de fichier libre.");
    }
}

public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}

public sealed class RangeReadStream : Stream
{
    private readonly FileStream _fs;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    public RangeReadStream(string path, long offset, long length)
    {
        _fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
        _offset = offset;
        _length = length;
        _fs.Seek(offset, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var want = (int)Math.Min(buffer.Length, _length - _position);
        if (want <= 0) return 0;
        var n = _fs.Read(buffer[..want]);
        _position += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var want = (int)Math.Min(buffer.Length, _length - _position);
        if (want <= 0) return 0;
        var n = await _fs.ReadAsync(buffer[..want], ct).ConfigureAwait(false);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target < 0 || target > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        _position = target;
        _fs.Seek(_offset + target, SeekOrigin.Begin);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fs.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class OffsetWriteStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly long _start;
    private readonly long _maxLength;
    private readonly Action<long>? _onWritten;
    private long _written;

    public OffsetWriteStream(SafeFileHandle handle, long start, long maxLength, Action<long>? onWritten = null)
    {
        _handle = handle;
        _start = start;
        _maxLength = maxLength;
        _onWritten = onWritten;
    }

    public long Written => _written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }

    private void Check(int count)
    {
        if (_written + count > _maxLength)
            throw new InvalidDataException("Le fragment reçu est plus grand que prévu.");
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        RandomAccess.Write(_handle, buffer, _start + _written);
        _written += buffer.Length;
        _onWritten?.Invoke(_written);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Check(buffer.Length);
        await RandomAccess.WriteAsync(_handle, buffer, _start + _written, ct).ConfigureAwait(false);
        _written += buffer.Length;
        _onWritten?.Invoke(_written);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class Md5WriteStream : Stream
{
    private readonly Stream _inner;
    private readonly System.Security.Cryptography.IncrementalHash _md5 =
        System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
    private long _length;

    public Md5WriteStream(Stream inner) => _inner = inner;

    public long BytesWritten => _length;
    public string Md5Hex() => Convert.ToHexStringLower(_md5.GetHashAndReset());

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _md5.AppendData(buffer);
        _inner.Write(buffer);
        _length += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _md5.AppendData(buffer.Span);
        await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        _length += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _md5.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class SpeedMeter
{
    private readonly object _lock = new();
    private readonly Queue<(long Ticks, long Total)> _samples = new();
    private long _total;
    private static readonly long Window = TimeSpan.FromSeconds(5).Ticks;

    public void Add(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            _total += bytes;
            var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
            _samples.Enqueue((now, _total));
            while (_samples.Count > 1 && now - _samples.Peek().Ticks > Window) _samples.Dequeue();
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return 0;
                var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
                var last = _samples.Last();
                if (now - last.Ticks > TimeSpan.FromSeconds(3).Ticks) return 0;
                var first = _samples.Peek();
                var dt = (last.Ticks - first.Ticks) / (double)TimeSpan.TicksPerSecond;
                if (dt < 0.25) return 0;
                return (last.Total - first.Total) / dt;
            }
        }
    }

    public void Reset()
    {
        lock (_lock) { _samples.Clear(); _total = 0; }
    }
}
