using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace mTiles.Services.Phone;

/// <summary>
/// The link library's log, into this application's own through <see cref="Trace"/>.
/// </summary>
/// <remarks>
/// Warnings and above only. The library narrates every reconnection, relay probe and heartbeat at the
/// lower levels, and a laptop that sleeps with a phone paired would fill the day's log with them —
/// burying the one line that says why a phone could not reach the machine.
/// </remarks>
internal sealed class TraceLoggerFactory(LogLevel minimum = LogLevel.Warning) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new TraceLogger(categoryName, minimum);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class TraceLogger(string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"[phone link] {category}: {formatter(state, exception)}";
            if (exception is not null) line += $" — {exception.GetType().Name}: {exception.Message}";

            if (logLevel >= LogLevel.Error) Trace.TraceError(line);
            else Trace.TraceWarning(line);
        }
    }
}
