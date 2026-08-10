using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaceNetScraper.App.Services;

/// <summary>A developer announcement to show on next launch, published via notice.json at the
/// repo root (see send-notice.ps1). An empty <see cref="Id"/> means "nothing to show".</summary>
public sealed record DeveloperNotice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Polls notice.json from the repo's raw GitHub content — a much lighter-weight channel than
/// cutting a full release (see UpdateChecker) for a one-off message: the developer edits one file
/// and pushes, no rebuild/install needed. The app shows it until the user explicitly dismisses it
/// (see AppSettings.LastSeenNoticeId); publishing a new note just means giving it a new Id, which
/// makes it show again even to someone who already dismissed an earlier one.
/// </summary>
public static class DeveloperNoticeChecker
{
    private const string RawUrl =
        "https://raw.githubusercontent.com/Vidusha-Mindula/RaceNetWeb-Scraper/main/notice.json";

    /// <summary>Returns the current notice, or null if there isn't one / the check failed for any
    /// reason (offline, file missing, malformed) — this must never block startup or bother the
    /// user with a failed background check, same as UpdateChecker.</summary>
    public static async Task<DeveloperNotice?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaceNetScraper.App", "1.0"));

            // raw.githubusercontent.com is fronted by a CDN that caches aggressively — a
            // cache-busting query string is the only reliable way for a just-published notice to
            // show up promptly instead of serving a stale copy for several minutes.
            var url = $"{RawUrl}?t={DateTimeOffset.UtcNow.Ticks}";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var notice = JsonSerializer.Deserialize<DeveloperNotice>(json);

            return string.IsNullOrWhiteSpace(notice?.Id) ? null : notice;
        }
        catch
        {
            return null;
        }
    }
}
