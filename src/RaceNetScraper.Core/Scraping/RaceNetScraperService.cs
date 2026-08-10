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
/// Racenet and Punters are separate front-ends over the same racing GraphQL backend, which is the
/// whole reason the two scrapers can emit identical JSON: the field names this reads
/// (selection.stats.*, selection.lastRun.*, competitor.*, event.prizeMoney[], ...) are the same
/// ones the Punters scraper reads out of Punters' Apollo cache, so the mapping below is shared
/// with it essentially verbatim.
///
/// How it gets the data, and why:
///   - Racenet's API (api.racenet.com.au/racing) sits behind CloudFront and rejects requests that
///     don't come from a real browser session, so a real Chromium is launched via Playwright and
///     pointed at Racenet's own form guide once. That single navigation establishes the cookies
///     and origin the API wants.
///   - From there every query is issued as a plain fetch() inside that page. This is the one
///     substantive difference from the Punters scraper, which could NOT do this (Punters' API
///     rejects an independently injected request even from inside its own page, so that scraper
///     has to drive the real UI and read back whatever the site itself resolved). Racenet's API
///     accepts it, which makes this engine markedly simpler and removes the Punters version's
///     two big limitations: it is not restricted to the handful of dates the site's own tabs
///     expose, and it doesn't have to scroll the page to provoke lazy per-runner form requests.
///   - Query documents are sent in full rather than by persisted-query hash, deliberately —
///     see <see cref="RaceNetGraphQl"/> for why (the hash registry differs per brand and rotates).
///
/// A few data notes worth keeping in mind:
///   - Racenet's meeting-list query returns per-event racePrizeMoneyValue/racePrizeMoneyUnit: the
///     race's prize money in its OWN native currency (e.g. GBP for a UK meeting). The true AUD
///     figure lives on the race's own event(...) query, so <see cref="ScrapeRaceAsync"/> overwrites
///     RacePrizeMoney/RacePrizeMoneyUnit/PrizeMoney in place with the AUD total and per-place
///     breakdown once a race has been scraped — matching what the downstream ingester expects
///     (always AUD), with no external FX lookup involved. Identical semantics to the Punters side.
///   - Racenet returns ONE weather object per meeting (not per event), so that single value is
///     copied onto every event when building the meeting export — a meeting is one place on one
///     day, so it's a fair proxy.
///   - Meetings are returned as a flat list; the two-tier Australia/International grouping the
///     ingester expects is inferred here from venue.country.iso2.
///   - meetingCategory is "Professional" only, which is what Racenet's own form guide shows.
///     Adding "Trial" also pulls in barrier-trial meetings (slugs ending "-bt") — see
///     <see cref="MeetingCategories"/> if that's ever wanted.
///   - For meetings/races Racenet hasn't finished processing, prize money breakdown and
///     historical form/stats for a runner are simply absent from the API.
/// </summary>
public sealed class RaceNetScraperService : IRaceNetScraperService
{
    private const string BaseUrl = "https://www.racenet.com.au";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>
    /// Region IDs to include. includeRegions:[] does NOT mean "all regions" — it silently
    /// restricts to Australia (confirmed empirically on this backend), so the full set has to be
    /// listed. This is the union of the IDs confirmed in production use: AU/NZ plus US(652),
    /// Canada(653) and the other international regions. A regionId is a geography, not a sport,
    /// so the same set applies to all three disciplines.
    /// </summary>
    private static readonly int[] IncludeRegions = { 650, 651, 652, 653, 639, 673, 642, 647, 641 };

    /// <summary>Matches what Racenet's own form guide lists. Add "Trial" to also pick up
    /// barrier-trial meetings.</summary>
    private static readonly string[] MeetingCategories = { "Professional" };

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

            // Chromium treats an off-screen window as occluded and throttles it like a background
            // tab (reduced timers, skipped rendering). This engine issues its queries via fetch()
            // rather than relying on the page's own lazy loading, so it's far less exposed to that
            // than the Punters scraper was — but a throttled renderer can still stall an in-flight
            // fetch, so the same flags are applied. They change nothing JS-visible on the page.
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
    /// The GraphQL "sport" enum value per discipline. These line up with the sportId the API
    /// returns on each meeting (1 / 21 / 22), which is also what the <see cref="Discipline"/>
    /// enum's own values are.
    /// </summary>
    private static string SportEnum(Discipline discipline) => discipline switch
    {
        Discipline.Horses => "HorseRacing",
        Discipline.Greyhounds => "GreyhoundRacing",
        Discipline.Harness => "HarnessRacing",
        _ => throw new ArgumentOutOfRangeException(nameof(discipline), discipline, null)
    };

