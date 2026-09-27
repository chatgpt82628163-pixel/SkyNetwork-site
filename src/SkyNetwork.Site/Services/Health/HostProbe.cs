using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SkyNetwork.Site.Services.Health;

/// <summary>
/// What the site can see of the server it runs on (Linux): load, memory, uptime and open ports from /proc, the state
/// of systemd services and their journals. Parsing is kept apart from reading, so it can be tested anywhere.
/// </summary>
public static class HostProbe
{
    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>/proc/loadavg: "0.08 0.02 0.04 1/390 86796" → the 1, 5 and 15 minute load.</summary>
    public static (double One, double Five, double Fifteen)? LoadAverage(string? text)
    {
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 3 }) return null;
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) &&
               double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var c)
            ? (a, b, c) : null;
    }

    /// <summary>/proc/meminfo → total and available memory in kB.</summary>
    public static (long TotalKb, long AvailableKb)? Memory(string? text)
    {
        if (text == null) return null;
        long? total = null, available = null;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)) continue;
            if (parts[0] == "MemTotal:") total = kb;
            else if (parts[0] == "MemAvailable:") available = kb;
        }
        return total is > 0 && available != null ? (total.Value, available.Value) : null;
    }

    /// <summary>/proc/uptime: "60432.13 240112.20" → time since the server started.</summary>
    public static TimeSpan? Uptime(string? text)
    {
        var first = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;
    }

    /// <summary>
    /// Whether a port is open in a /proc/net/tcp, tcp6, udp or udp6 table: for TCP listening (state 0A), for UDP bound.
    /// Nothing connects to the port, so the service does not see a visitor every half minute.
    /// </summary>
    public static bool HasPort(string? table, int port, bool tcp)
    {
        if (table == null) return false;
        string hexPort = port.ToString("X4", CultureInfo.InvariantCulture);
        foreach (var line in table.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4) continue;
            int colon = f[1].LastIndexOf(':');
            if (colon < 0 || !f[1][(colon + 1)..].Equals(hexPort, StringComparison.OrdinalIgnoreCase)) continue;
            if (!tcp || f[3] == "0A") return true;
        }
        return false;
    }

    /// <summary>A port open on this server (TCP listening or UDP bound), or null when that cannot be seen here.</summary>
    public static bool? PortOpen(int port, bool tcp)
    {
        if (!OperatingSystem.IsLinux()) return null;
        string kind = tcp ? "tcp" : "udp";
        string? v4 = Read($"/proc/net/{kind}"), v6 = Read($"/proc/net/{kind}6");
        if (v4 == null && v6 == null) return null;
        return HasPort(v4, port, tcp) || HasPort(v6, port, tcp);
    }

    /// <summary>Runs a program and returns what it printed (both streams), or null when it cannot run here.</summary>
    public static async Task<(int Exit, string Output)?> RunAsync(string program, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var psi = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                // Too slow, or the site is stopping: the program must not stay behind either way.
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>"active", "inactive", "failed", "activating"… from systemctl, or null when that cannot be asked here.</summary>
    public static async Task<string?> ServiceState(string unit, CancellationToken ct)
    {
        var r = await RunAsync("systemctl", ["is-active", unit], TimeSpan.FromSeconds(5), ct);
        if (r is not { } run) return null;
        var state = run.Output.Trim().Split('\n')[0].Trim();
        return state.Length == 0 ? null : state;
    }

    /// <summary>Whether the journal says it may not be read by this user.</summary>
    public static bool JournalDenied(string output) =>
        output.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("No journal files were opened", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Warnings and errors of services from "journalctl -o json" (one JSON object per line): time, service, text.
    /// Priority 0–3 is an error, 4 a warning.
    /// </summary>
    public static List<LogEntry> ParseJournal(string output)
    {
        var entries = new List<LogEntry>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (Text(r, "MESSAGE") is not { Length: > 0 } message) continue;
                // systemd's own notes about a service ("caddy.service: Failed with result 'timeout'.") name it in UNIT.
                string unit = Text(r, "UNIT") ?? Text(r, "_SYSTEMD_UNIT") ?? Text(r, "SYSLOG_IDENTIFIER") ?? "system";
                if (message.StartsWith(unit + ": ", StringComparison.Ordinal)) message = message[(unit.Length + 2)..];
                if (unit.EndsWith(".service", StringComparison.Ordinal)) unit = unit[..^8];
                int priority = int.TryParse(Text(r, "PRIORITY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 4;
                var time = long.TryParse(Text(r, "__REALTIME_TIMESTAMP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)
                    ? DateTime.UnixEpoch.AddTicks(us * 10) : DateTime.UtcNow;
                var firstLine = message.Split('\n')[0].Trim();
                entries.Add(new LogEntry(time, priority <= 3, unit, firstLine, message.Contains('\n') ? message : null, TemplateOf(firstLine)));
            }
            catch (JsonException)
            {
                // A line cut off in the middle: skipped.
            }
        }
        return entries;
    }

    private static string? Text(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Numbers and addresses out, so "client 10.0.0.5 dropped after 31 s" repeats count as one problem.</summary>
    public static string TemplateOf(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"\d+([.:]\d+)*", "#");
}
