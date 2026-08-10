namespace RaceNetScraper.Core.Scraping;

/// <summary>
/// The Racenet GraphQL contract, verified live against api.racenet.com.au on 2026-08-10.
///
/// Racenet and Punters are two front-ends over the same racing GraphQL backend, so the field
/// names here are the same ones PuntersScraper reads out of Punters' Apollo cache — which is why
/// the exported JSON comes out identically shaped. What differs is how we get at it: Punters'
/// site had to be driven through its own UI (its API rejected anything that didn't look like the
/// real page), whereas Racenet's API accepts a plain query issued from a warmed-up page context.
///
/// Notes from the live probing session, so nobody has to rediscover them:
///   - Endpoint: POST https://api.racenet.com.au/racing. The old puntapi.com host is dead
///     (always HTTP 403 via CloudFront), as is any request made from outside a real browser
///     session.
///   - Apollo's CSRF guard rejects a request that has neither a JSON content-type nor an
///     x-apollo-operation-name header ("This operation has been blocked as a potential Cross-Site
///     Request Forgery"). We send both.
///   - authorization: "Bearer none" is what the site itself sends for anonymous traffic.
///   - Full query documents are accepted, so we do NOT depend on the server's persisted-query
///     registry. That registry is per-brand and drifts: Punters' getEventById and
///     meetingsIndexByStartEndTime hashes happen to be registered on Racenet too, but its
///     fullFormsBySelectionIds hash is not (PERSISTED_QUERY_NOT_FOUND). Sending the query text
///     sidesteps the whole problem and survives their hash rotations.
///   - Schema introspection is disabled, so the argument/variable types below were pinned down
///     from the server's own validation errors. Two that are easy to get wrong:
///     competitorForms' selectionIds is [String!]! (not [String!]), and meetings' sport is a
///     Sport enum — meetings also takes NO brand or isTopFour argument, unlike the
///     persisted-query variables the Punters-side code passes.
/// </summary>
internal static class RaceNetGraphQl
{
    internal const string ApiUrl = "https://api.racenet.com.au/racing";

    /// <summary>
    /// Stats field set read by <c>mapStats</c> in RaceNetScraperService. Every name here was
    /// confirmed present on Racenet's Stats type — it is a superset of what Punters' cache
    /// exposed, so the shared mapping applies unchanged.
    /// </summary>
    private const string StatsFields =
        "rating winPercentage placePercentage totalRuns totalPlaces lastTenFigure lastYearRuns " +
        "lastYearPlaces currentSeasonRuns currentSeasonPlaces runsByTrainerJockey " +
        "placesByTrainerJockey runsByJockey placesByJockey runsByDistance placesByDistance " +
        "runsByTrack placesByTrack runsByDistTrack placesByDistTrack winRange totalPrizeMoney " +
        "averagePrizeMoney runsByTurf placesByTurf runsBySynth placesBySynth firstUpRuns " +
        "firstUpPlaces secondUpStarts secondUpPlaces thirdUpStarts thirdUpPlaces wetRuns " +
        "wetPlaces favRuns favPlaces nightRuns nightPlaces clockwiseRuns clockwisePlaces " +
        "aClockwiseRuns aClockwisePlaces group1Runs group1Places group2Runs group2Places " +
        "group3Runs group3Places listedRaceRuns listedRacePlaces classRuns classPlaces lastWin " +
        "firmRuns firmPlaces goodRuns goodPlaces softRuns softPlaces heavyRuns heavyPlaces " +
        "synthRun synthPlaces roi daysSinceLastRun trainerJockeyWin winDistanceCount lastRun";

    /// <summary>
    /// selection.lastRun — the single most recent start. Same field names as Punters', so
    /// <c>mapPastRunFromLastRun</c> is shared verbatim. Superseded per runner by
    /// <see cref="CompetitorFormsQuery"/> when that returns anything.
    /// </summary>
    private const string LastRunFields =
        "finishPosition eventStarters weight eventDistance eventNameForm meetingDate meetingName " +
        "trackCondition trackConditionRating startingWinPriceDecimal margin finishTime statusAbv " +
        "barrierRow barrierHandicap stewardsReport";