    /// <summary>
    /// Racenet's own form-guide URL per discipline, used only to warm up a session. Horses is
    /// deliberately the default fallback: any Racenet page establishes the cookies/origin the API
    /// wants, and the horse-racing form guide is the one URL confirmed stable.
    /// </summary>
    private static string FormGuidePath(Discipline discipline) => discipline switch
    {
        Discipline.Horses => "horse-racing",
        Discipline.Greyhounds => "greyhounds",
        Discipline.Harness => "harness",
        _ => "horse-racing"
    };

    /// <summary>
    /// Opens (once) a real Racenet page to sit on for the rest of the session. Every GraphQL call
    /// is a fetch() from inside this page, so its cookies/origin are what get the request past
    /// CloudFront. Kept alive for the whole scrape rather than per-call, so the cookie handshake
    /// is paid once instead of per query.
    /// </summary>
    private async Task<IPage> EnsureSessionPageAsync(Discipline discipline, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_context is null)
            throw new InvalidOperationException($"Call {nameof(InitializeAsync)} before scraping.");

        if (_sessionPage is { IsClosed: false })
            return _sessionPage;

        var url = $"{BaseUrl}/form-guide/{FormGuidePath(discipline)}";
        progress?.Report($"[R-{discipline.Code()}] Establishing a Racenet session via {url} ...");

        var page = await _context.NewPageAsync();
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
            await page.CloseAsync();
            throw new RaceNetScrapeException(
                $"Could not load {url} to establish a Racenet session: {ex.Message}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await page.WaitForTimeoutAsync(_settleDelayMs);

        _sessionPage = page;
        return page;
    }

    /// <summary>
    /// Issues one GraphQL operation from inside the warmed-up Racenet page and returns the raw
    /// response body. Both the JSON content-type and the x-apollo-operation-name header are sent
    /// because Apollo's CSRF guard rejects a request carrying neither.
    /// </summary>
    private async Task<JsonDocument> GraphQlAsync(
        IPage page, string operationName, string variablesJson, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string script = """
            async (req) => {
                try {
                    const r = await fetch(req.apiUrl, {
                        method: 'POST',
                        headers: {
                            'content-type': 'application/json',
                            'authorization': 'Bearer none',
                            'x-apollo-operation-name': req.op
                        },
                        credentials: 'include',
                        body: JSON.stringify({
                            operationName: req.op,
                            variables: JSON.parse(req.variables),
                            query: req.query
                        })
                    });
                    return JSON.stringify({ status: r.status, body: await r.text() });
                } catch (e) {
                    // A hard fetch failure (offline, blocked, page torn down) has no HTTP status
                    // to report, so pass back what the page looked like at the time instead —
                    // that's what distinguishes "bot-detection served us an interstitial" from
                    // "the network is down".
                    return JSON.stringify({
                        status: 0,
                        fetchError: String((e && e.message) || e),
                        diagnostics: `title="${document.title}" url="${location.href}" bodySnippet="${(document.body ? (document.body.innerText || '') : '').replace(/\s+/g, ' ').trim().slice(0, 200)}"`
                    });
                }
            }
            """;

        var raw = await page.EvaluateAsync<string>(script, new
        {
            apiUrl = RaceNetGraphQl.ApiUrl,
            op = operationName,
            variables = variablesJson,
            query
        });

        using var envelope = JsonDocument.Parse(raw);
        var root = envelope.RootElement;
        var status = root.GetProperty("status").GetInt32();

        if (status == 0)
        {
            var fetchError = root.TryGetProperty("fetchError", out var fe) ? fe.GetString() : "unknown error";
            var diagnostics = root.TryGetProperty("diagnostics", out var dg) ? dg.GetString() : "";
            throw new RaceNetScrapeException(
                $"'{operationName}' could not reach {RaceNetGraphQl.ApiUrl}: {fetchError}. {diagnostics}");
        }

        var body = root.GetProperty("body").GetString() ?? "";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // A non-JSON body is the signature of a CloudFront block page or bot-detection
            // interstitial rather than a real API error, so surface a snippet of it verbatim.
            throw new RaceNetScrapeException(
                $"'{operationName}' returned HTTP {status} with a non-JSON body (likely a block/challenge page): " +
                Snippet(body), ex);
        }

        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                .Where(m => !string.IsNullOrEmpty(m));

