namespace RaceNetScraper.Shared.Models;

public sealed class Country
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Iso2 { get; set; }
    public string? Iso3 { get; set; }
    public string? HorseCountry { get; set; }
}

public sealed class Venue
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? NameAbbrev { get; set; }
    public string? Slug { get; set; }
    public string? State { get; set; }
    public bool? IsMetro { get; set; }
    public bool? IsClockWise { get; set; }
    public string? TrackMapUrl { get; set; }
    public double? Straight { get; set; }
    public string? StraightUnit { get; set; }
    public double? Circumference { get; set; }
    public string? CircumferenceUnit { get; set; }
    public string? Address { get; set; }
    public string? WeatherLastUpdated { get; set; }
    public Country? Country { get; set; }
}

public sealed class TrackCondition
{
    public string? EventId { get; set; }
    public string? Overall { get; set; }
    public string? Rating { get; set; }
    public string? Surface { get; set; }
}

public sealed class Weather
{
    public string? Condition { get; set; }
    public string? Temperature { get; set; }
    public string? Wind { get; set; }
    public string? Humidity { get; set; }
}

public sealed class EntryCondition
{
    public string? Type { get; set; }
    public string? Description { get; set; }
}

public sealed class PrizeMoneyEntry
{
    public string? Position { get; set; }
    public double? Value { get; set; }
}

public sealed class RaceEvent
{
    public string? Id { get; set; }
    public string? MeetingId { get; set; }
    public string? Slug { get; set; }
    public string? Name { get; set; }
    public string? NameNews { get; set; }
    public int EventNumber { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public string? TrackType { get; set; }
    public bool IsResulted { get; set; }
    public string? ResultState { get; set; }
    public bool IsAbandoned { get; set; }
    public int? PlaceWinners { get; set; }
    public int? Distance { get; set; }
    public int? Starters { get; set; }
    public double? RacePrizeMoney { get; set; }
    /// <summary>Currency of <see cref="RacePrizeMoney"/>. Starts as the race's native currency
    /// (e.g. "GBP" for a UK meeting) from the meeting-list scrape, then becomes "AUD" once
    /// ScrapeRaceAsync/ScrapeRacesForMeetingAsync has scraped that race's own page and overwritten
    /// it with the AUD figure Racenet itself computes.</summary>
    public string? RacePrizeMoneyUnit { get; set; }
    public string? EventClass { get; set; }
    public string? GroupType { get; set; }
    public TrackCondition? TrackCondition { get; set; }
    public Weather? Weather { get; set; }
    public List<EntryCondition> EntryConditions { get; set; } = new();
    public List<PrizeMoneyEntry> PrizeMoney { get; set; } = new();
}

public sealed class Meeting
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? RailPosition { get; set; }
    public string? TimeGroup { get; set; }
    public string? MeetingDateUtc { get; set; }
    public string? MeetingDateLocal { get; set; }
    public string? RegionId { get; set; }
    public string? SportId { get; set; }
    public double? Penetrometer { get; set; }
    public string? TrackComments { get; set; }
    public bool? IsFuture { get; set; }
    public bool? TabStatus { get; set; }
    public string? MeetingCategory { get; set; }
    public string? MeetingStage { get; set; }
    public string? MeetingType { get; set; }
    public bool? IsAbandoned { get; set; }
    public bool? ShowSpeedMaps { get; set; }
    public bool? ShowSectionals { get; set; }
    public bool? ShowOdds { get; set; }
    public double? TotalPrizeMoney { get; set; }
    public string? State { get; set; }
    public Venue? Venue { get; set; }
    public List<RaceEvent> Events { get; set; } = new();
}

public sealed class MeetingGroup
{
    public string? Group { get; set; }
    public List<Meeting> Meetings { get; set; } = new();
}

public sealed class ScrapeResult
{
    public Discipline Discipline { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateTimeOffset ScrapedAtUtc { get; set; }
    public List<MeetingGroup> MeetingsGrouped { get; set; } = new();

    public int TotalMeetings => MeetingsGrouped.Sum(g => g.Meetings.Count);
    public int TotalRaces => MeetingsGrouped.Sum(g => g.Meetings.Sum(m => m.Events.Count));
}
