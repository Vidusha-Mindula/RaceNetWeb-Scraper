using RaceNetScraper.Shared.Scraping;

namespace RaceNetScraper.Core.Scraping;

/// <summary>
/// Checks whether a given <see cref="ScraperBrowserChoice"/> can actually be launched on this
/// machine, so the UI can disable/flag options that aren't usable here instead of letting someone
/// pick one and only find out it fails once a scrape is already underway.
/// </summary>
public static class ScraperBrowserAvailability
{
    public static bool IsInstalled(ScraperBrowserChoice browser) => browser switch
    {
        ScraperBrowserChoice.Chrome => HasPlaywrightBrowserFolder("chromium"),
        ScraperBrowserChoice.Firefox => HasPlaywrightBrowserFolder("firefox"),
        ScraperBrowserChoice.Edge => IsEdgeInstalled(),
        _ => false
    };

    /// <summary>A one-line, user-facing explanation of how to fix it when <see cref="IsInstalled"/>
    /// returns false for this browser — surfaced directly in status/error text rather than a raw
    /// exception message.</summary>
    public static string InstallHint(ScraperBrowserChoice browser) => browser switch
    {
        ScraperBrowserChoice.Chrome => "Run: pwsh <app folder>/playwright.ps1 install chromium",
        ScraperBrowserChoice.Firefox => "Run: pwsh <app folder>/playwright.ps1 install firefox",
        ScraperBrowserChoice.Edge => "Install Microsoft Edge on this PC (https://www.microsoft.com/edge), or pick a different browser.",
        _ => ""
    };

    // Playwright downloads Chromium/Firefox/WebKit into a shared cache folder (one subfolder per
    // browser + version, e.g. "chromium-1117") rather than installing them like a normal app -
    // PLAYWRIGHT_BROWSERS_PATH overrides the location if set, same variable Playwright's own
    // install script and runtime both respect.
    private static bool HasPlaywrightBrowserFolder(string prefix)
    {
        var basePath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(basePath))
        {
            basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        }

        return Directory.Exists(basePath) && Directory.EnumerateDirectories(basePath, $"{prefix}-*").Any();
    }

    // Edge isn't downloaded by Playwright at all - the "msedge" channel launches whatever Edge is
    // already installed on the machine, so availability just means checking its usual install path.
    private static bool IsEdgeInstalled()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.Any(File.Exists);
    }
}
