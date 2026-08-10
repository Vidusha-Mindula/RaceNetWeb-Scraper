namespace RaceNetScraper.Shared.Models;

public sealed class GraphQlResponse
{
    public GraphQlData? Data { get; set; }
    public List<GraphQlError>? Errors { get; set; }
}

public sealed class GraphQlData
{
    public List<MeetingGroup>? MeetingsGrouped { get; set; }
}

public sealed class GraphQlError
{
    public string? Message { get; set; }
}
