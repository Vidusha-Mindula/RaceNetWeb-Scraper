namespace RaceNetScraper.Shared.Models;

public sealed class PersonRef
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
}

public sealed class CarryingWeight
{
    public string? WeightKg { get; set; }
    public string? WeightImp { get; set; }
    public string? WeightLbs { get; set; }
}

public sealed class RaceResultRef
{
    public string? HorseName { get; set; }
    public int? Position { get; set; }
}

public sealed class PastRun
{
    public string? Type { get; set; }
    public int? FinishPosition { get; set; }
    public int? Starters { get; set; }
    public int? RaceNumber { get; set; }
    public string? Course { get; set; }
    public string? Date { get; set; }
    public string? Distance { get; set; }
    public string? RaceName { get; set; }
    public string? StartingPrice { get; set; }
    public string? JockeyName { get; set; }
    public string? WinnerName { get; set; }
    public string? SecondName { get; set; }
    public string? ThirdName { get; set; }
    public string? LengthsBehind { get; set; }
    public string? TrackCondition { get; set; }
    public string? EventClass { get; set; }
    public string? FinishTime { get; set; }
    public string? BarrierPosition { get; set; }
    public string? RunDetail { get; set; }
    public RaceResultRef? RaceResult { get; set; }
}

public sealed class PerformanceStatistics
{
    public string? Career { get; set; }
    public string? WinPer { get; set; }
    public string? PlacePer { get; set; }
    public string? ShowPer { get; set; }
    public string? Last10Starts { get; set; }
    public string? Last12Months { get; set; }
    public string? Season { get; set; }
    public string? Track { get; set; }
    public string? Distance { get; set; }
    public string? TrackDistanceCombo { get; set; }
    public string? WetConditions { get; set; }
    public string? PrizeMoney { get; set; }
    public string? AvgPrizeMoney { get; set; }
    public string? WinRange { get; set; }
    public string? Rating { get; set; }
    public string? JockeyWinPer { get; set; }
    public string? JockeyHorse { get; set; }
    public string? FirstUp { get; set; }
    public string? SecondUp { get; set; }
    public string? ThirdUp { get; set; }
    public string? Firm { get; set; }
    public string? Good { get; set; }
    public string? Soft { get; set; }
    public string? Heavy { get; set; }
    public string? Synthetic { get; set; }
    public string? Turf { get; set; }
    public string? Dirt { get; set; }
    public string? Dry { get; set; }
    public string? Clockwise { get; set; }
    public string? AntiClockwise { get; set; }
    public string? Class { get; set; }
    public string? AsFavourite { get; set; }
    public string? Group1 { get; set; }
    public string? Group2 { get; set; }
    public string? Group3 { get; set; }
    public string? Listed { get; set; }
    public string? Night { get; set; }
    /// <summary>Trainer+jockey combo record ("T/J" stat) — distinct from
    /// <see cref="JockeyHorse"/>, which is this jockey's record on this specific horse.</summary>
    public string? TrainerJockey { get; set; }
    public string? Roi { get; set; }
    public string? LastWin { get; set; }
    public int? DaysSinceLastRun { get; set; }
    /// <summary>Subscriber-only aggregate: average rating across runs. Left null by scrapers
    /// that don't source this (e.g. no authenticated subscriber session).</summary>
    public string? AvgRating { get; set; }
    /// <summary>Subscriber-only aggregate: average early (speed map) position.</summary>
    public string? AvgEarlyPosition { get; set; }
    /// <summary>Subscriber-only sectional aggregates (average time/position at the 800m/600m/400m/200m marks).</summary>
    public string? AvgL800 { get; set; }
    public string? AvgL600 { get; set; }
    public string? AvgL400 { get; set; }
    public string? AvgL200 { get; set; }
}

