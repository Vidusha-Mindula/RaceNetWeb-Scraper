using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using RaceNetScraper.Shared.Json;
using RaceNetScraper.Shared.Models;
using RaceNetScraper.Shared.Scraping;

namespace RaceNetScraper.Core.Scraping;

/// <summary>
/// Scrapes Racenet.com.au's meeting/race listings for a given discipline and date, and emits the
/// shared <see cref="Meeting"/>/<see cref="RaceDetail"/> DTOs (from RaceNetScraper.Shared) that
/// TroyenRaceIngestor expects — byte-compatible with what PuntersScraper produces.
///
/// How it gets the data, and why (this changed from the original design — see below):
///   - Racenet's API (api.racenet.com.au/racing) sits behind CloudFront and, more importantly,
///     its GraphQL responses carry no Access-Control-Allow-Origin header — a fetch() issued from
///     inside a Racenet page (which is what this engine originally did) is blocked by the
///     browser's own CORS enforcement, confirmed live: the request reaches the server and gets a
///     real response, but the browser refuses to hand it to our script. A plain out-of-browser
///     HttpClient (or Playwright's IAPIRequestContext) isn't CORS-restricted, but CloudFront
///     fingerprints those as non-browser traffic and blocks them outright (HTTP 403) regardless.
///   - What DOES work: Racenet's own site is server-rendered (Nuxt 2) — every page ships with the
///     already-resolved GraphQL data baked into `window.__NUXT__.apollo.defaultClient`, a
///     normalized Apollo cache (ROOT_QUERY entries plus `Typename:id` entity records with
///     `{type:'id', id, typename}` references between them). This engine navigates to the real
///     page exactly like a visitor would and reads that embedded cache instead of issuing any
///     request of its own — the same approach the Punters engine already used out of necessity
///     (see PuntersScraperService), just against Nuxt 2's cache shape instead of Nuxt 3's.
///   - Consequence: this can only see whatever window of data the page itself rendered. Racenet's
///     own form-guide index always renders the current AEST racing day (verified live) — the
///     "Tomorrow"/"Thursday"/etc. date tabs did not update that embedded cache when driven
///     programmatically, so multi-day scraping is NOT currently supported; requesting a date
///     other than "today" fails loudly (see ScrapeMeetingsAsync) rather than silently returning
///     wrong data.
///   - Consequence: race detail needs one full page navigation per race (the race's own
///     "/overview" page), not a single batched query — meaning a full meeting scrape now costs
///     one navigation for the listing plus one per race. Keep that in mind: it's meaningfully more
///     load against the live site per scrape than the original fetch-based design, and more
///     navigations than PuntersScraperService's own per-race approach if this ever needs
///     throttling back.
///
/// A few data notes worth keeping in mind:
///   - Per-meeting weather isn't present on the listing page's Meeting entity (only per-event, on
///     the race's own page) — Events in the meetings list simply carry a null weather.
///   - Racenet's Selection entity has no free-text "racing colours" field at all in this data
///     source, so every runner's SilkColourText always falls through to the SilkSvgDescriber
///     image-based fallback now, not just "the rare case where it's missing" as before.
///   - Meetings are returned as a flat list; the two-tier Australia/International grouping the
///     ingester expects is inferred here from venue.country.iso2, same as PuntersScraper.
///   - For meetings/races Racenet hasn't finished processing, prize money breakdown and
///     historical form/stats for a runner are simply absent from the page.
/// </summary>
public sealed class RaceNetScraperService : IRaceNetScraperService
{
    private const string BaseUrl = "https://www.racenet.com.au";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowser? _cdpBrowser;
    private IBrowserContext? _context;
    private IPage? _sessionPage;
    private Process? _browserProcess;
    private bool _headless;
    private int _navigationTimeoutMs = 45_000;
    private int _settleDelayMs = 1500;

    public async Task InitializeAsync(ScraperOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScraperOptions();
        _headless = options.Headless;

        _playwright = await Playwright.CreateAsync();

        // Firefox is a different engine entirely: none of Chromium's automation-fingerprint or
        // bot-detection workarounds apply to it, so it launches plain (see ScraperBrowserChoice's
        // doc comment for why that also means it's less proven against Racenet's bot-detection).
        if (options.Browser == ScraperBrowserChoice.Firefox)
        {
            // Firefox has no equivalent of Chromium's "--window-position" launch argument, so
            // "hidden but not headless" (see HideWindow's doc comment on ScraperOptions) has to
            // happen after the fact instead of via a launch flag — see OffScreenWindowMover.
            var launchedAt = DateTime.UtcNow;
            _browser = await _playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless
            });

