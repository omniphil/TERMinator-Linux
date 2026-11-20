using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TERMinator.Linux.Services;

/// <summary>One platform's entry in latest.json.</summary>
public sealed record UpdateInfo(Version Version, string Url, long Size, string Sha256, IReadOnlyList<string> Notes);

/// <summary>
/// Help → Check for Updates. Manual only, nothing runs at startup. Fetches latest.json and its
/// signature from the website, checks the signature against the key built in here, then downloads
/// the AppImage and checks its size and SHA-256 against the signed manifest before using it.
/// The manifest is made and signed by TERMinator-Windows/tools/release_manifest.py. Same as the
/// Windows client's Services/UpdateService.cs, reading the "linux" entry.
/// </summary>
public static class UpdateService
{
    private const string ManifestUrl = "https://deadmodemsociety.com/terminator/downloads/latest.json";
    public const string WebsiteUrl = "https://deadmodemsociety.com/terminator/";

    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo). The private key is kept offline.</summary>
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsgTI8QB4DFHN4YNvsncGKoKGWZh/RqhnnpUYsKwcCLTuzNfdiTJhJhMnHq3FQuwexqLgNu57XyfbXiLrN9fq/Q==";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-call timeouts below
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TERMinator-Linux/" + Views.MainWindow.AppVersion);
        return http;
    }

    public static Version CurrentVersion => Version.Parse(Views.MainWindow.AppVersion);

    /// <summary>The running AppImage's path, or null for a development build (dotnet TERMinator.dll).</summary>
    public static string? AppImagePath
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("APPIMAGE");
            return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
        }
    }

    /// <summary>The Linux entry of the signed manifest. Throws if it can't be fetched or the signature is wrong.</summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        // The query string keeps a proxy or CDN from handing back an old copy.
        string bust = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = await Http.GetByteArrayAsync(ManifestUrl + bust, timeout.Token);
        var sigText = await Http.GetStringAsync(ManifestUrl + ".sig" + bust, timeout.Token);

        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            byte[] sig;
            try { sig = Convert.FromBase64String(sigText.Trim()); }
            catch (FormatException) { throw new InvalidDataException("The update information is damaged (bad signature)."); }
            if (!ecdsa.VerifyData(body, sig, HashAlgorithmName.SHA256))
                throw new InvalidDataException("The update information failed its signature check.");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("linux", out var l))
            throw new InvalidDataException("No Linux version is listed yet.");
        var url = l.GetProperty("url").GetString() ?? "";
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update's download address isn't https.");
        var notes = l.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        return new UpdateInfo(Version.Parse(l.GetProperty("version").GetString() ?? "0.0"), url,
            l.GetProperty("size").GetInt64(), l.GetProperty("sha256").GetString() ?? "", notes);
    }

    /// <summary>
    /// Where the download goes: a hidden file next to the running AppImage (so it can be renamed over it), or
    /// ~/Downloads when that folder can't be written. <paramref name="replaces"/> is true for the first.
    /// </summary>
    public static string DownloadPath(UpdateInfo info, out bool replaces)
    {
        var appImage = AppImagePath;
        var dir = appImage != null ? Path.GetDirectoryName(appImage) : null;
        if (dir != null && CanWrite(dir))
        {
            replaces = true;
            return Path.Combine(dir, ".TERMinator-update.AppImage");
        }
        replaces = false;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        return Path.Combine(downloads, Path.GetFileName(new Uri(info.Url).LocalPath));
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".TERMinator-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads to <paramref name="path"/> and makes it executable once size and hash match; a failed or
    /// cancelled download is deleted.</summary>
    public static async Task DownloadAsync(UpdateInfo info, string path, IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    if (done + read > info.Size)
                        throw new InvalidDataException("The download is bigger than expected.");
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    sha.AppendData(buf, 0, read);
                    done += read;
                    progress.Report((double)done / Math.Max(1, info.Size));
                }
                if (done != info.Size)
                    throw new InvalidDataException("The download was cut short.");
                if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download doesn't match the signed checksum.");
            }
            if (!OperatingSystem.IsWindows())   // always true here; keeps the platform analyzer quiet
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                           UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            try { File.Delete(path); } catch { /* nothing to clean up */ }
            throw;
        }
    }

    /// <summary>
    /// Renames the downloaded AppImage over the running one (the running copy stays mounted, so this is safe) and
    /// starts the new one. Returns the error, or null when the new TERMinator is starting.
    /// </summary>
    public static string? ReplaceAndRelaunch(string downloaded)
    {
        var appImage = AppImagePath;
        if (appImage == null)
            return "TERMinator isn't running as an AppImage.";
        try
        {
            File.Move(downloaded, appImage, overwrite: true);
            // A fresh process, not a child that dies with this one: setsid detaches it from our session.
            var start = new ProcessStartInfo("setsid") { UseShellExecute = false };
            start.ArgumentList.Add(appImage);
            // Drop what this AppImage's AppRun added (its mount goes away when we exit); the new one adds its own
            var appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrEmpty(appDir))
            {
                var kept = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "")
                    .Split(':', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(appDir, StringComparison.Ordinal));
                start.Environment["LD_LIBRARY_PATH"] = string.Join(':', kept);
                if ((Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH") ?? "").StartsWith(appDir, StringComparison.Ordinal))
                    start.Environment.Remove("VLC_PLUGIN_PATH");
            }
            start.Environment.Remove("APPIMAGE");
            start.Environment.Remove("APPDIR");
            start.Environment.Remove("ARGV0");
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
