using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Pages.Staff;

[RequestSizeLimit(320 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 310 * 1024 * 1024)]
public sealed class ReleasesModel(CurrentUser me, ReleaseService releases) : StaffPageModel(me)
{
    protected override Perm Required => Perm.Releases;

    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$");
    private const long MaxInstallerBytes = 300L * 1024 * 1024;

    public IReadOnlyDictionary<string, IReadOnlyList<Release>> ByProduct { get; private set; } = new Dictionary<string, IReadOnlyList<Release>>();
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public void OnGet() => Load();

    private void Load()
    {
        var all = releases.AllProducts();
        var dict = new Dictionary<string, IReadOnlyList<Release>>();
        foreach (var p in ReleaseService.ValidProducts)
            dict[p] = all.Where(r => r.Product == p).ToList();
        ByProduct = dict;
    }

    public async Task<IActionResult> OnPostUploadAsync(string product, string version, string notes, string notesEn, IFormFile? installer)
    {
        if (!ReleaseService.ValidProducts.Contains(product))
            return BadRequest("Invalid product", null);
        if (!VersionPattern.IsMatch(version))
            return BadRequest("Version must be in the format 1.2.3", null);
        if (installer == null || installer.Length == 0)
            return BadRequest("No file uploaded", null);
        if (installer.Length > MaxInstallerBytes)
            return BadRequest("The file is larger than 300 MB", null);

        // Check MZ header while streaming; compute SHA-256 at the same time.
        string tempPath = Path.GetTempFileName();
        string sha256;
        long size;
        try
        {
            using var sha = SHA256.Create();
            await using var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            using var hashStream = new CryptoStream(temp, sha, CryptoStreamMode.Write);
            byte[] header = new byte[2];
            int headerBytes = 0;
            await using var stream = installer.OpenReadStream();
            int n;
            byte[] buf = new byte[65536];
            size = 0;
            while ((n = await stream.ReadAsync(buf)) > 0)
            {
                if (headerBytes < 2 && n > 0)
                {
                    int take = Math.Min(2 - headerBytes, n);
                    buf.AsSpan(0, take).CopyTo(header.AsSpan(headerBytes));
                    headerBytes += take;
                }
                await hashStream.WriteAsync(buf.AsMemory(0, n));
                size += n;
            }
            await hashStream.FlushFinalBlockAsync();
            sha256 = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            if (header[0] != 0x4D || header[1] != 0x5A)
            {
                File.Delete(tempPath);
                return BadRequest("The file is not a Windows executable (MZ header missing)", null);
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        string fileName = $"{product}-{version}.exe";
        System.IO.Directory.CreateDirectory(releases.Directory);
        string dest = releases.FilePath(fileName);

        var r = new Release
        {
            Product = product,
            Version = version,
            FileName = fileName,
            Size = size,
            Sha256 = sha256,
            Notes = notes,
            NotesEn = notesEn,
            UploadedBy = Me.Cid,
            CreatedAt = Database.Now(),
        };

        string? error = releases.Insert(r);
        if (error != null)
        {
            File.Delete(tempPath);
            return BadRequest(error, null);
        }

        File.Move(tempPath, dest, overwrite: false);
        Message = "Uploaded";
        Load();
        return Page();
    }

    public IActionResult OnPostPublish(long id)
    {
        releases.SetPublished(id, true);
        Message = "Published";
        Load();
        return Page();
    }

    public IActionResult OnPostUnpublish(long id)
    {
        releases.SetPublished(id, false);
        Message = "Unpublished";
        Load();
        return Page();
    }

    public IActionResult OnPostDelete(long id)
    {
        releases.Delete(id);
        Message = "Deleted";
        Load();
        return Page();
    }

    private IActionResult BadRequest(string error, object? _)
    {
        Error = error;
        Load();
        return Page();
    }
}
