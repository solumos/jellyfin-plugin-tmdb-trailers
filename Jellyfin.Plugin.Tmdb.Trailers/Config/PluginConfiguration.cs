using System.Collections.Generic;
using Jellyfin.Plugin.Tmdb.Trailers.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Tmdb.Trailers.Config;

/// <inheritdoc />
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        ApiKey = "4219e299c89411838049ab0dab19ebd5";
        Language = "en-US";
        EnableTrailersChannel = true;
        EnableTrailersUpcoming = true;
        EnableTrailersNowPlaying = true;
        TrailerLimit = 20;

        // Cinema mode defaults
        EnableCinemaMode = false;
        TrailerPreRollsLibrary = string.Empty;
        FeaturePreRollsLibrary = string.Empty;
        TrailerPreRollsRatingLimit = true;
        FeaturePreRollsRatingLimit = true;
        EnforceRatingLimitTrailers = true;
        TrailerPreRollsIgnoreOutOfSeason = true;
        FeaturePreRollsIgnoreOutOfSeason = true;
        SeasonalTagDefinitions = GetDefaultSeasonalTags();
        TrailerPreRollsSelections = new List<PreRollSelection>();
        FeaturePreRollsSelections = new List<PreRollSelection>();
        TrailerSelectionRules = GetDefaultTrailerSelectionRules();
    }

    /// <summary>
    /// Gets or sets the api key.
    /// Used to authenticate with tmdb.
    /// Using Jellyfin ApiKey.
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the language.
    /// Pass a ISO 639-1 value to display translated data for the fields that support it.
    /// </summary>
    public string Language { get; set; }

    /// <summary>
    /// Gets or sets region.
    /// Specify a ISO 3166-1 code to filter release dates. Must be uppercase.
    /// </summary>
    public string Region { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable the extras channel.
    /// </summary>
    public bool EnableExtrasChannel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable the trailers channel.
    /// </summary>
    public bool EnableTrailersChannel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable the upcoming trailers.
    /// </summary>
    public bool EnableTrailersUpcoming { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable the now playing trailers.
    /// </summary>
    public bool EnableTrailersNowPlaying { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable the popular trailers.
    /// </summary>
    public bool EnableTrailersPopular { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable the top rated trailers.
    /// </summary>
    public bool EnableTrailersTopRated { get; set; }

    /// <summary>
    /// Gets or sets the intro count.
    /// </summary>
    public int IntroCount { get; set; }

    /// <summary>
    /// Gets or sets the trailer limit per category.
    /// </summary>
    public int TrailerLimit { get; set; }

    // ==================== Cinema Mode Settings ====================

    /// <summary>
    /// Gets or sets a value indicating whether cinema mode is enabled.
    /// </summary>
    public bool EnableCinemaMode { get; set; }

    /// <summary>
    /// Gets or sets the library ID for trailer pre-rolls.
    /// </summary>
    public string TrailerPreRollsLibrary { get; set; }

    /// <summary>
    /// Gets or sets the library ID for feature pre-rolls.
    /// </summary>
    public string FeaturePreRollsLibrary { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enforce rating limits on trailer pre-rolls.
    /// </summary>
    public bool TrailerPreRollsRatingLimit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enforce rating limits on feature pre-rolls.
    /// </summary>
    public bool FeaturePreRollsRatingLimit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enforce rating limits on trailers.
    /// </summary>
    public bool EnforceRatingLimitTrailers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to ignore out-of-season trailer pre-rolls.
    /// </summary>
    public bool TrailerPreRollsIgnoreOutOfSeason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to ignore out-of-season feature pre-rolls.
    /// </summary>
    public bool FeaturePreRollsIgnoreOutOfSeason { get; set; }

    /// <summary>
    /// Gets or sets the seasonal tag definitions.
    /// </summary>
    public List<SeasonalTagDefinition> SeasonalTagDefinitions { get; set; }

    /// <summary>
    /// Gets or sets the trailer pre-roll selection criteria.
    /// </summary>
    public List<PreRollSelection> TrailerPreRollsSelections { get; set; }

    /// <summary>
    /// Gets or sets the feature pre-roll selection criteria.
    /// </summary>
    public List<PreRollSelection> FeaturePreRollsSelections { get; set; }

    /// <summary>
    /// Gets or sets the trailer selection rules.
    /// </summary>
    public List<TrailerSelectionRule> TrailerSelectionRules { get; set; }

    private static List<SeasonalTagDefinition> GetDefaultSeasonalTags()
    {
        return new List<SeasonalTagDefinition>
        {
            new() { Tag = "Christmas", StartMonth = 12, StartDay = 1, EndMonth = 12, EndDay = 31 },
            new() { Tag = "Halloween", StartMonth = 10, StartDay = 15, EndMonth = 10, EndDay = 31 },
            new() { Tag = "Valentine", StartMonth = 2, StartDay = 1, EndMonth = 2, EndDay = 14 },
            new() { Tag = "Summer", StartMonth = 6, StartDay = 1, EndMonth = 8, EndDay = 31 }
        };
    }

    private static List<TrailerSelectionRule> GetDefaultTrailerSelectionRules()
    {
        return new List<TrailerSelectionRule>
        {
            new() { RuleType = TrailerSelectionRuleType.Genre, Enabled = true, Priority = 1 },
            new() { RuleType = TrailerSelectionRuleType.Decade, Enabled = true, Priority = 2 },
            new() { RuleType = TrailerSelectionRuleType.Unplayed, Enabled = true, Priority = 3 }
        };
    }
}
