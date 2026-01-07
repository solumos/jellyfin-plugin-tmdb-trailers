namespace Jellyfin.Plugin.Tmdb.Trailers.Models;

/// <summary>
/// Types of trailer selection rules.
/// </summary>
public enum TrailerSelectionRuleType
{
    /// <summary>
    /// Match trailers by release year.
    /// </summary>
    Year,

    /// <summary>
    /// Match trailers by decade (e.g., 2020s).
    /// </summary>
    Decade,

    /// <summary>
    /// Match trailers by genre.
    /// </summary>
    Genre,

    /// <summary>
    /// Prefer recently added trailers.
    /// </summary>
    RecentlyAdded,

    /// <summary>
    /// Prefer unplayed trailers.
    /// </summary>
    Unplayed
}

/// <summary>
/// Defines a rule for selecting trailers.
/// </summary>
public class TrailerSelectionRule
{
    /// <summary>
    /// Gets or sets the rule type.
    /// </summary>
    public TrailerSelectionRuleType RuleType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this rule is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the priority (lower = higher priority).
    /// </summary>
    public int Priority { get; set; }
}
