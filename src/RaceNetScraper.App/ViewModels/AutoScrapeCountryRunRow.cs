using CommunityToolkit.Mvvm.ComponentModel;

namespace RaceNetScraper.App.ViewModels;

/// <summary>UI-bound mirror of <see cref="AutoScrapeCountryRun"/> — one country column within an
/// <see cref="AutoScrapeSlotRow"/> card.</summary>
public sealed partial class AutoScrapeCountryRunRow : ObservableObject
{
    [ObservableProperty]
    private bool enabled = true;

    [ObservableProperty]
    private bool includeToday = true;

    [ObservableProperty]
    private bool includeTomorrow = true;

    [ObservableProperty]
    private bool includeDayAfterTomorrow = true;

    [ObservableProperty]
    private bool horses = true;

    [ObservableProperty]
    private bool greyhounds = true;

    [ObservableProperty]
    private bool harness = true;

    /// <summary>Fires whenever any field changes, bubbled up by the owning
    /// <see cref="AutoScrapeSlotRow"/> into its own Changed event.</summary>
    public event Action? Changed;

    partial void OnEnabledChanged(bool value) => Changed?.Invoke();
    partial void OnIncludeTodayChanged(bool value) => Changed?.Invoke();
    partial void OnIncludeTomorrowChanged(bool value) => Changed?.Invoke();
    partial void OnIncludeDayAfterTomorrowChanged(bool value) => Changed?.Invoke();
    partial void OnHorsesChanged(bool value) => Changed?.Invoke();
    partial void OnGreyhoundsChanged(bool value) => Changed?.Invoke();
    partial void OnHarnessChanged(bool value) => Changed?.Invoke();

    public static AutoScrapeCountryRunRow From(AutoScrapeCountryRun run) => new()
    {
        Enabled = run.Enabled,
        IncludeToday = run.IncludeToday,
        IncludeTomorrow = run.IncludeTomorrow,
        IncludeDayAfterTomorrow = run.IncludeDayAfterTomorrow,
        Horses = run.Horses,
        Greyhounds = run.Greyhounds,
        Harness = run.Harness,
    };

    public AutoScrapeCountryRun ToRun() => new()
    {
        Enabled = Enabled,
        IncludeToday = IncludeToday,
        IncludeTomorrow = IncludeTomorrow,
        IncludeDayAfterTomorrow = IncludeDayAfterTomorrow,
        Horses = Horses,
        Greyhounds = Greyhounds,
        Harness = Harness,
    };
}
