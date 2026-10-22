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

    /// <summary>One entry per scheduled time — each with independent Australia and International
    /// day(s)/discipline(s) selections (either can be disabled), so e.g. a single 13:50 slot can
    /// scrape Australia-only Harness for Today while ALSO scraping International Harness for
    /// Today, in the same run. Fires once per slot whose time matches, each day (see
    /// MainViewModel.AutoScrapeTickAsync).</summary>
    public List<AutoScrapeSlot> AutoScrapeSlots { get; set; } = new()
    {
        new AutoScrapeSlot { Time = "06:00" },
        new AutoScrapeSlot { Time = "18:00" },
    };

    public DateTime? AutoScrapeLastRunUtc { get; set; }
    public string AutoScrapeLastRunSummary { get; set; } = "";

    /// <summary>Which browser to scrape with — "Chrome", "Firefox", or "Edge" (see the Browser
    /// tab). Stored as a string rather than the ScraperBrowserChoice enum directly for a
    /// human-readable settings.json. Defaults to Chrome, the only one these bot-detection
    /// workarounds have actually been tested against.</summary>
    public string ScraperBrowser { get; set; } = "Chrome";

    // --- RabbitMQ "meeting.scraped" event publishing (see RabbitMqMeetingEventPublisher). Off by
    // default (opt-in) so existing installs behave exactly as before until real connection details
    // are entered. Flat properties, matching this class's existing style (no nested config
    // sections). Like the S3 credentials above, these are reset to these baked-in defaults on every
    // installer update — re-enter host/credentials after an update if you rely on this. ---

    /// <summary>Master switch: when false, no events are published and the scrape path is
    /// untouched. Turn on deliberately on the machine that should notify downstream systems.</summary>
    public bool RabbitMqEnabled { get; set; } = true;

    public string RabbitMqHostName { get; set; } = "62.171.228.224";
    public int RabbitMqPort { get; set; } = 5672;
    public string RabbitMqVirtualHost { get; set; } = "/";

    public string RabbitMqUserName { get; set; } = "troyen";
    public string RabbitMqPassword { get; set; } = "Abcd123";

    /// <summary>Shared topic exchange every scraper publishes into — the same one the sibling
    /// Punters scraper uses. Configurable so ops can point this at whatever the priority-control
    /// project actually expects without a code change.</summary>
    public string RabbitMqExchangeName { get; set; } = "td.scrapers.events";

    /// <summary>Routing-key template; "{source}" and "{disciplineCode}" (lowercased) are filled per
    /// event, e.g. "racenet.meeting.scraped.t".</summary>
    public string RabbitMqRoutingKeyTemplate { get; set; } = "{source}.meeting.scraped.{disciplineCode}";

    /// <summary>Lowercase scraper token used in the routing key (distinct from the payload's
    /// "RaceNetScraper" source, which names the project).</summary>
    public string RabbitMqSource { get; set; } = "racenet";

    /// <summary>Priority (0–10 scale) stamped on every message. This app never sets any other
    /// value — re-prioritization is the priority-control project's job.</summary>
    public int RabbitMqDefaultPriority { get; set; } = 5;

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
