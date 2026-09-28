using Dapper;
using Microsoft.Extensions.Options;

namespace SkyNetwork.Site.Data;

public sealed class Release
{
    public long Id { get; set; }
    /// <summary>"skypilot" or "network-atc".</summary>
    public string Product { get; set; } = "";
    /// <summary>Semantic version, e.g. "0.3.0".</summary>
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
    public string NotesEn { get; set; } = "";
    public bool Published { get; set; }
    public long Downloads { get; set; }
    public long UploadedBy { get; set; }
    public long CreatedAt { get; set; }
    public DateTime Created => Time.Utc(CreatedAt);

    public System.Version ParsedVersion => System.Version.TryParse(Version, out var v) ? v : new System.Version(0, 0, 0);
}

/// <summary>Program installers stored on disk and tracked in the releases table.</summary>
public sealed class ReleaseService(Database db, IOptions<SiteOptions> options, IWebHostEnvironment env)
{
    public string Directory
    {
        get
        {
            string dir = options.Value.Releases;
            if (dir.Length == 0)
                dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.Database, env.ContentRootPath))!, "releases");
            return Path.GetFullPath(dir, env.ContentRootPath);
        }
    }

    public string FilePath(string fileName) => Path.Combine(Directory, fileName);

    public IReadOnlyList<Release> ForProduct(string product)
    {
        using var c = db.Open();
        return c.Query<Release>("SELECT * FROM releases WHERE product = @product ORDER BY created_at DESC", new { product }).ToList();
    }

    public IReadOnlyList<Release> AllProducts()
    {
        using var c = db.Open();
        return c.Query<Release>("SELECT * FROM releases ORDER BY product, created_at DESC").ToList();
    }

    /// <summary>The newest published release for a product by semantic version.</summary>
    public Release? LatestPublished(string product)
    {
        using var c = db.Open();
        var all = c.Query<Release>("SELECT * FROM releases WHERE product = @product AND published = 1", new { product }).ToList();
        return all.Count == 0 ? null : all.MaxBy(r => r.ParsedVersion);
    }

    public Release? Get(long id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Release>("SELECT * FROM releases WHERE id = @id", new { id });
    }

    public Release? GetByVersion(string product, string version)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Release>("SELECT * FROM releases WHERE product = @product AND version = @version", new { product, version });
    }

    /// <summary>Inserts a release record. Returns error text if the product+version already exists.</summary>
    public string? Insert(Release r)
    {
        using var c = db.Open();
        try
        {
            c.Execute("""
                INSERT INTO releases (product, version, file_name, size, sha256, notes, notes_en, published, uploaded_by, created_at)
                VALUES (@Product, @Version, @FileName, @Size, @Sha256, @Notes, @NotesEn, 0, @UploadedBy, @CreatedAt)
                """, r);
            return null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            return "Version already exists";
        }
    }

    public void SetPublished(long id, bool published)
    {
        using var c = db.Open();
        c.Execute("UPDATE releases SET published = @published WHERE id = @id", new { id, published = published ? 1 : 0 });
    }

    public void IncrementDownloads(long id)
    {
        using var c = db.Open();
        c.Execute("UPDATE releases SET downloads = downloads + 1 WHERE id = @id", new { id });
    }

    public void Delete(long id)
    {
        using var c = db.Open();
        var r = c.QuerySingleOrDefault<Release>("SELECT * FROM releases WHERE id = @id", new { id });
        if (r == null) return;
        c.Execute("DELETE FROM releases WHERE id = @id", new { id });
        try { File.Delete(FilePath(r.FileName)); } catch (IOException) { }
    }

    /// <summary>Clean display name for the installer download.</summary>
    public static string DisplayName(string product, string version) => product switch
    {
        "skypilot" => $"SkyPilot-Setup-{version}.exe",
        "network-atc" => $"Network-ATC-Setup-{version}.exe",
        _ => $"{product}-{version}.exe",
    };

    public static readonly string[] ValidProducts = ["skypilot", "network-atc"];
}
