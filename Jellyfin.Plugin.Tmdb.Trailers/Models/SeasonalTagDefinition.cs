namespace Jellyfin.Plugin.Tmdb.Trailers.Models;

/// <summary>
/// Defines a seasonal tag with active date range.
/// </summary>
public class SeasonalTagDefinition
{
    /// <summary>
    /// Gets or sets the tag name (e.g., "Christmas", "Halloween").
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start month (1-12).
    /// </summary>
    public int StartMonth { get; set; } = 1;

    /// <summary>
    /// Gets or sets the start day (1-31).
    /// </summary>
    public int StartDay { get; set; } = 1;

    /// <summary>
    /// Gets or sets the end month (1-12).
    /// </summary>
    public int EndMonth { get; set; } = 12;

    /// <summary>
    /// Gets or sets the end day (1-31).
    /// </summary>
    public int EndDay { get; set; } = 31;

    /// <summary>
    /// Checks if the current date is within this seasonal tag's active period.
    /// </summary>
    /// <returns>True if currently in season.</returns>
    public bool IsInSeason()
    {
        var now = DateTime.Now;
        var currentDayOfYear = now.DayOfYear;

        var startDate = new DateTime(now.Year, StartMonth, StartDay);
        var endDate = new DateTime(now.Year, EndMonth, EndDay);

        var startDayOfYear = startDate.DayOfYear;
        var endDayOfYear = endDate.DayOfYear;

        // Handle wrap-around (e.g., Christmas season Dec 1 - Jan 6)
        if (startDayOfYear <= endDayOfYear)
        {
            return currentDayOfYear >= startDayOfYear && currentDayOfYear <= endDayOfYear;
        }
        else
        {
            return currentDayOfYear >= startDayOfYear || currentDayOfYear <= endDayOfYear;
        }
    }
}
