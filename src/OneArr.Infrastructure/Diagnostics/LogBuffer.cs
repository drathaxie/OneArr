using Microsoft.Extensions.Logging;
using OneArr.Infrastructure.Http;

namespace OneArr.Infrastructure.Diagnostics;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Category, string Message);

/// <summary>
/// The most recent log messages, in memory, for the in-app log viewer and the diagnostics download. Bounded, so it never
/// grows; secrets in URLs are redacted before a message is kept.
/// </summary>
public sealed class LogBuffer
{
    public const int Capacity = 2000;
    private readonly LogEntry[] _entries = new LogEntry[Capacity];
    private readonly object _gate = new();
    private long _count;

    public void Add(LogEntry entry)
    {
        lock (_gate) _entries[_count++ % Capacity] = entry;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LogEntry> Recent(int limit, LogLevel minLevel)
    {
        lock (_gate)
        {
            var list = new List<LogEntry>();
            for (var i = _count - 1; i >= 0 && i >= _count - Capacity && list.Count < limit; i--)
            {
                var e = _entries[i % Capacity];
                if (Enum.TryParse<LogLevel>(e.Level, out var level) && level >= minLevel) list.Add(e);
            }
            return list;
        }
    }
}

public sealed class BufferLoggerProvider : ILoggerProvider
{
    private readonly LogBuffer _buffer;
    public BufferLoggerProvider(LogBuffer buffer) => _buffer = buffer;
    public ILogger CreateLogger(string categoryName) => new BufferLogger(_buffer, categoryName);
    public void Dispose() { }

    private sealed class BufferLogger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += $" ({exception.GetType().Name}: {exception.Message})";
            if (message.Length > 2000) message = message[..2000] + "…";
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, logLevel.ToString(), category, OutboundHttp.Redact(message)));
        }
    }
}