public sealed class RawStats
{
    public string? Rating { get; set; }
    public int? TotalRuns { get; set; }
    public List<int>? TotalPlaces { get; set; }
    public string? WinPercentage { get; set; }
    public string? PlacePercentage { get; set; }
    public string? TotalPrizeMoney { get; set; }
    public string? AveragePrizeMoney { get; set; }
    public string? WinRange { get; set; }
    public int? RunsByJockey { get; set; }
    public List<int>? PlacesByJockey { get; set; }
    public int? FirstUpRuns { get; set; }
    public List<int>? FirstUpPlaces { get; set; }
    public int? SecondUpStarts { get; set; }
    public int? ThirdUpStarts { get; set; }
    public int? LastYearRuns { get; set; }
    public int? RunsByDistance { get; set; }
    public int? RunsByTrack { get; set; }
    public int? RunsByDistTrack { get; set; }
    public int? FirmRuns { get; set; }
    public int? GoodRuns { get; set; }
    public int? SoftRuns { get; set; }
    public int? RunsByTurf { get; set; }
    public int? WetRuns { get; set; }
    public int? HeavyRuns { get; set; }
    public int? SynthRun { get; set; }
    public int? ClockwiseRuns { get; set; }
    public int? CurrentSeasonRuns { get; set; }
    public int? AClockwiseRuns { get; set; }
    public List<int>? SecondUpPlaces { get; set; }
    public List<int>? ThirdUpPlaces { get; set; }
    public List<int>? LastYearPlaces { get; set; }
    public List<int>? PlacesByDistance { get; set; }
    public List<int>? PlacesByTrack { get; set; }
    public List<int>? PlacesByDistTrack { get; set; }
    public List<int>? FirmPlaces { get; set; }
    public List<int>? GoodPlaces { get; set; }
    public List<int>? SoftPlaces { get; set; }
    public List<int>? PlacesByTurf { get; set; }
    public List<int>? WetPlaces { get; set; }
    public List<int>? HeavyPlaces { get; set; }
    public List<int>? SynthPlaces { get; set; }
    public List<int>? CurrentSeasonPlaces { get; set; }
    public List<int>? ClockwisePlaces { get; set; }
    public List<int>? AClockwisePlaces { get; set; }
    public int? RunsByClass { get; set; }
    public List<int>? PlacesByClass { get; set; }
    public int? DirtRuns { get; set; }
    public List<int>? DirtPlaces { get; set; }
    public int? DryRuns { get; set; }
    public List<int>? DryPlaces { get; set; }
    public int? FavRuns { get; set; }
    public List<int>? FavPlaces { get; set; }
    public int? Group1Runs { get; set; }
    public List<int>? Group1Places { get; set; }
    public int? Group2Runs { get; set; }
    public List<int>? Group2Places { get; set; }
    public int? Group3Runs { get; set; }
    public List<int>? Group3Places { get; set; }
    public int? ListedRuns { get; set; }
    public List<int>? ListedPlaces { get; set; }
    public int? NightRuns { get; set; }
    public List<int>? NightPlaces { get; set; }
    /// <summary>Trainer+jockey combo win percentage (raw "trainerJockeyWin" value) —
    /// what <see cref="PerformanceStatistics.JockeyWinPer"/> is formatted from.</summary>
    public int? TrainerJockeyWinPercentage { get; set; }
    public decimal? Roi { get; set; }
    public string? LastWin { get; set; }
    public int? DaysSinceLastRun { get; set; }
    public decimal? AvgRating { get; set; }
    public decimal? AvgEarlyPosition { get; set; }
    public decimal? AvgL800 { get; set; }
    public decimal? AvgL600 { get; set; }
    public decimal? AvgL400 { get; set; }
    public decimal? AvgL200 { get; set; }
}

public sealed class Runner
{
    public string? RunnerId { get; set; }
    public string? TabNumber { get; set; }
    public string? RunnerName { get; set; }
    public string? Discipline { get; set; }
    public string? Age { get; set; }
    public string? Sex { get; set; }
    public string? Colour { get; set; }
    public string? Sire { get; set; }
    public string? Dam { get; set; }
    public string? HorseCountry { get; set; }
    public string? Draw { get; set; }
    public string? BarrierPosition { get; set; }
    public string? Comment { get; set; }
    public string? SilkColourText { get; set; }
    /// <summary>The silk SVG Racenet actually serves for this runner. When
    /// <see cref="SilkColourText"/> isn't already populated from the API, it's derived from this
    /// image instead (see <see cref="SilkSvgDescriber"/>).</summary>
    public string? SilkImageUrl { get; set; }
    public string? GearChanges { get; set; }
    public PersonRef? Jockey { get; set; }
    public PersonRef? Trainer { get; set; }
    public CarryingWeight? CarryingWeight { get; set; }
    public string? LastRun { get; set; }
    public string? LastRunComment { get; set; }
    public List<PastRun> PastRuns { get; set; } = new();
    public PerformanceStatistics? PerformanceStatistics { get; set; }
    public RawStats? RawStats { get; set; }
    public object? Pointers { get; set; }
    public string? CurrentOdds { get; set; }
    public bool IsScratched { get; set; }
}

public sealed class RaceDistanceInfo
{
    public string? DistanceM { get; set; }
    public string? DistanceF { get; set; }
    public string? DistanceMi { get; set; }
}

public sealed class RaceDetail
{
    public string? MeetingId { get; set; }
    public string? MeetingName { get; set; }
    public string? Country { get; set; }
    public string? Date { get; set; }
    public string? RaceId { get; set; }
    public string? Slug { get; set; }
    public int RaceNumber { get; set; }
    public RaceDistanceInfo? RaceDistance { get; set; }
    public List<Runner> Runners { get; set; } = new();
}
