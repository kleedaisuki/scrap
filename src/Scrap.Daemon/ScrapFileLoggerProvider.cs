using System.Text;
using Microsoft.Extensions.Logging;
using Scrap.Platform.Paths;

namespace Scrap.Daemon;

/// <summary>
/// 将 daemon 自身的非敏感结构化事件写入当前 profile 私有日志，且绝不占用 client 的 stdout/stderr。
/// / Writes the daemon's non-sensitive structured events to the private profile log without touching client stdout/stderr.
/// </summary>
internal sealed class ScrapFileLoggerProvider : ILoggerProvider
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private StreamWriter? writer;

    /// <summary>
    /// 创建 UTF-8（无 BOM）append-only logger。 / Creates an append-only UTF-8 logger without a BOM.
    /// </summary>
    /// <param name="logFile">私有日志路径。 / Private log path.</param>
    public ScrapFileLoggerProvider(string logFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
        path = Path.GetFullPath(logFile);
        try
        {
            writer = OpenWriter(rotate: true);
        }
        catch (Exception exception) when (IsLoggingIoFailure(exception))
        {
            writer = null;
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            DisableLogging();
        }
    }

    private void Write(LogLevel level, string category, EventId eventId, string message)
    {
        lock (gate)
        {
            try
            {
                if (writer is null)
                {
                    return;
                }

                if (writer.BaseStream.Length >= MaximumLogBytes)
                {
                    writer.Dispose();
                    writer = OpenWriter(rotate: true);
                }

                writer.WriteLine(
                    $"{DateTimeOffset.UtcNow:O} [{level}] {category} {eventId.Id}:{eventId.Name} {message}");
            }
            catch (Exception sinkFailure) when (IsLoggingIoFailure(sinkFailure))
            {
                DisableLogging();
            }
        }
    }

    private StreamWriter OpenWriter(bool rotate)
    {
        PlatformPathPermissions.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        if (rotate && File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
        {
            File.Move(path, $"{path}.1", overwrite: true);
        }

        if (!File.Exists(path))
        {
            using FileStream created = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }

        PlatformPathPermissions.EnsurePrivateFile(path);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false, true)) { AutoFlush = true };
    }

    private void DisableLogging()
    {
        StreamWriter? current = Interlocked.Exchange(ref writer, null);
        if (current is null)
        {
            return;
        }

        try
        {
            current.Dispose();
        }
        catch (Exception exception) when (IsLoggingIoFailure(exception))
        {
            // Logging is best-effort and must never break a completed data operation.
        }
    }

    private static bool IsLoggingIoFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ObjectDisposedException or PlatformPathException;

    private sealed class FileLogger : ILogger
    {
        private readonly ScrapFileLoggerProvider provider;
        private readonly string category;

        public FileLogger(ScrapFileLoggerProvider provider, string category)
        {
            this.provider = provider;
            this.category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // The exception object is deliberately not formatted: it could contain an IPC value.
            try
            {
                provider.Write(logLevel, category, eventId, formatter(state, null));
            }
            catch (Exception sinkFailure) when (IsLoggingIoFailure(sinkFailure))
            {
                // A diagnostic sink cannot become part of request success semantics.
            }
        }
    }
}
