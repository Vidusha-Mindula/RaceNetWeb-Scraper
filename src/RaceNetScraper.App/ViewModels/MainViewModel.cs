using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceNetScraper.App.Services;
using RaceNetScraper.Core.Scraping;
using RaceNetScraper.Shared.Json;
using RaceNetScraper.Shared.Models;
using RaceNetScraper.Shared.Scraping;

namespace RaceNetScraper.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<(DateOnly Date, Discipline Discipline), ScrapeResult> _lastResults = new();

    /// <summary>Race detail keyed by raceId.</summary>
    private readonly Dictionary<string, RaceDetail> _raceDetails = new();

    private readonly AppSettings _settings = AppSettings.Load();
    private bool _loadingSettings;

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

    private sealed record ScrapeRequest(
        List<Discipline> Disciplines, List<DateOnly> Dates, string CountryFilter, string CourseFilter, bool ForceUploadToS3);

    /// <summary>Ticks on the UI thread (via WPF's Dispatcher) so its handler can safely touch
    /// <see cref="Meetings"/> and other bound properties directly, the same as a button click —
    /// a background <see cref="System.Threading.Timer"/> would require manual Dispatcher.Invoke
    /// marshaling to avoid a cross-thread collection exception.</summary>
    private readonly DispatcherTimer _autoScrapeTimer;

    /// <summary>"{yyyy-MM-dd}T{HH:mm}" of the last slot that actually fired — guards against
    /// firing twice for the same configured time if a tick happens to land on it more than once.</summary>
    private string? _lastAutoScrapeSlotKey;

    public MainViewModel()
    {
        _loadingSettings = true;
        DownloadFolder = _settings.DownloadFolder;
        AutoExportAfterScrape = _settings.AutoExportAfterScrape;
        UploadToS3 = _settings.UploadToS3;
        S3BucketName = _settings.S3BucketName;
        AutoScrapeEnabled = _settings.AutoScrapeEnabled;
        AutoScrapeTimesOfDay = _settings.AutoScrapeTimesOfDay;
        AutoScrapeIncludeToday = _settings.AutoScrapeIncludeToday;
        AutoScrapeIncludeTomorrow = _settings.AutoScrapeIncludeTomorrow;
        AutoScrapeIncludeDayAfterTomorrow = _settings.AutoScrapeIncludeDayAfterTomorrow;
        AutoScrapeHorses = _settings.AutoScrapeHorses;
        AutoScrapeGreyhounds = _settings.AutoScrapeGreyhounds;
        AutoScrapeHarness = _settings.AutoScrapeHarness;
        AutoScrapeLastRunSummary = _settings.AutoScrapeLastRunSummary;
        UseFirefoxBrowser = _settings.ScraperBrowser == nameof(ScraperBrowserChoice.Firefox);
        UseEdgeBrowser = _settings.ScraperBrowser == nameof(ScraperBrowserChoice.Edge);
        UseChromeBrowser = !UseFirefoxBrowser && !UseEdgeBrowser;
        _loadingSettings = false;

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

    /// <summary>Optional filter: only scrape meetings whose venue country matches this ISO2 code (e.g. "AU", "NZ", "US"). Blank = no filter.</summary>
    [ObservableProperty]
    private string countryCodeFilter = "";

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
    /// listed time in <see cref="AutoScrapeTimesOfDay"/>, for as long as this app stays open —
    /// there is no scheduling once the app is closed. Off by default — if this app is installed on
    /// more than one PC, having it on everywhere means every PC scrapes/uploads at the same
    /// scheduled times, so it's opt-in per machine.</summary>
    [ObservableProperty]
    private bool autoScrapeEnabled;

    /// <summary>Comma-separated 24h "HH:mm" times, e.g. "06:00,18:00" — fires once per listed time
    /// each day, so multiple daily runs are just multiple entries here.</summary>
    [ObservableProperty]
    private string autoScrapeTimesOfDay = "06:00,18:00";

    [ObservableProperty]
    private bool autoScrapeIncludeToday = true;

    [ObservableProperty]
    private bool autoScrapeIncludeTomorrow = true;

    [ObservableProperty]
    private bool autoScrapeIncludeDayAfterTomorrow = true;

    [ObservableProperty]
    private bool autoScrapeHorses = true;

    [ObservableProperty]
    private bool autoScrapeGreyhounds = true;

    [ObservableProperty]
    private bool autoScrapeHarness = true;

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
            request.Disciplines, request.Dates, request.CountryFilter, request.CourseFilter,
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

    partial void OnAutoScrapeTimesOfDayChanged(string value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeTimesOfDay = value);
    }

    partial void OnAutoScrapeIncludeTodayChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeIncludeToday = value);
    }

    partial void OnAutoScrapeIncludeTomorrowChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeIncludeTomorrow = value);
    }

    partial void OnAutoScrapeIncludeDayAfterTomorrowChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeIncludeDayAfterTomorrow = value);
    }

    partial void OnAutoScrapeHorsesChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeHorses = value);
    }

    partial void OnAutoScrapeGreyhoundsChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeGreyhounds = value);
    }

    partial void OnAutoScrapeHarnessChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.AutoScrapeHarness = value);
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

    /// <summary>Ticks every 30s on the UI thread; fires <see cref="ScrapeDatesAsync"/> once per
    /// configured time-of-day slot, for whichever days/disciplines are configured for auto-scrape
    /// (independent of the manual panel's own selections above), across every country/course.
    /// Bounded to today/tomorrow/day-after-tomorrow since that's as far ahead as Racenet's own
    /// date tabs go (see RaceNetScraperService.MaxTabDaysAhead) — there is no "yesterday" option
    /// at all, unlike Punters, since Racenet has no past-date tabs to drive either.</summary>
    private async Task AutoScrapeTickAsync()
    {
        if (!AutoScrapeEnabled || IsBusy) return;

        var now = DateTime.Now;
        var currentSlot = now.ToString("HH:mm");
        var configuredTimes = AutoScrapeTimesOfDay.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!configuredTimes.Contains(currentSlot)) return;

        var slotKey = $"{now:yyyy-MM-dd}T{currentSlot}";
        if (slotKey == _lastAutoScrapeSlotKey) return;
        _lastAutoScrapeSlotKey = slotKey;

        var disciplines = new List<Discipline>();
        if (AutoScrapeHorses) disciplines.Add(Discipline.Horses);
        if (AutoScrapeGreyhounds) disciplines.Add(Discipline.Greyhounds);
        if (AutoScrapeHarness) disciplines.Add(Discipline.Harness);
        if (disciplines.Count == 0) return;

        var today = DateOnly.FromDateTime(now);
        var dates = new List<DateOnly>();
        if (AutoScrapeIncludeToday) dates.Add(today);
        if (AutoScrapeIncludeTomorrow) dates.Add(today.AddDays(1));
        if (AutoScrapeIncludeDayAfterTomorrow) dates.Add(today.AddDays(2));
        if (dates.Count == 0) return;

        await ScrapeDatesAsync(disciplines, dates, countryFilter: "", courseFilter: "", forceUploadToS3: true);

        AutoScrapeLastRunSummary = StatusText;
        SaveSetting(s =>
        {
            s.AutoScrapeLastRunUtc = DateTime.UtcNow;
            s.AutoScrapeLastRunSummary = StatusText;
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

        await ScrapeDatesAsync(
            disciplines,
            new List<DateOnly> { DateOnly.FromDateTime(SelectedDate) },
            CountryCodeFilter.Trim(),
            CourseNameFilter.Trim());
    }

    /// <summary>
    /// Shared scrape orchestration behind both the manual Scrape button (a single-element
    /// <paramref name="dates"/> list built from <see cref="SelectedDate"/>) and the auto-scrape
    /// timer (<see cref="AutoScrapeTickAsync"/>, typically today/tomorrow/day-after-tomorrow) —
    /// one browser session covers every date/discipline combination in <paramref name="dates"/> x
    /// <paramref name="disciplines"/>, with results from every date accumulating into the same
    /// <see cref="Meetings"/> grid.
    /// </summary>
    /// <param name="forceUploadToS3">Auto-scrape always passes true here — its whole point is
    /// unattended delivery into the bucket for TroyenRaceIngestor, so it uploads regardless of
    /// whether the manual Scraper tab's "Also upload to S3" checkbox happens to be on.</param>
    /// <param name="isResume">True when called from <see cref="ContinueAsync"/> after a Stop —
    /// skips the usual "clear everything and start fresh" step, so meetings/races already
    /// captured (and this run's original request, in <see cref="_lastScrapeRequest"/>) survive
    /// into this call instead of being wiped.</param>
    private async Task ScrapeDatesAsync(
        List<Discipline> disciplines, List<DateOnly> dates, string countryFilter, string courseFilter,
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
            _lastScrapeRequest = new ScrapeRequest(disciplines, dates, countryFilter, courseFilter, forceUploadToS3);
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

        // A meeting counts as fully done once every one of its races has recorded detail AND
        // (whichever of these are actually enabled) its S3 upload/local export has run —
        // evaluated live off current checkbox state and _raceDetails/row flags rather than any
        // separate "done" list, so it works identically whether this is a fresh run or a resume.
        bool RowNeedsUploadOrExport(MeetingRow r) =>
            ((UploadToS3 || forceUploadToS3) && !r.UploadedToS3) ||
            (AutoExportAfterScrape && !string.IsNullOrWhiteSpace(DownloadFolder) && !r.ExportedLocally);
        bool RowIsFullyDone(MeetingRow r) =>
            r.Meeting.Events.All(e => e.Id is not null && _raceDetails.ContainsKey(e.Id)) && !RowNeedsUploadOrExport(r);

        try
        {
            await using IRaceNetScraperService service = new RaceNetScraperService();
            await service.InitializeAsync(new ScraperOptions { Headless = Headless, Browser = browser }, token);

            // Phase 1 — list every requested date/discipline combo's meetings up front (just
            // meeting-list requests, no per-race scraping yet), so the whole list is visible —
            // and reorderable via the grid's Priority Up/Down buttons — before Phase 2 below
            // commits to any particular scrape order. A combo already fetched on an earlier
            // attempt (isResume, tracked via _lastResults) is skipped rather than re-fetched.
            foreach (var date in dates)
            foreach (var discipline in disciplines)
            {
                token.ThrowIfCancellationRequested();
                if (isResume && _lastResults.ContainsKey((date, discipline))) continue;

                try
                {
                    var result = await VpnRotator.RunWithRotationOnBlockAsync(
                        () => service.ScrapeMeetingsAsync(discipline, date, progress: progress, cancellationToken: token),
                        progress, token);

                    // Apply the country/course filters right away, so nothing downstream
                    // (grid, race-detail scraping, export) ever sees or processes a meeting
                    // that doesn't match — this is what makes the filters actually skip the
                    // slow per-race scraping for excluded meetings, not just hide them.
                    result.MeetingsGrouped = result.MeetingsGrouped
                        .Select(g => new MeetingGroup
                        {
                            Group = g.Group,
                            Meetings = g.Meetings.Where(m => MatchesFilters(m, countryFilter, courseFilter)).ToList()
                        })
                        .Where(g => g.Meetings.Count > 0)
                        .ToList();

                    _lastResults[(date, discipline)] = result;

                    var addedAny = false;
                    foreach (var group in result.MeetingsGrouped)
                    {
                        foreach (var meeting in group.Meetings)
                        {
                            Meetings.Add(MeetingRow.From(discipline, group.Group ?? "", meeting, date));
                            addedAny = true;
                        }
                    }

                    if (!addedAny && (countryFilter.Length > 0 || courseFilter.Length > 0))
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
                    if (raceEvent.Id is not null && _raceDetails.ContainsKey(raceEvent.Id)) continue;

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
                        progress.Report(
                            $"[R-{discipline.Code()}] Race {raceEvent.EventNumber} ({row.MeetingName}) failed, skipping: {ex.Message}");
                    }

                    row.RacesProcessed++;
                }

                // Uploaded to S3 independently of the local folder export below — so S3
                // delivery doesn't depend on a download folder being configured at all.
                // forceUploadToS3 is what lets auto-scrape always push to the bucket
                // regardless of the manual "Also upload to S3" checkbox's current state.
                // Guarded by UploadedToS3 so a resumed run never uploads the same meeting twice.
                if ((UploadToS3 || forceUploadToS3) && !row.UploadedToS3)
                {
                    var (uploaded, failed) = await UploadMeetingToS3Async(discipline, row.Group, row.Meeting);
                    totalS3UploadedCount += uploaded;
                    totalS3FailedCount += failed;
                    row.UploadedToS3 = true;
                    progress.Report(
                        $"[R-{discipline.Code()}] Uploaded {row.MeetingName} to S3: {uploaded} file(s)." +
                        (failed > 0 ? $" {failed} failed." : ""));
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
                StatusText = $"Done. {Meetings.Count} meeting(s) loaded from {dates.Count} date(s) / {disciplines.Count} discipline(s), " +
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
    private async Task<(int uploaded, int failed)> UploadMeetingToS3Async(Discipline discipline, string group, Meeting meeting)
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

        var (u, f) = await UploadJsonToS3Async(meetingFolderName, MeetingFileName(discipline), meetingPayload);
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
