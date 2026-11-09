using System.Text;
using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Infrastructure.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information, int keepDays = 14)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var f in Directory.EnumerateFiles(directory, "multidrive-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keepDays)) File.Delete(f);
        }
        catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    internal bool IsEnabled(LogLevel level) => level >= _minimum;

    internal void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
          .Append(level.ToString()[..3].ToUpperInvariant()).Append(' ')
          .Append(category.Split('.').Last()).Append(" | ")
          .Append(Redactor.Redact(message));
        if (ex is not null)
            sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(Redactor.Redact(ex.Message));
        sb.AppendLine();
        var path = Path.Combine(_directory, $"multidrive-{DateTime.Now:yyyyMMdd}.log");
        lock (_lock)
        {
            try { File.AppendAllText(path, sb.ToString(), Encoding.UTF8); }
            catch { }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => _owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
