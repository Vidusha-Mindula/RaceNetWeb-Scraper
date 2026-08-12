using System.Collections.ObjectModel;
using System.IO;
using Amazon.S3;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceNetScraper.App.Services;

namespace RaceNetScraper.App.ViewModels;

/// <summary>Backs the Bucket window: lists objects in the configured S3 bucket and deletes
/// selected ones. Reads <see cref="AppSettings"/> fresh on every refresh, so it always reflects
/// whatever bucket/keys are currently configured on the main window.</summary>
public sealed partial class BucketViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _loadingSettings;

    public ObservableCollection<S3ObjectRow> Objects { get; } = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private string bucketName = "";

    /// <summary>S3 access key, editable here (rather than fixed at install time) so credentials
    /// can be updated without reinstalling. Lives on the Bucket tab, next to the bucket it
    /// actually authenticates against, rather than on the Scraper tab.</summary>
    [ObservableProperty]
    private string s3AccessKey = "";

    /// <summary>S3 secret key — see <see cref="S3AccessKey"/>.</summary>
    [ObservableProperty]
    private string s3SecretKey = "";

    public BucketViewModel()
    {
        _loadingSettings = true;
        S3AccessKey = _settings.S3AccessKey;
        S3SecretKey = _settings.S3SecretKey;
        _loadingSettings = false;
    }

    public int SelectedCount => Objects.Count(o => o.IsSelected);
    public string DeleteSelectedLabel => $"Delete selected ({SelectedCount})";
    public bool CanDeleteSelected => SelectedCount > 0 && !IsBusy;

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DeleteSelectedLabel));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanDeleteSelected));

    /// <summary>Re-reads settings.json fresh, applies one field, and saves — rather than mutating
    /// the long-lived <see cref="_settings"/> field directly. MainViewModel keeps its own
    /// independent AppSettings instance for the fields it owns (download folder, bucket name,
    /// ...), and AppSettings.Save() serializes the WHOLE object: saving straight from this stale
    /// in-memory copy would silently overwrite whatever MainViewModel had just written with
    /// whatever values were in memory here since this control loaded.</summary>
    private static void SaveSetting(Action<AppSettings> mutate)
    {
        var settings = AppSettings.Load();
        mutate(settings);
        settings.Save();
    }

    partial void OnS3AccessKeyChanged(string value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.S3AccessKey = value);
    }

    partial void OnS3SecretKeyChanged(string value)
    {
        if (_loadingSettings) return;
        SaveSetting(s => s.S3SecretKey = value);
    }

    /// <summary>Amazon's SDK exception carries the actual S3 error code and request id, which
    /// tell apart otherwise-identical-looking failures (e.g. a true bucket-policy AccessDenied vs.
    /// RequestTimeTooSkewed from a wrong system clock vs. SignatureDoesNotMatch) - plain
    /// ex.Message alone often doesn't distinguish these, which matters since "same credentials
    /// work on other machines" points at something machine/network-specific rather than config.
    /// The full detail (including AmazonId2 and the raw response body) is too long for this
    /// one-line status bar - see %LOCALAPPDATA%\RaceNetScraper\s3-debug.log
    /// (S3BucketService.LogFailure) for that.</summary>
    private static string Describe(Exception ex) => ex is AmazonS3Exception s3
        ? $"{s3.Message} (ErrorCode={s3.ErrorCode}, RequestId={s3.RequestId}, Id2={s3.AmazonId2}, HttpStatus={(int)s3.StatusCode}) - see s3-debug.log"
        : ex.Message;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Objects) row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Objects) row.IsSelected = false;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Loading...";
        try
        {
            var settings = AppSettings.Load();
            BucketName = settings.S3BucketName;

            var items = await S3BucketService.ListObjectsAsync(settings);
            Objects.Clear();
            foreach (var item in items)
            {
                var row = S3ObjectRow.From(item);
                row.SelectionChanged += NotifySelectionChanged;
                Objects.Add(row);
            }
            StatusText = $"{Objects.Count} file(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to list bucket: {Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    public async Task UploadAsync(IReadOnlyList<string> localFilePaths)
    {
        if (localFilePaths.Count == 0) return;

        IsBusy = true;
        StatusText = $"Uploading {localFilePaths.Count} file(s)...";
        try
        {
            var settings = AppSettings.Load();
            var uploaded = 0;
            var errors = new List<string>();

            foreach (var path in localFilePaths)
            {
                try
                {
                    await S3BucketService.UploadFileAsync(settings, path);
                    uploaded++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {Describe(ex)}");
                }
            }

            StatusText = errors.Count > 0
                ? $"Uploaded {uploaded} file(s). {errors.Count} failed: {string.Join(" | ", errors)}"
                : $"Uploaded {uploaded} file(s).";

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Upload failed: {Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return;

        IsBusy = true;
        StatusText = $"Deleting {keys.Count} file(s)...";
        try
        {
            var settings = AppSettings.Load();
            var (deleted, errors) = await S3BucketService.DeleteObjectsAsync(settings, keys);

            foreach (var row in Objects.Where(o => keys.Contains(o.Key) && !errors.Any(e => e.StartsWith(o.Key + ":"))).ToList())
            {
                Objects.Remove(row);
            }

            StatusText = errors.Count > 0
                ? $"Deleted {deleted} file(s). {errors.Count} failed: {string.Join(" | ", errors)}"
                : $"Deleted {deleted} file(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }
}
