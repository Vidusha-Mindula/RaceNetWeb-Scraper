using CommunityToolkit.Mvvm.ComponentModel;

namespace RaceNetScraper.App.ViewModels;

/// <summary>One checkbox row in the manual Scraper tab's country picker (see MainViewModel's
/// CountryOptions) — same "row wraps its own selection state" shape as S3ObjectRow.</summary>
public sealed partial class CountryOption : ObservableObject
{
    public required string Iso2 { get; init; }
    public required string Name { get; init; }

    [ObservableProperty]
    private bool isSelected;

    /// <summary>Fires whenever <see cref="IsSelected"/> changes, so MainViewModel can recompute
    /// CountryCodeFilter without polling every option on every checkbox click.</summary>
    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
