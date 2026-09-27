namespace SkyNetwork.Site.Services.Health;

/// <summary>
/// The site's own warnings and errors of the last day, kept in memory for the status panel (the full log stays in the
/// system journal). Registered as a logging provider, so every part of the site reports here without changes.
/// </summary>
public sealed class RecentLog : ILoggerProvider
{
    private const int Capacity = 1000;
    private static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    private readonly Queue<LogEntry> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    public void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity || _entries.Count > 0 && entry.Time - _entries.Peek().Time > Keep) _entries.Dequeue();
        }
    }

    /// <summary>Entries newer than <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<LogEntry> Since(DateTime since)
    {
        lock (_entries) return _entries.Where(e => e.Time >= since).ToList();
    }

    /// <summary>
    /// Noise that is not a problem: ASP.NET says it overrides caching headers on every page with a form, and at every
    /// start that the sign-in keys are stored unencrypted on the server's disk, both as warnings.
    /// </summary>
    private static bool Ignored(string category, EventId id, LogLevel level) =>
        category == "Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery" && id.Id == 8 ||
        category.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal) && level == LogLevel.Warning;

    private sealed class Logger(RecentLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || Ignored(category, eventId, logLevel)) return;
            string message = formatter(state, exception);
            if (message.Length == 0 && exception != null) message = exception.Message;
            // The message as written in the code ("METAR {Icao}: {Error}"), so repeats with other values count as one.
            string template = state is IReadOnlyList<KeyValuePair<string, object?>> values &&
                              values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is string format ? format : message;
            string source = category.StartsWith("SkyNetwork.", StringComparison.Ordinal) ? category[(category.LastIndexOf('.') + 1)..] : category;
            string? details = exception == null ? null : $"{exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}".Trim();
            log.Add(new LogEntry(DateTime.UtcNow, logLevel >= LogLevel.Error, "site", $"{source}: {message}", details, $"{category}|{template}"));
        }
    }
}
