using System.Text.Json;

namespace RaceNetScraper.Shared.Json;

public static class ScraperJsonOptions
{
    /// <summary>
    /// Options for deserializing Racenet's GraphQL responses (camelCase field names).
    /// </summary>
    public static readonly JsonSerializerOptions Deserialize = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Options for writing our own output files (indented, camelCase).
    /// </summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
