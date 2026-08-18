namespace RaceNetScraper.Shared.Scraping;

/// <summary>
/// Which browser engine <see cref="RaceNetScraper.Core.Scraping.IRaceNetScraperService"/> drives.
/// Chrome and Edge are both Chromium under the hood (Edge via a real, separately-installed
/// executable, same as Chrome), so the CDP-attach bot-detection workaround in
/// RaceNetScraperService applies to both. Firefox is a genuinely different rendering engine —
/// those Chromium-specific workarounds don't apply to it, so it may not get past Racenet's
/// bot-detection as reliably; try Chrome or Edge first if Firefox gets blocked.
/// </summary>
public enum ScraperBrowserChoice
{
    Chrome = 0,
    Firefox = 1,
    Edge = 2
}
