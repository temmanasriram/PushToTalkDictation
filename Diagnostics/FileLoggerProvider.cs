using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PushToTalkDictation.Diagnostics;

/// <summary>
/// Minimal append-only file logger. A tray app has no console, and for a background
/// process the log is the only way to find out why a keystroke did nothing.
/// Writes are queued and flushed on a single background thread so logging never
/// blocks the hook callback or the audio thread.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly LogLevel _minimum;
    private readonly string _path;
    private readonly Thread _writer;
    private bool _disposed;

    public FileLoggerProvider(string filePath, LogLevel minimum)
    {
        _minimum = minimum;
        _path = ResolveLogPath(filePath);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        RollIfLarge(_path);

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "log-writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Enqueue(string line)
    {
        if (_disposed) return;
        _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        var buffer = new StringBuilder();

        foreach (var line in _queue.GetConsumingEnumerable())
        {
            buffer.Clear().AppendLine(line);

            // Drain whatever else is waiting so bursts become one write.
            while (buffer.Length < 32_768 && _queue.TryTake(out var more))
                buffer.AppendLine(more);

            try
            {
                File.AppendAllText(_path, buffer.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    private static void RollIfLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 4 * 1024 * 1024)
            {
                var archived = Path.ChangeExtension(path, $".{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.Move(path, archived, overwrite: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Relative paths resolve under %LOCALAPPDATA%\PushToTalkDictation.</summary>
    public static string ResolveLogPath(string filePath)
    {
        if (Path.IsPathRooted(filePath)) return filePath;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PushToTalkDictation");

        return Path.Combine(root, filePath);
    }

    public static string ResolveLogDirectory(string filePath) =>
        Path.GetDirectoryName(ResolveLogPath(filePath))!;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _shortCategory = category.Split('.').Last();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Abbrev(logLevel)}] {_shortCategory}: {message}";

            if (exception is not null)
                line += Environment.NewLine + exception;

            provider.Enqueue(line);
        }

        private static string Abbrev(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
