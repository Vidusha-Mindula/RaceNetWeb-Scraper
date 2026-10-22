using RaceNetScraper.Shared.Models;

namespace RaceNetScraper.Shared.Messaging;

/// <summary>
/// v1 "meeting.scraped" event — published by the desktop app (<c>RaceNetScraper.App</c>) into the
/// same external <c>td.scrapers.events</c> exchange the sibling Punters scraper uses, one event
/// per finished meeting.
///
/// Do not change field names or shape without bumping <see cref="SchemaVersion"/> — a downstream
/// consumer may already depend on this exact shape.
///
/// Consumers should dedupe on the (<see cref="MeetingId"/>, <see cref="DisciplineCode"/>,
/// <see cref="MeetingDateLocal"/>) triple rather than on <see cref="EventId"/> — a meeting can be
/// legitimately rescraped (manual re-run, auto-scrape overlap) and will republish with a fresh
/// <see cref="EventId"/> each time.
/// </summary>
public sealed record MeetingScrapedEvent
{
    public int SchemaVersion { get; init; } = 1;
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public string EventType { get; init; } = "meeting.scraped";

    /// <summary>Identifies the scraper *project*, not the front-end — always "RaceNetScraper".
    /// (The routing key's own lowercase source token, e.g. "racenet", is a separate, configurable
    /// value — see the publisher.)</summary>
    public string Source { get; init; } = "RaceNetScraper";

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int Priority { get; init; } = 5;

    /// <summary>Discipline name, e.g. "Horses" / "Greyhounds" / "Harness".</summary>
    public string? Discipline { get; init; }

    /// <summary>Single-letter discipline code from <see cref="DisciplineExtensions.Code"/> (T/G/H).</summary>
    public string? DisciplineCode { get; init; }

    public string? MeetingId { get; init; }
    public string? MeetingName { get; init; }

    /// <summary>The meeting's exported JSON object name in S3 — the slug-prefixed name exactly as
    /// uploaded, e.g. "swan-hill-20260922-TR-2026-09-22-11-42-50-meeting.json". It is the full
    /// object name under the configured S3 folder (the key is "{folder}/{meetingFileName}"), so a
    /// consumer can locate the file directly. (The local export uses the same base name nested in a
    /// per-meeting folder.)</summary>
    public string? MeetingFileName { get; init; }

    /// <summary>Slug every one of this meeting's exported files is prefixed with in S3 (e.g.
    /// "swan-hill-20260922"). It comes from Racenet's own meeting slug, so it includes the date and
    /// cannot be reconstructed from <see cref="MeetingName"/> alone. It is the common prefix for
    /// the meeting file AND every race DataDump file, so a consumer can list
    /// "{folder}/{meetingSlug}-*" to find every file belonging to this meeting.</summary>
    public string? MeetingSlug { get; init; }

    public string? MeetingDateLocal { get; init; }
    public string? MeetingDateUtc { get; init; }
    public string? VenueState { get; init; }
    public string? VenueCountry { get; init; }
    public int RaceCount { get; init; }

    /// <summary>Shared by every meeting event from the same scrape run.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Builds an event from a scraped <paramref name="meeting"/> and its
    /// <paramref name="discipline"/>. <paramref name="correlationId"/> is scoped to one scrape run
    /// (every meeting in that run shares it); <paramref name="priority"/> is the configured default
    /// (5) — this contract never re-prioritizes after the fact.</summary>
    public static MeetingScrapedEvent Create(
        Discipline discipline, Meeting meeting, Guid correlationId, int priority = 5,
        string? meetingFileName = null, string? meetingSlug = null) => new()
    {
        Priority = priority,
        Discipline = discipline.ToString(),
        DisciplineCode = discipline.Code(),
        MeetingId = meeting.Id,
        MeetingName = meeting.Name,
        MeetingFileName = meetingFileName,
        MeetingSlug = meetingSlug,
        MeetingDateLocal = meeting.MeetingDateLocal,
        MeetingDateUtc = meeting.MeetingDateUtc,
        // Meeting.State carries the venue state (e.g. "VIC") in the scraped payload; fall back to
        // the nested Venue.State if the top-level one is blank.
        VenueState = string.IsNullOrWhiteSpace(meeting.State) ? meeting.Venue?.State : meeting.State,
        VenueCountry = meeting.Venue?.Country?.Iso3,
        RaceCount = meeting.Events.Count,
        CorrelationId = correlationId.ToString(),
    };
}
