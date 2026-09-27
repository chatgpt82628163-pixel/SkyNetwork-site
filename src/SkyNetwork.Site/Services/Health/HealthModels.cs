namespace SkyNetwork.Site.Services.Health;

/// <summary>How a part of the infrastructure is doing; the order is the severity (the panel shows the worst).</summary>
public enum HealthLevel
{
    Ok = 0,
    /// <summary>Could not be checked here (not on the server, no permission, not set up).</summary>
    Unknown = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>A text on the status panel: an English key (translated on the page with L.F) and its values.</summary>
public sealed record Say(string Key, params object?[] Args);

/// <summary>
/// One check of the status panel: what it is, how it is doing, a short value ("12 ms", "3 pilots"), and when it is not
/// fine, what is wrong in plain words and what to do about it.
/// </summary>
public sealed record HealthCheck(string Id, string Group, Say Title, HealthLevel Level, Say Value, Say? Problem = null, Say? Fix = null)
{
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    /// <summary>A command to run on the server, shown so it can be copied.</summary>
    public string? Command { get; init; }
}

/// <summary>A warning or error from the site's own log or from the journal of a service on the server.</summary>
/// <param name="Source">"site", or the service ("skynet-fsd", "skynet-voice", …).</param>
/// <param name="Template">The message without its values, so repeats of one problem are counted together.</param>
public sealed record LogEntry(DateTime Time, bool IsError, string Source, string Message, string? Details, string Template);

/// <summary>Repeats of one message from one source, newest first.</summary>
public sealed record LogGroup(string Source, bool IsError, string Message, string? Details, int Count, DateTime First, DateTime Last);
