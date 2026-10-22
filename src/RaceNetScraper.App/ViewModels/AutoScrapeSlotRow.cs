using CommunityToolkit.Mvvm.ComponentModel;

namespace RaceNetScraper.App.ViewModels;

/// <summary>One editable card in the Auto Scraper tab's slot list — the UI-bound mirror of
/// <see cref="AutoScrapeSlot"/>: one shared time, plus an independent
/// <see cref="AutoScrapeCountryRunRow"/> for Australia and one for International.</summary>
public sealed partial class AutoScrapeSlotRow : ObservableObject
{
    [ObservableProperty]
    private string time = "06:00";

    public AutoScrapeCountryRunRow Australia { get; } = new();
    public AutoScrapeCountryRunRow International { get; } = new();

    /// <summary>Fires whenever the time or either country's settings change, so MainViewModel can
    /// persist the whole slot list without polling every row.</summary>
    public event Action? Changed;

    public AutoScrapeSlotRow()
    {
        Australia.Changed += () => Changed?.Invoke();
        International.Changed += () => Changed?.Invoke();
    }

    partial void OnTimeChanged(string value) => Changed?.Invoke();

    public static AutoScrapeSlotRow From(AutoScrapeSlot slot)
    {
        var row = new AutoScrapeSlotRow { Time = slot.Time };
        row.Australia.Enabled = slot.Australia.Enabled;
        row.Australia.IncludeToday = slot.Australia.IncludeToday;
        row.Australia.IncludeTomorrow = slot.Australia.IncludeTomorrow;
        row.Australia.IncludeDayAfterTomorrow = slot.Australia.IncludeDayAfterTomorrow;
        row.Australia.Horses = slot.Australia.Horses;
        row.Australia.Greyhounds = slot.Australia.Greyhounds;
        row.Australia.Harness = slot.Australia.Harness;
        row.International.Enabled = slot.International.Enabled;
        row.International.IncludeToday = slot.International.IncludeToday;
        row.International.IncludeTomorrow = slot.International.IncludeTomorrow;
        row.International.IncludeDayAfterTomorrow = slot.International.IncludeDayAfterTomorrow;
        row.International.Horses = slot.International.Horses;
        row.International.Greyhounds = slot.International.Greyhounds;
        row.International.Harness = slot.International.Harness;
        return row;
    }

    public AutoScrapeSlot ToSlot() => new()
    {
        Time = Time,
        Australia = Australia.ToRun(),
        International = International.ToRun(),
    };
}
