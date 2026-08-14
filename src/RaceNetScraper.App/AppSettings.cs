using System.IO;
using System.Text.Json;

namespace RaceNetScraper.App;

/// <summary>Small persisted user-preference blob, stored outside the install folder in its own
/// "RaceNetScraper" folder. NOT preserved across installs/updates on purpose — the installer
/// (see installer/RaceNetScraper.iss's WriteDefaultSettings) overwrites this file with baked-in
/// defaults on every version, to stop per-machine config drift from silently surviving updates.</summary>
public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = "";
    public bool AutoExportAfterScrape { get; set; }

    public bool UploadToS3 { get; set; }
    public string S3Endpoint { get; set; } = "https://s3.troyendata.com";

    // Deliberately no default access/secret key or bucket name here (source is public) — set
    // these via the app's own UI on first run, or by hand-editing settings.json at the path
    // below; either way they're saved locally and never checked into source control.
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string S3BucketName { get; set; } = "";
    public string S3Folder { get; set; } = "pending";

    /// <summary>Id of the last developer notice (see DeveloperNoticeChecker) the user explicitly
    /// dismissed. A notice with a different Id is treated as new and shown again, even if an
    /// earlier one was already read.</summary>
    public string LastSeenNoticeId { get; set; } = "";

    // --- Auto Scrape (see MainViewModel's DispatcherTimer) — only fires while this app is open.
    // Off by default (opt-in) — if this app is installed on more than one PC, having it on by
    // default everywhere means every PC scrapes/uploads at the same scheduled times, causing
    // duplicate work. Turn it on deliberately on only one machine via the "Enabled" checkbox. ---
    public bool AutoScrapeEnabled { get; set; }

    /// <summary>Comma-separated 24h "HH:mm" times, e.g. "06:00,18:00" — fires once per listed time
    /// each day, so multiple daily runs are just multiple entries here.</summary>
    public string AutoScrapeTimesOfDay { get; set; } = "06:00,18:00";
    public bool AutoScrapeIncludeToday { get; set; } = true;
    public bool AutoScrapeIncludeTomorrow { get; set; } = true;
    public bool AutoScrapeIncludeDayAfterTomorrow { get; set; } = true;
    public bool AutoScrapeHorses { get; set; } = true;
    public bool AutoScrapeGreyhounds { get; set; } = true;
    public bool AutoScrapeHarness { get; set; } = true;

    public DateTime? AutoScrapeLastRunUtc { get; set; }
    public string AutoScrapeLastRunSummary { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RaceNetScraper", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash the app.
            return new AppSettings();
        }
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
