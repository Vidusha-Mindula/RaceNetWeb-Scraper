using CommunityToolkit.Mvvm.ComponentModel;
using RaceNetScraper.Shared.Models;

namespace RaceNetScraper.App.ViewModels;

/// <summary>
/// A single meeting flattened for display in the results grid. Keeps a reference to the
/// underlying Discipline/Meeting so export and race-detail scraping can work directly off
/// what's shown in the grid.
/// </summary>
public sealed partial class MeetingRow : ObservableObject
{
    public Discipline DisciplineEnum { get; init; }
    public Meeting Meeting { get; init; } = null!;
    public string Group { get; init; } = "";
    public DateOnly Date { get; init; }

    public string Discipline => DisciplineEnum.Code();
    public string MeetingName => Meeting.Name ?? "";
    public string? State => Meeting.State;
    public string? Country => Meeting.Venue?.Country?.Iso3;
    public int RaceCount => Meeting.Events.Count;
    public string? MeetingStage => Meeting.MeetingStage;

    public string? FirstRaceLocalTime => Meeting.Events
        .Where(e => e.StartTime is not null)
        .OrderBy(e => e.StartTime)
        .FirstOrDefault()?.StartTime?.ToLocalTime().ToString("t");

    public string? TrackCondition => Meeting.Events
        .Where(e => e.StartTime is not null)
        .OrderBy(e => e.StartTime)
        .FirstOrDefault()?.TrackCondition?.Overall;

    /// <summary>How many of this meeting's races have full runner detail scraped, updated live as scraping progresses.</summary>
    [ObservableProperty]
    private int racesWithDetail;

    /// <summary>Races attempted so far (success or failure) — drives <see cref="ProgressPercent"/>
    /// so the bar reaches 100% once the meeting's race loop finishes, rather than stalling short
    /// of full whenever a race fails and is skipped.</summary>
    [ObservableProperty]
    private int racesProcessed;

    /// <summary>0-100. RacesProcessed/RaceCount — a meeting with no races reads as fully done
    /// rather than 0%.</summary>
    public int ProgressPercent => RaceCount == 0 ? 100 : (int)Math.Round(100.0 * RacesProcessed / RaceCount);

    partial void OnRacesProcessedChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));

    /// <summary>Set once this meeting's S3 upload has actually run — lets
    /// <see cref="MainViewModel"/>'s race-scrape loop tell "already uploaded" apart from "not due
    /// yet", so resuming after Stop (see MainViewModel.ContinueAsync) never uploads the same
    /// meeting twice.</summary>
    public bool UploadedToS3 { get; set; }

    /// <summary>Same idea as <see cref="UploadedToS3"/>, for the local JSON export step.</summary>
    public bool ExportedLocally { get; set; }

    public static MeetingRow From(Discipline discipline, string group, Meeting meeting, DateOnly date) => new()
    {
        DisciplineEnum = discipline,
        Meeting = meeting,
        Group = group,
        Date = date
    };
}
