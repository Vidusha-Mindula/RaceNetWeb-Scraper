using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Text.Json;
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
    private readonly Dictionary<Discipline, ScrapeResult> _lastResults = new();

    /// <summary>Race detail keyed by raceId.</summary>
    private readonly Dictionary<string, RaceDetail> _raceDetails = new();

    private readonly AppSettings _settings = AppSettings.Load();
    private bool _loadingSettings;

    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _cts;
    private string? _pendingNoticeId;

    public MainViewModel()
    {
        _loadingSettings = true;
        DownloadFolder = _settings.DownloadFolder;
        AutoExportAfterScrape = _settings.AutoExportAfterScrape;
        UploadToS3 = _settings.UploadToS3;
        S3BucketName = _settings.S3BucketName;
        _loadingSettings = false;

        _ = CheckForUpdatesAsync();
        _ = CheckForDeveloperNoticeAsync();
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

    /// <summary>True once a developer notice (see DeveloperNoticeChecker) the user hasn't already
    /// dismissed has been found — drives the "Developer Note" banner's visibility.</summary>
    [ObservableProperty]
    private bool developerNoticeVisible;

    [ObservableProperty]
    private string developerNoticeTitle = "";

    [ObservableProperty]
    private string developerNoticeMessage = "";

    public ObservableCollection<MeetingRow> Meetings { get; } = new();

    partial void OnIsBusyChanged(bool value)
    {
        ScrapeCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

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
    /// Scrapes meetings for every selected discipline and always follows up by scraping full
    /// runner/jockey/form detail for every race in every matching meeting — there is no
    /// separate "meetings only" mode; one Scrape click always gets everything.
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

        var countryFilter = CountryCodeFilter.Trim();
        var courseFilter = CourseNameFilter.Trim();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        IsStopping = false;
        Meetings.Clear();
        _lastResults.Clear();
        _raceDetails.Clear();
        StatusText = "Starting browser...";

        IProgress<string> progress = new Progress<string>(msg => StatusText = msg);
        var date = DateOnly.FromDateTime(SelectedDate);
        var disciplineFailures = new List<string>();

        // Running totals for auto-export, which now happens per-meeting (see the export call
        // inside the race-detail loop below) rather than once at the very end.
        var totalFileCount = 0;
        var totalMeetingFolderCount = 0;
        var totalS3UploadedCount = 0;
        var totalS3FailedCount = 0;

        try
        {
            await using IRaceNetScraperService service = new RaceNetScraperService();
            await service.InitializeAsync(new ScraperOptions { Headless = Headless }, token);

            foreach (var discipline in disciplines)
            {
                token.ThrowIfCancellationRequested();
                var rows = new List<MeetingRow>();
                try
                {
                    var result = await service.ScrapeMeetingsAsync(discipline, date, progress: progress, cancellationToken: token);

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

                    _lastResults[discipline] = result;

                    foreach (var group in result.MeetingsGrouped)
                    {
                        foreach (var meeting in group.Meetings)
                        {
                            var row = MeetingRow.From(discipline, group.Group ?? "", meeting);
                            rows.Add(row);
                            Meetings.Add(row);
                        }
                    }

                    if (rows.Count == 0 && (countryFilter.Length > 0 || courseFilter.Length > 0))
                    {
                        progress.Report($"[P-{discipline.Code()}] No meetings matched the country/course filter.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"[P-{discipline.Code()}] Failed: {ex.Message}";
                    disciplineFailures.Add(message);
                    StatusText = message;
                    continue;
                }

                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();

                    // Scraped one race at a time, deliberately NOT concurrently: each race's full
                    // past-run history only arrives via a scroll-triggered lazy load
                    // (ScrapeFullFormsAsync), and Chromium throttles that kind of activity on
                    // background/inactive tabs - running multiple races' tabs open at once meant
                    // whichever weren't the foreground tab silently lost their full form history
                    // and fell back to a single lastRun entry. Sequential keeps every race's tab
                    // in the foreground for its own scroll-and-wait step.
                    foreach (var raceEvent in row.Meeting.Events)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var detail = await service.ScrapeRaceAsync(discipline, row.Meeting, raceEvent, progress, token);
                            if (detail.RaceId is not null) _raceDetails[detail.RaceId] = detail;
                            row.RacesWithDetail++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            progress.Report(
                                $"[P-{discipline.Code()}] Race {raceEvent.EventNumber} ({row.MeetingName}) failed, skipping: {ex.Message}");
                        }

                        row.RacesProcessed++;
                    }

                    // Export this meeting right away instead of waiting for every other
                    // meeting/discipline in this run to finish scraping too — so a long
                    // multi-meeting scrape has already saved (and, if enabled, uploaded) each
                    // meeting as soon as it's ready, rather than losing everything scraped so
                    // far if the run is interrupted or fails partway through.
                    if (AutoExportAfterScrape && !string.IsNullOrWhiteSpace(DownloadFolder))
                    {
                        var exportResult = await ExportMeetingAsync(discipline, row.Group, row.Meeting, DownloadFolder);
                        totalFileCount += exportResult.FileCount;
                        totalMeetingFolderCount += exportResult.MeetingFolderCount;
                        totalS3UploadedCount += exportResult.S3UploadedCount;
                        totalS3FailedCount += exportResult.S3FailedCount;
                        progress.Report(
                            $"[P-{discipline.Code()}] Exported {row.MeetingName}: {exportResult.FileCount} file(s)." +
                            FormatS3Suffix(exportResult));
                    }
                }
            }

            if (_lastResults.Count > 0)
            {
                StatusText = $"Done. {Meetings.Count} meeting(s) loaded from {_lastResults.Count} discipline(s), " +
                             $"{_raceDetails.Count} race(s) with full runner detail.";
                SystemSounds.Asterisk.Play();

                if (AutoExportAfterScrape)
                {
                    if (string.IsNullOrWhiteSpace(DownloadFolder))
                    {
                        StatusText += " Auto-export skipped — no download folder set.";
                    }
                    else
                    {
                        // Each meeting was already exported as soon as its races finished
                        // scraping (see the export call in the race-detail loop above) — this
                        // just reports the running totals from those per-meeting exports.
                        StatusText += $" Auto-exported {totalFileCount} file(s) across {totalMeetingFolderCount} meeting folder(s) to {DownloadFolder} as each meeting finished." +
                                      FormatS3Suffix(new ExportResult(totalFileCount, totalMeetingFolderCount, totalS3UploadedCount, totalS3FailedCount));
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
                StatusText = "Finished, but no meetings matched for the selected date/discipline(s)/filters.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Stopped by user. {Meetings.Count} meeting(s) loaded, " +
                          $"{_raceDetails.Count} race(s) with full runner detail before stopping.";
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

    private static bool MatchesFilters(Meeting meeting, string countryFilter, string courseFilter)
    {
        if (countryFilter.Length > 0)
        {
            var iso2 = meeting.Venue?.Country?.Iso2;
            if (!string.Equals(iso2, countryFilter, StringComparison.OrdinalIgnoreCase))
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

        foreach (var (discipline, result) in _lastResults)
        {
            foreach (var group in result.MeetingsGrouped)
            {
                foreach (var meeting in group.Meetings)
                {
                    var meetingResult = await ExportMeetingAsync(discipline, group.Group ?? "", meeting, targetFolder);
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
    /// <see cref="ScrapeAsync"/> as soon as each meeting's races finish, rather than waiting for
    /// the whole scrape to complete).
    /// </summary>
    private async Task<ExportResult> ExportMeetingAsync(Discipline discipline, string group, Meeting meeting, string targetFolder)
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

        if (await WriteAndMaybeUploadAsync(meetingFolder, meetingFolderName, MeetingFileName(discipline), meetingPayload))
            s3UploadedCount++;
        else if (UploadToS3)
            s3FailedCount++;
        fileCount++;

        foreach (var raceEvent in meeting.Events)
        {
            if (raceEvent.Id is null || !_raceDetails.TryGetValue(raceEvent.Id, out var detail))
                continue;

            if (await WriteAndMaybeUploadAsync(meetingFolder, meetingFolderName, DataDumpFileName(detail.RaceNumber), detail))
                s3UploadedCount++;
            else if (UploadToS3)
                s3FailedCount++;
            fileCount++;
        }

        return new ExportResult(fileCount, 1, s3UploadedCount, s3FailedCount);
    }

    /// <summary>Writes one file locally (nested under its meeting folder, as always), and if
    /// S3 upload is enabled, also uploads the same content directly into the S3 bucket's
    /// configured folder — flat, with the meeting slug folded into the filename instead of a
    /// per-meeting prefix, purely so files from different meetings can't collide once there's
    /// no folder nesting to keep them apart.</summary>
    /// <returns>true if an S3 upload was attempted and succeeded.</returns>
    private async Task<bool> WriteAndMaybeUploadAsync(string localFolder, string meetingFolderName, string fileName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ScraperJsonOptions.Write);
        File.WriteAllText(Path.Combine(localFolder, fileName), json);

        if (!UploadToS3) return false;

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
