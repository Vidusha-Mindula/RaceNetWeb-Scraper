using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceNetScraper.App.Services;
using RaceNetScraper.Core.Scraping;
using RaceNetScraper.Shared.Json;
using RaceNetScraper.Shared.Messaging;
using RaceNetScraper.Shared.Models;
using RaceNetScraper.Shared.Scraping;

namespace RaceNetScraper.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<(DateOnly Date, Discipline Discipline), ScrapeResult> _lastResults = new();

    /// <summary>Race detail keyed by raceId.</summary>
    private readonly Dictionary<string, RaceDetail> _raceDetails = new();

    /// <summary>Failed-attempt count per raceId — lets the Phase 2 loop below give up on a race
    /// that keeps failing (e.g. Racenet never returning event data for it) instead of retrying it
    /// forever every time its meeting's row is revisited, which previously stuck a meeting at 0%
    /// detail indefinitely with no way for the run to ever finish. See RowIsFullyDone.</summary>
    private readonly Dictionary<string, int> _raceAttempts = new();

    private readonly AppSettings _settings = AppSettings.Load();
    private bool _loadingSettings;

    /// <summary>Publishes one "meeting.scraped" event per finished meeting (see the publish hook in
    /// <see cref="ScrapeDatesAsync"/>). One long-lived instance, next to <see cref="_settings"/>,
    /// disposed on app exit via <see cref="DisposeEventPublisherAsync"/> from App.OnExit.</summary>
    private readonly RabbitMqMeetingEventPublisher _eventPublisher;

    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _cts;
    private string? _pendingNoticeId;

    /// <summary>The exact request (disciplines/dates/filters) behind the run currently sitting in
    /// <see cref="Meetings"/> — remembered so <see cref="ContinueAsync"/> can re-enter
    /// <see cref="ScrapeDatesAsync"/> with the same parameters after a Stop, rather than needing
    /// the user to re-select everything.</summary>
    private ScrapeRequest? _lastScrapeRequest;

    /// <summary>True only right after a run ends via Stop (not a clean finish or a hard failure) —
    /// gates <see cref="ContinueCommand"/> so it's only ever offered when there's actually
    /// unfinished work left over from <see cref="_lastScrapeRequest"/>.</summary>
    private bool _canResume;

    /// <summary>One discipline/date combo plus which country group it's scoped to — a single
    /// <see cref="ScrapeDatesAsync"/> call takes a list of these so e.g. Australia and
    /// International (each with their own day(s)/discipline(s)) can be listed and scraped
    /// together, in one browser session, instead of needing separate calls.</summary>
    private sealed record ScrapeSubRequest(List<Discipline> Disciplines, List<DateOnly> Dates, string GroupFilter);

    private sealed record ScrapeRequest(
        List<ScrapeSubRequest> SubRequests, string CountryFilter, string CourseFilter, bool ForceUploadToS3);

    /// <summary>Ticks on the UI thread (via WPF's Dispatcher) so its handler can safely touch
    /// <see cref="Meetings"/> and other bound properties directly, the same as a button click —
    /// a background <see cref="System.Threading.Timer"/> would require manual Dispatcher.Invoke
    /// marshaling to avoid a cross-thread collection exception.</summary>
    private readonly DispatcherTimer _autoScrapeTimer;

    /// <summary>"{yyyy-MM-dd}T{HH:mm}" of every slot that's already fired today — guards against
    /// firing the same slot twice if a tick happens to land on its time more than once (a slot's
    /// Australia and International runs both count as part of that one firing). Pruned back to
    /// just today's entries on each tick rather than ever being cleared outright, so it doesn't
    /// grow unbounded across a long-running session.</summary>
    private readonly HashSet<string> _firedAutoScrapeSlotKeys = new();

    public MainViewModel()
    {
        _eventPublisher = new RabbitMqMeetingEventPublisher(_settings);

        _loadingSettings = true;
        DownloadFolder = _settings.DownloadFolder;
        AutoExportAfterScrape = _settings.AutoExportAfterScrape;
        UploadToS3 = _settings.UploadToS3;
        S3BucketName = _settings.S3BucketName;
        AutoScrapeEnabled = _settings.AutoScrapeEnabled;
        foreach (var slot in _settings.AutoScrapeSlots)
        {
            var row = AutoScrapeSlotRow.From(slot);
            row.Changed += SaveAutoScrapeSlots;
            AutoScrapeSlots.Add(row);
        }
        AutoScrapeLastRunSummary = _settings.AutoScrapeLastRunSummary;
        UseFirefoxBrowser = _settings.ScraperBrowser == nameof(ScraperBrowserChoice.Firefox);
        UseEdgeBrowser = _settings.ScraperBrowser == nameof(ScraperBrowserChoice.Edge);
        UseChromeBrowser = !UseFirefoxBrowser && !UseEdgeBrowser;
        _loadingSettings = false;

        foreach (var option in CountryOptions) option.SelectionChanged += RecomputeCountryCodeFilter;

        _ = CheckForUpdatesAsync();
        _ = CheckForDeveloperNoticeAsync();

        _autoScrapeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoScrapeTimer.Tick += (_, _) => _ = AutoScrapeTickAsync();
        _autoScrapeTimer.Start();
    }

    [ObservableProperty]
    private DateTime selectedDate = DateTime.Today;

    [ObservableProperty]
    private bool scrapeHorses = true;

    [ObservableProperty]
    private bool scrapeGreyhounds = true;

    [ObservableProperty]
    private bool scrapeHarness = true;

    [ObservableProperty]
    private bool headless;

    // --- Browser choice (see the Browser tab). Backed by three mutually-exclusive RadioButtons
    // (same GroupName in XAML) rather than one enum-valued property, since that's a plain bool
    // binding with no converter needed, matching every other checkbox/toggle in this app. ---
    [ObservableProperty]
    private bool useChromeBrowser = true;

    [ObservableProperty]
    private bool useFirefoxBrowser;

    [ObservableProperty]
    private bool useEdgeBrowser;

    private ScraperBrowserChoice SelectedScraperBrowser =>
        UseFirefoxBrowser ? ScraperBrowserChoice.Firefox :
        UseEdgeBrowser ? ScraperBrowserChoice.Edge :
        ScraperBrowserChoice.Chrome;

    // Checked once at startup (installed/uninstalled doesn't change while the app is running) and
    // bound to each RadioButton's IsEnabled on the Browser tab, so picking an unusable option
    // isn't even possible rather than failing later when a scrape actually starts.
    public bool IsChromeInstalled { get; } = ScraperBrowserAvailability.IsInstalled(ScraperBrowserChoice.Chrome);
    public bool IsFirefoxInstalled { get; } = ScraperBrowserAvailability.IsInstalled(ScraperBrowserChoice.Firefox);
    public bool IsEdgeInstalled { get; } = ScraperBrowserAvailability.IsInstalled(ScraperBrowserChoice.Edge);

    /// <summary>Comma-separated ISO2 codes of every checked <see cref="CountryOptions"/> row plus
    /// every code typed into <see cref="ManualCountryCodes"/> (e.g. "AU,NZ,US"), recomputed by
    /// <see cref="RecomputeCountryCodeFilter"/> whenever either changes. Blank = no filter. This is
    /// what's actually passed to <see cref="ScrapeDatesAsync"/> — the checklist and text box are
    /// just its UI.</summary>
    [ObservableProperty]
    private string countryCodeFilter = "";

    /// <summary>Free-text ISO2 codes (comma/space-separated, e.g. "IN, SE") for countries not on
    /// the curated <see cref="CountryOptions"/> list — merged into <see cref="CountryCodeFilter"/>
    /// alongside whatever's checked, rather than replacing it.</summary>
    [ObservableProperty]
    private string manualCountryCodes = "";

    partial void OnManualCountryCodesChanged(string value) => RecomputeCountryCodeFilter();

    /// <summary>Curated checklist backing the manual Scraper tab's country picker. Deliberately a
    /// fixed common-racing-countries list rather than derived from live scrape results — there's
    /// no way to know what countries exist for a date without already having scraped it, which is
    /// the very thing this filter exists to narrow down before doing. Extend this list by hand if
    /// Racenet starts covering somewhere not on it.</summary>
    public ObservableCollection<CountryOption> CountryOptions { get; } = new()
    {
        new CountryOption { Iso2 = "AU", Name = "Australia" },
        new CountryOption { Iso2 = "NZ", Name = "New Zealand" },
        new CountryOption { Iso2 = "GB", Name = "United Kingdom" },
        new CountryOption { Iso2 = "IE", Name = "Ireland" },
        new CountryOption { Iso2 = "FR", Name = "France" },
        new CountryOption { Iso2 = "US", Name = "United States" },
        new CountryOption { Iso2 = "HK", Name = "Hong Kong" },
        new CountryOption { Iso2 = "SG", Name = "Singapore" },
        new CountryOption { Iso2 = "JP", Name = "Japan" },
        new CountryOption { Iso2 = "ZA", Name = "South Africa" },
        new CountryOption { Iso2 = "AE", Name = "UAE" },
        new CountryOption { Iso2 = "MO", Name = "Macau" },
        new CountryOption { Iso2 = "DE", Name = "Germany" },
        new CountryOption { Iso2 = "CA", Name = "Canada" },
        new CountryOption { Iso2 = "TR", Name = "Turkey" },
        new CountryOption { Iso2 = "ES", Name = "Spain" },
        new CountryOption { Iso2 = "MX", Name = "Mexico" },
        new CountryOption { Iso2 = "PA", Name = "Panama" },
    };

    /// <summary>Shown on the picker button — e.g. "All countries", "Australia", or "3 countries"
    /// once more than two codes are active (comma-joining every name gets unreadable past that).
    /// See <see cref="SelectedCountriesDetail"/> for the untruncated version shown below it.</summary>
    public string CountryFilterSummary
    {
        get
        {
            var codes = ParseCountryCodes(CountryCodeFilter);
            if (codes.Length == 0) return "All countries";
            return codes.Length > 2 ? $"{codes.Length} countries" : string.Join(", ", CountryLabels(codes));
        }
    }

    /// <summary>Every active country's full name (or raw code, if it's a manually-typed one not on
    /// the curated list), comma-joined with nothing left out — unlike <see cref="CountryFilterSummary"/>,
    /// which truncates past two so the picker button itself stays a fixed width. Bound below the
    /// picker so the exact selection stays visible without reopening the popup. Empty when nothing
    /// is selected (all countries).</summary>
    public string SelectedCountriesDetail => string.Join(", ", CountryLabels(ParseCountryCodes(CountryCodeFilter)));

    public bool HasCountriesSelected => CountryCodeFilter.Length > 0;

    private IEnumerable<string> CountryLabels(IEnumerable<string> codes) => codes.Select(code =>
        CountryOptions.FirstOrDefault(c => string.Equals(c.Iso2, code, StringComparison.OrdinalIgnoreCase))?.Name
        ?? code.ToUpperInvariant());

    [RelayCommand]
    private void ClearCountrySelection()
    {
        foreach (var option in CountryOptions) option.IsSelected = false;
        ManualCountryCodes = "";
    }

    private void RecomputeCountryCodeFilter()
    {
        var checkedCodes = CountryOptions.Where(c => c.IsSelected).Select(c => c.Iso2);
        var manualCodes = ParseCountryCodes(ManualCountryCodes).Select(c => c.ToUpperInvariant());
        CountryCodeFilter = string.Join(",", checkedCodes.Concat(manualCodes).Distinct(StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CountryFilterSummary));
        OnPropertyChanged(nameof(SelectedCountriesDetail));
        OnPropertyChanged(nameof(HasCountriesSelected));
    }

    /// <summary>Optional filter: only scrape meetings whose course/meeting name contains this text (case-insensitive). Blank = no filter.</summary>
    [ObservableProperty]
    private string courseNameFilter = "";

    [ObservableProperty]
    private bool isBusy;

    /// <summary>True while a cancellation has been requested but the scrape hasn't unwound yet —
    /// disables the Stop button so a second click can't fire mid-teardown.</summary>
    [ObservableProperty]
    private bool isStopping;

    [ObservableProperty]
    private string statusText = "Ready.";

    /// <summary>Default folder that Export JSON opens to, and that auto-export writes to
    /// directly (no dialog). Remembered across app restarts.</summary>
    [ObservableProperty]
    private string downloadFolder = "";

    /// <summary>When set, a successful scrape immediately exports to <see cref="DownloadFolder"/>
    /// with no folder-picker prompt.</summary>
    [ObservableProperty]
    private bool autoExportAfterScrape;

    /// <summary>When set, every exported file is also uploaded straight into the configured
    /// S3 bucket/folder (flat — no per-meeting nesting there, unlike the local export).</summary>
    [ObservableProperty]
    private bool uploadToS3;

    /// <summary>S3 bucket to upload to when <see cref="UploadToS3"/> is set. Editable in the UI
    /// rather than fixed at install time, so the same install can be pointed at different
    /// buckets. Remembered across app restarts.</summary>
    [ObservableProperty]
    private string s3BucketName = "";

    /// <summary>When set, <see cref="AutoScrapeTickAsync"/> fires a scrape automatically at each
    /// slot in <see cref="AutoScrapeSlots"/> whose time matches, for as long as this app stays
    /// open — there is no scheduling once the app is closed.</summary>
    [ObservableProperty]
    private bool autoScrapeEnabled;

    /// <summary>Every scheduled time, editable on the Auto Scraper tab — each with its own
    /// independent Australia and International day(s)/discipline(s) selection (either or both can
    /// be disabled). Each row persists itself (via <see cref="SaveAutoScrapeSlots"/>) whenever its
    /// time or either country's settings change, same pattern as <see cref="CountryOptions"/>.</summary>
    public ObservableCollection<AutoScrapeSlotRow> AutoScrapeSlots { get; } = new();

    [RelayCommand]
    private void AddAutoScrapeSlot()
    {
        var row = new AutoScrapeSlotRow();
        row.Changed += SaveAutoScrapeSlots;
        AutoScrapeSlots.Add(row);
        SaveAutoScrapeSlots();
    }

    [RelayCommand]
    private void RemoveAutoScrapeSlot(AutoScrapeSlotRow? row)
    {
        if (row is null) return;
        AutoScrapeSlots.Remove(row);
        SaveAutoScrapeSlots();
    }

    private void SaveAutoScrapeSlots()
    {
        SaveSetting(s => s.AutoScrapeSlots = AutoScrapeSlots.Select(r => r.ToSlot()).ToList());
    }

    /// <summary>Closes the RabbitMQ connection (if one was opened this session) — called from
    /// App.OnExit.</summary>
    public ValueTask DisposeEventPublisherAsync() => _eventPublisher.DisposeAsync();

    [ObservableProperty]
    private string autoScrapeLastRunSummary = "";

    /// <summary>True once a newer release than the one currently running has been found on
    /// GitHub — drives the update banner's visibility in MainWindow.</summary>
    [ObservableProperty]
    private bool updateAvailable;

    [ObservableProperty]
    private string updateStatusText = "";

    [ObservableProperty]
    private string updateButtonText = "Update Now";

    [ObservableProperty]
    private bool isUpdating;

    /// <summary>0-100 while the installer downloads. Only meaningful when
    /// <see cref="IsUpdateDownloadIndeterminate"/> is false — see UpdateChecker.DownloadInstallerAsync.</summary>
    [ObservableProperty]
    private double updateDownloadPercent;

    /// <summary>True if the download has no known total size to compute a percentage against
    /// (GitHub didn't send a Content-Length) — shows a spinning bar instead of a stalled 0%, so
    /// it's still clear the download is progressing rather than stuck.</summary>
    [ObservableProperty]
    private bool isUpdateDownloadIndeterminate;

    // --- "Switch version" (see the Browser tab) — installs any past release on demand, not just
    // whatever CheckAsync surfaces automatically, so this is also how a downgrade is done: pick
    // an older version from the list and click Install, same silent install/relaunch flow as the
    // update banner's "Update Now". ---

    public ObservableCollection<ReleaseInfo> AvailableReleases { get; } = new();

    [ObservableProperty]
    private ReleaseInfo? selectedRelease;

    [ObservableProperty]
    private bool isLoadingReleases;

    [ObservableProperty]
    private bool isInstallingVersion;

    [ObservableProperty]
    private string versionPickerStatus = "";

    partial void OnSelectedReleaseChanged(ReleaseInfo? value) => InstallSelectedVersionCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingReleasesChanged(bool value) => LoadReleasesCommand.NotifyCanExecuteChanged();

    partial void OnIsInstallingVersionChanged(bool value)
    {
        LoadReleasesCommand.NotifyCanExecuteChanged();
        InstallSelectedVersionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Populates <see cref="AvailableReleases"/> from GitHub — not done automatically at
    /// startup (unlike the newer-version banner's own background check) since this is an
    /// on-demand "show me the options" action, not something worth an API call every launch.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadReleases))]
    private async Task LoadReleasesAsync()
    {
        IsLoadingReleases = true;
        VersionPickerStatus = "Checking available versions...";

        var releases = await UpdateChecker.ListReleasesAsync();
        AvailableReleases.Clear();
        foreach (var release in releases) AvailableReleases.Add(release);

        SelectedRelease = AvailableReleases.FirstOrDefault(r => r.DisplayText != $"v{AppVersion}") ?? AvailableReleases.FirstOrDefault();
        VersionPickerStatus = AvailableReleases.Count == 0
            ? "Couldn't load releases from GitHub — check your connection, or the repo isn't configured yet."
            : $"{AvailableReleases.Count} version(s) available. Currently running v{AppVersion}.";

        IsLoadingReleases = false;
    }

    private bool CanLoadReleases() => !IsLoadingReleases && !IsInstallingVersion;

    /// <summary>Downloads and silently installs <see cref="SelectedRelease"/> — identical flow to
    /// <see cref="UpdateNowAsync"/>, just against whichever release the user picked instead of
    /// always the newest. Inno Setup's own [Files] entries use "ignoreversion" (see
    /// installer/RaceNetScraper.iss), so installing an older build over a newer one really does
    /// downgrade the files on disk rather than being silently skipped.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallSelectedVersion))]
    private async Task InstallSelectedVersionAsync()
    {
        if (SelectedRelease is not { } release) return;

        try
        {
            IsInstallingVersion = true;
            VersionPickerStatus = $"Downloading {release.DisplayText}... 0%";

            IProgress<double> downloadProgress = new Progress<double>(pct =>
            {
                VersionPickerStatus = pct < 0
                    ? $"Downloading {release.DisplayText}..."
                    : $"Downloading {release.DisplayText}... {pct:0}%";
            });

            var installerPath = await UpdateChecker.DownloadInstallerAsync(release.DownloadUrl, downloadProgress);

            VersionPickerStatus = "Launching installer...";
            UpdateChecker.LaunchInstaller(installerPath);

            // Same reasoning as UpdateNowAsync: the installer needs this process's files
            // unlocked, so closing right after launching it is what makes that possible.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            VersionPickerStatus = $"Install failed: {ex.Message}";
            IsInstallingVersion = false;
        }
    }

    private bool CanInstallSelectedVersion() => SelectedRelease is not null && !IsInstallingVersion;

    /// <summary>True once a developer notice (see DeveloperNoticeChecker) the user hasn't already
    /// dismissed has been found — drives the "Developer Note" banner's visibility.</summary>
    [ObservableProperty]
    private bool developerNoticeVisible;

    [ObservableProperty]
    private string developerNoticeTitle = "";

    [ObservableProperty]
    private string developerNoticeMessage = "";

    public ObservableCollection<MeetingRow> Meetings { get; } = new();

    /// <summary>Read once from the running build's own assembly metadata (see UpdateChecker,
    /// which already reads this to compare against GitHub releases) — the single source both the
    /// title bar and header subtitle display from, so they can never show a different version than
    /// what the update check itself is comparing against.</summary>
    public string AppVersion => UpdateChecker.CurrentVersionText;

    public string WindowTitle => $"Racenet Meetings Scraper v{AppVersion}  ·  by VM";

    partial void OnIsBusyChanged(bool value)
    {
        ScrapeCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ClearResultsCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves a meeting higher in <see cref="Meetings"/> — since the race-scrape loop in
    /// <see cref="ScrapeDatesAsync"/> always picks whichever unfinished meeting currently sits
    /// highest in this same list, reordering it here (before or even while a scrape is running)
    /// directly changes what gets scraped next.</summary>
    [RelayCommand]
    private void MoveMeetingUp(MeetingRow? row)
    {
        if (row is null) return;
        var index = Meetings.IndexOf(row);
        if (index <= 0) return;
        Meetings.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveMeetingDown(MeetingRow? row)
    {
        if (row is null) return;
        var index = Meetings.IndexOf(row);
        if (index < 0 || index >= Meetings.Count - 1) return;
        Meetings.Move(index, index + 1);
    }

    /// <summary>Resumes the scrape stopped via <see cref="Stop"/> — re-enters
    /// <see cref="ScrapeDatesAsync"/> with the same request, which skips every date/discipline
    /// combo already fetched and every race already recorded, so only what didn't finish gets
    /// (re)done.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        if (_lastScrapeRequest is not { } request) return;
        await ScrapeDatesAsync(
            request.SubRequests, request.CountryFilter, request.CourseFilter,
            request.ForceUploadToS3, isResume: true);
    }

    private bool CanContinue() => !IsBusy && _canResume && _lastScrapeRequest is not null;

    partial void OnIsStoppingChanged(bool value) => StopCommand.NotifyCanExecuteChanged();

    /// <summary>Cancels the running scrape. Takes effect at the next checkpoint the scraper
    /// checks — typically within a few seconds, once the in-flight page navigation/settle
    /// finishes — rather than instantly, since Playwright's own calls don't observe the token
    /// directly.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (!IsBusy || _cts is null) return;
        IsStopping = true;
        StatusText = "Stopping — finishing the current request...";
        _cts.Cancel();
    }

    private bool CanStop() => IsBusy && !IsStopping;

    /// <summary>Clears the results grid/status (from either a finished, stopped, or failed run) —
    /// shared by both the Scraper and Auto Scraper tabs since they show the same
    /// <see cref="Meetings"/> collection. Disabled while a scrape is running so it can't be used
    /// to wipe an in-progress run's rows out from under it.</summary>
    [RelayCommand(CanExecute = nameof(CanClearResults))]
    private void ClearResults()
    {
        Meetings.Clear();
        _lastResults.Clear();
        _raceDetails.Clear();
        _raceAttempts.Clear();
        _lastScrapeRequest = null;
        _canResume = false;
        ContinueCommand.NotifyCanExecuteChanged();
        StatusText = "Ready.";
    }

    private bool CanClearResults() => !IsBusy;

    /// <summary>Re-reads settings.json fresh, applies one field, and saves — rather than mutating
    /// the long-lived <see cref="_settings"/> field directly. BucketViewModel keeps its own
    /// independent AppSettings instance for the access/secret key fields it owns, and
    /// AppSettings.Save() serializes the WHOLE object: saving straight from this stale in-memory
    /// copy would silently overwrite whatever BucketViewModel had just written with whatever
    /// values were in memory here since startup (confirmed live — this is exactly why access/
    /// secret key edits on the Bucket tab were getting wiped out again shortly after).</summary>
    private static void SaveSetting(Action<AppSettings> mutate)
    {
        var settings = AppSettings.Load();
        mutate(settings);
        settings.Save();
    }

    partial void OnDownloadFolderChanged(string value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.DownloadFolder = value);
    }

    partial void OnAutoExportAfterScrapeChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoExportAfterScrape = value);
    }

    partial void OnUploadToS3Changed(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.UploadToS3 = value);
    }

    partial void OnS3BucketNameChanged(string value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.S3BucketName = value);
    }

    partial void OnAutoScrapeEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeEnabled = value);
    }

    // Checking one RadioButton in the "ScraperBrowser" group unchecks whichever one was
    // previously checked, which fires that sibling's changed handler with false at the same time
    // (as the framework unchecks it) — reacting to that false transition too would just mean
    // whichever handler happens to run last decides the saved value instead of the one the user
    // actually clicked, hence the `!value` guard below on every one of these.
    partial void OnUseChromeBrowserChanged(bool value)
    {
        if (_loadingSettings || !value) return;
        SaveSetting(s => s.ScraperBrowser = nameof(ScraperBrowserChoice.Chrome));
    }

    partial void OnUseFirefoxBrowserChanged(bool value)
    {
        if (_loadingSettings || !value) return;
        SaveSetting(s => s.ScraperBrowser = nameof(ScraperBrowserChoice.Firefox));
    }

    partial void OnUseEdgeBrowserChanged(bool value)
    {
        if (_loadingSettings || !value) return;
        SaveSetting(s => s.ScraperBrowser = nameof(ScraperBrowserChoice.Edge));
    }

    /// <summary>Ticks every 30s on the UI thread. Every <see cref="AutoScrapeSlots"/> entry whose
    /// time matches now, times its enabled country run(s) (Australia and/or International) into
    /// one combined <see cref="ScrapeDatesAsync"/> call — a single browser session that lists
    /// every one of them up front and only then scrapes full race detail for the combined set, so
    /// e.g. Australia's and International's meetings both end up in the grid together rather than
    /// International only appearing once Australia is already fully done. Bounded to
    /// today/tomorrow/day-after-tomorrow since that's as far ahead as Racenet's own date tabs go
    /// (see RaceNetScraperService.MaxTabDaysAhead) — there is no "yesterday" option at all, unlike
    /// Punters, since Racenet has no past-date tabs to drive either.</summary>
    private async Task AutoScrapeTickAsync()
    {
        if (!AutoScrapeEnabled || IsBusy) return;

        var now = DateTime.Now;
        var todayPrefix = now.ToString("yyyy-MM-dd");
        _firedAutoScrapeSlotKeys.RemoveWhere(k => !k.StartsWith(todayPrefix, StringComparison.Ordinal));

        var currentTime = now.ToString("HH:mm");
        var dueSlots = AutoScrapeSlots
            .Where(s => s.Time == currentTime)
            .Where(s => _firedAutoScrapeSlotKeys.Add($"{todayPrefix}T{s.Time}"))
            .ToList();
        if (dueSlots.Count == 0) return;

        var today = DateOnly.FromDateTime(now);
        var requests = new List<ScrapeSubRequest>();
        var firedLabels = new List<string>();

        foreach (var slot in dueSlots)
        foreach (var (groupName, run) in new[] { ("Australia", slot.Australia), ("International", slot.International) })
        {
            if (!run.Enabled) continue;

            var disciplines = new List<Discipline>();
            if (run.Horses) disciplines.Add(Discipline.Horses);
            if (run.Greyhounds) disciplines.Add(Discipline.Greyhounds);
            if (run.Harness) disciplines.Add(Discipline.Harness);
            if (disciplines.Count == 0) continue;

            var dates = new List<DateOnly>();
            if (run.IncludeToday) dates.Add(today);
            if (run.IncludeTomorrow) dates.Add(today.AddDays(1));
            if (run.IncludeDayAfterTomorrow) dates.Add(today.AddDays(2));
            if (dates.Count == 0) continue;

            requests.Add(new ScrapeSubRequest(disciplines, dates, groupName));
            firedLabels.Add($"{slot.Time} {groupName}");
        }

        if (requests.Count == 0) return;

        await ScrapeDatesAsync(requests, countryFilter: "", courseFilter: "", forceUploadToS3: true);

        AutoScrapeLastRunSummary = $"[{string.Join(", ", firedLabels)}] {StatusText}";
        SaveSetting(s =>
        {
            s.AutoScrapeLastRunUtc = DateTime.UtcNow;
            s.AutoScrapeLastRunSummary = AutoScrapeLastRunSummary;
        });
    }

    private async Task CheckForUpdatesAsync()
    {
        var update = await UpdateChecker.CheckAsync();
        if (update is null) return;

        _pendingUpdate = update;
        UpdateStatusText = $"Update available: v{update.Version}";
        UpdateAvailable = true;
    }

    private async Task CheckForDeveloperNoticeAsync()
    {
        var notice = await DeveloperNoticeChecker.CheckAsync();
        if (notice is null) return;
        if (notice.Id == _settings.LastSeenNoticeId) return;

        _pendingNoticeId = notice.Id;
        DeveloperNoticeTitle = notice.Title;
        DeveloperNoticeMessage = notice.Message;
        DeveloperNoticeVisible = true;
    }

    /// <summary>The only way this banner closes — a deliberate "I've read this" action rather
    /// than an easy-to-misclick X, so dismissing it actually means the user saw the message.</summary>
    [RelayCommand]
    private void DismissDeveloperNotice()
    {
        if (_pendingNoticeId is null) return;

        SaveSetting(s => s.LastSeenNoticeId = _pendingNoticeId);
        DeveloperNoticeVisible = false;
    }

    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNowAsync()
    {
        if (_pendingUpdate is null) return;

        try
        {
            IsUpdating = true;
            UpdateDownloadPercent = 0;
            IsUpdateDownloadIndeterminate = false;
            UpdateButtonText = "Downloading... 0%";

            IProgress<double> downloadProgress = new Progress<double>(pct =>
            {
                if (pct < 0)
                {
                    IsUpdateDownloadIndeterminate = true;
                    UpdateButtonText = "Downloading...";
                    return;
                }

                UpdateDownloadPercent = pct;
                UpdateButtonText = $"Downloading... {pct:0}%";
            });

            var installerPath = await UpdateChecker.DownloadInstallerAsync(_pendingUpdate.DownloadUrl, downloadProgress);

            UpdateButtonText = "Launching installer...";
            UpdateChecker.LaunchInstaller(installerPath);

            // The installer needs this process's files unlocked to overwrite them — closing
            // right after launching it (rather than waiting for it to finish) is what makes that
            // possible, same as a user manually closing the app before running Setup.exe by hand.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText = $"Update failed: {ex.Message}";
            IsUpdating = false;
            IsUpdateDownloadIndeterminate = false;
            UpdateDownloadPercent = 0;
            UpdateButtonText = "Update Now";
        }
    }

    private bool CanUpdateNow() => UpdateAvailable && !IsUpdating;

    partial void OnUpdateAvailableChanged(bool value) => UpdateNowCommand.NotifyCanExecuteChanged();

    partial void OnIsUpdatingChanged(bool value) => UpdateNowCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void BrowseDownloadFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the default folder to save scraped JSON files to",
            SelectedPath = DownloadFolder,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DownloadFolder = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Scrapes meetings for every selected discipline on the selected date, and always follows up
    /// by scraping full runner/jockey/form detail for every race in every matching meeting — there
    /// is no separate "meetings only" mode; one Scrape click always gets everything.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScrape))]
    private async Task ScrapeAsync()
    {
        var disciplines = new List<Discipline>();
        if (ScrapeHorses) disciplines.Add(Discipline.Horses);
        if (ScrapeGreyhounds) disciplines.Add(Discipline.Greyhounds);
        if (ScrapeHarness) disciplines.Add(Discipline.Harness);

        if (disciplines.Count == 0)
        {
            StatusText = "Select at least one discipline (Horses / Greyhounds / Harness).";
            return;
        }

        var subRequest = new ScrapeSubRequest(disciplines, new List<DateOnly> { DateOnly.FromDateTime(SelectedDate) }, GroupFilter: "");
        await ScrapeDatesAsync(
            new List<ScrapeSubRequest> { subRequest },
            CountryCodeFilter.Trim(),
            CourseNameFilter.Trim());
    }

    /// <summary>
    /// Shared scrape orchestration behind both the manual Scrape button (a single-element
    /// <paramref name="requests"/> list built from <see cref="SelectedDate"/>) and the auto-scrape
    /// timer (<see cref="AutoScrapeTickAsync"/>, one sub-request per enabled country) — one
    /// browser session covers every discipline/date combo across every sub-request, with results
    /// from all of them accumulating into the same <see cref="Meetings"/> grid. Listing (Phase 1)
    /// happens for every sub-request before race-detail scraping (Phase 2) starts for any of them,
    /// so e.g. Australia's and International's meetings both show up together quickly rather than
    /// International only appearing once Australia's full race detail is already done.
    /// </summary>
    /// <param name="forceUploadToS3">Auto-scrape always passes true here — its whole point is
    /// unattended delivery into the bucket for TroyenRaceIngestor, so it uploads regardless of
    /// whether the manual Scraper tab's "Also upload to S3" checkbox happens to be on.</param>
    /// <param name="isResume">True when called from <see cref="ContinueAsync"/> after a Stop —
    /// skips the usual "clear everything and start fresh" step, so meetings/races already
    /// captured (and this run's original request, in <see cref="_lastScrapeRequest"/>) survive
    /// into this call instead of being wiped.</param>
    private async Task ScrapeDatesAsync(
        List<ScrapeSubRequest> requests, string countryFilter, string courseFilter,
        bool forceUploadToS3 = false, bool isResume = false)
    {
        var browser = SelectedScraperBrowser;
        if (!ScraperBrowserAvailability.IsInstalled(browser))
        {
            StatusText = $"{browser} isn't available on this PC. {ScraperBrowserAvailability.InstallHint(browser)}";
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        IsStopping = false;
        if (!isResume)
        {
            Meetings.Clear();
            _lastResults.Clear();
            _raceDetails.Clear();
            _raceAttempts.Clear();
            _lastScrapeRequest = new ScrapeRequest(requests, countryFilter, courseFilter, forceUploadToS3);
        }
        _canResume = false;
        StatusText = isResume ? "Resuming..." : "Starting browser...";

        IProgress<string> progress = new Progress<string>(msg => StatusText = msg);
        var disciplineFailures = new List<string>();

        // Running totals for auto-export, which now happens per-meeting (see the export call
        // inside the race-detail loop below) rather than once at the very end.
        var totalFileCount = 0;
        var totalMeetingFolderCount = 0;
        var totalS3UploadedCount = 0;
        var totalS3FailedCount = 0;

        // Per-meeting RabbitMQ event publishing (independent, non-fatal notification path — see the
        // publish call in the race-detail loop below). One correlation id is shared by every
        // meeting event from this single scrape run.
        var totalEventsPublishedCount = 0;
        var totalEventsFailedCount = 0;
        var eventCorrelationId = Guid.NewGuid();

        // A race that keeps failing (e.g. Racenet never returns event data for it — confirmed
        // live) would otherwise retry forever, every time its meeting's row is revisited, since
        // nothing else ever marks it resolved. Capping attempts and counting an exhausted race as
        // "resolved" (not "succeeded") is what lets a meeting with a genuinely broken race still
        // finish and let the run move on, instead of stalling at 0% detail indefinitely.
        //
        // Racenet occasionally lists a race with no id at all (e.g. a TBA/placeholder race —
        // confirmed live) — ScrapeRaceAsync immediately rejects that as unscrapable, but
        // _raceDetails/_raceAttempts are keyed by id, so a null id could never be tracked or
        // capped by that machinery, and RaceIsResolved required a non-null id to ever return
        // true. The result: a meeting containing one of these never became "resolved", so the
        // Phase 2 loop below picked its row again and again forever, re-attempting that same
        // race every single pass and never finishing. Falling back to a per-instance key when
        // Id is missing lets such a race flow through the normal attempt-cap/give-up path
        // instead.
        const int MaxRaceAttempts = 3;
        string RaceKey(RaceEvent e) => e.Id ?? $"#{RuntimeHelpers.GetHashCode(e)}";
        bool RaceIsResolved(RaceEvent e) =>
            _raceDetails.ContainsKey(RaceKey(e)) || _raceAttempts.GetValueOrDefault(RaceKey(e)) >= MaxRaceAttempts;

        // A meeting counts as fully done once every one of its races is resolved (has detail, or
        // has exhausted its retry attempts) AND (whichever of these are actually enabled) its S3
        // upload/local export has run — evaluated live off current checkbox state and
        // _raceDetails/row flags rather than any separate "done" list, so it works identically
        // whether this is a fresh run or a resume.
        bool RowNeedsUploadOrExport(MeetingRow r) =>
            ((UploadToS3 || forceUploadToS3) && !r.UploadedToS3) ||
            (AutoExportAfterScrape && !string.IsNullOrWhiteSpace(DownloadFolder) && !r.ExportedLocally) ||
            (_settings.RabbitMqEnabled && !r.EventPublished);
        bool RowIsFullyDone(MeetingRow r) =>
            r.Meeting.Events.All(RaceIsResolved) && !RowNeedsUploadOrExport(r);

        try
        {
            await using IRaceNetScraperService service = new RaceNetScraperService();
            await service.InitializeAsync(new ScraperOptions { Headless = Headless, Browser = browser }, token);

            // Phase 1 — list every requested date/discipline combo's meetings up front (just
            // meeting-list requests, no per-race scraping yet), so the whole list is visible —
            // and reorderable via the grid's Priority Up/Down buttons — before Phase 2 below
            // commits to any particular scrape order. Fetched once per unique (date, discipline)
            // combo even if more than one sub-request wants it (e.g. an Australia sub-request and
            // an International sub-request both wanting Today's Harness) — one fetch already
            // returns every country/group, so each matching sub-request just applies its own group
            // filter to that one result rather than fetching it again. A combo already fetched on
            // an earlier attempt (isResume, tracked via _lastResults) is skipped rather than
            // re-fetched.
            // Guards against the same real-world meeting being listed twice within one run (e.g.
            // Auto Scrape with both "Tomorrow" and "Day after tomorrow" enabled together, or a
            // single slot's Australia and International sub-requests both covering the same date).
            // If Racenet's per-date data ever isn't strictly scoped to the requested day — the same
            // kind of adjacent-day bleed confirmed live on the sibling Punters scraper — the same
            // meeting could get added under two different dates. The second occurrence would then
            // sit in the grid forever stuck at 0 races/0% detail: Phase 2 below treats a row as
            // already done once every one of its races is in the shared _raceDetails cache (keyed
            // by race id, which the duplicate shares with the original), so it'd be silently
            // skipped rather than actually processed. Seeded from whatever's already in Meetings so
            // this also holds across a resume.
            var addedMeetingIds = new HashSet<(Discipline Discipline, string Id)>(
                Meetings.Where(r => r.Meeting.Id is not null).Select(r => (r.DisciplineEnum, r.Meeting.Id!)));

            var uniqueCombos = requests
                .SelectMany(r => r.Dates.SelectMany(date => r.Disciplines.Select(discipline => (date, discipline))))
                .Distinct();
            foreach (var (date, discipline) in uniqueCombos)
            {
                token.ThrowIfCancellationRequested();
                if (isResume && _lastResults.ContainsKey((date, discipline))) continue;

                try
                {
                    var result = await VpnRotator.RunWithRotationOnBlockAsync(
                        () => service.ScrapeMeetingsAsync(discipline, date, progress: progress, cancellationToken: token),
                        progress, token);

                    _lastResults[(date, discipline)] = result;

                    // Every sub-request wanting this combo applies its own group/country/course
                    // filter right away, so nothing downstream (grid, race-detail scraping,
                    // export) ever sees or processes a meeting that doesn't match — this is what
                    // makes the filters actually skip the slow per-race scraping for excluded
                    // meetings, not just hide them.
                    var matchingRequests = requests.Where(r => r.Dates.Contains(date) && r.Disciplines.Contains(discipline)).ToList();
                    var addedAny = false;
                    foreach (var request in matchingRequests)
                    foreach (var group in result.MeetingsGrouped)
                    {
                        if (request.GroupFilter.Length > 0 && group.Group != request.GroupFilter) continue;

                        foreach (var meeting in group.Meetings.Where(m => MatchesFilters(m, countryFilter, courseFilter)))
                        {
                            addedAny = true;
                            if (meeting.Id is not null && !addedMeetingIds.Add((discipline, meeting.Id)))
                            {
                                continue; // already listed under a different date/group this run
                            }

                            Meetings.Add(MeetingRow.From(discipline, group.Group ?? "", meeting, date));
                        }
                    }

                    if (!addedAny && (countryFilter.Length > 0 || courseFilter.Length > 0 || matchingRequests.Any(r => r.GroupFilter.Length > 0)))
                    {
                        progress.Report($"[R-{discipline.Code()}] {date:yyyy-MM-dd}: No meetings matched the country/course filter.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"[R-{discipline.Code()}] {date:yyyy-MM-dd}: Failed: {ex.Message}";
                    disciplineFailures.Add(message);
                    StatusText = message;
                }
            }

            // Phase 2 — race every meeting's full detail. Always re-picks whichever unfinished
            // meeting currently sits highest in Meetings (rather than snapshotting an order up
            // front), so the Priority Up/Down buttons have a real, live effect on what gets
            // scraped next — including while this phase is already running.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var row = Meetings.FirstOrDefault(r => !RowIsFullyDone(r));
                if (row is null) break;

                var discipline = row.DisciplineEnum;

                // Only races this meeting doesn't already have detail for — on a fresh run
                // that's all of them, on a resume it's whatever didn't finish (or was never
                // reached) before the previous Stop. Scraped one race at a time, deliberately
                // NOT concurrently — each race is a full page navigation (see
                // RaceNetScraperService's class remarks), and running several at once would mean
                // juggling multiple pages on the one shared browser context this service keeps
                // for its whole session.
                foreach (var raceEvent in row.Meeting.Events)
                {
                    var raceKey = RaceKey(raceEvent);
                    if (_raceDetails.ContainsKey(raceKey)) continue;
                    if (_raceAttempts.GetValueOrDefault(raceKey) >= MaxRaceAttempts) continue;

                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var detail = await VpnRotator.RunWithRotationOnBlockAsync(
                            () => service.ScrapeRaceAsync(discipline, row.Meeting, raceEvent, progress, token),
                            progress, token);
                        if (detail.RaceId is not null) _raceDetails[detail.RaceId] = detail;
                        row.RacesWithDetail++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // RacesProcessed (which drives ProgressPercent) only increments once a
                        // race is FINALLY resolved — succeeded above, or given up on below —
                        // never on a mid-retry attempt. Counting every attempt let the percentage
                        // hit a false 100% while races were still stuck retrying in the
                        // background (confirmed live: "100%" shown with the status bar still
                        // actively loading a race) — the percentage must only ever reflect races
                        // that are actually finished, not attempts made. The ongoing status-bar
                        // text below already shows the run is still working during a retry.
                        var attempts = _raceAttempts[raceKey] = _raceAttempts.GetValueOrDefault(raceKey) + 1;
                        if (attempts >= MaxRaceAttempts)
                        {
                            row.RacesProcessed++;
                            progress.Report(
                                $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({row.MeetingName}) failed {attempts} time(s), giving up: {ex.Message}");
                            continue;
                        }

                        progress.Report(
                            $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({row.MeetingName}) failed (attempt {attempts}/{MaxRaceAttempts}), will retry: {ex.Message}");
                        continue;
                    }

                    row.RacesProcessed++;
                }

                // Don't upload/export/publish until every one of this meeting's races is actually
                // resolved (has detail, or has exhausted its retry attempts) — a race that still
                // needs another attempt is common (Racenet's per-race pages lazily hydrate; a race
                // can easily need a 2nd/3rd pass), and the steps below set UploadedToS3/
                // ExportedLocally/EventPublished permanently true the first time they run. Running
                // them on an interim pass — before _raceDetails actually has every race's detail —
                // meant the meeting got exported/uploaded/published with whatever subset of races
                // had succeeded so far (often zero), and never again once the rest finished
                // retrying, since those flags were already set (confirmed live: a meeting's
                // exported folder containing only its *-meeting.json, no per-race DataDump files,
                // even though every race eventually got detail). Looping back here instead lets the
                // outer while(true) revisit this row once its still-unresolved races get their next
                // attempt, same as it already does for RowIsFullyDone's own race-resolution check.
                if (!row.Meeting.Events.All(RaceIsResolved))
                {
                    continue;
                }

                // Generate this meeting's export file name once (and keep it on the row), so the
                // S3 upload and the RabbitMQ event below both reference the exact same name —
                // MeetingFileName uses DateTime.Now, so regenerating it would drift by a second
                // and the event's meetingFileName wouldn't match the file that was actually
                // uploaded, including when a Stop/resume lands between the two steps.
                var meetingFileName = row.MeetingFileName ??= MeetingFileName(discipline);

                // The slug every one of this meeting's S3 objects is prefixed with — computed
                // the same way UploadMeetingToS3Async/ExportMeetingAsync do (deterministic for a
                // given meeting), so the event's meetingSlug matches the actual bucket prefix.
                // Carried in the event because it can't be reconstructed from the meeting name
                // alone (Racenet's slug includes the date, e.g. "swan-hill-20260922").
                var meetingSlug = Slugify(row.Meeting.Slug ?? row.Meeting.Name ?? row.Meeting.Id ?? "meeting");

                // Uploaded to S3 independently of the local folder export below — so S3
                // delivery doesn't depend on a download folder being configured at all.
                // forceUploadToS3 is what lets auto-scrape always push to the bucket
                // regardless of the manual "Also upload to S3" checkbox's current state.
                // Guarded by UploadedToS3 so a resumed run never uploads the same meeting twice.
                if ((UploadToS3 || forceUploadToS3) && !row.UploadedToS3)
                {
                    var (uploaded, failed) = await UploadMeetingToS3Async(discipline, row.Group, row.Meeting, meetingFileName);
                    totalS3UploadedCount += uploaded;
                    totalS3FailedCount += failed;
                    row.UploadedToS3 = true;
                    progress.Report(
                        $"[R-{discipline.Code()}] Uploaded {row.MeetingName} to S3: {uploaded} file(s)." +
                        (failed > 0 ? $" {failed} failed." : ""));
                }

                // Publish a "meeting.scraped" event as soon as this meeting is ready — a
                // parallel, lightweight notification alongside the S3 upload above, never a
                // replacement for it. Only the live per-meeting scrape path publishes; the
                // manual "Export JSON..." path (ExportMeetingAsync) deliberately does not, so
                // re-exporting already-scraped results never re-fires the event. Non-fatal by
                // contract: the publisher never throws, so a broker outage only bumps the
                // failed count and is surfaced in the status text — the scrape is unaffected.
                // Guarded by EventPublished so a resumed run never re-publishes; it's set even
                // on failure (like UploadedToS3), otherwise the row would never count as done.
                if (_settings.RabbitMqEnabled && !row.EventPublished)
                {
                    // Carry the full S3 object base name (slug-prefixed), matching the uploaded
                    // file exactly — e.g. "swan-hill-20260922-TR-2026-09-22-11-42-50-meeting.json".
                    // The upload above still gets the bare name; it prepends the slug itself.
                    var meetingObjectName = $"{meetingSlug}-{meetingFileName}";
                    var evt = MeetingScrapedEvent.Create(
                        discipline, row.Meeting, eventCorrelationId, _settings.RabbitMqDefaultPriority, meetingObjectName, meetingSlug);
                    if (await _eventPublisher.PublishMeetingScrapedAsync(evt, progress, token))
                        totalEventsPublishedCount++;
                    else
                        totalEventsFailedCount++;
                    row.EventPublished = true;
                }

                // Export this meeting right away instead of waiting for every other
                // meeting/discipline/date in this run to finish scraping too — so a long
                // multi-meeting scrape has already saved each meeting as soon as it's ready,
                // rather than losing everything scraped so far if the run is interrupted or
                // fails partway through. S3 upload is handled above, not here — passing
                // uploadToS3: false avoids uploading the same file twice. Guarded by
                // ExportedLocally so a resumed run never exports the same meeting twice.
                if (AutoExportAfterScrape && !string.IsNullOrWhiteSpace(DownloadFolder) && !row.ExportedLocally)
                {
                    var exportResult = await ExportMeetingAsync(discipline, row.Group, row.Meeting, DownloadFolder, uploadToS3: false);
                    totalFileCount += exportResult.FileCount;
                    totalMeetingFolderCount += exportResult.MeetingFolderCount;
                    row.ExportedLocally = true;
                    progress.Report($"[R-{discipline.Code()}] Exported {row.MeetingName}: {exportResult.FileCount} file(s).");
                }
            }

            if (_lastResults.Count > 0)
            {
                var totalDateCount = requests.SelectMany(r => r.Dates).Distinct().Count();
                var totalDisciplineCount = requests.SelectMany(r => r.Disciplines).Distinct().Count();
                StatusText = $"Done. {Meetings.Count} meeting(s) loaded from {totalDateCount} date(s) / {totalDisciplineCount} discipline(s), " +
                             $"{_raceDetails.Count} race(s) with full runner detail.";
                SystemSounds.Asterisk.Play();

                // Each meeting was already uploaded to S3 (if applicable) as soon as its races
                // finished scraping (see the upload call in the race-detail loop above) — this
                // just reports the running totals from those per-meeting uploads.
                if (UploadToS3 || forceUploadToS3)
                {
                    StatusText += totalS3FailedCount > 0
                        ? $" Uploaded {totalS3UploadedCount} file(s) to S3 ({totalS3FailedCount} failed — see above)."
                        : $" Uploaded {totalS3UploadedCount} file(s) to S3.";
                }

                if (_settings.RabbitMqEnabled)
                {
                    StatusText += totalEventsFailedCount > 0
                        ? $" Published {totalEventsPublishedCount} RabbitMQ event(s) ({totalEventsFailedCount} failed — see above)."
                        : $" Published {totalEventsPublishedCount} RabbitMQ event(s).";
                }

                if (AutoExportAfterScrape)
                {
                    if (string.IsNullOrWhiteSpace(DownloadFolder))
                    {
                        StatusText += " Auto-export skipped — no download folder set.";
                    }
                    else
                    {
                        StatusText += $" Auto-exported {totalFileCount} file(s) across {totalMeetingFolderCount} meeting folder(s) to {DownloadFolder} as each meeting finished.";
                    }
                }
            }
            else if (disciplineFailures.Count > 0)
            {
                // Keep the actual error visible instead of overwriting it with a generic
                // "see status messages above" — StatusText only ever holds the latest message,
                // so there's nowhere else to actually see it once this line replaces it.
                StatusText = "Finished with errors: " + string.Join(" | ", disciplineFailures);
            }
            else
            {
                StatusText = "Finished, but no meetings matched for the selected date(s)/discipline(s)/filters.";
            }
        }
        catch (OperationCanceledException)
        {
            _canResume = true;
            StatusText = $"Stopped by user. {Meetings.Count} meeting(s) loaded, " +
                          $"{_raceDetails.Count} race(s) with full runner detail before stopping. " +
                          "Click Continue to pick up where it left off.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scrape failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsStopping = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Splits a comma/whitespace-separated ISO2 list (e.g. "AU, NZ") into its codes.
    /// Blank input yields an empty set, meaning "no filter — all countries".</summary>
    private static string[] ParseCountryCodes(string countryFilter) =>
        countryFilter.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchesFilters(Meeting meeting, string countryFilter, string courseFilter)
    {
        var countryCodes = ParseCountryCodes(countryFilter);
        if (countryCodes.Length > 0)
        {
            var iso2 = meeting.Venue?.Country?.Iso2;
            if (iso2 is null || !countryCodes.Any(code => string.Equals(iso2, code, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (courseFilter.Length > 0)
        {
            if (meeting.Name is null || meeting.Name.IndexOf(courseFilter, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    private bool CanScrape() => !IsBusy;

    /// <summary>
    /// Exports everything scraped, always organized meeting-wise: one subfolder per meeting
    /// (named after its course), containing that meeting's "...-meeting.json" file plus a
    /// separate "R{n}-...-DataDump.json" file for each of its races that has runner detail.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportJsonAsync()
    {
        if (_lastResults.Count == 0)
        {
            StatusText = "Nothing to export yet — run a scrape first.";
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a folder to save the scraped JSON files",
            SelectedPath = DownloadFolder,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        // Remember the chosen folder as the new default for next time (manual or auto).
        DownloadFolder = dialog.SelectedPath;

        var result = await ExportJsonToFolderAsync(dialog.SelectedPath);
        StatusText = $"Exported {result.FileCount} file(s) across {result.MeetingFolderCount} meeting folder(s) to {dialog.SelectedPath}." +
                     FormatS3Suffix(result);
    }

    private readonly record struct ExportResult(int FileCount, int MeetingFolderCount, int S3UploadedCount, int S3FailedCount);

    private static string FormatS3Suffix(ExportResult result)
    {
        if (result.S3UploadedCount == 0 && result.S3FailedCount == 0) return "";
        var suffix = $" Uploaded {result.S3UploadedCount} file(s) to S3.";
        if (result.S3FailedCount > 0) suffix += $" {result.S3FailedCount} S3 upload(s) failed — see above.";
        return suffix;
    }

    private async Task<ExportResult> ExportJsonToFolderAsync(string targetFolder)
    {
        var meetingFolderCount = 0;
        var fileCount = 0;
        var s3UploadedCount = 0;
        var s3FailedCount = 0;

        foreach (var ((_, discipline), result) in _lastResults)
        {
            foreach (var group in result.MeetingsGrouped)
            {
                foreach (var meeting in group.Meetings)
                {
                    var meetingResult = await ExportMeetingAsync(discipline, group.Group ?? "", meeting, targetFolder, uploadToS3: UploadToS3);
                    fileCount += meetingResult.FileCount;
                    meetingFolderCount += meetingResult.MeetingFolderCount;
                    s3UploadedCount += meetingResult.S3UploadedCount;
                    s3FailedCount += meetingResult.S3FailedCount;
                }
            }
        }

        return new ExportResult(fileCount, meetingFolderCount, s3UploadedCount, s3FailedCount);
    }

    /// <summary>
    /// Exports a single meeting (its "...-meeting.json" plus one "R{n}-...-DataDump.json" per
    /// race that already has runner detail scraped) — the shared unit of work behind both the
    /// manual "Export JSON..." button (<see cref="ExportJsonToFolderAsync"/>, looping over every
    /// meeting from the last scrape) and per-meeting auto-export (called directly from
    /// <see cref="ScrapeDatesAsync"/> as soon as each meeting's races finish, rather than waiting
    /// for the whole scrape to complete).
    /// </summary>
    /// <param name="uploadToS3">Whether this call should also upload to S3. Kept as an explicit
    /// parameter rather than always reading the <see cref="UploadToS3"/> checkbox directly — the
    /// per-meeting export during a live scrape passes false, since S3 upload there is handled
    /// independently (see <see cref="UploadMeetingToS3Async"/>) to avoid uploading the same file
    /// twice; only the manual "Export JSON..." button passes the checkbox's live value.</param>
    private async Task<ExportResult> ExportMeetingAsync(Discipline discipline, string group, Meeting meeting, string targetFolder, bool uploadToS3)
    {
        var fileCount = 0;
        var s3UploadedCount = 0;
        var s3FailedCount = 0;

        var meetingFolderName = Slugify(meeting.Slug ?? meeting.Name ?? meeting.Id ?? "meeting");
        var meetingFolder = Path.Combine(targetFolder, meetingFolderName);
        Directory.CreateDirectory(meetingFolder);

        // Required top-level shape for TroyenRaceIngestor: data.meetingsGrouped[].{group,meetings[]}.
        var meetingPayload = new
        {
            data = new
            {
                meetingsGrouped = new[]
                {
                    new { group, meetings = new[] { BuildMeetingExport(meeting) } }
                }
            }
        };

        if (await WriteAndMaybeUploadAsync(meetingFolder, meetingFolderName, MeetingFileName(discipline), meetingPayload, uploadToS3))
            s3UploadedCount++;
        else if (uploadToS3)
            s3FailedCount++;
        fileCount++;

        foreach (var raceEvent in meeting.Events)
        {
            if (raceEvent.Id is null || !_raceDetails.TryGetValue(raceEvent.Id, out var detail))
                continue;

            if (await WriteAndMaybeUploadAsync(meetingFolder, meetingFolderName, DataDumpFileName(detail.RaceNumber), detail, uploadToS3))
                s3UploadedCount++;
            else if (uploadToS3)
                s3FailedCount++;
            fileCount++;
        }

        return new ExportResult(fileCount, 1, s3UploadedCount, s3FailedCount);
    }

    /// <summary>Writes one file locally (nested under its meeting folder, as always), and if
    /// <paramref name="uploadToS3"/> is set, also uploads the same content directly into the S3
    /// bucket's configured folder — flat, with the meeting slug folded into the filename instead
    /// of a per-meeting prefix, purely so files from different meetings can't collide once there's
    /// no folder nesting to keep them apart.</summary>
    /// <returns>true if an S3 upload was attempted and succeeded.</returns>
    private async Task<bool> WriteAndMaybeUploadAsync(string localFolder, string meetingFolderName, string fileName, object payload, bool uploadToS3)
    {
        var json = JsonSerializer.Serialize(payload, ScraperJsonOptions.Write);
        File.WriteAllText(Path.Combine(localFolder, fileName), json);

        if (!uploadToS3) return false;

        try
        {
            // Not _settings: that's loaded once at startup and never refreshed, so it would still
            // carry blank/stale S3 keys if they were entered on the Bucket tab after this window
            // opened — confirmed live as the reason bucket listing worked (BucketViewModel always
            // reloads fresh) while the scrape's own upload kept failing with the same keys.
            await S3JsonUploader.UploadAsync(AppSettings.Load(), $"{meetingFolderName}-{fileName}", json);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"S3 upload failed for {fileName}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Uploads a single meeting straight to S3 (no local temp folder involved) — called
    /// directly from <see cref="ScrapeDatesAsync"/> as soon as each meeting's races finish,
    /// independent of local folder export.</summary>
    private async Task<(int uploaded, int failed)> UploadMeetingToS3Async(Discipline discipline, string group, Meeting meeting, string meetingFileName)
    {
        var meetingFolderName = Slugify(meeting.Slug ?? meeting.Name ?? meeting.Id ?? "meeting");
        var uploaded = 0;
        var failed = 0;

        var meetingPayload = new
        {
            data = new
            {
                meetingsGrouped = new[]
                {
                    new { group, meetings = new[] { BuildMeetingExport(meeting) } }
                }
            }
        };

        var (u, f) = await UploadJsonToS3Async(meetingFolderName, meetingFileName, meetingPayload);
        uploaded += u; failed += f;

        foreach (var raceEvent in meeting.Events)
        {
            if (raceEvent.Id is null || !_raceDetails.TryGetValue(raceEvent.Id, out var detail))
                continue;

            (u, f) = await UploadJsonToS3Async(meetingFolderName, DataDumpFileName(detail.RaceNumber), detail);
            uploaded += u; failed += f;
        }

        return (uploaded, failed);
    }

    private async Task<(int uploaded, int failed)> UploadJsonToS3Async(string meetingFolderName, string fileName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ScraperJsonOptions.Write);
        try
        {
            // See the comment in WriteAndMaybeUploadAsync — AppSettings.Load() fresh, not _settings.
            await S3JsonUploader.UploadAsync(AppSettings.Load(), $"{meetingFolderName}-{fileName}", json);
            return (1, 0);
        }
        catch (Exception ex)
        {
            StatusText = $"S3 upload failed for {fileName}: {ex.Message}";
            return (0, 1);
        }
    }

    private bool CanExport() => !IsBusy && _lastResults.Count > 0;

    // "{TR|GR|HR}-{yyyy-MM-dd}-{HH-mm-ss}-meeting.json" — TroyenRaceIngestor only recognizes
    // those exact TR/GR/HR prefixes.
    private static string MeetingFileName(Discipline discipline) =>
        $"{discipline.FilePrefix()}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-meeting.json";

    // "R{n}-{yyyyMMddHHmmss}-DataDump.json"
    private static string DataDumpFileName(int raceNumber) =>
        $"R{raceNumber}-{DateTime.Now:yyyyMMddHHmmss}-DataDump.json";

    /// <summary>
    /// Builds a meeting matching TroyenRaceIngestor's MeetingFileDto shape exactly (field
    /// names/nesting).
    /// </summary>
    private object BuildMeetingExport(Meeting meeting) => new
    {
        id = meeting.Id,
        name = meeting.Name,
        meetingDateUtc = meeting.MeetingDateUtc,
        meetingDateLocal = meeting.MeetingDateLocal,
        meetingType = meeting.MeetingType,
        meetingCategory = meeting.MeetingCategory,
        meetingStage = meeting.MeetingStage,
        isFuture = meeting.IsFuture ?? IsMeetingInFuture(meeting),
        tabStatus = meeting.TabStatus,
        state = meeting.State,
        slug = meeting.Slug,
        trackComments = meeting.TrackComments,
        penetrometer = meeting.Penetrometer,
        railPosition = meeting.RailPosition,
        isAbandoned = meeting.IsAbandoned ?? false,
        showSpeedMaps = meeting.ShowSpeedMaps ?? true,
        showSectionals = meeting.ShowSectionals ?? true,
        showOdds = meeting.ShowOdds ?? true,
        venue = meeting.Venue,
        events = meeting.Events.Select(e => new
        {
            id = e.Id,
            meetingId = e.MeetingId ?? meeting.Id,
            slug = e.Slug,
            name = e.Name,
            startTime = e.StartTime,
            eventNumber = e.EventNumber,
            eventClass = e.EventClass,
            status = e.Status,
            distance = e.Distance,
            starters = e.Starters,
            isResulted = e.IsResulted,
            isAbandoned = e.IsAbandoned,
            racePrizeMoney = e.RacePrizeMoney,
            trackCondition = e.TrackCondition,
            weather = e.Weather,
            entryConditions = e.EntryConditions,
            prizeMoney = e.PrizeMoney
        })
    };

    private static bool IsMeetingInFuture(Meeting meeting) =>
        !DateOnly.TryParse(meeting.MeetingDateLocal, out var d) || d >= DateOnly.FromDateTime(DateTime.Today);

    private static string Slugify(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
