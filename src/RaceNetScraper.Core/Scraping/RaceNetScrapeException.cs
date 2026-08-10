namespace RaceNetScraper.Core.Scraping;

public sealed class RaceNetScrapeException : Exception
{
    public RaceNetScrapeException(string message) : base(message)
    {
    }

    public RaceNetScrapeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