    /// <summary>
    /// The meetings list for one day. Returns a FLAT data.meetings array — the two-tier
    /// Australia/International grouping TroyenRaceIngestor expects is inferred from
    /// venue.country.iso2, exactly as the Punters scraper does.
    ///
    /// Every field the Meeting/RaceEvent DTOs carry is selected here, including several the
    /// Punters path had to leave null (trackComments, penetrometer, railPosition, isFuture,
    /// nameAbbrev, isMetro, country.horseCountry, nameNews, starters, placeWinners, resultState,
    /// trackType, entryConditions, prizeMoney) — same JSON shape, more of it populated.
    /// </summary>
    internal const string MeetingsQuery = """
        query meetingsIndexByStartEndTime($startTime: String!, $endTime: String!, $sport: Sport, $meetingCategory: [String!], $includeRegions: [Int!]) {
          meetings(startTime: $startTime, endTime: $endTime, sport: $sport, meetingCategory: $meetingCategory, includeRegions: $includeRegions) {
            id name slug meetingDateUtc meetingDateLocal meetingCategory meetingStage meetingType
            railPosition tabStatus regionId sportId trackComments penetrometer isFuture state
            isAbandoned showSpeedMaps showSectionals showOdds
            venue {
              id name slug state address isMetro isClockWise trackMapUrl straight straightUnit
              circumference circumferenceUnit weatherLastUpdated nameAbbrev
              country { id name iso2 iso3 horseCountry }
            }
            weather { condition temperature wind humidity }
            events {
              id meetingId slug name nameNews eventNumber startTime endTime status distance
              starters resultState placeWinners isResulted isAbandoned eventClass groupType
              trackType racePrizeMoney racePrizeMoneyUnit racePrizeMoneyValue
              trackCondition { eventId overall rating surface }
              entryConditions { type description }
              prizeMoney { position value }
            }
          }
        }
        """;

    /// <summary>
    /// One race's full field: runners, jockeys, trainers, breeding, weights, silks, stats and
    /// the single most recent run. Also carries the AUD prize money (racePrizeMoney plus the
    /// per-place prizeMoney breakdown) that overwrites the meeting-list's native-currency figure.
    /// </summary>
    internal const string EventQuery = """
        query getEventById($eventId: String!) {
          event(id: $eventId) {
            id slug name nameNews eventNumber distance status starters placeWinners resultState
            isResulted isAbandoned meetingId winningTime pace
            racePrizeMoney racePrizeMoneyUnit racePrizeMoneyValue
            prizeMoney { position value }
            entryConditions { type description }
            trackCondition { eventId overall rating surface }
            selections {
              id competitorNumber barrierNumber barrierRow barrierHandicap weight weightUnit
              status silkImageUrl racingColours gearChanges comments startingPrice formLetters
              ratingOfficial isEmergency jockeyWeight jockeyWeightClaim
              jockey { id name slug }
              trainer { id name slug }
              competitor {
                id name slug age colour sex sexShort sire dam owner racingColours imageUrl
                smallImageUrl
                horseCountry { iso2 iso3 }
              }
              stats { STATS_FIELDS }
              lastRun { LASTRUN_FIELDS }
              quickForm { name description indicator priority }
              predictorRatings {
                weight barrier careerWinRate careerPlaceRate careerPrizeMoney averagePrizeMoney
                jockeyWins jockeyHorseWins trainerWins trackPlacings distPlacings
                trackDistPlacings firmPlacings goodPlacings softPlacings heavyPlacings
                synthPlacings lateSpeed
              }
              prediction {
                selectionId modelOutput modelRank width length winningChance speedMeasure
                finishSpeed closingSpeedRating barrierSpeedRating
              }
              puntersEdge { rating price }
              flucs { summary open low high }
            }
          }
        }
        """;

    /// <summary>
    /// Each runner's last starts, batched across the whole field in one request. This is the
    /// piece that makes the Racenet engine simpler than the Punters one: Punters only fired the
    /// equivalent query lazily as each runner's row scrolled into view, so that scraper had to
    /// click "Show All Form" and sweep the page to provoke them. Here it is just a query.
    ///
    /// forms[] carries barrier and starting price only inside formLine.summaryMarkup free text
    /// (e.g. "Barrier: 8, SP $1.3"), which is why MapPastRunFromForm regexes them out.
    /// </summary>
    internal const string CompetitorFormsQuery = """
        query fullFormsBySelectionIds($selectionIds: [String!]!, $limit: Int) {
          competitorForms(selectionIds: $selectionIds, limit: $limit) {
            selectionId
            forms {
              isTrial finishPosition eventStarters eventNumber meetingName meetingDate
              eventDistance eventNameForm eventNameNews margin trackCondition
              trackConditionRating finishTime videoComment videoNote
              formLine {
                summaryMarkup
                places { finishPosition competitorName }
              }
            }
          }
        }
        """;

    internal static string BuildEventQuery() => EventQuery
        .Replace("STATS_FIELDS", StatsFields)
        .Replace("LASTRUN_FIELDS", LastRunFields);
}
