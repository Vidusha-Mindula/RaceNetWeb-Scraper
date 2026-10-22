namespace RaceNetScraper.App;

/// <summary>Which day(s)/discipline(s) to scrape for one country group within a slot — see
/// <see cref="AutoScrapeSlot"/>.</summary>
public sealed class AutoScrapeCountryRun
{
    /// <summary>When false, this country group is skipped entirely for this slot's time — lets a
    /// slot cover just Australia, just International, or both.</summary>
    public bool Enabled { get; set; } = true;

    public bool IncludeToday { get; set; } = true;
    public bool IncludeTomorrow { get; set; } = true;
    public bool IncludeDayAfterTomorrow { get; set; } = true;

    public bool Horses { get; set; } = true;
    public bool Greyhounds { get; set; } = true;
    public bool Harness { get; set; } = true;
}

/// <summary>One scheduled Auto Scraper time — fires <see cref="Australia"/> and/or
/// <see cref="International"/> (whichever are enabled) back-to-back at <see cref="Time"/>, each
/// with its own day(s)/discipline(s). This is what lets e.g. a single 13:50 slot scrape
/// Australia-only Harness for Today while ALSO scraping International Harness for Today, in the
/// same run, rather than needing two separate slots (which risked the second one being silently
/// skipped if the first was still busy when its own time arrived — see AutoScrapeTickAsync).</summary>
public sealed class AutoScrapeSlot
{
    /// <summary>24h "HH:mm", e.g. "06:00".</summary>
    public string Time { get; set; } = "06:00";

    public AutoScrapeCountryRun Australia { get; set; } = new();
    public AutoScrapeCountryRun International { get; set; } = new();
}
