namespace RaceNetScraper.Shared.Scraping;

public sealed class ScraperOptions
{
    /// <summary>
    /// Run Chromium headless. Defaults to false: headless Chromium was observed getting
    /// hard-blocked (HTTP 403) on the very first page load by bot-detection, while a headed
    /// (visible) browser passed every time in testing. Try true first if you'd rather not see
    /// the browser window — if you start seeing HTTP 403s, switch back to false.
    /// </summary>
    public bool Headless { get; set; } = false;

    /// <summary>
    /// When Headless is false, position the browser window off-screen so it renders as a real
    /// (non-headless) browser — which is what gets past bot-detection reliably — but never
    /// actually appears on screen or steals focus. This is the recommended way to run
    /// "invisibly": true headless mode gets blocked, but you also don't want a visible window
    /// popping up. Has no effect when Headless is true. Defaults to true.
    /// </summary>
    public bool HideWindow { get; set; } = true;

    /// <summary>
    /// Page navigation timeout in milliseconds.
    /// </summary>
    public int NavigationTimeoutMs { get; set; } = 45_000;

    /// <summary>
    /// How long to let the page settle after load before issuing our own query, so the
    /// request looks like it follows a normal page visit rather than firing instantly.
    /// </summary>
    public int SettleDelayMs { get; set; } = 1500;
}
