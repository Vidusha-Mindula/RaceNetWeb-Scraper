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

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _sessionPage;
    private int _navigationTimeoutMs = 45_000;
    private int _settleDelayMs = 1500;

    public async Task InitializeAsync(ScraperOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScraperOptions();

        var args = new List<string> { "--disable-blink-features=AutomationControlled" };
        if (!options.Headless && options.HideWindow)
        {
            args.Add("--window-position=-32000,-32000");
            args.Add("--disable-backgrounding-occluded-windows");
            args.Add("--disable-renderer-backgrounding");
            args.Add("--disable-background-timer-throttling");
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Args = args
        });

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
            Locale = "en-AU",
            TimezoneId = "Australia/Sydney"
        });

        await _context.AddInitScriptAsync(
            "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");

        _navigationTimeoutMs = options.NavigationTimeoutMs;
        _settleDelayMs = options.SettleDelayMs;
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

    private async Task<IPage> EnsurePageAsync()
    {
        if (_context is null)
            throw new InvalidOperationException($"Call {nameof(InitializeAsync)} before scraping.");

        if (_sessionPage is { IsClosed: false })
            return _sessionPage;

        _sessionPage = await _context.NewPageAsync();
        return _sessionPage;
    }

    private async Task NavigateAsync(IPage page, string url, CancellationToken cancellationToken)
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
        await page.WaitForTimeoutAsync(_settleDelayMs);
    }

    private static string Snippet(string s) =>
        string.IsNullOrEmpty(s) ? "(empty response)"
            : Regex.Replace(s, @"\s+", " ").Trim() is var flat && flat.Length <= 300 ? flat : flat[..300] + "...";

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
        await NavigateAsync(page, url, cancellationToken);

        // "Today" here must be Sydney's calendar day (the browser context's TimezoneId, set in
        // InitializeAsync), not the host machine's local date — Racenet's date tabs ("Tomorrow",
        // weekday names, ...) are labeled relative to Sydney's clock, and a scraper running in any
        // other timezone can otherwise land a day off, and so on the wrong tab entirely.
        var todayStr = await page.EvaluateAsync<string>("""
            () => {
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
        await NavigateAsync(page, url, cancellationToken);

        string raw;
        try
        {
            raw = await page.EvaluateAsync<string>("(args) => {" + DenormalizeJs + ReadRaceDetailScript + "}", new
            {
                eventId = raceEvent.Id,
                meetingId = meeting.Id,
                meetingName = meeting.Name,
                country = meeting.Venue?.Country?.Iso3,
                date = meeting.MeetingDateLocal
            });
        }
        catch (Exception ex)
        {
            throw new RaceNetScrapeException($"Reading race detail from {url} failed: {ex.Message}", ex);
        }

        using var envelope = JsonDocument.Parse(raw);
        var root = envelope.RootElement;

        if (root.TryGetProperty("error", out _))
        {
            var diagnostics = root.TryGetProperty("diagnostics", out var dg) ? dg.GetString() : "";
            throw new RaceNetScrapeException(
                $"Racenet returned no event data for race id {raceEvent.Id} ({meeting.Name} R{raceEvent.EventNumber}) " +
                $"at {url}. {diagnostics}");
        }

        if (root.TryGetProperty("eventMeta", out var eventMeta))
            BackfillRaceEvent(raceEvent, eventMeta);

        if (!root.TryGetProperty("raceDetail", out var raceDetailEl))
        {
            throw new RaceNetScrapeException(
                $"Racenet's page had no race detail for {raceEvent.Id}. Response: {Snippet(raw)}");
        }

        var detail = JsonSerializer.Deserialize<RaceDetail>(raceDetailEl.GetRawText(), ScraperJsonOptions.Deserialize)
            ?? throw new RaceNetScrapeException("Could not parse race detail (empty result).");

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
    /// Walks Nuxt 2's normalized Apollo cache (`window.__NUXT__.apollo.defaultClient`) and
    /// resolves every `{type:'id', id, typename}` reference into the actual entity it points at,
    /// recursively — the same denormalization Apollo's own InMemoryCache does internally, just
    /// re-implemented in plain JS since we only have the raw cache map to work with here.
    /// </summary>
    private const string DenormalizeJs = """
        function denormalize(value, cache, seen) {
            if (value === null || value === undefined) return value;
            if (Array.isArray(value)) return value.map(v => denormalize(v, cache, seen));
            if (typeof value !== 'object') return value;
            if (value.type === 'id' && typeof value.id === 'string') {
                if (seen.has(value.id)) return null;
                const entity = cache[value.id];
                if (!entity) return null;
                seen.add(value.id);
                const result = denormalize(entity, cache, seen);
                seen.delete(value.id);
                return result;
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
    /// Reads one race's Event entity plus its ROOT_QUERY.stats(...)/ROOT_QUERY.competitorForms(...)
    /// entries (each keyed by selectionId) out of the page's own embedded cache, and normalizes
    /// them into the RaceDetail JSON shape — runners, jockeys, trainers, stats and up to 5 past
    /// runs per runner, all resolved in this single page already (no extra queries needed, unlike
    /// the old two-step "event query, then a follow-up form-history query" design).
    /// </summary>
    private const string ReadRaceDetailScript = """
            const apollo = (window.__NUXT__ || {}).apollo || {};
            const dc = apollo.defaultClient || {};
            const eventKey = 'Event:' + args.eventId;

            if (!dc[eventKey]) {
                return JSON.stringify({
                    error: 'NO_EVENT',
                    diagnostics: `title="${document.title}" url="${location.href}" bodySnippet="${(document.body ? (document.body.innerText || '') : '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`
                });
            }

            const event_ = denormalize(dc[eventKey], dc, new Set());

            const statsBySelectionId = {};
            for (const k of Object.keys(dc).filter(k => k.startsWith('ROOT_QUERY.stats('))) {
                const entry = denormalize(dc[k], dc, new Set());
                if (entry && entry.selectionId) statsBySelectionId[entry.selectionId] = entry;
            }
            const formsBySelectionId = {};
            for (const k of Object.keys(dc).filter(k => k.startsWith('ROOT_QUERY.competitorForms('))) {
                const entry = denormalize(dc[k], dc, new Set());
                if (entry && entry.selectionId) formsBySelectionId[entry.selectionId] = entry;
            }

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

            // Racenet's stats feed hands back pre-formatted "runs:place-place-place" ratio
            // strings directly (e.g. "7:1-0-1") rather than separate run/place counts, so this
            // pulls the raw numbers back out for RawStats' more granular int/list fields.
            function parseRatio(s) {
                if (!s || typeof s !== 'string' || s.indexOf(':') === -1) return { runs: null, places: null };
                const [runsPart, placesPart] = s.split(':');
                const runs = parseInt(runsPart, 10);
                const places = placesPart ? placesPart.split('-').map(n => parseInt(n, 10)) : null;
                return { runs: isNaN(runs) ? null : runs, places };
            }

            function pct(v) { return v != null ? (v + '%') : null; }
            function money(v) { return v != null ? ('$' + v) : null; }
            function str(v) { return v != null ? String(v) : null; }

            function mapStats(st) {
                if (!st) return { performanceStatistics: null, rawStats: null };

                const winRangeStr = (st.winRange && st.winRange.json && st.winRange.json.length)
                    ? st.winRange.json.join('-') + 'm' : null;
                const career = parseRatio(st.career);
                const jockeyHorse = parseRatio(st.jockeyHorse);
                const jockeyWinPer = (jockeyHorse.runs && jockeyHorse.places)
                    ? pct(Math.round((jockeyHorse.places[0] / jockeyHorse.runs) * 100)) : null;

                return {
                    performanceStatistics: {
                        career: st.career || null,
                        winPer: pct(st.winPercentage),
                        placePer: pct(st.placePercentage),
                        showPer: pct(st.placePercentage),
                        last10Starts: st.lastTenFigure || null,
                        last12Months: st.lastYear || null,
                        season: st.currentSeason || null,
                        track: st.track || null,
                        distance: st.distance || null,
                        trackDistanceCombo: st.trackDistance || null,
                        wetConditions: st.wet || null,
                        prizeMoney: money(st.totalPrizeMoney),
                        avgPrizeMoney: money(st.averagePrizeMoney),
                        winRange: winRangeStr,
                        rating: str(st.rating),
                        jockeyWinPer: jockeyWinPer,
                        jockeyHorse: st.jockeyHorse || null,
                        firstUp: st.firstUp || null,
                        secondUp: st.secondUp || null,
                        thirdUp: st.thirdUp || null,
                        firm: st.firm || null,
                        good: st.good || null,
                        soft: st.soft || null,
                        heavy: st.heavy || null,
                        synthetic: st.synthetic || null,
                        turf: st.turf || null,
                        dirt: st.dirt || null,
                        dry: st.dry || null,
                        clockwise: st.clockwise || null,
                        antiClockwise: st.antiClockwise || null,
                        class: st.class || null,
                        asFavourite: st.fav || null,
                        group1: st.group1 || null,
                        group2: st.group2 || null,
                        group3: st.group3 || null,
                        listed: st.listed || null,
                        night: st.night || null,
                        trainerJockey: st.trainerJockey || null,
                        roi: st.roi != null ? String(st.roi) : null,
                        lastWin: st.lastWin || null,
                        daysSinceLastRun: st.daysSinceLastRun != null ? st.daysSinceLastRun : null,
                        avgRating: st.avgRT != null ? String(st.avgRT) : null,
                        avgEarlyPosition: st.avgEP || null,
                        avgL800: st.avgL800 != null ? String(st.avgL800) : null,
                        avgL600: st.avgL600 != null ? String(st.avgL600) : null,
                        avgL400: st.avgL400 != null ? String(st.avgL400) : null,
                        avgL200: st.avgL200 != null ? String(st.avgL200) : null
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
                        firstUpRuns: parseRatio(st.firstUp).runs,
                        firstUpPlaces: parseRatio(st.firstUp).places,
                        secondUpStarts: parseRatio(st.secondUp).runs,
                        secondUpPlaces: parseRatio(st.secondUp).places,
                        thirdUpStarts: parseRatio(st.thirdUp).runs,
                        thirdUpPlaces: parseRatio(st.thirdUp).places,
                        lastYearRuns: parseRatio(st.lastYear).runs,
                        lastYearPlaces: parseRatio(st.lastYear).places,
                        runsByDistance: parseRatio(st.distance).runs,
                        placesByDistance: parseRatio(st.distance).places,
                        runsByTrack: parseRatio(st.track).runs,
                        placesByTrack: parseRatio(st.track).places,
                        runsByDistTrack: parseRatio(st.trackDistance).runs,
                        placesByDistTrack: parseRatio(st.trackDistance).places,
                        firmRuns: parseRatio(st.firm).runs,
                        firmPlaces: parseRatio(st.firm).places,
                        goodRuns: parseRatio(st.good).runs,
                        goodPlaces: parseRatio(st.good).places,
                        softRuns: parseRatio(st.soft).runs,
                        softPlaces: parseRatio(st.soft).places,
                        runsByTurf: parseRatio(st.turf).runs,
                        placesByTurf: parseRatio(st.turf).places,
                        wetRuns: parseRatio(st.wet).runs,
                        wetPlaces: parseRatio(st.wet).places,
                        heavyRuns: parseRatio(st.heavy).runs,
                        heavyPlaces: parseRatio(st.heavy).places,
                        synthRun: parseRatio(st.synthetic).runs,
                        synthPlaces: parseRatio(st.synthetic).places,
                        clockwiseRuns: parseRatio(st.clockwise).runs,
                        clockwisePlaces: parseRatio(st.clockwise).places,
                        currentSeasonRuns: parseRatio(st.currentSeason).runs,
                        currentSeasonPlaces: parseRatio(st.currentSeason).places,
                        aClockwiseRuns: parseRatio(st.antiClockwise).runs,
                        aClockwisePlaces: parseRatio(st.antiClockwise).places,
                        runsByClass: parseRatio(st.class).runs,
                        placesByClass: parseRatio(st.class).places,
                        dirtRuns: parseRatio(st.dirt).runs,
                        dirtPlaces: parseRatio(st.dirt).places,
                        dryRuns: parseRatio(st.dry).runs,
                        dryPlaces: parseRatio(st.dry).places,
                        favRuns: parseRatio(st.fav).runs,
                        favPlaces: parseRatio(st.fav).places,
                        group1Runs: parseRatio(st.group1).runs,
                        group1Places: parseRatio(st.group1).places,
                        group2Runs: parseRatio(st.group2).runs,
                        group2Places: parseRatio(st.group2).places,
                        group3Runs: parseRatio(st.group3).runs,
                        group3Places: parseRatio(st.group3).places,
                        listedRuns: parseRatio(st.listed).runs,
                        listedPlaces: parseRatio(st.listed).places,
                        nightRuns: parseRatio(st.night).runs,
                        nightPlaces: parseRatio(st.night).places,
                        trainerJockeyWinPercentage: st.trainerJockeyWin != null ? st.trainerJockeyWin : null,
                        roi: st.roi != null ? st.roi : null,
                        lastWin: st.lastWin || null,
                        daysSinceLastRun: st.daysSinceLastRun != null ? st.daysSinceLastRun : null,
                        avgRating: st.avgRT != null ? st.avgRT : null,
                        avgEarlyPosition: null,
                        avgL800: st.avgL800 != null ? st.avgL800 : null,
                        avgL600: st.avgL600 != null ? st.avgL600 : null,
                        avgL400: st.avgL400 != null ? st.avgL400 : null,
                        avgL200: st.avgL200 != null ? st.avgL200 : null
                    }
                };
            }

            // Racenet's competitorForms feed carries the runner's own margin behind the winner
            // (0 if it won) plus the winner/second/third names directly — no regex-scraping a
            // free-text summary needed here, unlike the old fetch-based feed.
            function mapPastRunFromForm(f) {
                const trackCondition = (f.trackCondition != null && f.trackConditionRating != null)
                    ? (f.trackCondition + ' ' + f.trackConditionRating) : (f.trackCondition || null);

                return {
                    type: f.isTrial ? 'TRIAL' : 'RACE',
                    finishPosition: f.finishPosition != null ? f.finishPosition : null,
                    starters: f.eventStarters != null ? f.eventStarters : null,
                    raceNumber: f.eventNumber != null ? f.eventNumber : null,
                    course: f.meetingName || null,
                    date: f.meetingDate || null,
                    distance: f.eventDistance != null ? (f.eventDistance + 'm') : null,
                    raceName: null,
                    startingPrice: f.startingWinPriceDecimal != null ? ('$' + f.startingWinPriceDecimal) : null,
                    jockeyName: null,
                    winnerName: f.winnerName || null,
                    secondName: f.secondName || null,
                    thirdName: f.thirdName || null,
                    lengthsBehind: f.margin != null ? (f.margin + 'L') : null,
                    trackCondition: trackCondition,
                    eventClass: null,
                    finishTime: null,
                    barrierPosition: f.barrier || null,
                    runDetail: f.videoComment || f.videoNote || null,
                    raceResult: { horseName: null, position: f.finishPosition != null ? f.finishPosition : null }
                };
            }

            function buildRunner(sel) {
                const c = sel.competitor || {};
                const statsEntry = statsBySelectionId[sel.id];
                const formsEntry = formsBySelectionId[sel.id];
                const mapped = mapStats(statsEntry);
                const pastRuns = (formsEntry && formsEntry.forms ? formsEntry.forms : [])
                    .map(mapPastRunFromForm).slice(0, 5);

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
                    // No free-text racing-colours field exists on this data source at all — always
                    // left null so the caller's SilkSvgDescriber image-based fallback fires.
                    silkColourText: null,
                    silkImageUrl: sel.silkImageUrl || null,
                    gearChanges: sel.gearChanges || null,
                    jockey: sel.jockey ? { id: sel.jockey.id, name: sel.jockey.name, slug: sel.jockey.slug } : null,
                    trainer: sel.trainer ? { id: sel.trainer.id, name: sel.trainer.name, slug: sel.trainer.slug } : null,
                    carryingWeight: kgToWeightInfo(sel.weight),
                    lastRun: pastRuns.length ? pastRuns[0].date : null,
                    lastRunComment: null,
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
            const runners = selections.map(buildRunner);

            return JSON.stringify({
                eventMeta: {
                    racePrizeMoney: event_.racePrizeMoney != null ? event_.racePrizeMoney : null,
                    racePrizeMoneyUnit: event_.racePrizeMoneyUnit || 'AUD',
                    prizeMoney: event_.prizeMoney || [],
                    starters: event_.starters != null ? event_.starters : null,
                    resultState: event_.resultState || null,
                    placeWinners: event_.placeWinners != null ? event_.placeWinners : null
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
        if (_context is not null) await _context.CloseAsync();
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
