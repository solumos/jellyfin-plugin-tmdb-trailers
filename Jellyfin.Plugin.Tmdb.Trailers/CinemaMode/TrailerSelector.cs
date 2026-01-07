using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Tmdb.Trailers.Config;
using Jellyfin.Plugin.Tmdb.Trailers.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TMDbLib.Objects.Search;

namespace Jellyfin.Plugin.Tmdb.Trailers.CinemaMode;

/// <summary>
/// Selects trailers from TMDb cache based on smart selection rules.
/// </summary>
public class TrailerSelector
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerSelector"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="memoryCache">The memory cache.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The plugin configuration.</param>
    public TrailerSelector(
        ILibraryManager libraryManager,
        IMemoryCache memoryCache,
        ILogger logger,
        PluginConfiguration config)
    {
        _libraryManager = libraryManager;
        _memoryCache = memoryCache;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Selects trailers based on the movie being played and selection rules.
    /// </summary>
    /// <param name="movie">The movie being played.</param>
    /// <param name="user">The user watching.</param>
    /// <param name="cachedTrailerIds">The list of cached trailer IDs.</param>
    /// <param name="count">The number of trailers to select.</param>
    /// <returns>The selected trailer IDs.</returns>
    public IEnumerable<Guid> SelectTrailers(Movie movie, User user, IList<string> cachedTrailerIds, int count)
    {
        if (count <= 0 || cachedTrailerIds.Count == 0)
        {
            return Enumerable.Empty<Guid>();
        }

        var enabledRules = _config.TrailerSelectionRules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ToList();

        // If no rules enabled, use random selection
        if (enabledRules.Count == 0)
        {
            return GetRandomTrailers(cachedTrailerIds, count);
        }

        // Score each trailer based on the rules
        var scoredTrailers = new List<(string Id, int Score, bool Played, DateTime? DateAdded)>();

        foreach (var trailerId in cachedTrailerIds)
        {
            var score = ScoreTrailer(trailerId, movie, user, enabledRules);
            var trailerGuid = GetTrailerGuid(trailerId);
            var trailerItem = _libraryManager.GetItemById(trailerGuid);

            var played = false;
            DateTime? dateAdded = null;

            if (trailerItem != null)
            {
                played = trailerItem.IsPlayed(user);
                dateAdded = trailerItem.DateCreated;
            }

            scoredTrailers.Add((trailerId, score, played, dateAdded));
        }

        // Sort by score descending, then apply secondary sorting based on rules
        var sortedTrailers = ApplySecondarySort(scoredTrailers, enabledRules);

        // Take top scorers with some randomization among ties
        var selected = sortedTrailers
            .Take(count)
            .Select(t => GetTrailerGuid(t.Id))
            .ToList();

        _logger.LogDebug(
            "Selected {Count} trailers for movie {Movie} (requested {Requested})",
            selected.Count,
            movie.Name,
            count);

        return selected;
    }

    private int ScoreTrailer(string trailerId, Movie movie, User user, List<TrailerSelectionRule> rules)
    {
        var score = 0;

        // Try to get the cached SearchMovie data for this trailer
        if (!_memoryCache.TryGetValue($"{trailerId}-item", out SearchMovie trailerMovie))
        {
            // If no cached data, give a neutral score
            return 0;
        }

        foreach (var rule in rules)
        {
            // Higher priority rules (lower number) give more points
            var priorityWeight = 10 - Math.Min(rule.Priority, 9);

            switch (rule.RuleType)
            {
                case TrailerSelectionRuleType.Genre:
                    score += ScoreGenreMatch(trailerMovie, movie) * priorityWeight;
                    break;

                case TrailerSelectionRuleType.Year:
                    score += ScoreYearMatch(trailerMovie, movie) * priorityWeight;
                    break;

                case TrailerSelectionRuleType.Decade:
                    score += ScoreDecadeMatch(trailerMovie, movie) * priorityWeight;
                    break;

                case TrailerSelectionRuleType.RecentlyAdded:
                    // Score based on date will be handled in secondary sort
                    break;

                case TrailerSelectionRuleType.Unplayed:
                    // Score based on played status will be handled in secondary sort
                    break;
            }
        }

        // Apply rating enforcement if enabled
        if (_config.EnforceRatingLimitTrailers && !IsRatingAppropriate(trailerMovie, movie))
        {
            score -= 100; // Heavily penalize inappropriate ratings
        }

        return score;
    }

    private static int ScoreGenreMatch(SearchMovie trailer, Movie movie)
    {
        var movieGenres = movie.Genres ?? Array.Empty<string>();
        if (movieGenres.Length == 0)
        {
            return 0;
        }

        // TMDb SearchMovie doesn't have genres directly, so we use genre_ids
        // For simplicity, return 1 if we can't determine (we'd need to look up genre mappings)
        // In a full implementation, we'd map genre_ids to genre names
        return 1;
    }

    private static int ScoreYearMatch(SearchMovie trailer, Movie movie)
    {
        if (!movie.ProductionYear.HasValue || trailer.ReleaseDate == null)
        {
            return 0;
        }

        var trailerYear = trailer.ReleaseDate.Value.Year;
        var movieYear = movie.ProductionYear.Value;
        var yearDiff = Math.Abs(trailerYear - movieYear);

        // More points for closer years
        return yearDiff switch
        {
            0 => 5,
            1 => 4,
            2 => 3,
            <= 5 => 2,
            <= 10 => 1,
            _ => 0
        };
    }

    private static int ScoreDecadeMatch(SearchMovie trailer, Movie movie)
    {
        if (!movie.ProductionYear.HasValue || trailer.ReleaseDate == null)
        {
            return 0;
        }

        var trailerDecade = (trailer.ReleaseDate.Value.Year / 10) * 10;
        var movieDecade = (movie.ProductionYear.Value / 10) * 10;

        return trailerDecade == movieDecade ? 3 : 0;
    }

    private static bool IsRatingAppropriate(SearchMovie trailer, Movie movie)
    {
        // If movie has no rating, allow any trailer
        if (string.IsNullOrEmpty(movie.OfficialRating))
        {
            return true;
        }

        // TMDb SearchMovie doesn't include certification/rating directly
        // We'd need to look up the movie details to get this
        // For now, allow all trailers when we can't determine the rating
        return true;
    }

    private IEnumerable<(string Id, int Score, bool Played, DateTime? DateAdded)> ApplySecondarySort(
        List<(string Id, int Score, bool Played, DateTime? DateAdded)> trailers,
        List<TrailerSelectionRule> rules)
    {
        IOrderedEnumerable<(string Id, int Score, bool Played, DateTime? DateAdded)> sorted =
            trailers.OrderByDescending(t => t.Score);

        // Apply secondary sorting based on rule priorities
        foreach (var rule in rules)
        {
            switch (rule.RuleType)
            {
                case TrailerSelectionRuleType.Unplayed:
                    sorted = sorted.ThenBy(t => t.Played); // Unplayed first
                    break;

                case TrailerSelectionRuleType.RecentlyAdded:
                    sorted = sorted.ThenByDescending(t => t.DateAdded ?? DateTime.MinValue);
                    break;
            }
        }

        // Add randomization for equal scores
        return sorted.ThenBy(_ => Random.Shared.Next());
    }

    private IEnumerable<Guid> GetRandomTrailers(IList<string> cachedTrailerIds, int count)
    {
        var shuffled = cachedTrailerIds.OrderBy(_ => Random.Shared.Next()).ToList();
        return shuffled.Take(count).Select(GetTrailerGuid);
    }

    private static Guid GetTrailerGuid(string trailerId)
    {
        return MediaBrowser.Common.Extensions.GuidExtensions.GetMD5(trailerId);
    }
}
