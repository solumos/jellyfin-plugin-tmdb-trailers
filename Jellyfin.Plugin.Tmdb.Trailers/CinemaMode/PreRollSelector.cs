#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Tmdb.Trailers.Config;
using Jellyfin.Plugin.Tmdb.Trailers.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tmdb.Trailers.CinemaMode;

/// <summary>
/// Selects pre-roll content from local libraries.
/// </summary>
public class PreRollSelector
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreRollSelector"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The plugin configuration.</param>
    public PreRollSelector(ILibraryManager libraryManager, ILogger logger, PluginConfiguration config)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Gets a trailer pre-roll that matches the given movie.
    /// </summary>
    /// <param name="movie">The movie being played.</param>
    /// <returns>The pre-roll item, or null if none found.</returns>
    public BaseItem? GetTrailerPreRoll(Movie movie)
    {
        if (string.IsNullOrEmpty(_config.TrailerPreRollsLibrary))
        {
            return null;
        }

        return GetPreRoll(
            _config.TrailerPreRollsLibrary,
            _config.TrailerPreRollsSelections,
            _config.TrailerPreRollsRatingLimit,
            _config.TrailerPreRollsIgnoreOutOfSeason,
            movie);
    }

    /// <summary>
    /// Gets a feature pre-roll that matches the given movie.
    /// </summary>
    /// <param name="movie">The movie being played.</param>
    /// <returns>The pre-roll item, or null if none found.</returns>
    public BaseItem? GetFeaturePreRoll(Movie movie)
    {
        if (string.IsNullOrEmpty(_config.FeaturePreRollsLibrary))
        {
            return null;
        }

        return GetPreRoll(
            _config.FeaturePreRollsLibrary,
            _config.FeaturePreRollsSelections,
            _config.FeaturePreRollsRatingLimit,
            _config.FeaturePreRollsIgnoreOutOfSeason,
            movie);
    }

    private BaseItem? GetPreRoll(
        string libraryId,
        List<PreRollSelection> selections,
        bool enforceRating,
        bool ignoreOutOfSeason,
        Movie movie)
    {
        if (!Guid.TryParse(libraryId, out var libraryGuid))
        {
            _logger.LogWarning("Invalid library ID: {LibraryId}", libraryId);
            return null;
        }

        var library = _libraryManager.GetItemById(libraryGuid);
        if (library == null)
        {
            _logger.LogWarning("Library not found: {LibraryId}", libraryId);
            return null;
        }

        // Get active seasonal tags
        var activeSeasonalTags = _config.SeasonalTagDefinitions
            .Where(s => s.IsInSeason())
            .Select(s => s.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Try each selection rule in priority order
        var orderedSelections = selections.OrderBy(s => s.Priority).ToList();

        foreach (var selection in orderedSelections)
        {
            var item = FindMatchingPreRoll(library, selection, movie, enforceRating, ignoreOutOfSeason, activeSeasonalTags);
            if (item != null)
            {
                _logger.LogDebug("Found pre-roll {Name} for movie {Movie}", item.Name, movie.Name);
                return item;
            }
        }

        // Fallback: return any random item from the library
        var fallback = GetRandomItemFromLibrary(library, movie, enforceRating);
        if (fallback != null)
        {
            _logger.LogDebug("Using fallback pre-roll {Name} for movie {Movie}", fallback.Name, movie.Name);
        }

        return fallback;
    }

    private BaseItem? FindMatchingPreRoll(
        BaseItem library,
        PreRollSelection selection,
        Movie movie,
        bool enforceRating,
        bool ignoreOutOfSeason,
        HashSet<string> activeSeasonalTags)
    {
        var query = new InternalItemsQuery
        {
            Parent = library,
            IsFolder = false,
            Recursive = true,
            MediaTypes = new[] { MediaType.Video }
        };

        var items = _libraryManager.GetItemList(query);

        foreach (var item in items.OrderBy(_ => Random.Shared.Next()))
        {
            // Check rating enforcement
            if (enforceRating && !IsRatingAppropriate(item, movie))
            {
                continue;
            }

            // Check seasonal filtering
            if (ignoreOutOfSeason && selection.IsSeasonal)
            {
                var itemTags = item.Tags ?? Array.Empty<string>();
                var hasActiveSeason = itemTags.Any(t => activeSeasonalTags.Contains(t));
                if (!hasActiveSeason)
                {
                    continue;
                }
            }

            // Check selection criteria
            if (MatchesSelection(item, selection, movie))
            {
                return item;
            }
        }

        return null;
    }

    private bool MatchesSelection(BaseItem item, PreRollSelection selection, Movie movie)
    {
        // Check name match
        if (!string.IsNullOrEmpty(selection.Name) &&
            !item.Name.Contains(selection.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check year match
        if (selection.Year.HasValue && item.ProductionYear != selection.Year.Value)
        {
            return false;
        }

        // Check decade match
        if (selection.Decade.HasValue)
        {
            var itemDecade = (item.ProductionYear / 10) * 10;
            if (itemDecade != selection.Decade.Value)
            {
                return false;
            }
        }

        // Check genre match
        if (selection.Genres.Count > 0)
        {
            var itemGenres = item.Genres ?? Array.Empty<string>();
            var movieGenres = movie.Genres ?? Array.Empty<string>();

            var matchingGenres = itemGenres.Intersect(selection.Genres, StringComparer.OrdinalIgnoreCase);
            if (!matchingGenres.Any())
            {
                return false;
            }
        }

        // Check tags match
        if (selection.Tags.Count > 0)
        {
            var itemTags = item.Tags ?? Array.Empty<string>();
            if (selection.RequireAllTags)
            {
                if (!selection.Tags.All(t => itemTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            else
            {
                if (!selection.Tags.Any(t => itemTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private BaseItem? GetRandomItemFromLibrary(BaseItem library, Movie movie, bool enforceRating)
    {
        var query = new InternalItemsQuery
        {
            Parent = library,
            IsFolder = false,
            Recursive = true,
            MediaTypes = new[] { MediaType.Video }
        };

        var items = _libraryManager.GetItemList(query);

        if (enforceRating)
        {
            items = items.Where(i => IsRatingAppropriate(i, movie)).ToList();
        }

        return items.OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
    }

    private static bool IsRatingAppropriate(BaseItem preRoll, Movie movie)
    {
        // If movie has no rating, allow any pre-roll
        if (string.IsNullOrEmpty(movie.OfficialRating))
        {
            return true;
        }

        // If pre-roll has no rating, allow it
        if (string.IsNullOrEmpty(preRoll.OfficialRating))
        {
            return true;
        }

        // Simple rating comparison - pre-roll rating should be <= movie rating
        // This is a simplified approach; a full implementation would use rating definitions
        var ratingOrder = new[] { "G", "PG", "PG-13", "R", "NC-17" };
        var movieRatingIndex = Array.FindIndex(ratingOrder, r => r.Equals(movie.OfficialRating, StringComparison.OrdinalIgnoreCase));
        var preRollRatingIndex = Array.FindIndex(ratingOrder, r => r.Equals(preRoll.OfficialRating, StringComparison.OrdinalIgnoreCase));

        // If ratings not found in our list, allow the combination
        if (movieRatingIndex < 0 || preRollRatingIndex < 0)
        {
            return true;
        }

        return preRollRatingIndex <= movieRatingIndex;
    }
}
