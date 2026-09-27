using System.Text.RegularExpressions;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Localization;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Tests;

/// <summary>Every English text the site shows has a Russian translation.</summary>
public class LocalizationTests
{
    private static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SkyNetwork.Site.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SkyNetwork.Site");
    }

    private static readonly Regex[] Patterns =
    [
        new(@"L\[""((?:[^""\\]|\\.)+)""\]"),
        new(@"L\.F\(""((?:[^""\\]|\\.)+)"""),
        new(@"this\.T\(""((?:[^""\\]|\\.)+)"""),
        // Messages set in page models and English errors returned by services.
        new(@"\b(?:Error|Message)\s*\??\??=\s*""((?:[^""\\]|\\.)+)"""),
        new(@"\b(?:Error|Message)\s*=\s*\w+\s*\?\s*""((?:[^""\\]|\\.)+)""\s*:\s*""(?:[^""\\]|\\.)+"""),
        new(@"\b(?:Error|Message)\s*=\s*\w+\s*\?\s*""(?:[^""\\]|\\.)+""\s*:\s*""((?:[^""\\]|\\.)+)"""),
        new(@"return ""([A-Z{][^""]* [^""]*)"";"),
        // Texts of the status page, translated when the page shows them.
        new(@"new Say\(""((?:[^""\\]|\\.)+)"""),
    ];

    private static IEnumerable<string> UsedKeys()
    {
        string src = SourceDir();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cshtml") || f.EndsWith(".cs")) && !f.Contains("/bin/") && !f.Contains("/obj/")
                                 && !f.EndsWith("Ru.cs")))
        {
            string text = File.ReadAllText(file);
            foreach (var re in Patterns)
                foreach (Match m in re.Matches(text))
                    yield return Regex.Unescape(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"L\.Plural\([^,]+,\s*""([^""]+)"",\s*""([^""]+)""\)"))
                yield return m.Groups[1].Value + "|" + m.Groups[2].Value;
        }
        foreach (var r in Ratings.Controller.Append(Ratings.SUP).Append(Ratings.ADM)) yield return Ratings.Long(r);
        foreach (var ladder in new[] { PilotRatings.Pilot, PilotRatings.Military })
            foreach (var (_, name, privileges) in ladder.Levels) { yield return name; yield return privileges; }
        foreach (var (_, title) in TrainingTracks.All) yield return title;
        foreach (var title in Permissions.Roles.Values) yield return title;
        foreach (var text in new[] { SignupGuard.RealName, SignupGuard.BadName, SignupGuard.BadEmail, SignupGuard.TemporaryEmail, SignupGuard.NotAPerson })
            yield return text;
        foreach (var title in SupportService.TicketStatuses.Values) yield return title;
        foreach (var title in DivisionService.RequestStatuses.Values) yield return title;
        foreach (var title in ConnectService.Scopes.Values) yield return title;
        foreach (var action in new[] { "rating", "staff-rank", "pilot-rating", "military-rating", "suspend", "unsuspend", "password-reset", "roles", "note",
                     "event", "event-delete", "news", "news-delete", "booking-delete", "ticket",
                     "division-key", "rating-request", "connect-client" })
            yield return AuditService.Title(action);
    }

    [Fact]
    public void EveryTextHasARussianTranslation()
    {
        var used = UsedKeys().Distinct().ToList();
        Assert.True(used.Count > 400, $"only {used.Count} texts found: is the source scan broken?");
        var missing = used.Where(k => !Ru.Texts.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Missing Russian translations:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TranslationsKeepFormatPlaceholders()
    {
        foreach (var (en, ru) in Ru.Texts)
        {
            var a = Regex.Matches(en, @"\{\d\}").Select(m => m.Value).Order();
            var b = Regex.Matches(ru, @"\{\d\}").Select(m => m.Value).Order();
            Assert.True(a.SequenceEqual(b), $"Placeholders differ: {en}");
        }
    }

    [Fact]
    public void RussianPlurals()
    {
        var l = new Lang();
        l.Set("ru");
        Assert.Equal("пилот", l.Plural(1, "pilot", "pilots"));
        Assert.Equal("пилота", l.Plural(3, "pilot", "pilots"));
        Assert.Equal("пилотов", l.Plural(11, "pilot", "pilots"));
        Assert.Equal("пилот", l.Plural(21, "pilot", "pilots"));
        l.Set("en");
        Assert.Equal("pilots", l.Plural(2, "pilot", "pilots"));
    }
}