            if (!options.Headless && options.HideWindow)
            {
                // 20s upper bound to allow for first-run Firefox profile creation, which is much
                // slower than a normal launch — TryMoveOffScreenAsync returns well before that in
                // the common case, shortly after it confirms a window was actually moved.
                await OffScreenWindowMover.TryMoveOffScreenAsync(
                    "firefox", launchedAt, TimeSpan.FromSeconds(20), cancellationToken);
            }

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
                Locale = "en-AU",
                TimezoneId = "Australia/Sydney"
            });
            await _context.AddInitScriptAsync(
                "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");
        }
        else
        {
            // A Playwright-LAUNCHED Chromium/Edge — even with AutomationControlled disabled —
            // still carries other automation fingerprints (--enable-automation, injected CDP init
            // scripts) that Racenet's bot-detection interstitial ("Checking your browser...")
            // detects and never clears, no matter how long you wait. Confirmed live: a plain curl
            // request and a Playwright-launched headed Chromium both got stuck on/blocked by the
            // exact same challenge page. The fix isn't a smarter wait, it's not looking automated
            // in the first place. So when the real system browser is actually installed, this
            // starts it as an ordinary OS process (nothing Playwright-specific about the launch)
            // and attaches to it over CDP instead — see InitViaCdpAsync. Only when that executable
            // can't be found does this fall back to the previous Playwright-launched approach,
            // which remains just as exposed to the same detection as before.
            var executablePath = options.Browser == ScraperBrowserChoice.Edge
                ? FindEdgeExecutablePath()
                : FindChromeExecutablePath();

            if (executablePath is not null)
            {
                await InitViaCdpAsync(executablePath, options, cancellationToken);
            }
            else
            {
                var args = new List<string> { "--disable-blink-features=AutomationControlled" };
                if (!options.Headless && options.HideWindow)
                {
                    args.Add("--window-position=-32000,-32000");
                    args.Add("--disable-backgrounding-occluded-windows");
                    args.Add("--disable-renderer-backgrounding");
                    args.Add("--disable-background-timer-throttling");
                }

                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = options.Headless,
                    Args = args,
                    Channel = options.Browser == ScraperBrowserChoice.Edge ? "msedge" : null
                });

                // No UserAgent override here (deliberately): a hardcoded string goes stale the
                // moment the bundled Chromium build moves on, and Playwright's UserAgent option
                // only rewrites the User-Agent header/navigator.userAgent — it does NOT touch the
                // Sec-CH-UA Client Hints headers, which keep reporting the browser's real version
                // regardless. A stale "Chrome/124" override alongside a much newer real
                // `sec-ch-ua` value is a UA/Client-Hints mismatch — a well-known, highly reliable
                // bot signal (confirmed against this exact issue by a sibling scraper against a
                // similar site).
                _context = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
                    Locale = "en-AU",
                    TimezoneId = "Australia/Sydney"
                });
                await _context.AddInitScriptAsync(
                    "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");
            }
        }

        _navigationTimeoutMs = options.NavigationTimeoutMs;
        _settleDelayMs = options.SettleDelayMs;
    }

    // ── CDP-attach browser init (see the comment in InitializeAsync for why) ─────────────────

    /// <summary>
    /// Starts <paramref name="executablePath"/> as an ordinary OS process — not via Playwright's
    /// own launcher — with its own on-disk profile, then attaches Playwright to it over the
    /// Chrome DevTools Protocol. A persistent <c>--user-data-dir</c> means any clearance cookie
    /// Racenet's bot-detection sets survives across runs once earned, so only the first run (or
    /// the first run after it expires) actually has to clear the challenge at all.
    /// </summary>
    private async Task InitViaCdpAsync(string executablePath, ScraperOptions options, CancellationToken cancellationToken)
    {
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RaceNetScraper", "browser-profile", options.Browser.ToString().ToLowerInvariant());
        Directory.CreateDirectory(userDataDir);

        var port = FindFreeTcpPort();
        var psi = new ProcessStartInfo { FileName = executablePath, UseShellExecute = false };
        psi.ArgumentList.Add($"--remote-debugging-port={port}");
        psi.ArgumentList.Add($"--user-data-dir={userDataDir}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        // Stop the browser from throttling/backgrounding/discarding the controlled tab. Without
        // these, a heavy race page can get its renderer discarded or the tab put to sleep, which
        // drops the CDP target mid-scrape ("Target page, context or browser has been closed").
        // NOTE: --disable-features must only ever appear ONCE on the command line — Chromium
        // only honors the last occurrence rather than merging repeats, so every disabled
        // feature has to be listed together here.
        //
        // msImplicitSignin/msSyncConsentUI: Windows' own signed-in account otherwise gets
        // auto-detected and Edge offers to sync that real account's browsing data (passwords,
        // history) into this profile — both a privacy problem (the user's real account/data
        // linked into an automation profile) and a reliability one (a browser-chrome overlay the
        // page-level bot-detection below can't see or dismiss, since it isn't part of the page's
        // own DOM). --disable-sync (a separate flag, not a --disable-features entry) stops the
        // sync relationship itself.
        psi.ArgumentList.Add(
            "--disable-features=msEdgeWelcomeExperience,msFirstRunExperience,msSleepingTabs," +
            "IntensiveWakeUpThrottling,msImplicitSignin,msSyncConsentUI");
        psi.ArgumentList.Add("--disable-sync");
        psi.ArgumentList.Add("--disable-background-timer-throttling");
        psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        psi.ArgumentList.Add("--disable-renderer-backgrounding");
        psi.ArgumentList.Add("--disable-dev-shm-usage");
        if (options.Headless)
        {
            psi.ArgumentList.Add("--headless=new");
        }
        else if (options.HideWindow)
        {
            // Same off-screen trick as the Playwright-launched path — still a real, visible-to-
            // Chromium window (so it isn't throttled like an actually-minimized one), just placed
            // off the desktop. Note: this also means nobody can manually solve a challenge that
            // doesn't clear on its own — see WaitForChallengeToClearAsync's manual-solve prompt,
            // which needs HideWindow=false to actually be usable.
            psi.ArgumentList.Add("--window-position=-32000,-32000");
        }
        else
        {
            psi.ArgumentList.Add("--start-maximized");
        }
        psi.ArgumentList.Add("about:blank");

        _browserProcess = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {executablePath}.");

        var endpoint = $"http://127.0.0.1:{port}";
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var version = await http.GetStringAsync($"{endpoint}/json/version");
                    if (!string.IsNullOrWhiteSpace(version)) break;
                }
                catch
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new InvalidOperationException(
                            $"{Path.GetFileName(executablePath)} DevTools endpoint did not become available within 30s.");
                    }
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        _cdpBrowser = await _playwright!.Chromium.ConnectOverCDPAsync(endpoint);
        _context = _cdpBrowser.Contexts.Count > 0 ? _cdpBrowser.Contexts[0] : await _cdpBrowser.NewContextAsync();
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Same candidate paths ScraperBrowserAvailability.IsEdgeInstalled checks for UI gating —
    // Edge isn't downloaded by Playwright, so availability just means checking its usual install path.
    private static string? FindEdgeExecutablePath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // Unlike ScraperBrowserAvailability's "Chrome" (which means Playwright's bundled Chromium
    // download), this specifically looks for a real, separately-installed Google Chrome — the
    // CDP-attach trick only works against a genuinely, independently-launched browser process.
    // Falls back to the bundled Chromium in InitializeAsync when this isn't found.
    private static string? FindChromeExecutablePath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Racenet's own form-guide URL per discipline. Any page on the site establishes the same
    /// embedded-cache mechanism, but each discipline's own listing is what actually carries that
    /// discipline's meetings.
    /// </summary>
    private static string FormGuidePath(Discipline discipline) => discipline switch
    {
        Discipline.Horses => "horse-racing",
        Discipline.Greyhounds => "greyhounds",
        Discipline.Harness => "harness",
        _ => "horse-racing"
    };

    // Set by the session page's own Crash event (see EnsurePageAsync) — a renderer crash
    // (confirmed live during a long multi-meeting run: "Page crashed" on every navigation from
    // ~race 30 onward) does NOT make Playwright's IPage.IsClosed true, since the page was never
    // explicitly closed — it's just permanently unusable from that point on. Without tracking
    // this separately, EnsurePageAsync kept handing back the same dead page forever, so every
    // single race for the rest of that run (and even the next discipline's own meeting listing)
    // failed identically instead of recovering with a fresh page.
    private bool _pageCrashed;

    private async Task<IPage> EnsurePageAsync()
    {
        if (_context is null)
            throw new InvalidOperationException($"Call {nameof(InitializeAsync)} before scraping.");

        if (_sessionPage is { IsClosed: false } && !_pageCrashed)
            return _sessionPage;

        _pageCrashed = false;
        _sessionPage = await _context.NewPageAsync();
        _sessionPage.Crash += (_, _) => _pageCrashed = true;

        // Only the Playwright-launched paths (Firefox / no-real-browser-found fallback) set a
        // viewport above — the CDP-attach path (the one actually used whenever a real Chrome/Edge
        // is installed) never did, leaving the page's layout at whatever size the real browser
        // window happened to open at. Racenet's race pages are responsive and switch to a
        // different (narrower, no expand-toggle) layout below a certain width — confirmed live:
        // without this, ScrapeRaceAsync's DOM-based "Recent Starts" lookup silently found zero
        // toggles and fell back to a single run every time. Forcing a consistent desktop size
        // here guarantees the same rendered layout regardless of which path actually launched.
        await _sessionPage.SetViewportSizeAsync(1400, 900);
        return _sessionPage;
    }

    private async Task NavigateAsync(
        IPage page, string url, IProgress<string>? progress, string disciplineCode, CancellationToken cancellationToken)
    {
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _navigationTimeoutMs
            });
        }
        catch (Exception ex)
        {
            throw new RaceNetScrapeException($"Could not load {url}: {ex.Message}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await WaitForChallengeIfPresentAsync(page, progress, disciplineCode, cancellationToken);
        await page.WaitForTimeoutAsync(_settleDelayMs);
    }

    private static string Snippet(string s) =>
        string.IsNullOrEmpty(s) ? "(empty response)"
            : Regex.Replace(s, @"\s+", " ").Trim() is var flat && flat.Length <= 300 ? flat : flat[..300] + "...";

    // ── Bot-detection challenge handling ──────────────────────────────────────────
    // Ported from a sibling scraper (PuntersWebScraper) that already solved this exact problem
    // against a similar site. Detection here only ever checks for the challenge's *presence*
    // (title/body text/known selectors) — it never tries to reach into a Turnstile-style widget
    // itself, since InitializeAsync/InitViaCdpAsync's CDP-attach approach is what actually gets
    // a challenge to validate; this is just the wait-and-retry loop around that.

    /// <summary>
    /// Detects a bot-detection interstitial by its page title and well-known challenge markers —
    /// this is what Racenet showed live ("Checking your browser... Just checking your browser
    /// This should only take a moment.") when scraped from a datacenter/cloud IP. Deliberately
    /// conservative (returns false rather than throwing) on any evaluation failure — a navigation
    /// in flight or a not-yet-ready page just means "unknown, let the caller retry".
    /// </summary>
    private static async Task<bool> IsChallengePageAsync(IPage page)
    {
        try
        {
            var title = await page.TitleAsync();
            if (title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase))
                return true;

            return await page.EvaluateAsync<bool>("""
                () => {
                    const text = document.body ? document.body.innerText : '';
                    if (/just checking your browser|checking if the site connection is secure|verify you are human/i.test(text)) return true;
                    return !!document.querySelector('#challenge-form, #cf-challenge-running, .cf-turnstile, iframe[src*="challenges.cloudflare.com"]');
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A checkbox-style challenge widget's checkbox commonly lives inside a *closed* shadow root
    /// — no selector, in any tool, can target it directly, so the only reliable way in is a real
    /// mouse click at its actual on-screen position. <c>#w</c> is the div such widgets are
    /// typically rendered into, in the page's own light DOM (not shadowed), so its bounding box
    /// gives real, reliable coordinates to click within. There's no way to tell from outside
    /// whether it's currently showing an inert "Verifying..." spinner or the actual checkbox
    /// (both occupy the same box), so a click landing on the spinner is just a harmless no-op —
    /// the caller re-attempts this periodically rather than treating one attempt as definitive.
    /// </summary>
    private static async Task TryClickChallengeCheckboxAsync(IPage page, CancellationToken cancellationToken)
    {
        var widget = page.Locator("#w");
        if (await widget.CountAsync() == 0) return;

        var box = await widget.BoundingBoxAsync();
        if (box is not { Height: > 20 }) return;

        cancellationToken.ThrowIfCancellationRequested();

        // Click near the checkbox's left edge (vertically centered) rather than dead center of
        // the whole widget box, which is mostly label text rather than checkbox.
        var targetX = box.X + 20;
        var targetY = box.Y + box.Height / 2;

        await page.Mouse.MoveAsync((float)targetX - 30, (float)targetY - 10);
        await page.WaitForTimeoutAsync(Random.Shared.Next(150, 350));
        await page.Mouse.MoveAsync((float)targetX, (float)targetY);
        await page.WaitForTimeoutAsync(Random.Shared.Next(250, 500));
        await page.Mouse.DownAsync();
        await page.WaitForTimeoutAsync(Random.Shared.Next(60, 140));
        await page.Mouse.UpAsync();
    }

    /// <summary>
    /// Polls until the interstitial goes away or <paramref name="deadline"/> is hit, actively
    /// clicking a checkbox-style challenge widget (see TryClickChallengeCheckboxAsync) every ~8
    /// seconds along the way rather than passively waiting for it to clear on its own. A
    /// Playwright-LAUNCHED browser's clicks get rejected/ignored outright regardless of precision
    /// — but InitViaCdpAsync means this is a genuinely non-automated browser instead, so an actual
    /// click now gets evaluated on its own merits. If it's still stuck after a while and the
    /// browser is actually visible on screen (Headless=false, HideWindow=false), this also prints
    /// a one-time note that a human is welcome to click it too — not required, since the automated
    /// clicking keeps running regardless, just an extra avenue. Either way, the persistent profile
    /// then stores whatever clearance cookie was earned, so later runs skip the challenge entirely.
    /// </summary>
    private async Task WaitForChallengeToClearAsync(IPage page, DateTime deadline, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        var promptedForManualSolve = false;
        var nextClickAttempt = DateTime.MinValue; // click immediately on the first iteration

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTime.UtcNow >= nextClickAttempt)
            {
                await TryClickChallengeCheckboxAsync(page, cancellationToken);
                nextClickAttempt = DateTime.UtcNow.AddSeconds(8);
            }

            await page.WaitForTimeoutAsync(2_000);
            if (!await IsChallengePageAsync(page))
            {
                await page.WaitForTimeoutAsync(1_500); // let the real page render
                return;
            }

            if (!_headless && !promptedForManualSolve && DateTime.UtcNow - start > TimeSpan.FromSeconds(30))
            {
                promptedForManualSolve = true;
                Console.WriteLine();
                Console.WriteLine("============================================================");
                Console.WriteLine(" Racenet is still asking to verify the browser.");
                Console.WriteLine(" Feel free to click any 'Verify you are human' checkbox");
                Console.WriteLine(" yourself in the browser window that opened — scraping");
                Console.WriteLine(" resumes automatically either way.");
                Console.WriteLine("============================================================");
                Console.WriteLine();
            }
        }
    }

    /// <summary>
    /// Wraps a navigation with detect → wait-for-clear → (bounded) reload-and-retry, so a
    /// challenge that's slow but solvable doesn't get mistaken for a hard failure, and one that
    /// genuinely won't clear still gives up rather than hanging forever. Headless runs get a
    /// short budget (nobody can manually solve it); a visible run gets several minutes, since the
    /// manual-solve prompt in WaitForChallengeToClearAsync needs that time to actually be useful.
    /// </summary>
    private async Task WaitForChallengeIfPresentAsync(
        IPage page, IProgress<string>? progress, string disciplineCode, CancellationToken cancellationToken,
        int maxAttempts = 3)
    {
        var budgetMs = _headless ? 90_000 : 300_000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsChallengePageAsync(page)) return;

            progress?.Report($"[R-{disciplineCode}] Bot-detection challenge detected (attempt {attempt}) — waiting for it to clear...");
            var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
            await WaitForChallengeToClearAsync(page, deadline, cancellationToken);

            if (!await IsChallengePageAsync(page)) return;
            if (attempt == maxAttempts) return; // let the caller's own diagnostics report the stuck page

            progress?.Report($"[R-{disciplineCode}] Challenge still up — reloading and retrying...");
            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
        }
    }

    /// <summary>
    /// How far ahead Racenet's own named date tabs go (Today, Tomorrow, then this many more named
    /// weekday tabs) before falling back to an unspecific "Future" bucket that doesn't map to one
    /// exact date and so can't be targeted here. There is no "Yesterday" tab, unlike Punters —
    /// past dates aren't reachable at all via this mechanism.
    /// </summary>
    private const int MaxTabDaysAhead = 4;

    public async Task<ScrapeResult> ScrapeMeetingsAsync(
        Discipline discipline,
        DateOnly startDate,
        DateOnly? endDate = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (endDate is { } explicitEnd && explicitEnd != startDate)
        {
            throw new RaceNetScrapeException(
                "Only single-day scraping is supported: Racenet's own UI presents one day at a " +
                "time (via date tabs), so there is no single request that covers a range. Call " +
                $"{nameof(ScrapeMeetingsAsync)} once per day instead.");
        }

        var page = await EnsurePageAsync();
        var url = $"{BaseUrl}/form-guide/{FormGuidePath(discipline)}";
        progress?.Report($"[R-{discipline.Code()}] Loading {url} ...");
        await NavigateAsync(page, url, progress, discipline.Code(), cancellationToken);

        // "Today" here must match whatever calendar day Racenet's own server-rendered page
        // considers "Today" — NOT computed from new Date() (confirmed live: the host machine's
        // own clock can simply be wrong relative to the outside world, not just a timezone
        // mismatch — observed a full day of skew, browser clock reporting 2026-09-30 while
        // Racenet's own already-loaded "Today" data was dated 2026-10-01). Reading the page's own
        // default-loaded meetings component (the same one findMeetingsForDate below walks for,
        // just without a specific target date to match — on a fresh page load, before any tab is
        // clicked, whichever component already has meetings loaded IS the "Today" one) sidesteps
        // any local clock entirely, since it's the server's own rendered date, not the client's.
        var todayStr = await page.EvaluateAsync<string>("""
            () => {
                const root = document.querySelector('#__nuxt') && document.querySelector('#__nuxt').__vue__;
                const seen = new Set();
                function walk(comp, depth) {
                    if (!comp || seen.has(comp) || depth > 30) return null;
                    seen.add(comp);
                    if (comp.$data && comp.$data.dataDate && comp.$data.meetings) return comp.$data.dataDate;
                    for (const child of (comp.$children || [])) {
                        const found = walk(child, depth + 1);
                        if (found) return found;
                    }
                    return null;
                }
                const siteToday = root ? walk(root, 0) : null;
                if (siteToday) return siteToday;

                // Fallback only if Racenet's own layout changed enough that the walk above can't
                // find the default-loaded component at all — better than failing outright, even
                // though this reintroduces the host-clock dependency the walk exists to avoid.
                const d = new Date();
                const p = n => String(n).padStart(2, '0');
                return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
            }
            """);
        var today = DateOnly.Parse(todayStr);
        var dayOffset = startDate.DayNumber - today.DayNumber;

        if (dayOffset < 0 || dayOffset > MaxTabDaysAhead)
        {
            throw new RaceNetScrapeException(
                $"{startDate:yyyy-MM-dd} is {(dayOffset < 0 ? $"{-dayOffset} day(s) in the past" : $"{dayOffset} days ahead")}, " +
                $"which is outside Racenet's named date tabs (Today + {MaxTabDaysAhead} days ahead; no past dates).");
        }

        if (dayOffset > 0)
        {
            var tabLabel = dayOffset switch
            {
                1 => "Tomorrow",
                _ => startDate.ToDateTime(TimeOnly.MinValue).DayOfWeek.ToString()
            };
            progress?.Report($"[R-{discipline.Code()}] Clicking the '{tabLabel}' date tab ...");

            // Not GetByRole(AriaRole.Link, ...): these tabs are <a> elements with no href, which
            // ARIA gives no implicit role at all (confirmed live — GetByRole finds zero matches),
            // so this matches on the tab's own CSS class plus its text instead. None of the tab
            // labels (Tomorrow/Thursday/Friday/Saturday/...) are substrings of each other, so a
            // plain HasTextString match is unambiguous.
            var tab = page.Locator("a.tab").Filter(new LocatorFilterOptions { HasTextString = tabLabel });
            if (await tab.CountAsync() == 0)
            {
                throw new RaceNetScrapeException(
                    $"Could not find a '{tabLabel}' date tab on {url}. Racenet may have changed its " +
                    "date-tab labels/layout since this was written.");
            }

            // Racenet's own lazy-loaded promo banner frequently ends up sitting visually on top of
            // the tab bar (it starts as a zero-height placeholder and expands once its ad content
            // loads), which blocks a normal click (the tab itself also carries
            // "disable-pointer-events", so a plain click retries against whatever's on top of it
            // forever). Removing every fixed/sticky element sidesteps that regardless of which one
            // it is on any given day.
            await page.EvaluateAsync("""
                () => {
                    for (const el of document.querySelectorAll('*')) {
                        const style = getComputedStyle(el);
                        if ((style.position === 'fixed' || style.position === 'sticky') && el.offsetHeight > 0) el.remove();
                    }
                }
                """);

            await tab.First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
            cancellationToken.ThrowIfCancellationRequested();
        }

        progress?.Report(
            $"[R-{discipline.Code()}] Reading meetings for {startDate:yyyy-MM-dd} from the page's own data ...");

        // Clicking a date tab updates the target component's data asynchronously — how long that
        // takes varies noticeably run to run (confirmed live: sometimes near-instant, sometimes a
        // couple of seconds), so this polls for up to ~15s rather than trusting one fixed delay to
        // always be enough. "Today" (dayOffset == 0, no click above) is already there from the
        // page's own initial load, so this always finds it on the very first attempt in that case.
        var targetDate = startDate.ToString("yyyy-MM-dd");
        var script = "(args) => {" + ReadMeetingsScript + "}";
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonDocument envelope;
        JsonElement root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string raw;
            try
            {
                raw = await page.EvaluateAsync<string>(script, new { targetDate });
            }
            catch (Exception ex)
            {
                throw new RaceNetScrapeException($"Reading meetings from {url} failed: {ex.Message}", ex);
            }

            envelope = JsonDocument.Parse(raw);
            root = envelope.RootElement;

            if (!root.TryGetProperty("error", out _)) break;

            if (DateTime.UtcNow >= deadline)
            {
                var diagnostics = root.TryGetProperty("diagnostics", out var dg) ? dg.GetString() : "";
                envelope.Dispose();
                throw new RaceNetScrapeException(
                    $"Racenet's page at {url} didn't have {startDate:yyyy-MM-dd}'s meetings data " +
                    $"after waiting for it. {diagnostics}");
            }

            envelope.Dispose();
            await page.WaitForTimeoutAsync(750);
        }

        using (envelope)
        {
            var response = JsonSerializer.Deserialize<GroupedMeetingsPayload>(root.GetRawText(), ScraperJsonOptions.Deserialize)
                ?? throw new RaceNetScrapeException("Could not parse Racenet's embedded meetings data (empty result).");

            var groups = response.Data?.MeetingsGrouped ?? new List<MeetingGroup>();
            progress?.Report(
                $"[R-{discipline.Code()}] Found {groups.Sum(g => g.Meetings.Count)} meeting(s) " +
                $"across {groups.Count} group(s).");

            return new ScrapeResult
            {
                Discipline = discipline,
                StartDate = startDate,
                EndDate = startDate,
                ScrapedAtUtc = DateTimeOffset.UtcNow,
                MeetingsGrouped = groups
            };
        }
    }

    public async Task<RaceDetail> ScrapeRaceAsync(
        Discipline discipline,
        Meeting meeting,
        RaceEvent raceEvent,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(raceEvent.Id) || string.IsNullOrEmpty(raceEvent.Slug) || string.IsNullOrEmpty(meeting.Slug))
        {
            throw new RaceNetScrapeException(
                "Race id/slug or meeting slug is missing — pass in the Meeting/RaceEvent objects returned by " +
                $"{nameof(ScrapeMeetingsAsync)}, not hand-built ones.");
        }

        var page = await EnsurePageAsync();
        var url = $"{BaseUrl}/form-guide/{FormGuidePath(discipline)}/{meeting.Slug}/{raceEvent.Slug}/overview";

        progress?.Report(
            $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}): loading {url} ...");
        await NavigateAsync(page, url, progress, discipline.Code(), cancellationToken);

        var script = "async (args) => {" + DenormalizeJs + ReadRaceDetailScript + "}";
        var scriptArgs = new
        {
            eventId = raceEvent.Id,
            meetingId = meeting.Id,
            meetingName = meeting.Name,
            country = meeting.Venue?.Country?.Iso3,
            date = meeting.MeetingDateLocal
        };

        // Reading immediately after navigation can race Racenet's own lazy hydration of this
        // race's Apollo cache entry — confirmed live: a freshly-navigated race page can briefly
        // have no Event:{id} entry yet, the same kind of asynchronous-data delay
        // ScrapeMeetingsAsync's listing reader above already polls around ("clicking a date tab
        // updates data asynchronously... sometimes near-instant, sometimes a couple of
        // seconds"). Polling here the same way — instead of failing on the very first read —
        // avoids spurious "no event data" failures for a race that's simply still mid-load, and
        // (via the Phase 2 loop's own give-up-after-N-attempts guard) removes the main way a
        // meeting's races could previously fail outright every single time.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string raw = "";
        JsonDocument envelope;
        JsonElement root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                raw = await page.EvaluateAsync<string>(script, scriptArgs);
            }
            catch (Exception ex)
            {
                throw new RaceNetScrapeException($"Reading race detail from {url} failed: {ex.Message}", ex);
            }

            envelope = JsonDocument.Parse(raw);
            root = envelope.RootElement;

            if (!root.TryGetProperty("error", out _)) break;

            if (DateTime.UtcNow >= deadline)
            {
                var diagnostics = root.TryGetProperty("diagnostics", out var dg) ? dg.GetString() : "";
                envelope.Dispose();
                throw new RaceNetScrapeException(
                    $"Racenet returned no event data for race id {raceEvent.Id} ({meeting.Name} R{raceEvent.EventNumber}) " +
                    $"at {url} after waiting for it. {diagnostics}");
            }

            envelope.Dispose();
            await page.WaitForTimeoutAsync(750);
        }

        RaceDetail detail;
        using (envelope)
        {
            if (root.TryGetProperty("eventMeta", out var eventMeta))
                BackfillRaceEvent(raceEvent, eventMeta);

            if (!root.TryGetProperty("raceDetail", out var raceDetailEl))
            {
                throw new RaceNetScrapeException(
                    $"Racenet's page had no race detail for {raceEvent.Id}. Response: {Snippet(raw)}");
            }

            detail = JsonSerializer.Deserialize<RaceDetail>(raceDetailEl.GetRawText(), ScraperJsonOptions.Deserialize)
                ?? throw new RaceNetScrapeException("Could not parse race detail (empty result).");
        }

        foreach (var runner in detail.Runners)
        {
            runner.Discipline = discipline.Code();

            // Racenet's page carries no free-text silk description at all (unlike the old
            // fetch-based query), so this fallback now fires for every runner rather than rarely.
            if (string.IsNullOrEmpty(runner.SilkColourText) && !string.IsNullOrEmpty(runner.SilkImageUrl))
            {
                runner.SilkColourText = await SilkSvgDescriber.DescribeAsync(runner.SilkImageUrl);
            }
        }

        progress?.Report(
            $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}): {detail.Runners.Count} runner(s).");

        return detail;
    }

    /// <summary>
    /// Copies the fields a race's own page resolves better than the meeting list onto the
    /// caller's <see cref="RaceEvent"/>. The prize money part matters most: the meeting list
    /// carries each race's NATIVE currency total, while the race page's own racePrizeMoney is the
    /// AUD figure the downstream ingester expects, with prizeMoney[] the matching per-place AUD
    /// breakdown. raceEvent is the same object living in meeting.Events, so this is visible to
    /// the caller.
    /// </summary>
    private static void BackfillRaceEvent(RaceEvent raceEvent, JsonElement eventEl)
    {
        if (eventEl.TryGetProperty("racePrizeMoney", out var aud) && aud.ValueKind == JsonValueKind.Number)
        {
            raceEvent.RacePrizeMoney = aud.GetDouble();
            raceEvent.RacePrizeMoneyUnit =
                eventEl.TryGetProperty("racePrizeMoneyUnit", out var unit) && unit.ValueKind == JsonValueKind.String
                    ? unit.GetString() : "AUD";
        }

        if (eventEl.TryGetProperty("prizeMoney", out var breakdown) && breakdown.ValueKind == JsonValueKind.Array
            && breakdown.GetArrayLength() > 0)
        {
            raceEvent.PrizeMoney = breakdown.EnumerateArray()
                .Select(p => new PrizeMoneyEntry
                {
                    Position = p.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.String
                        ? pos.GetString() : null,
                    Value = p.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Number
                        ? val.GetDouble() : (double?)null
                })
                .ToList();
        }

        if (eventEl.TryGetProperty("starters", out var starters) && starters.ValueKind == JsonValueKind.Number)
            raceEvent.Starters = starters.GetInt32();

        if (eventEl.TryGetProperty("resultState", out var resultState) && resultState.ValueKind == JsonValueKind.String)
            raceEvent.ResultState = resultState.GetString();

        if (eventEl.TryGetProperty("placeWinners", out var placeWinners) && placeWinners.ValueKind == JsonValueKind.Number)
            raceEvent.PlaceWinners = placeWinners.GetInt32();

        // Racenet has no per-meeting weather query (unlike Punters, which does and copies it onto
        // every event at meeting-scrape time) — it's only resolved once this specific race's own
        // page has been read, so it's backfilled here rather than at ScrapeMeetingsAsync time.
        if (eventEl.TryGetProperty("weather", out var weather) && weather.ValueKind == JsonValueKind.Object)
        {
            raceEvent.Weather = new Weather
            {
                Condition = weather.TryGetProperty("condition", out var cond) && cond.ValueKind == JsonValueKind.String
                    ? cond.GetString() : null,
                Temperature = weather.TryGetProperty("temperature", out var temp) && temp.ValueKind == JsonValueKind.String
                    ? temp.GetString() : null,
                Wind = weather.TryGetProperty("wind", out var wind) && wind.ValueKind == JsonValueKind.String
                    ? wind.GetString() : null,
                Humidity = weather.TryGetProperty("humidity", out var humidity) && humidity.ValueKind == JsonValueKind.String
                    ? humidity.GetString() : null
            };
        }
    }

    public async Task<List<RaceDetail>> ScrapeRacesForMeetingAsync(
        Discipline discipline,
        Meeting meeting,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RaceDetail>();
        foreach (var raceEvent in meeting.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ScrapeRaceAsync(discipline, meeting, raceEvent, progress, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report(
                    $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}) failed, skipping: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>DTO purely for deserializing the shape <see cref="ReadMeetingsScript"/> builds on
    /// the JS side — never sent anywhere, just a typed stepping stone to <see cref="ScrapeResult"/>.</summary>
    private sealed class GroupedMeetingsPayload
    {
        public GroupedMeetingsData? Data { get; set; }
    }

    private sealed class GroupedMeetingsData
    {
        public List<MeetingGroup>? MeetingsGrouped { get; set; }
    }

    /// <summary>
    /// Walks a normalized Apollo cache and resolves every reference into the actual entity it
    /// points at, recursively — the same denormalization Apollo's own InMemoryCache does
    /// internally, just re-implemented in plain JS since we only have the raw cache map to work
    /// with here.
    /// </summary>
    // Racenet has moved this around more than once (confirmed live, each time): originally
    // window.__NUXT__.apollo.defaultClient with {type:'id', id, typename} reference objects keyed
    // by a bare id; then migrated to Nuxt 3's #__nuxt.__vue_app__ payload with {__ref:
    // "Typename:id"} references keyed by that same compound string; and as of this writing it's
    // back to window.__NUXT__.apollo.defaultClient again — except NOW the reference objects look
    // like {type:'id', id:"Typename:id", typename} (the OLD reference *shape*, but keyed by the
    // NEW compound string, not a bare id) — so neither of this function's two previous
    // single-shape implementations actually resolved anything; each just silently handed back the
    // raw unresolved reference object instead of recursing, since neither recognized the other's
    // ref shape. That's the direct cause of ScrapeRaceAsync returning "no event data" for every
    // single race: the 'event(...)' ROOT_QUERY entry WAS present and non-null — it just denormalized
    // to a tiny {type,id,typename} stub instead of the real Event object, which then failed the
    // "root.TryGetProperty('raceDetail', ...)" check downstream. Handling both reference shapes
    // (by id when it's a compound "Typename:id" string, or by __ref) makes this resilient to
    // Racenet changing the shape again without caring which cache location it currently lives in
    // either — see the cache lookup in ReadRaceDetailScript below, which tries both locations.
    private const string DenormalizeJs = """
        function denormalize(value, cache, seen) {
            if (value === null || value === undefined) return value;
            if (Array.isArray(value)) return value.map(v => denormalize(v, cache, seen));
            if (typeof value !== 'object') return value;
            const refKey = typeof value.__ref === 'string' ? value.__ref
                : (value.type === 'id' && typeof value.id === 'string' ? value.id : null);
            if (refKey !== null) {
                if (seen.has(refKey)) return null;
                const entity = cache[refKey];
                if (!entity) return null;
                return denormalize(entity, cache, new Set(seen).add(refKey));
            }
            const out = {};
            for (const k of Object.keys(value)) out[k] = denormalize(value[k], cache, seen);
            return out;
        }
        """;

    /// <summary>
    /// Reads the form-guide page's own meetings for a specific date out of the LIVE Nuxt 2 Vue
    /// component tree, then re-groups into the Australia/International shape TroyenRaceIngestor
    /// expects (inferred from venue.country.iso2, same as PuntersScraper — Racenet's own grouping
    /// isn't necessarily just those two buckets).
    ///
    /// This deliberately does NOT read window.__NUXT__ (checked live, several times): that object
    /// is only the frozen SSR hydration payload, never touched again once the page has hydrated —
    /// clicking a date tab updates the live app's own component state, not that frozen snapshot,
    /// so it always still reports whatever date was server-rendered first regardless of which tab
    /// is now showing on screen. The Apollo cache under window.__NUXT__.apollo has the same
    /// problem one level deeper: confirmed live that clicking a date tab leaves it completely
    /// untouched too. What DOES update is the specific page component's own reactive `data()` —
    /// `dataDate`/`meetings` — found here by walking $children from the Nuxt root looking for a
    /// component whose dataDate matches what was asked for. This is also simpler than the cache
    /// route: the meetings here are already fully resolved plain objects (no {type:'id'}
    /// references to walk), and already pre-grouped, since it's literally what the page itself
    /// renders.
    /// </summary>
    private const string ReadMeetingsScript = """
            function diagnostics() {
                return `title="${document.title}" url="${location.href}" bodySnippet="${(document.body ? (document.body.innerText || '') : '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`;
            }

            function findMeetingsForDate(targetDate) {
                const root = document.querySelector('#__nuxt') && document.querySelector('#__nuxt').__vue__;
                if (!root) return null;

                const seen = new Set();
                function walk(comp, depth) {
                    if (!comp || seen.has(comp) || depth > 30) return null;
                    seen.add(comp);
                    if (comp.$data && comp.$data.dataDate === targetDate && comp.$data.meetings) {
                        return comp.$data.meetings;
                    }
                    for (const child of (comp.$children || [])) {
                        const found = walk(child, depth + 1);
                        if (found) return found;
                    }
                    return null;
                }
                return walk(root, 0);
            }

            const groupsRaw = findMeetingsForDate(args.targetDate);
            if (!groupsRaw) {
                return JSON.stringify({ error: 'NO_DATA_FOR_DATE', diagnostics: diagnostics() });
            }

            const allMeetings = (Array.isArray(groupsRaw) ? groupsRaw : Object.values(groupsRaw))
                .flatMap(g => (g && g.meetings) ? g.meetings : []);

            function buildEvent(meeting, e) {
                return {
                    id: e.id,
                    meetingId: e.meetingId || meeting.id,
                    slug: e.slug,
                    name: e.name,
                    nameNews: e.nameNews || null,
                    eventNumber: e.eventNumber,
                    status: e.status || null,
                    startTime: e.startTime || null,
                    endTime: e.endTime || null,
                    trackType: e.trackType || null,
                    isResulted: !!e.isResulted,
                    resultState: e.resultState || null,
                    isAbandoned: !!e.isAbandoned,
                    placeWinners: e.placeWinners != null ? e.placeWinners : null,
                    distance: e.distance != null ? e.distance : null,
                    starters: e.starters != null ? e.starters : null,
                    racePrizeMoney: e.racePrizeMoneyValue != null ? e.racePrizeMoneyValue
                        : (e.racePrizeMoney != null ? e.racePrizeMoney : null),
                    racePrizeMoneyUnit: e.racePrizeMoneyUnit || null,
                    eventClass: e.eventClass || null,
                    groupType: e.groupType || null,
                    trackCondition: e.trackCondition || null,
                    // Not present on the listing page's Meeting entity at all (only per-event, on
                    // the race's own page) — left null here rather than guessed at.
                    weather: null,
                    entryConditions: e.entryConditions || [],
                    prizeMoney: e.prizeMoney || []
                };
            }

            function buildMeeting(m) {
                return {
                    id: m.id,
                    name: m.name,
                    slug: m.slug,
                    railPosition: m.railPosition || null,
                    timeGroup: m.timeGroup || null,
                    meetingDateUtc: m.meetingDateUtc || null,
                    meetingDateLocal: m.meetingDateLocal || null,
                    regionId: m.regionId || null,
                    sportId: m.sportId || null,
                    penetrometer: m.penetrometer != null ? m.penetrometer : null,
                    trackComments: m.trackComments || null,
                    isFuture: m.isFuture != null ? m.isFuture : null,
                    tabStatus: m.tabStatus != null ? m.tabStatus : null,
                    meetingCategory: m.meetingCategory || null,
                    meetingStage: m.meetingStage || null,
                    meetingType: m.meetingType || null,
                    isAbandoned: m.isAbandoned != null ? m.isAbandoned : null,
                    showSpeedMaps: m.showSpeedMaps != null ? m.showSpeedMaps : null,
                    showSectionals: m.showSectionals != null ? m.showSectionals : null,
                    showOdds: m.showOdds != null ? m.showOdds : null,
                    totalPrizeMoney: m.totalPrizeMoney != null ? m.totalPrizeMoney : null,
                    state: m.state || (m.venue && m.venue.state) || null,
                    venue: m.venue ? {
                        id: m.venue.id,
                        name: m.venue.name,
                        nameAbbrev: m.venue.nameAbbrev || null,
                        slug: m.venue.slug,
                        state: m.venue.state,
                        isMetro: m.venue.isMetro != null ? m.venue.isMetro : null,
                        isClockWise: m.venue.isClockWise != null ? m.venue.isClockWise : null,
                        trackMapUrl: m.venue.trackMapUrl || null,
                        straight: m.venue.straight != null ? m.venue.straight : null,
                        straightUnit: m.venue.straightUnit || null,
                        circumference: m.venue.circumference != null ? m.venue.circumference : null,
                        circumferenceUnit: m.venue.circumferenceUnit || null,
                        address: m.venue.address || null,
                        weatherLastUpdated: m.venue.weatherLastUpdated || null,
                        country: m.venue.country ? {
                            id: m.venue.country.id,
                            name: m.venue.country.name,
                            iso2: m.venue.country.iso2,
                            iso3: m.venue.country.iso3,
                            horseCountry: m.venue.country.horseCountry || null
                        } : null
                    } : null,
                    events: (m.events || []).map(e => buildEvent(m, e))
                };
            }

            const mapped = allMeetings.map(buildMeeting);

            const groupsMap = new Map();
            for (const m of mapped) {
                const iso2 = m.venue && m.venue.country && m.venue.country.iso2;
                const group = iso2 === 'AU' ? 'Australia' : 'International';
                if (!groupsMap.has(group)) groupsMap.set(group, []);
                groupsMap.get(group).push(m);
            }

            const order = ['Australia', 'International'];
            const meetingsGrouped = order
                .filter(g => groupsMap.has(g))
                .map(g => ({ group: g, meetings: groupsMap.get(g) }));

            return JSON.stringify({ data: { meetingsGrouped } });
        """;

    /// <summary>
    /// Reads one race's Event entity (selections/jockeys/trainers/stats/last run all denormalized
    /// inline) out of Racenet's Nuxt 3 Apollo cache and normalizes it into the RaceDetail JSON
    /// shape. Only the single most-recent past run is available here (via selection.lastRun) —
    /// there is no separate multi-run form-history query on this page at all (confirmed live),
    /// the same limitation the sibling Punters scraper already documents and accepts for its own
    /// Nuxt 3 pages. Stats arrive as raw totalRuns/totalPlaces[]-style counters (not pre-formatted
    /// ratio strings the way Racenet's old Nuxt 2 feed shipped them).
    /// </summary>
    private const string ReadRaceDetailScript = """
            function diagnostics() {
                return `title="${document.title}" url="${location.href}" bodySnippet="${(document.body ? (document.body.innerText || '') : '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`;
            }

            // Try both locations Racenet has served this cache from (see DenormalizeJs's remarks
            // for the history) rather than assuming whichever one happened to work when this was
            // last checked live — confirmed live (2026-10-22): the #__nuxt.__vue_app__ path is
            // empty again and window.__NUXT__.apollo.defaultClient is what's actually populated.
            const nuxtEl = document.getElementById('__nuxt');
            const vueApp = nuxtEl && nuxtEl.__vue_app__;
            const nuxtCtx = vueApp && vueApp.config.globalProperties.$nuxt;
            const vueAppCache = nuxtCtx && nuxtCtx.payload && nuxtCtx.payload.data && nuxtCtx.payload.data['_apollo:default'];
            const windowNuxtCache = window.__NUXT__ && window.__NUXT__.apollo && window.__NUXT__.apollo.defaultClient;
            const cache = vueAppCache || windowNuxtCache;
            if (!cache) {
                return JSON.stringify({ error: 'NO_APOLLO_CACHE', diagnostics: diagnostics() });
            }

            const rootQuery = cache['ROOT_QUERY'];
            const eventKey = rootQuery && Object.keys(rootQuery).find(k => k.startsWith('event('));
            if (!eventKey || !rootQuery[eventKey]) {
                return JSON.stringify({ error: 'NO_EVENT', diagnostics: diagnostics() });
            }

            const event_ = denormalize(rootQuery[eventKey], cache, new Set());

            // Career/form stats and past-run history no longer live inline on the Selection
            // entity itself (sel.stats/sel.lastRun are both just plain absent on this feed, not
            // null-but-present) — Racenet split them out into two separate root-level queries,
            // 'stats(events:...)' (one entry per selection, by selectionId) and
            // 'competitorForms(selectionIds:...)' (one entry per NON-scratched selection, each
            // carrying up to 5 recent runs). Both are read here and joined back onto their
            // selection by id below in buildRunner, same as the page's own "Field"/"Form" tabs do.
            const statsRootKey = Object.keys(rootQuery).find(k => k.startsWith('stats('));
            const statsArray = statsRootKey ? (denormalize(rootQuery[statsRootKey], cache, new Set()) || []) : [];
            const statsBySelectionId = new Map(
                statsArray.filter(Boolean).map(s => [String(s.selectionId), s]));

            const formsRootKey = Object.keys(rootQuery).find(k => k.startsWith('competitorForms('));
            const formsArray = formsRootKey ? (denormalize(rootQuery[formsRootKey], cache, new Set()) || []) : [];
            const formsBySelectionId = new Map(
                formsArray.filter(Boolean).map(f => [String(f.selectionId), f.forms || []]));

            function slugify(s) {
                return (s || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
            }

            // 1 furlong = 201.168m, 1 furlong = 220 yards, matching the samples/*-DataDump.json
            // ground truth.
            function metersToDistanceInfo(m) {
                if (m == null) return { distanceM: null, distanceF: null, distanceMi: null };
                const furlongs = m / 201.168;
                let wholeFurlongs = Math.floor(furlongs);
                let yards = Math.round((furlongs - wholeFurlongs) * 220);
                if (yards >= 220) { yards -= 220; wholeFurlongs += 1; }
                return {
                    distanceM: m + 'm',
                    distanceF: furlongs.toFixed(1),
                    distanceMi: wholeFurlongs + 'f ' + yards + 'y'
                };
            }

            function kgToWeightInfo(kg) {
                if (kg == null) return { weightKg: null, weightImp: null, weightLbs: null };
                const lbsTotal = Math.round(Number(kg) * 2.20462);
                const stone = Math.floor(lbsTotal / 14);
                const remainder = lbsTotal % 14;
                return {
                    weightKg: String(kg),
                    weightImp: stone + '-' + remainder,
                    weightLbs: String(lbsTotal)
                };
            }

            function pct(v) { return v != null ? (v + '%') : null; }
            function money(v) { return v != null ? ('$' + v) : null; }
            function str(v) { return v != null ? String(v) : null; }

            // Each entry here is a SelectionResult from the 'competitorForms' root query (see the
            // lookup built above), unlike the old single inline sel.lastRun field this replaces —
            // it carries winner/second/third names, the jockey, and the race's own class/name
            // (via its nested selection.event) directly, richer than the old lastRun ever was.
            function mapPastRunFromCompetitorForm(entry) {
                if (!entry) return null;
                const ev = entry.selection && entry.selection.event;
                return {
                    type: 'RACE',
                    finishPosition: entry.finishPosition ?? null,
                    starters: entry.eventStarters ?? null,
                    raceNumber: entry.eventNumber ?? null,
                    course: entry.meetingName ?? null,
                    date: entry.meetingDate ?? null,
                    distance: entry.eventDistance != null ? (entry.eventDistance + 'm') : null,
                    raceName: (ev && (ev.name || ev.nameForm)) || null,
                    startingPrice: entry.startingWinPriceDecimal != null ? ('$' + entry.startingWinPriceDecimal) : null,
                    jockeyName: (entry.jockey && entry.jockey.name) || null,
                    winnerName: entry.winnerName ?? null,
                    secondName: entry.secondName ?? null,
                    thirdName: entry.thirdName ?? null,
                    lengthsBehind: entry.margin != null ? (entry.margin + 'L') : null,
                    trackCondition: (entry.trackCondition != null && entry.trackConditionRating != null)
                        ? (entry.trackCondition + ' ' + entry.trackConditionRating) : (entry.trackCondition ?? null),
                    eventClass: (ev && ev.eventClass) || null,
                    // Not exposed anywhere in this feed — this runner's OWN finish time isn't
                    // carried on the SelectionResult (only the race winner's winnerTime is), so
                    // there's nothing accurate to derive it from.
                    finishTime: null,
                    barrierPosition: entry.barrier ?? null,
                    runDetail: entry.stewardsReport || entry.videoComment || null,
                    raceResult: { horseName: null, position: entry.finishPosition ?? null }
                };
            }

            // Ratio fields here (career/lastYear/track/firstUp/... — see the full list below)
            // arrive as single pre-formatted "runs:win-place-show" strings now, not a separate
            // raw run-count field plus a places array the way the old feed split them — parseRatio
            // splits them back apart for RawStats' own raw int/List<int> fields, while
            // PerformanceStatistics' (string) fields use the ratio string as-is.
            function parseRatio(s) {
                if (typeof s !== 'string' || s.indexOf(':') === -1) return { runs: null, places: null };
                const [runsPart, placesPart] = s.split(':');
                const runs = parseInt(runsPart, 10);
                const places = (placesPart || '').split('-').map(n => parseInt(n, 10));
                return {
                    runs: Number.isFinite(runs) ? runs : null,
                    places: places.length && places.every(Number.isFinite) ? places : null
                };
            }

            function numOrNull(v) {
                const n = Number(v);
                return Number.isFinite(n) ? n : null;
            }

            function formatRatio(runs, places) {
                if (runs == null) return null;
                const p = places || [0, 0, 0];
                return runs + ':' + p.join('-');
            }

            // Racenet serves "stats" in two different shapes depending on discipline (confirmed
            // live): current horses pages give already-formatted "runs:win-place-show" ratio
            // strings per category via the separate stats(events:...) root query (see the lookup
            // built above, and statsBySelectionId's use in buildRunner below); greyhounds/harness
            // pages instead carry raw run-count + place-array pairs directly inline on the
            // Selection itself (sel.stats), the same shape horses used to use before this stopped
            // working for them specifically. Treating these as interchangeable by only reading one
            // shape's field names (an earlier version of this function only read the horses one)
            // silently zeroed out every stat for every other discipline — statPair reads whichever
            // one is actually present for a given category, so mapStats works for either.
            function statPair(ratioStr, rawRuns, rawPlaces) {
                if (typeof ratioStr === 'string') {
                    const parsed = parseRatio(ratioStr);
                    return { str: ratioStr, runs: parsed.runs, places: parsed.places };
                }
                if (rawRuns != null) {
                    return { str: formatRatio(rawRuns, rawPlaces), runs: rawRuns, places: rawPlaces ?? null };
                }
                return { str: null, runs: null, places: null };
            }

            // Field names below match Racenet's own RawStats/PerformanceStatistics DTO field-by-
            // field (see RaceDetail.cs) wherever either of this feed's two "stats" shapes kept the
            // old name — the couple that were renamed (or restructured from raw counts into a
            // combined ratio string) are called out below. The avgRating/avgEarlyPosition/
            // avgL800-L200 subscriber-only aggregates are only present in the horses shape
            // (confirmed live) — avgEarlyPosition in particular is a text label (e.g. "V. Slow"),
            // not a number, so it's only usable for PerformanceStatistics' string field, not
            // RawStats' decimal one (numOrNull naturally yields null for it).
            function mapStats(st) {
                if (!st) return { performanceStatistics: null, rawStats: null };

                // Horses wraps this array-typed field as {type:'json', json:[...]}; greyhounds/
                // harness give it as a plain array directly.
                const winRangeArr = Array.isArray(st.winRange) ? st.winRange : (st.winRange && st.winRange.json);
                const winRangeStr = Array.isArray(winRangeArr) && winRangeArr.length
                    ? winRangeArr.join('-') + 'm' : null;
                const placePer = pct(st.placePercentage);
                // trainerJockeyWin arrives as an already-computed percentage number in both
                // shapes, not raw runsByJockey/placesByJockey counts to derive a percentage from.
                const jockeyWinPer = pct(st.trainerJockeyWin);

                // Each pulls from whichever shape st actually has for that category — see
                // statPair's own remarks above. Greyhounds/harness use "Starts"/"Runs"
                // inconsistently across fields (secondUpStarts/thirdUpStarts vs firstUpRuns) and
                // one or two field names that don't quite match what they're paired with in
                // Racenet's own raw feed (dirtRuns pairs with dryPlaces there, not dirtPlaces;
                // synth is named runsBySynth/placesBySynth, not synthRun/synthPlaces) — handled
                // defensively below rather than assumed to always line up.
                const career = statPair(st.career, st.totalRuns, st.totalPlaces);
                const lastYear = statPair(st.lastYear, st.lastYearRuns, st.lastYearPlaces);
                const season = statPair(st.currentSeason, st.currentSeasonRuns, st.currentSeasonPlaces);
                const track = statPair(st.track, st.runsByTrack, st.placesByTrack);
                const distanceStat = statPair(st.distance, st.runsByDistance, st.placesByDistance);
                const trackDistance = statPair(st.trackDistance, st.runsByDistTrack, st.placesByDistTrack);
                // Racenet's feed only exposes one trainer+jockey combo ratio for greyhounds/
                // harness (runsByTrainerJockey/placesByTrainerJockey) rather than two distinct
                // ones — used for both JockeyHorse and TrainerJockey below since there's no
                // separate raw field for either; horses' own stats query does give a distinct
                // st.trainerJockey string, used here when present.
                const jockeyHorse = statPair(st.jockeyHorse, st.runsByTrainerJockey, st.placesByTrainerJockey);
                const trainerJockeyStat = statPair(st.trainerJockey, st.runsByTrainerJockey, st.placesByTrainerJockey);
                const firstUp = statPair(st.firstUp, st.firstUpRuns, st.firstUpPlaces);
                const secondUp = statPair(st.secondUp, st.secondUpStarts, st.secondUpPlaces);
                const thirdUp = statPair(st.thirdUp, st.thirdUpStarts, st.thirdUpPlaces);
                const firm = statPair(st.firm, st.firmRuns, st.firmPlaces);
                const good = statPair(st.good, st.goodRuns, st.goodPlaces);
                const soft = statPair(st.soft, st.softRuns, st.softPlaces);
                const heavy = statPair(st.heavy, st.heavyRuns, st.heavyPlaces);
                const wet = statPair(st.wet, st.wetRuns, st.wetPlaces);
                const turf = statPair(st.turf, st.runsByTurf, st.placesByTurf);
                const dirt = statPair(st.dirt, st.dirtRuns, st.dirtPlaces ?? st.dryPlaces);
                const dry = statPair(st.dry, st.dryRuns, st.dryPlaces);
                const synthetic = statPair(st.synthetic, st.synthRun ?? st.runsBySynth, st.synthPlaces ?? st.placesBySynth);
                const clockwise = statPair(st.clockwise, st.clockwiseRuns, st.clockwisePlaces);
                const antiClockwise = statPair(st.antiClockwise, st.aClockwiseRuns, st.aClockwisePlaces);
                const classStat = statPair(st.class, st.classRuns, st.classPlaces);
                const fav = statPair(st.fav, st.favRuns, st.favPlaces);
                const group1 = statPair(st.group1, st.group1Runs, st.group1Places);
                const group2 = statPair(st.group2, st.group2Runs, st.group2Places);
                const group3 = statPair(st.group3, st.group3Runs, st.group3Places);
                const listed = statPair(st.listed, st.listedRaceRuns, st.listedRacePlaces);
                const night = statPair(st.night, st.nightRuns, st.nightPlaces);

                return {
                    performanceStatistics: {
                        career: career.str,
                        winPer: pct(st.winPercentage),
                        placePer: placePer,
                        showPer: placePer,
                        last10Starts: st.lastTenFigure ?? null,
                        last12Months: lastYear.str,
                        season: season.str,
                        track: track.str,
                        distance: distanceStat.str,
                        trackDistanceCombo: trackDistance.str,
                        wetConditions: wet.str,
                        prizeMoney: money(st.totalPrizeMoney),
                        avgPrizeMoney: money(st.averagePrizeMoney),
                        winRange: winRangeStr,
                        rating: str(st.rating),
                        jockeyWinPer: jockeyWinPer,
                        jockeyHorse: jockeyHorse.str,
                        firstUp: firstUp.str,
                        secondUp: secondUp.str,
                        thirdUp: thirdUp.str,
                        firm: firm.str,
                        good: good.str,
                        soft: soft.str,
                        heavy: heavy.str,
                        synthetic: synthetic.str,
                        turf: turf.str,
                        dirt: dirt.str,
                        dry: dry.str,
                        clockwise: clockwise.str,
                        antiClockwise: antiClockwise.str,
                        class: classStat.str,
                        asFavourite: fav.str,
                        group1: group1.str,
                        group2: group2.str,
                        group3: group3.str,
                        listed: listed.str,
                        night: night.str,
                        trainerJockey: trainerJockeyStat.str,
                        roi: str(st.roi),
                        lastWin: st.lastWin || null,
                        daysSinceLastRun: st.daysSinceLastRun != null ? st.daysSinceLastRun : null,
                        avgRating: str(numOrNull(st.avgRT)),
                        avgEarlyPosition: st.avgEP || null,
                        avgL800: str(numOrNull(st.avgL800)),
                        avgL600: str(numOrNull(st.avgL600)),
                        avgL400: str(numOrNull(st.avgL400)),
                        avgL200: str(numOrNull(st.avgL200))
                    },
                    rawStats: {
                        rating: str(st.rating),
                        totalRuns: career.runs,
                        totalPlaces: career.places,
                        winPercentage: pct(st.winPercentage),
                        placePercentage: pct(st.placePercentage),
                        totalPrizeMoney: money(st.totalPrizeMoney),
                        averagePrizeMoney: money(st.averagePrizeMoney),
                        winRange: winRangeStr,
                        runsByJockey: jockeyHorse.runs,
                        placesByJockey: jockeyHorse.places,
                        firstUpRuns: firstUp.runs,
                        firstUpPlaces: firstUp.places,
                        secondUpStarts: secondUp.runs,
                        secondUpPlaces: secondUp.places,
                        thirdUpStarts: thirdUp.runs,
                        thirdUpPlaces: thirdUp.places,
                        lastYearRuns: lastYear.runs,
                        lastYearPlaces: lastYear.places,
                        runsByDistance: distanceStat.runs,
                        placesByDistance: distanceStat.places,
                        runsByTrack: track.runs,
                        placesByTrack: track.places,
                        runsByDistTrack: trackDistance.runs,
                        placesByDistTrack: trackDistance.places,
                        firmRuns: firm.runs,
                        firmPlaces: firm.places,
                        goodRuns: good.runs,
                        goodPlaces: good.places,
                        softRuns: soft.runs,
                        softPlaces: soft.places,
                        runsByTurf: turf.runs,
                        placesByTurf: turf.places,
                        wetRuns: wet.runs,
                        wetPlaces: wet.places,
                        heavyRuns: heavy.runs,
                        heavyPlaces: heavy.places,
                        synthRun: synthetic.runs,
                        synthPlaces: synthetic.places,
                        clockwiseRuns: clockwise.runs,
                        clockwisePlaces: clockwise.places,
                        currentSeasonRuns: season.runs,
                        currentSeasonPlaces: season.places,
                        aClockwiseRuns: antiClockwise.runs,
                        aClockwisePlaces: antiClockwise.places,
                        runsByClass: classStat.runs,
                        placesByClass: classStat.places,
                        dirtRuns: dirt.runs,
                        dirtPlaces: dirt.places,
                        dryRuns: dry.runs,
                        dryPlaces: dry.places,
                        favRuns: fav.runs,
                        favPlaces: fav.places,
                        group1Runs: group1.runs,
                        group1Places: group1.places,
                        group2Runs: group2.runs,
                        group2Places: group2.places,
                        group3Runs: group3.runs,
                        group3Places: group3.places,
                        listedRuns: listed.runs,
                        listedPlaces: listed.places,
                        nightRuns: night.runs,
                        nightPlaces: night.places,
                        trainerJockeyWinPercentage: st.trainerJockeyWin != null ? st.trainerJockeyWin : null,
                        roi: st.roi != null ? st.roi : null,
                        lastWin: st.lastWin || null,
                        daysSinceLastRun: st.daysSinceLastRun != null ? st.daysSinceLastRun : null,
                        avgRating: numOrNull(st.avgRT),
                        avgEarlyPosition: numOrNull(st.avgEP),
                        avgL800: numOrNull(st.avgL800),
                        avgL600: numOrNull(st.avgL600),
                        avgL400: numOrNull(st.avgL400),
                        avgL200: numOrNull(st.avgL200)
                    }
                };
            }

            // Greyhounds/harness carry a single past run directly inline on the Selection
            // (sel.lastRun — a SelectionResult, like horses' own competitorForms entries, just
            // without winner/second/third names or the jockey exposed). Horses' current pages
            // have no such inline field at all — see mapPastRunFromCompetitorForm and its use in
            // buildRunner below for that shape instead.
            function mapPastRunFromInlineLastRun(lr) {
                if (!lr) return null;
                return {
                    type: 'RACE',
                    finishPosition: lr.finishPosition ?? null,
                    starters: lr.eventStarters ?? null,
                    raceNumber: null,
                    course: lr.meetingName ?? null,
                    date: lr.meetingDate ?? null,
                    distance: lr.eventDistance != null ? (lr.eventDistance + 'm') : null,
                    raceName: lr.eventNameForm ?? null,
                    startingPrice: lr.startingWinPriceDecimal != null ? ('$' + lr.startingWinPriceDecimal) : null,
                    jockeyName: null,
                    winnerName: null,
                    secondName: null,
                    thirdName: null,
                    lengthsBehind: lr.margin != null ? (lr.margin + 'L') : null,
                    trackCondition: (lr.trackCondition != null && lr.trackConditionRating != null)
                        ? (lr.trackCondition + ' ' + lr.trackConditionRating) : (lr.trackCondition ?? null),
                    eventClass: null,
                    finishTime: lr.finishTime ?? null,
                    barrierPosition: lr.barrierRow ?? null,
                    runDetail: lr.stewardsReport ?? null,
                    raceResult: { horseName: null, position: lr.finishPosition ?? null }
                };
            }

            // dd/mm/yy (as rendered on the page) -> yyyy-MM-dd, matching the ISO dates the rest of
            // this script's date fields already use.
            function parseDmyToIso(s) {
                const m = /^(\d{2})\/(\d{2})\/(\d{2})$/.exec(s || '');
                if (!m) return s || null;
                return `20${m[3]}-${m[2]}-${m[1]}`;
            }

            // Reads one already-expanded "Recent Starts" row's fixed-position child elements
            // (confirmed live: the header's 8 direct children are always in this exact order —
            // position, margin, date, distance, condition, class/box, time, venue+race) into a
            // PastRun-shaped object. No API/GraphQL call involved — purely what's rendered.
            function parseRecentStartRow(row) {
                const header = row.querySelector('.recent-start-row__header');
                if (!header) return null;
                const c = header.children;
                const posWrap = c[0];
                const posBadge = posWrap ? posWrap.querySelector('.selection-badge span') : null;
                const finishPosition = posBadge ? parseInt(posBadge.textContent.replace(/\D/g, ''), 10) : null;
                const startersMatch = posWrap ? /of\s+(\d+)/.exec(posWrap.textContent) : null;
                const starters = startersMatch ? parseInt(startersMatch[1], 10) : null;
                const margin = c[1] ? c[1].textContent.trim() : null;
                const date = c[2] ? c[2].textContent.trim() : null;
                const distance = c[3] ? c[3].textContent.trim() : null;
                const condition = c[4] ? c[4].textContent.trim() : null;
                const classSpans = c[5] ? Array.from(c[5].querySelectorAll('span')).map(s => s.textContent.trim()) : [];
                const time = c[6] ? c[6].textContent.trim() : null;
                const venueRaceEl = c[7];
                const raceNumSpan = venueRaceEl ? venueRaceEl.querySelector('span') : null;
                const raceNumText = raceNumSpan ? raceNumSpan.textContent.trim() : null;
                const raceNumber = raceNumText ? parseInt(raceNumText.replace(/\D/g, ''), 10) : null;
                let venue = venueRaceEl ? venueRaceEl.textContent.trim() : null;
                if (venue && raceNumText) venue = venue.replace(raceNumText, '').trim();

                return {
                    type: 'RACE',
                    finishPosition: Number.isFinite(finishPosition) ? finishPosition : null,
                    starters: Number.isFinite(starters) ? starters : null,
                    raceNumber: Number.isFinite(raceNumber) ? raceNumber : null,
                    course: venue || null,
                    date: parseDmyToIso(date),
                    distance: distance || null,
                    raceName: null,
                    startingPrice: null,
                    jockeyName: null,
                    winnerName: null,
                    secondName: null,
                    thirdName: null,
                    lengthsBehind: margin || null,
                    trackCondition: condition || null,
                    eventClass: classSpans[0] || null,
                    finishTime: time || null,
                    barrierPosition: classSpans[1] || null,
                    runDetail: null,
                    raceResult: { horseName: null, position: Number.isFinite(finishPosition) ? finishPosition : null }
                };
            }

            // Greyhounds/harness race pages show each runner's single inline sel.lastRun by
            // default, but a "Recent Starts" panel with up to 5 runs is available per runner by
            // clicking that runner's own expand toggle — confirmed live, no API call, purely a UI
            // interaction this walks through one runner at a time (an accordion: opening one
            // closes any previously-open one, so this never has more than one panel open at once).
            // Horses pages have no such toggle at all, so this is a no-op for them (0 found below).
            async function scrapePastRunsFromDom() {
                const results = new Map();
                // The Apollo cache being populated (what the polling loop above already waited
                // for) doesn't mean Vue has actually painted these toggle elements into the DOM
                // yet (confirmed live) — poll for them the same way the rest of this script polls
                // for data, and re-query by index every time they're needed below rather than
                // keeping one static array: expanding a row re-renders the surrounding table,
                // which replaces (not reuses) every other row's DOM nodes, so a statically-
                // captured reference to any OTHER toggle goes stale the moment the first one opens.
                const queryToggles = () => Array.from(document.querySelectorAll('.form-guide-overview-selection__toggle'));
                let toggleCount = 0;
                for (let attempt = 0; attempt < 10; attempt++) {
                    toggleCount = queryToggles().length;
                    if (toggleCount) break;
                    await new Promise(r => setTimeout(r, 500));
                }

                // The expanded "Recent Starts" panel is a SINGLE shared element reused for
                // whichever row is currently open — only its data-selection-id attribute changes
                // between clicks, the element itself never disappears once first opened — so
                // checking just "does a matching container exist" would pass instantly on every
                // click after the first, reading the PREVIOUS runner's still-present (stale) data
                // before the new click's update had actually landed. Tracking the last-seen id and
                // waiting for it to actually change avoids that.
                async function waitForOpenPanel(expectedStaleId) {
                    for (let attempt = 0; attempt < 16; attempt++) {
                        await new Promise(r => setTimeout(r, 350));
                        const candidate = document.querySelector('.form-guide-overview-recent-starts[data-selection-id]');
                        const candidateSelId = candidate ? candidate.getAttribute('data-selection-id') : null;
                        if (candidate && candidateSelId !== expectedStaleId && candidate.querySelector('.recent-start-row')) {
                            // The rows render progressively (one by one) rather than all at once,
                            // so the first sighting here often only has 1 of the eventual ~5 rows
                            // in place — wait for the count to stop growing before reading.
                            let rowCount = candidate.querySelectorAll('.recent-start-row').length;
                            for (let settle = 0; settle < 8; settle++) {
                                await new Promise(r => setTimeout(r, 300));
                                const newCount = candidate.querySelectorAll('.recent-start-row').length;
                                if (newCount === rowCount) break;
                                rowCount = newCount;
                            }
                            return candidate;
                        }
                    }
                    return null;
                }

                // Closing isn't just a nicety between runners — if it silently fails (confirmed
                // live: a bare click() occasionally doesn't register, maybe mid-animation) the
                // NEXT runner's open attempt above waits for data-selection-id to change away from
                // a value that in fact never changed in the first place, and just times out with
                // nothing captured. Verifying the close actually took (and retrying it once more
                // if not) before moving on is what makes each runner's turn reliable rather than a
                // coin flip inherited from whichever runner came before it.
                async function closePanelAndVerify(toggleIndex, openSelId) {
                    for (let attempt = 0; attempt < 2; attempt++) {
                        const closeToggle = queryToggles()[toggleIndex];
                        if (closeToggle) closeToggle.click();
                        for (let check = 0; check < 6; check++) {
                            await new Promise(r => setTimeout(r, 250));
                            const candidate = document.querySelector('.form-guide-overview-recent-starts[data-selection-id]');
                            const candidateSelId = candidate ? candidate.getAttribute('data-selection-id') : null;
                            if (candidateSelId !== openSelId) return true;
                        }
                    }
                    return false;
                }

                let lastSeenSelId = null;
                for (let i = 0; i < toggleCount; i++) {
                    try {
                        const toggle = queryToggles()[i];
                        if (!toggle) continue;
                        // A toggle that isn't actually scrolled into view never opens its panel at
                        // all (a lazy-mount/visibility check on Racenet's own side, confirmed live).
                        toggle.scrollIntoView();
                        await new Promise(r => setTimeout(r, 300));
                        toggle.click();

                        const container = await waitForOpenPanel(lastSeenSelId);
                        if (!container) continue;

                        const selId = container.getAttribute('data-selection-id');
                        const runs = Array.from(container.querySelectorAll('.recent-start-row'))
                            .map(parseRecentStartRow)
                            .filter(Boolean);
                        if (runs.length) results.set(selId, runs);

                        // Leaving this runner's panel open and moving straight to the next toggle
                        // leaves the shared panel stuck on this runner forever — closing it first
                        // (clicking the same toggle again) is what lets the next one open cleanly.
                        const closed = await closePanelAndVerify(i, selId);
                        // lastSeenSelId only needs to track the id the NEXT open should treat as
                        // stale — if closing genuinely failed after both attempts, leaving it as
                        // this runner's id (rather than forcing it forward) is still correct: the
                        // next runner's own wait just keeps polling for a different id to appear.
                        lastSeenSelId = closed ? null : selId;
                    } catch (e) {
                        // One runner's panel failing to open/parse shouldn't lose the rest.
                    }
                }
                return results;
            }

            function buildRunner(sel) {
                const c = sel.competitor || {};
                // sel.stats (greyhounds/harness) vs the separate stats(events:...) query, keyed
                // by selection id (horses) — see mapStats' own remarks on the two shapes.
                const stEntry = sel.stats || statsBySelectionId.get(String(sel.id)) || null;
                const mapped = mapStats(stEntry);
                // Preference order: the DOM-scraped "Recent Starts" panel (up to 5 runs, greyhounds/
                // harness — see scrapePastRunsFromDom) > the separate competitorForms query (up to
                // 5 runs, horses) > the single inline sel.lastRun (greyhounds/harness fallback, if
                // that runner's toggle didn't open/parse for whatever reason).
                const domRuns = domPastRunsBySelectionId.get(String(sel.id));
                const pastRuns = (domRuns && domRuns.length) ? domRuns
                    : (formsBySelectionId.get(String(sel.id)) || []).length
                        ? formsBySelectionId.get(String(sel.id)).map(mapPastRunFromCompetitorForm).filter(Boolean)
                        : (sel.lastRun ? [mapPastRunFromInlineLastRun(sel.lastRun)].filter(Boolean) : []);

                const hasPointers = sel.prediction || sel.puntersEdge
                    || (sel.starRatings && sel.starRatings.length)
                    || (sel.selectionComments && sel.selectionComments.length);
                const pointers = hasPointers ? {
                    prediction: sel.prediction || null,
                    puntersEdge: sel.puntersEdge || null,
                    starRatings: sel.starRatings || null,
                    selectionComments: sel.selectionComments || null,
                    classChange: sel.classChange || null
                } : null;

                return {
                    runnerId: 'runner-' + (c.slug || slugify(c.name)),
                    tabNumber: sel.competitorNumber != null ? String(sel.competitorNumber) : null,
                    runnerName: c.name || null,
                    age: c.age != null ? String(c.age) : null,
                    sex: c.sexShort || null,
                    colour: c.colour || null,
                    sire: c.sire || null,
                    dam: c.dam || null,
                    horseCountry: c.country || null,
                    draw: sel.barrierNumber || null,
                    barrierPosition: sel.barrierNumber || null,
                    comment: sel.comments || null,
                    // Unlike the old Nuxt 2 feed, competitor.racingColours is now a real free-text
                    // silk description (confirmed live) — used directly when present, falling
                    // back to the image-based SilkSvgDescriber (see ScrapeRaceAsync) only when
                    // it isn't.
                    silkColourText: c.racingColours || null,
                    silkImageUrl: sel.silkImageUrl || null,
                    gearChanges: sel.gearChanges || null,
                    jockey: sel.jockey ? { id: sel.jockey.id, name: sel.jockey.name, slug: sel.jockey.slug } : null,
                    trainer: sel.trainer ? { id: sel.trainer.id, name: sel.trainer.name, slug: sel.trainer.slug } : null,
                    carryingWeight: kgToWeightInfo(sel.weight),
                    lastRun: pastRuns.length ? pastRuns[0].date : null,
                    // The stats query's own "lastRun" is a short human-readable summary (e.g.
                    // "5th/10 1400m 2 Weeks GTON"), distinct from pastRuns[0] (the structured
                    // SelectionResult from the separate competitorForms query above).
                    lastRunComment: (stEntry && stEntry.lastRun) ? stEntry.lastRun.trim() : null,
                    pastRuns: pastRuns,
                    performanceStatistics: mapped.performanceStatistics,
                    rawStats: mapped.rawStats,
                    pointers: pointers,
                    currentOdds: sel.startingPrice != null ? ('$' + sel.startingPrice) : null,
                    isScratched: sel.status === 'SCRATCHED' || sel.status === 'SCR' || sel.status === 'WDN'
                };
            }

            const selectionsKey = Object.keys(event_).find(k => k.startsWith('selections('));
            const selections = selectionsKey ? (event_[selectionsKey] || []) : (event_.selections || []);
            const domPastRunsBySelectionId = await scrapePastRunsFromDom();
            const runners = selections.map(buildRunner);

            // Racenet's event entity no longer carries weather at all under Nuxt 3 (confirmed
            // live — absent from the Event type entirely, unlike the old Nuxt 2 feed), so this
            // is always null now; kept defensive rather than removed in case a future response
            // ever reintroduces it under a different field.
            function cleanWeatherField(s, prefix) {
                if (!s) return null;
                return s.toLowerCase().startsWith(prefix.toLowerCase()) ? s.slice(prefix.length).trim() : s;
            }
            const w = event_.weather;
            const weather = w ? {
                condition: w.condition || null,
                temperature: w.temperature != null ? (w.temperature + (w.temperatureUnits || '')) : null,
                wind: cleanWeatherField(w.wind, 'Wind:'),
                humidity: cleanWeatherField(w.humidity, 'Humidity:')
            } : null;

            return JSON.stringify({
                eventMeta: {
                    racePrizeMoney: event_.racePrizeMoney != null ? event_.racePrizeMoney : null,
                    racePrizeMoneyUnit: event_.racePrizeMoneyUnit || 'AUD',
                    prizeMoney: event_.prizeMoney || [],
                    starters: event_.starters != null ? event_.starters : null,
                    resultState: event_.resultState || null,
                    placeWinners: event_.placeWinners != null ? event_.placeWinners : null,
                    weather: weather
                },
                raceDetail: {
                    meetingId: args.meetingId || null,
                    meetingName: args.meetingName ? String(args.meetingName).toUpperCase() : null,
                    country: args.country || null,
                    date: args.date || null,
                    raceId: event_.id,
                    slug: event_.slug,
                    raceNumber: event_.eventNumber,
                    raceDistance: metersToDistanceInfo(event_.distance),
                    runners: runners
                }
            });
        """;

    public async ValueTask DisposeAsync()
    {
        if (_sessionPage is { IsClosed: false }) await _sessionPage.CloseAsync();

        if (_cdpBrowser is not null)
        {
            try { await _cdpBrowser.CloseAsync(); } catch { /* already gone */ }
        }
        else
        {
            if (_context is not null) await _context.CloseAsync();
            if (_browser is not null) await _browser.CloseAsync();
        }

        // The CDP path's browser process wasn't launched by Playwright, so it won't be cleaned
        // up automatically — kill it ourselves.
        try
        {
            if (_browserProcess is { HasExited: false })
            {
                _browserProcess.Kill(entireProcessTree: true);
                _browserProcess.WaitForExit(5_000);
            }
        }
        catch { /* already exited */ }
        _browserProcess?.Dispose();

        _playwright?.Dispose();
    }
}
