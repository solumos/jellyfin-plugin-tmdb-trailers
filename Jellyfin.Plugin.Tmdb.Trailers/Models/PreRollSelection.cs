namespace Jellyfin.Plugin.Tmdb.Trailers.Models;

/// <summary>
/// Defines selection criteria for pre-roll content.
/// </summary>
public class PreRollSelection
{
    /// <summary>
    /// Gets or sets the name/title to match (optional).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the year to match (optional).
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the decade to match (optional, e.g., 2020).
    /// </summary>
    public int? Decade { get; set; }

    /// <summary>
    /// Gets or sets the genres to match (optional).
    /// </summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>
    /// Gets or sets the studios to match (optional).
    /// </summary>
    public List<string> Studios { get; set; } = new();

    /// <summary>
    /// Gets or sets the tags to match (optional).
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether all tags must match (vs any tag).
    /// </summary>
    public bool RequireAllTags { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a seasonal selection.
    /// </summary>
    public bool IsSeasonal { get; set; }

    /// <summary>
    /// Gets or sets the priority (lower = higher priority).
    /// </summary>
    public int Priority { get; set; }
}
