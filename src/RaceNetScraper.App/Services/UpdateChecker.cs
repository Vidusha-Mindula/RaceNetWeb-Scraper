using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RaceNetScraper.App.Services;

/// <summary>A newer build found on GitHub Releases.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, string ReleaseUrl);

/// <summary>One installable release — same shape as <see cref="UpdateInfo"/>, but returned for
/// every release <see cref="UpdateChecker.ListReleasesAsync"/> finds (older and newer alike),
/// rather than only a release newer than what's currently running.</summary>
public sealed record ReleaseInfo(Version Version, string DownloadUrl, string ReleaseUrl)
{
    public string DisplayText => $"v{Version.ToString(3)}";
}

/// <summary>
/// Checks GitHub Releases for a build newer than the one currently running, using the public
/// "latest release" API — no custom manifest to host, GitHub's own release metadata (tag_name +
/// assets[].browser_download_url) is the manifest. Matches the installer's own naming
/// (RaceNetScraperSetup-{version}.exe, from installer/RaceNetScraper.iss) to find the right asset.
///
/// Only works once RepoOwner/RepoName below point at a real GitHub repo with at least one
/// release whose asset is the Inno Setup installer built by installer/build-racenet-installer.ps1
/// (tag the release e.g. "v2.6.0" and attach the matching RaceNetScraperSetup-2.6.0.exe).
/// A private repo needs an Authorization: Bearer &lt;token&gt; header added to the request below —
/// the public endpoint used here only works for public repos.
/// </summary>
public static class UpdateChecker
{
    private const string RepoOwner = "Vidusha-Mindula";
    private const string RepoName = "RaceNetWeb-Scraper";

    private const string AssetNamePrefix = "RaceNetScraperSetup-";
    private const string AssetNameSuffix = ".exe";

    private static Version? CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>The running build's version, formatted the same way releases are tagged
    /// (e.g. "1.1.8") — the single place MainWindow's title bar reads this from, so it can never
    /// drift from what <see cref="CheckAsync"/> itself compares against.</summary>
    public static string CurrentVersionText => CurrentVersion?.ToString(3) ?? "dev";

    public static bool IsConfigured => RepoOwner != "REPLACE_ME" && RepoName != "REPLACE_ME";

    /// <summary>Returns the newer release's info, or null if this is already the latest version,
    /// the repo isn't configured yet, or the check failed for any reason (offline, rate-limited,
    /// no releases yet) — a failed background check should never block startup or bother the user.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaceNetScraper.App", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (tag is null) return null;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latestVersion)) return null;

            var current = CurrentVersion;
            if (current is not null && latestVersion <= current) return null;

            var downloadUrl = FindInstallerAssetUrl(root);
            if (downloadUrl is null) return null;

            var releaseUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? downloadUrl : downloadUrl;
            return new UpdateInfo(latestVersion, downloadUrl, releaseUrl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every installable release found on GitHub — older and newer than what's currently
    /// running alike — so the "Switch version" picker can offer a deliberate downgrade, not just
    /// the newest build <see cref="CheckAsync"/> would ever surface on its own. Uses the releases
    /// LIST endpoint (up to 30 most recent) rather than "/releases/latest" for that reason. Returns
    /// an empty list rather than throwing on any failure (offline, rate-limited, repo not
    /// configured) — same "never bother the user with a plumbing error" rule as
    /// <see cref="CheckAsync"/>.</summary>
    public static async Task<List<ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return new List<ReleaseInfo>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaceNetScraper.App", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return new List<ReleaseInfo>();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var results = new List<ReleaseInfo>();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                if (tag is null) continue;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;

                var downloadUrl = FindInstallerAssetUrl(release);
                if (downloadUrl is null) continue;

                var releaseUrl = release.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? downloadUrl : downloadUrl;
                results.Add(new ReleaseInfo(version, downloadUrl, releaseUrl));
            }

            return results.OrderByDescending(r => r.Version).ToList();
        }
        catch
        {
            return new List<ReleaseInfo>();
        }
    }

    /// <summary>Shared by <see cref="CheckAsync"/> and <see cref="ListReleasesAsync"/>: finds the
    /// Inno Setup installer asset (matching AssetNamePrefix/AssetNameSuffix) on one release JSON
    /// element, or null if that release doesn't have one attached.</summary>
    private static string? FindInstallerAssetUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (name is null) continue;
            if (!name.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(AssetNameSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            return asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
        }

        return null;
    }

    /// <summary>Downloads the installer .exe to a temp file, reporting progress as it goes so the
    /// caller can show a real percentage rather than an indefinite spinner. Caller is expected to
    /// launch it (<see cref="LaunchInstaller"/>) and then shut the running app down so the
    /// installer can overwrite its files.</summary>
    /// <param name="progress">
    /// Reports 0-100 as bytes arrive. If the server doesn't send a Content-Length (rare for a
    /// GitHub release asset, but not guaranteed), this reports -1 once and never again — the
    /// caller should treat that as "show an indeterminate bar instead", since there's no total to
    /// compute a percentage against.
    /// </param>
    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaceNetScraper.App", "1.0"));

        var fileName = downloadUrl.Split('/').Last();
        var path = Path.Combine(Path.GetTempPath(), fileName);

        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes is null or <= 0)
        {
            progress?.Report(-1);
        }

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(path);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes is > 0)
            {
                progress?.Report(Math.Min(100.0, totalRead * 100.0 / totalBytes.Value));
            }
        }

        return path;
    }

    /// <summary>Launches the downloaded installer fully unattended — no wizard pages, no "Next"/
    /// "Install"/"Finish" clicks required. /SILENT shows a single progress window with no
    /// interaction (so it's still visible that something's happening) rather than /VERYSILENT's
    /// zero UI. /CLOSEAPPLICATIONS tells Inno Setup to close whatever process has this app's files
    /// locked automatically via Windows' Restart Manager, as a safety net in case the caller's own
    /// Application.Current.Shutdown() (called right after this) hasn't fully finished tearing down
    /// yet. The installer's own [Run] section relaunches the app once it's done (see
    /// RaceNetScraper.iss — that entry deliberately has no "skipifsilent", so it still fires here).
    /// PrivilegesRequired=lowest in the .iss means no UAC prompt either way.</summary>
    public static void LaunchInstaller(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS",
            UseShellExecute = true
        });
    }
}