            doc.Dispose();
            throw new RaceNetScrapeException(
                $"'{operationName}' was rejected by {RaceNetGraphQl.ApiUrl} (HTTP {status}): " +
                string.Join(" | ", messages));
        }

        if (status != 200)
        {
            doc.Dispose();
            throw new RaceNetScrapeException(
                $"'{operationName}' returned HTTP {status} from {RaceNetGraphQl.ApiUrl}: {Snippet(body)}");
        }

        return doc;
    }

    private static string Snippet(string s) =>
        string.IsNullOrEmpty(s) ? "(empty response)"
            : Regex.Replace(s, @"\s+", " ").Trim() is var flat && flat.Length <= 300 ? flat : flat[..300] + "...";

    public async Task<ScrapeResult> ScrapeMeetingsAsync(
        Discipline discipline,
        DateOnly startDate,
        DateOnly? endDate = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveEnd = endDate ?? startDate;
        if (effectiveEnd < startDate)
        {
            throw new RaceNetScrapeException(
                $"endDate ({effectiveEnd:yyyy-MM-dd}) is before startDate ({startDate:yyyy-MM-dd}).");
        }

        var page = await EnsureSessionPageAsync(discipline, progress, cancellationToken);

        // The API's "meetings on date X" window is the AEST calendar day for X expressed in UTC
        // (AEST = UTC+10), NOT a naive UTC midnight-to-midnight — getting this wrong shifts the
        // whole result set by a day. Note that international meetings legitimately carry the
        // PREVIOUS calendar date inside an AU day's window (a UK twilight meeting on the 24th
        // falls inside AU's 25th), which is the same behaviour the Punters exports show.
        var startTime = startDate.AddDays(-1).ToString("yyyy-MM-dd") + "T14:00:00.000Z";
        var endTime = effectiveEnd.ToString("yyyy-MM-dd") + "T12:59:59.999Z";

        var variables = JsonSerializer.Serialize(new
        {
            startTime,
            endTime,
            sport = SportEnum(discipline),
            meetingCategory = MeetingCategories,
            includeRegions = IncludeRegions
        });

        progress?.Report(
            $"[R-{discipline.Code()}] Querying meetings for {startDate:yyyy-MM-dd}" +
            (effectiveEnd != startDate ? $" to {effectiveEnd:yyyy-MM-dd}" : "") + " ...");

        using var doc = await GraphQlAsync(
            page, "meetingsIndexByStartEndTime", variables, RaceNetGraphQl.MeetingsQuery, cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("meetings", out var meetingsEl)
            || meetingsEl.ValueKind != JsonValueKind.Array)
        {
            throw new RaceNetScrapeException(
                "Racenet's meetings response had no data.meetings array — the query may need " +
                $"updating for a schema change. Response: {Snippet(doc.RootElement.GetRawText())}");
        }

        var groupedJson = await MapMeetingsAsync(page, meetingsEl.GetRawText());

        var response = JsonSerializer.Deserialize<GroupedMeetingsPayload>(groupedJson, ScraperJsonOptions.Deserialize)
            ?? throw new RaceNetScrapeException("Could not parse Racenet meetings response (empty result).");

        var groups = response.Data?.MeetingsGrouped ?? new List<MeetingGroup>();
        progress?.Report(
            $"[R-{discipline.Code()}] Received {groups.Sum(g => g.Meetings.Count)} meeting(s) " +
            $"across {groups.Count} group(s).");

        return new ScrapeResult
        {
            Discipline = discipline,
            StartDate = startDate,
            EndDate = effectiveEnd,
            ScrapedAtUtc = DateTimeOffset.UtcNow,
            MeetingsGrouped = groups
        };
    }

    public async Task<RaceDetail> ScrapeRaceAsync(
        Discipline discipline,
        Meeting meeting,
        RaceEvent raceEvent,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(raceEvent.Id))
        {
            throw new RaceNetScrapeException(
                "Race id is missing — pass in the Meeting/RaceEvent objects returned by " +
                $"{nameof(ScrapeMeetingsAsync)}, not hand-built ones.");
        }

        var page = await EnsureSessionPageAsync(discipline, progress, cancellationToken);

        progress?.Report(
            $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}): fetching field ...");

        using var doc = await GraphQlAsync(
            page,
            "getEventById",
            JsonSerializer.Serialize(new { eventId = raceEvent.Id }),
            RaceNetGraphQl.BuildEventQuery(),
            cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("event", out var eventEl)
            || eventEl.ValueKind != JsonValueKind.Object)
        {
            throw new RaceNetScrapeException(
                $"Racenet returned no event for race id {raceEvent.Id} ({meeting.Name} R{raceEvent.EventNumber}).");
        }

        // meetingName/country/date aren't on the event query — they come from the Meeting the
        // caller already has, which is where the Punters scraper got them too (it read them from
        // a sibling meeting(...) cache entry on the race page).
        var meetingContext = JsonSerializer.Serialize(new
        {
            meetingId = meeting.Id,
            meetingName = meeting.Name,
            country = meeting.Venue?.Country?.Iso3,
            date = meeting.MeetingDateLocal
        });

        var mappedJson = await MapRaceDetailAsync(page, eventEl.GetRawText(), meetingContext);

        using var mappedDoc = JsonDocument.Parse(mappedJson);
        var detail = JsonSerializer.Deserialize<RaceDetail>(mappedJson, ScraperJsonOptions.Deserialize)
            ?? throw new RaceNetScrapeException("Could not parse race detail (empty result).");

        BackfillRaceEvent(raceEvent, eventEl);

        // Each runner's deeper form history, batched for the whole field in one request. The
        // Punters scraper had to provoke these per-runner by scrolling; here it's just a query.
        var selectionIds = mappedDoc.RootElement.TryGetProperty("runners", out var runnersProp)
            && runnersProp.ValueKind == JsonValueKind.Array
                ? runnersProp.EnumerateArray()
                    .Select(r => r.TryGetProperty("selectionId", out var sid) && sid.ValueKind == JsonValueKind.String
                        ? sid.GetString() : null)
                    .ToList()
                : new List<string?>();

        var fullForms = await ScrapeFullFormsAsync(
            page, selectionIds.Where(id => id != null).Select(id => id!).ToList(), progress, discipline, cancellationToken);

        for (var i = 0; i < detail.Runners.Count; i++)
        {
            var runner = detail.Runners[i];
            runner.Discipline = discipline.Code();

            // Racenet carries a real free-text silk description (racingColours) for almost every
            // runner, so this image-based fallback should rarely actually fire — kept for the
            // rare cases where it's missing.
            if (string.IsNullOrEmpty(runner.SilkColourText) && !string.IsNullOrEmpty(runner.SilkImageUrl))
            {
                runner.SilkColourText = await SilkSvgDescriber.DescribeAsync(runner.SilkImageUrl);
            }

            // Overwrite the single lastRun-derived entry with up to 5 real, distinct past runs.
            var selectionId = i < selectionIds.Count ? selectionIds[i] : null;
            if (selectionId != null && fullForms.TryGetValue(selectionId, out var forms) && forms.Count > 0)
            {
                var pastRuns = new List<PastRun>();
                var seenRaceKeys = new HashSet<string>();
                foreach (var form in forms)
                {
                    var mapped = MapPastRunFromForm(form);

                    // The form feed occasionally lists the same underlying race twice — once with
                    // full detail, once as a sparser duplicate under a slightly different
                    // course/date spelling. Course/date aren't a safe dedup key and the margin can
                    // round differently between the two, but the runner's own finish position plus
                    // the winner/2nd/3rd/SP match exactly on every duplicate pair seen.
                    var key = string.Join('|', mapped.FinishPosition, mapped.WinnerName,
                        mapped.SecondName, mapped.ThirdName, mapped.StartingPrice);
                    if (!seenRaceKeys.Add(key)) continue;

                    pastRuns.Add(mapped);
                    if (pastRuns.Count == 5) break;
                }

                if (pastRuns.Count > 0)
                {
                    runner.PastRuns = pastRuns;
                    runner.LastRun = pastRuns[0].Date;
                }
            }
        }

        progress?.Report(
            $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({meeting.Name}): {detail.Runners.Count} runner(s).");

        return detail;
    }

    /// <summary>
    /// Copies the fields a race's own event(...) query resolves better than the meeting list onto
    /// the caller's <see cref="RaceEvent"/>. The prize money part matters most: the meeting list
    /// carries each race's NATIVE currency total, while event.racePrizeMoney is the AUD figure the
    /// downstream ingester expects, with event.prizeMoney[] the matching per-place AUD breakdown.
    /// raceEvent is the same object living in meeting.Events, so this is visible to the caller.
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

    /// <summary>
    /// Fetches every runner's form history in one batched request. Best-effort, like
    /// <see cref="SilkSvgDescriber"/>: if it fails or comes back short, those runners simply keep
    /// the single lastRun entry the event query already gave them, rather than failing the race.
    /// </summary>
    private async Task<Dictionary<string, List<JsonElement>>> ScrapeFullFormsAsync(
        IPage page, List<string> selectionIds, IProgress<string>? progress, Discipline discipline,
        CancellationToken cancellationToken)
    {
        var formsBySelectionId = new Dictionary<string, List<JsonElement>>();
        if (selectionIds.Count == 0) return formsBySelectionId;

        JsonDocument doc;
        try
        {
            doc = await GraphQlAsync(
                page,
                "fullFormsBySelectionIds",
                JsonSerializer.Serialize(new { selectionIds, limit = 100 }),
                RaceNetGraphQl.CompetitorFormsQuery,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report(
                $"[R-{discipline.Code()}] Full form unavailable ({ex.Message}); " +
                "keeping each runner's single last run.");
            return formsBySelectionId;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("competitorForms", out var cf)
                || cf.ValueKind != JsonValueKind.Array)
            {
                return formsBySelectionId;
            }

            foreach (var entry in cf.EnumerateArray())
            {
                if (!entry.TryGetProperty("selectionId", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                if (!entry.TryGetProperty("forms", out var formsEl) || formsEl.ValueKind != JsonValueKind.Array) continue;

                formsBySelectionId[idEl.GetString()!] = formsEl.EnumerateArray().Select(f => f.Clone()).ToList();
            }
        }

        if (formsBySelectionId.Count < selectionIds.Count)
        {
            progress?.Report(
                $"[R-{discipline.Code()}] Only got full form for {formsBySelectionId.Count}/{selectionIds.Count} " +
                "runner(s); the rest keep their single last run.");
        }

        return formsBySelectionId;
    }

    /// <summary>
    /// Maps one entry from competitorForms' forms[] into the same <see cref="PastRun"/> shape used
    /// for the single-run lastRun fallback. This endpoint's shape differs from selection.lastRun:
    /// barrier and starting price are embedded in the free-text formLine.summaryMarkup rather than
    /// dedicated fields, so they're pulled out with a regex; the runner's own jockey for that run
    /// isn't exposed here either, matching the lastRun mapping (which also leaves it null).
    /// </summary>
    private static PastRun MapPastRunFromForm(JsonElement f)
    {
        string? GetStr(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? GetInt(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        double? GetDouble(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        bool GetBool(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

        string? summaryMarkup = null;
        string? winnerName = null, secondName = null, thirdName = null;
        if (f.TryGetProperty("formLine", out var formLine) && formLine.ValueKind == JsonValueKind.Object)
        {
            summaryMarkup = formLine.TryGetProperty("summaryMarkup", out var sm) && sm.ValueKind == JsonValueKind.String
                ? sm.GetString() : null;

            if (formLine.TryGetProperty("places", out var placesEl) && placesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in placesEl.EnumerateArray())
                {
                    var pos = p.TryGetProperty("finishPosition", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt32() : (int?)null;
                    var name = p.TryGetProperty("competitorName", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null;
                    if (pos == 1) winnerName = name;
                    else if (pos == 2) secondName = name;
                    else if (pos == 3) thirdName = name;
                }
            }
        }

        string? barrier = null;
        string? startingPrice = null;
        if (summaryMarkup != null)
        {
            var barrierMatch = Regex.Match(summaryMarkup, @"Barrier:\s*(\d+)");
            if (barrierMatch.Success) barrier = barrierMatch.Groups[1].Value;

            var spMatch = Regex.Match(summaryMarkup, @"SP\s*\$([\d.]+)");
            if (spMatch.Success) startingPrice = "$" + spMatch.Groups[1].Value;
        }

        var trackCondition = GetStr("trackCondition");
        // trackConditionRating comes back as a number here (5) but as a string on
        // selection.lastRun, so it's read defensively either way to keep the joined
        // "Soft 5"形 output identical between the two paths.
        var trackConditionRating = f.TryGetProperty("trackConditionRating", out var tcr)
            ? tcr.ValueKind switch
            {
                JsonValueKind.String => tcr.GetString(),
                JsonValueKind.Number => tcr.GetRawText(),
                _ => null
            }
            : null;
        var margin = GetDouble("margin");
        var eventDistance = GetInt("eventDistance");
        var finishPosition = GetInt("finishPosition");

        return new PastRun
        {
            Type = GetBool("isTrial") ? "TRIAL" : "RACE",
            FinishPosition = finishPosition,
            Starters = GetInt("eventStarters"),
            RaceNumber = GetInt("eventNumber"),
            Course = GetStr("meetingName"),
            Date = GetStr("meetingDate"),
            Distance = eventDistance != null ? $"{eventDistance}m" : null,
            RaceName = GetStr("eventNameForm") ?? GetStr("eventNameNews"),
            StartingPrice = startingPrice,
            JockeyName = null,
            WinnerName = winnerName,
            SecondName = secondName,
            ThirdName = thirdName,
            LengthsBehind = margin != null ? $"{margin}L" : null,
            TrackCondition = trackCondition != null && trackConditionRating != null
                ? $"{trackCondition} {trackConditionRating}" : trackCondition,
            EventClass = null,
            FinishTime = GetStr("finishTime"),
            BarrierPosition = barrier,
            RunDetail = GetStr("videoComment") ?? GetStr("videoNote"),
            RaceResult = new RaceResultRef { HorseName = null, Position = finishPosition }
        };
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

    /// <summary>DTO purely for deserializing the shape <see cref="MapMeetingsAsync"/> builds on the
    /// JS side — never sent anywhere, just a typed stepping stone to <see cref="ScrapeResult"/>.</summary>
    private sealed class GroupedMeetingsPayload
    {
        public GroupedMeetingsData? Data { get; set; }
    }

    private sealed class GroupedMeetingsData
    {
        public List<MeetingGroup>? MeetingsGrouped { get; set; }
    }

    /// <summary>
    /// Normalizes the flat meetings array into the grouped
    /// data.meetingsGrouped[].{group,meetings[]} shape TroyenRaceIngestor's MeetingFileDto expects.
    ///
    /// Runs as JS in the page rather than in C# on purpose: it's the same mapping the Punters
    /// scraper performs in its own page context, and keeping it in JS keeps the number→string
    /// coercions ("$" + value, String(id)) byte-identical between the two scrapers' output. A C#
    /// rewrite would risk subtle format drift (13920 vs 13920.0) in files a downstream ingester
    /// already parses.
    /// </summary>
    private static async Task<string> MapMeetingsAsync(IPage page, string meetingsJson)
    {
        const string script = """
            (meetingsJson) => {
                const meetings = JSON.parse(meetingsJson);

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
                        // racePrizeMoneyValue + racePrizeMoneyUnit is the race's prize money in its
                        // OWN native currency (e.g. 14756/"GBP" for a UK meeting). ScrapeRaceAsync
                        // overwrites this with the true AUD figure (event.racePrizeMoney) once that
                        // race has been scraped — until then it stays in native currency.
                        racePrizeMoney: e.racePrizeMoneyValue != null ? e.racePrizeMoneyValue
                            : (e.racePrizeMoney != null ? e.racePrizeMoney : null),
                        racePrizeMoneyUnit: e.racePrizeMoneyUnit || null,
                        eventClass: e.eventClass || null,
                        groupType: e.groupType || null,
                        trackCondition: e.trackCondition || null,
                        // Racenet has no per-event weather: its meeting query returns ONE weather
                        // object per meeting, so it's copied onto every event here (a meeting is
                        // one place on one day, so this is a fair proxy).
                        weather: meeting.weather || null,
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

                const groupsMap = new Map();
                for (const m of meetings) {
                    const iso2 = m.venue && m.venue.country && m.venue.country.iso2;
                    const group = iso2 === 'AU' ? 'Australia' : 'International';
                    if (!groupsMap.has(group)) groupsMap.set(group, []);
                    groupsMap.get(group).push(buildMeeting(m));
                }

                // Australia first, matching the ordering the ingester's sample files show.
                const order = ['Australia', 'International'];
                const meetingsGrouped = order
                    .filter(g => groupsMap.has(g))
                    .map(g => ({ group: g, meetings: groupsMap.get(g) }));

                return JSON.stringify({ data: { meetingsGrouped } });
            }
            """;

        return await page.EvaluateAsync<string>(script, meetingsJson);
    }

    /// <summary>
    /// Normalizes one event's runners/jockeys/trainers/form/stats into the RaceDetail JSON shape.
    /// Only the single most-recent past run is available here (via selection.lastRun); each
    /// runner's deeper history is filled in afterwards by <see cref="ScrapeFullFormsAsync"/>,
    /// which needs each runner's raw selectionId (carried through in the output below) to line
    /// results back up.
    ///
    /// This is the Punters scraper's mapping essentially verbatim — same field names, same
    /// formatting helpers (ratio/pct/money), same furlong and stone-pound conversions — which is
    /// what makes the two scrapers' DataDump files interchangeable.
    /// </summary>
    private static async Task<string> MapRaceDetailAsync(IPage page, string eventJson, string meetingContextJson)
    {
        const string script = """
            (args) => {
                const event_ = JSON.parse(args.eventJson);
                const ctx = JSON.parse(args.meetingContextJson);
                const selections = event_.selections || [];

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

                function ratio(runs, places) {
                    if (runs === null || runs === undefined) return null;
                    const p = places || [0, 0, 0];
                    return runs + ':' + p.join('-');
                }

                function pct(v) { return v != null ? (v + '%') : null; }
                function money(v) { return v != null ? ('$' + v) : null; }
                function str(v) { return v != null ? String(v) : null; }

                function mapPastRunFromLastRun(lr) {
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

                function mapStats(st) {
                    if (!st) return { performanceStatistics: null, rawStats: null };

                    const winRangeStr = Array.isArray(st.winRange) && st.winRange.length
                        ? st.winRange.join('-') + 'm' : null;
                    const placePer = pct(st.placePercentage);
                    const jockeyWinPer = (st.runsByJockey != null && st.runsByJockey > 0 && st.placesByJockey)
                        ? pct(Math.round((st.placesByJockey[0] / st.runsByJockey) * 100)) : null;
                    const synthRuns = st.synthRun ?? st.runsBySynth ?? null;
                    const synthPlaces = st.synthPlaces ?? st.placesBySynth ?? null;

                    return {
                        performanceStatistics: {
                            career: ratio(st.totalRuns, st.totalPlaces),
                            winPer: pct(st.winPercentage),
                            placePer: placePer,
                            showPer: placePer,
                            last10Starts: st.lastTenFigure ?? null,
                            last12Months: ratio(st.lastYearRuns, st.lastYearPlaces),
                            season: ratio(st.currentSeasonRuns, st.currentSeasonPlaces),
                            track: ratio(st.runsByTrack, st.placesByTrack),
                            distance: ratio(st.runsByDistance, st.placesByDistance),
                            trackDistanceCombo: ratio(st.runsByDistTrack, st.placesByDistTrack),
                            wetConditions: ratio(st.wetRuns, st.wetPlaces),
                            prizeMoney: money(st.totalPrizeMoney),
                            avgPrizeMoney: money(st.averagePrizeMoney),
                            winRange: winRangeStr,
                            rating: str(st.rating),
                            jockeyWinPer: jockeyWinPer,
                            jockeyHorse: ratio(st.runsByJockey, st.placesByJockey),
                            firstUp: ratio(st.firstUpRuns, st.firstUpPlaces),
                            secondUp: ratio(st.secondUpStarts, st.secondUpPlaces),
                            thirdUp: ratio(st.thirdUpStarts, st.thirdUpPlaces),
                            firm: ratio(st.firmRuns, st.firmPlaces),
                            good: ratio(st.goodRuns, st.goodPlaces),
                            soft: ratio(st.softRuns, st.softPlaces),
                            heavy: ratio(st.heavyRuns, st.heavyPlaces),
                            synthetic: ratio(synthRuns, synthPlaces),
                            turf: ratio(st.runsByTurf, st.placesByTurf),
                            clockwise: ratio(st.clockwiseRuns, st.clockwisePlaces),
                            antiClockwise: ratio(st.aClockwiseRuns, st.aClockwisePlaces),
                            class: ratio(st.classRuns, st.classPlaces),
                            asFavourite: ratio(st.favRuns, st.favPlaces),
                            group1: ratio(st.group1Runs, st.group1Places),
                            group2: ratio(st.group2Runs, st.group2Places),
                            group3: ratio(st.group3Runs, st.group3Places),
                            listed: ratio(st.listedRaceRuns, st.listedRacePlaces),
                            night: ratio(st.nightRuns, st.nightPlaces),
                            trainerJockey: ratio(st.runsByTrainerJockey, st.placesByTrainerJockey),
                            roi: st.roi != null ? String(st.roi) : null,
                            lastWin: st.lastWin ?? null,
                            daysSinceLastRun: st.daysSinceLastRun ?? null
                        },
                        rawStats: {
                            rating: str(st.rating),
                            totalRuns: st.totalRuns ?? null,
                            totalPlaces: st.totalPlaces ?? null,
                            winPercentage: pct(st.winPercentage),
                            placePercentage: pct(st.placePercentage),
                            totalPrizeMoney: money(st.totalPrizeMoney),
                            averagePrizeMoney: money(st.averagePrizeMoney),
                            winRange: winRangeStr,
                            runsByJockey: st.runsByJockey ?? 0,
                            placesByJockey: st.placesByJockey ?? [0, 0, 0],
                            firstUpRuns: st.firstUpRuns ?? null,
                            firstUpPlaces: st.firstUpPlaces ?? null,
                            secondUpStarts: st.secondUpStarts ?? null,
                            thirdUpStarts: st.thirdUpStarts ?? null,
                            lastYearRuns: st.lastYearRuns ?? null,
                            runsByDistance: st.runsByDistance ?? null,
                            runsByTrack: st.runsByTrack ?? null,
                            runsByDistTrack: st.runsByDistTrack ?? null,
                            firmRuns: st.firmRuns ?? null,
                            goodRuns: st.goodRuns ?? null,
                            softRuns: st.softRuns ?? null,
                            runsByTurf: st.runsByTurf ?? null,
                            wetRuns: st.wetRuns ?? null,
                            heavyRuns: st.heavyRuns ?? null,
                            synthRun: synthRuns,
                            clockwiseRuns: st.clockwiseRuns ?? null,
                            currentSeasonRuns: st.currentSeasonRuns ?? null,
                            aClockwiseRuns: st.aClockwiseRuns ?? null,
                            secondUpPlaces: st.secondUpPlaces ?? null,
                            thirdUpPlaces: st.thirdUpPlaces ?? null,
                            lastYearPlaces: st.lastYearPlaces ?? null,
                            placesByDistance: st.placesByDistance ?? null,
                            placesByTrack: st.placesByTrack ?? null,
                            placesByDistTrack: st.placesByDistTrack ?? null,
                            firmPlaces: st.firmPlaces ?? null,
                            goodPlaces: st.goodPlaces ?? null,
                            softPlaces: st.softPlaces ?? null,
                            placesByTurf: st.placesByTurf ?? null,
                            wetPlaces: st.wetPlaces ?? null,
                            heavyPlaces: st.heavyPlaces ?? null,
                            synthPlaces: synthPlaces,
                            currentSeasonPlaces: st.currentSeasonPlaces ?? null,
                            clockwisePlaces: st.clockwisePlaces ?? null,
                            aClockwisePlaces: st.aClockwisePlaces ?? null,
                            runsByClass: st.classRuns ?? null,
                            placesByClass: st.classPlaces ?? null,
                            favRuns: st.favRuns ?? null,
                            favPlaces: st.favPlaces ?? null,
                            group1Runs: st.group1Runs ?? null,
                            group1Places: st.group1Places ?? null,
                            group2Runs: st.group2Runs ?? null,
                            group2Places: st.group2Places ?? null,
                            group3Runs: st.group3Runs ?? null,
                            group3Places: st.group3Places ?? null,
                            listedRuns: st.listedRaceRuns ?? null,
                            listedPlaces: st.listedRacePlaces ?? null,
                            nightRuns: st.nightRuns ?? null,
                            nightPlaces: st.nightPlaces ?? null,
                            trainerJockeyWinPercentage: st.trainerJockeyWin ?? null,
                            roi: st.roi ?? null,
                            lastWin: st.lastWin ?? null,
                            daysSinceLastRun: st.daysSinceLastRun ?? null
                        }
                    };
                }

                const runners = selections.map(sel => {
                    const c = sel.competitor || {};
                    const mapped = mapStats(sel.stats);
                    const pastRuns = sel.lastRun ? [mapPastRunFromLastRun(sel.lastRun)] : [];
                    const lastRunComment = sel.stats && sel.stats.lastRun ? String(sel.stats.lastRun).trim() : null;

                    // Bonus enrichment Racenet carries (speed-map prediction, edge rating,
                    // quick-form indicators, price flucs) — stashed under pointers (an untyped
                    // object on the shared Runner DTO) rather than dropped, since it doesn't fit
                    // any existing typed field.
                    const pointers = (sel.quickForm || sel.predictorRatings || sel.prediction || sel.puntersEdge || sel.flucs) ? {
                        quickForm: sel.quickForm || null,
                        predictorRatings: sel.predictorRatings || null,
                        prediction: sel.prediction || null,
                        puntersEdge: sel.puntersEdge || null,
                        flucs: sel.flucs || null
                    } : null;

                    return {
                        runnerId: 'runner-' + (c.slug || slugify(c.name)),
                        // Raw selection id, only used C#-side to correlate this runner with its
                        // competitorForms response — not a field on the shared Runner DTO, so it's
                        // simply ignored on deserialize.
                        selectionId: sel.id != null ? String(sel.id) : null,
                        tabNumber: sel.competitorNumber != null ? String(sel.competitorNumber) : null,
                        runnerName: c.name || null,
                        age: c.age != null ? String(c.age) : null,
                        sex: c.sexShort || null,
                        colour: c.colour || null,
                        sire: c.sire || null,
                        dam: c.dam || null,
                        // Racing-industry country abbreviation shown after a horse's name (e.g.
                        // "(GB)", "(NZ)") to disambiguate horses of the same name.
                        horseCountry: (c.horseCountry && (c.horseCountry.iso3 || c.horseCountry.iso2)) || null,
                        draw: sel.barrierNumber || null,
                        barrierPosition: sel.barrierNumber || null,
                        comment: sel.comments || null,
                        silkColourText: sel.racingColours || c.racingColours || null,
                        silkImageUrl: sel.silkImageUrl || (c.imageUrl ? (c.imageUrl.startsWith('//') ? 'https:' + c.imageUrl : c.imageUrl) : null),
                        gearChanges: sel.gearChanges || null,
                        jockey: sel.jockey ? { id: sel.jockey.id, name: sel.jockey.name, slug: sel.jockey.slug } : null,
                        trainer: sel.trainer ? { id: sel.trainer.id, name: sel.trainer.name, slug: sel.trainer.slug } : null,
                        carryingWeight: kgToWeightInfo(sel.weight),
                        lastRun: pastRuns.length ? pastRuns[0].date : null,
                        lastRunComment: lastRunComment,
                        pastRuns: pastRuns,
                        performanceStatistics: mapped.performanceStatistics,
                        rawStats: mapped.rawStats,
                        pointers: pointers,
                        currentOdds: sel.startingPrice != null ? ('$' + sel.startingPrice) : null,
                        isScratched: sel.status === 'SCR' || sel.status === 'Scratched'
                            || sel.status === 'SCRATCHED' || sel.status === 'WDN'
                    };
                });

                return JSON.stringify({
                    meetingId: ctx.meetingId || event_.meetingId || null,
                    meetingName: ctx.meetingName ? String(ctx.meetingName).toUpperCase() : null,
                    country: ctx.country || null,
                    date: ctx.date || null,
                    raceId: event_.id,
                    slug: event_.slug,
                    raceNumber: event_.eventNumber,
                    raceDistance: metersToDistanceInfo(event_.distance),
                    runners: runners
                });
            }
            """;

        return await page.EvaluateAsync<string>(script, new { eventJson, meetingContextJson });
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionPage is { IsClosed: false }) await _sessionPage.CloseAsync();
        if (_context is not null) await _context.CloseAsync();
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
