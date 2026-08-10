namespace RaceNetScraper.Shared.Models;

/// <summary>
/// Racing discipline (horses/greyhounds/harness), as used across meeting/race scraping.
/// </summary>
public enum Discipline
{
    Horses = 1,
    Greyhounds = 21,
    Harness = 22
}

public static class DisciplineExtensions
{
    /// <summary>
    /// The form-guide URL segment used to establish a browsing session for each discipline.
    /// </summary>
    public static string FormGuidePath(this Discipline discipline) => discipline switch
    {
        Discipline.Horses => "horse-racing",
        Discipline.Greyhounds => "greyhounds",
        Discipline.Harness => "harness",
        _ => throw new ArgumentOutOfRangeException(nameof(discipline), discipline, null)
    };

    /// <summary>
    /// Single-letter discipline code used throughout the sample data (T/G/H).
    /// </summary>
    public static string Code(this Discipline discipline) => discipline switch
    {
        Discipline.Horses => "T",
        Discipline.Greyhounds => "G",
        Discipline.Harness => "H",
        _ => throw new ArgumentOutOfRangeException(nameof(discipline), discipline, null)
    };

    /// <summary>
    /// Two-letter discipline code used as a filename prefix for exported JSON (TR/GR/HR).
    /// </summary>
    public static string FilePrefix(this Discipline discipline) => discipline switch
    {
        Discipline.Horses => "TR",
        Discipline.Greyhounds => "GR",
        Discipline.Harness => "HR",
        _ => throw new ArgumentOutOfRangeException(nameof(discipline), discipline, null)
    };
}
