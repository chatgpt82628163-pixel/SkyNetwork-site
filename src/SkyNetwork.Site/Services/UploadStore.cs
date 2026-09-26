using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Banner images for events and news and members' profile pictures, kept on disk (by default "uploads" next to the database) under
/// random names and served from /uploads/…. Only JPEG, PNG and WebP are accepted, recognised by
/// their content rather than the file name, up to 5 MB.
/// </summary>
public sealed partial class UploadStore(IOptions<SiteOptions> options, IWebHostEnvironment env)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    /// <summary>Profile pictures are shown small: 2 MB is plenty.</summary>
    public const long AvatarMaxBytes = 2 * 1024 * 1024;

    [GeneratedRegex("^[a-f0-9]{32}\\.(jpg|png|webp)$")]
    private static partial Regex FileName();

    public string Directory
    {
        get
        {
            string dir = options.Value.Uploads;
            if (dir.Length == 0)
                dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.Database, env.ContentRootPath))!, "uploads");
            return Path.GetFullPath(dir, env.ContentRootPath);
        }
    }

    /// <summary>Stores an image; returns its file name, or an English error.</summary>
    public async Task<(string? Name, string? Error)> SaveImageAsync(IFormFile file, CancellationToken ct = default, long maxBytes = MaxBytes)
    {
        if (file.Length == 0) return (null, "The file is empty");
        if (file.Length > maxBytes) return (null, maxBytes == AvatarMaxBytes ? "The image is larger than 2 MB" : "The image is larger than 5 MB");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        string? ext = Sniff(bytes);
        if (ext == null) return (null, "Only JPEG, PNG and WebP images can be uploaded");
        System.IO.Directory.CreateDirectory(Directory);
        string name = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + "." + ext;
        await File.WriteAllBytesAsync(Path.Combine(Directory, name), bytes, ct);
        return (name, null);
    }

    /// <summary>
    /// A picture after an edit: a new upload replaces <paramref name="current"/>, <paramref name="remove"/> clears it. The replaced
    /// file stays on disk until the caller has saved and deletes it; on an error <paramref name="current"/> is kept.
    /// </summary>
    public async Task<(string Name, string? Error)> ReplaceAsync(IFormFile? upload, bool remove, string current, CancellationToken ct = default)
    {
        if (upload is not { Length: > 0 }) return (remove ? "" : current, null);
        var (name, error) = await SaveImageAsync(upload, ct);
        return error == null ? (name!, null) : (current, error);
    }

    public void Delete(string? name)
    {
        if (name == null || !FileName().IsMatch(name)) return;
        try { File.Delete(Path.Combine(Directory, name)); } catch (IOException) { }
    }

    /// <summary>The stored file for a request path name, or null (unknown names never reach the disk).</summary>
    public string? PathOf(string name) =>
        FileName().IsMatch(name) && File.Exists(Path.Combine(Directory, name)) ? Path.Combine(Directory, name) : null;

    public static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    internal static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "webp";
        return null;
    }
}
