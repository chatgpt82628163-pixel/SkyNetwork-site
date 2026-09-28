using System.Globalization;

namespace SkyNetwork.Site;

/// <summary>
/// Formatting shared by pages: everything on the network runs in UTC. Month names and units follow
/// the visitor's language (the request's UI culture, set from the language cookie).
/// </summary>
public static class Format
{
    private static CultureInfo Culture => CultureInfo.CurrentUICulture;
    private static bool Ru => Culture.TwoLetterISOLanguageName == "ru";

    public static string Utc(DateTime t) => t.ToString(Ru ? "dd.MM.yyyy HH:mm" : "dd MMM yyyy HH:mm", Culture) + "z";
    public static string Date(DateTime t) => t.ToString("d MMMM yyyy", Culture);
    public static string DayMonth(DateTime t) => t.ToString("d MMM", Culture).TrimEnd('.');
    public static string Time(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture) + "z";
    public static string Span(DateTime a, DateTime b) =>
        a.Date == b.Date ? $"{DayMonth(a)}, {Time(a)}–{Time(b)}" : $"{DayMonth(a)} {Time(a)} — {DayMonth(b)} {Time(b)}";

    public static string Hours(double hours) =>
        hours.ToString(hours < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + (Ru ? " ч" : " h");

    public static string Duration(TimeSpan d) => Ru
        ? (d.TotalHours >= 1 ? $"{(int)d.TotalHours} ч {d.Minutes:00} мин" : $"{d.Minutes} мин")
        : (d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : $"{d.Minutes} min");

    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public static long? ParseUtc(string? date, string? time)
    {
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time)) return null;
        return DateTime.TryParseExact($"{date} {time}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds() : null;
    }

    public static string InputDate(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string InputTime(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Bytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return bytes + " B";
    }
}
