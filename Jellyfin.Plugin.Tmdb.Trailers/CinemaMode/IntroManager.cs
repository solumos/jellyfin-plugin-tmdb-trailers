using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Tmdb.Trailers.Config;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tmdb.Trailers.CinemaMode;

/// <summary>
/// Manages the full cinema mode intro sequence.
/// </summary>
public class IntroManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<IntroManager> _logger;
    private readonly PreRollSelector _preRollSelector;
    private readonly TrailerSelector _trailerSelector;
    private readonly PluginConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="memoryCache">The memory cache.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="preRollLogger">The logger for pre-roll selector.</param>
    /// <param name="trailerLogger">The logger for trailer selector.</param>
    public IntroManager(
        ILibraryManager libraryManager,
        IMemoryCache memoryCache,
        ILogger<IntroManager> logger,
        ILogger<PreRollSelector> preRollLogger,
        ILogger<TrailerSelector> trailerLogger)
    {
        _libraryManager = libraryManager;
        _memoryCache = memoryCache;
        _logger = logger;
        _config = TmdbTrailerPlugin.Instance.Configuration;

        _preRollSelector = new PreRollSelector(
            libraryManager,
            preRollLogger,
            _config);

        _trailerSelector = new TrailerSelector(
            libraryManager,
            memoryCache,
            trailerLogger,
            _config);

        _logger.LogInformation(
            "IntroManager initialized. Cinema mode: {Enabled}, Trailer pre-roll library: {TrailerLib}, Feature pre-roll library: {FeatureLib}",
            _config.EnableCinemaMode,
            string.IsNullOrEmpty(_config.TrailerPreRollsLibrary) ? "none" : _config.TrailerPreRollsLibrary,
            string.IsNullOrEmpty(_config.FeaturePreRollsLibrary) ? "none" : _config.FeaturePreRollsLibrary);
    }

    /// <summary>
    /// Gets the full intro sequence for a movie.
    /// </summary>
    /// <param name="item">The item being played.</param>
    /// <param name="user">The user watching.</param>
    /// <param name="cachedTrailerIds">The cached trailer IDs.</param>
    /// <param name="trailerCount">The number of trailers to include.</param>
    /// <returns>The ordered list of intro items.</returns>
    public IEnumerable<IntroInfo> GetIntroSequence(
        BaseItem item,
        User user,
        IList<string> cachedTrailerIds,
        int trailerCount)
    {
        var intros = new List<IntroInfo>();

        // Only apply cinema mode for movies
        if (item is not Movie movie)
        {
            _logger.LogDebug("Item {Name} is not a movie, using random trailers", item.Name);
            return GetRandomTrailers(cachedTrailerIds, trailerCount);
        }

        if (!_config.EnableCinemaMode)
        {
            _logger.LogDebug("Cinema mode disabled, using random trailers for {Movie}", movie.Name);
            return GetRandomTrailers(cachedTrailerIds, trailerCount);
        }

        _logger.LogDebug("Building cinema mode intro sequence for {Movie}", movie.Name);

        // 1. Trailer pre-roll (e.g., "Coming attractions")
        var trailerPreRoll = _preRollSelector.GetTrailerPreRoll(movie);
        if (trailerPreRoll != null)
        {
            _logger.LogDebug("Adding trailer pre-roll: {Name}", trailerPreRoll.Name);
            intros.Add(new IntroInfo { ItemId = trailerPreRoll.Id });
        }

        // 2. Trailers (smart selection)
        var selectedTrailers = _trailerSelector.SelectTrailers(
            movie,
            user,
            cachedTrailerIds,
            trailerCount);

        foreach (var trailerId in selectedTrailers)
        {
            intros.Add(new IntroInfo { ItemId = trailerId });
        }

        _logger.LogDebug("Added {Count} trailers to sequence", selectedTrailers.Count());

        // 3. Feature pre-roll (e.g., "Feature presentation")
        var featurePreRoll = _preRollSelector.GetFeaturePreRoll(movie);
        if (featurePreRoll != null)
        {
            _logger.LogDebug("Adding feature pre-roll: {Name}", featurePreRoll.Name);
            intros.Add(new IntroInfo { ItemId = featurePreRoll.Id });
        }

        _logger.LogInformation(
            "Cinema mode sequence for {Movie}: {TrailerPreRoll} trailer pre-roll, {Trailers} trailers, {FeaturePreRoll} feature pre-roll",
            movie.Name,
            trailerPreRoll != null ? 1 : 0,
            selectedTrailers.Count(),
            featurePreRoll != null ? 1 : 0);

        return intros;
    }

    private IEnumerable<IntroInfo> GetRandomTrailers(IList<string> cachedTrailerIds, int count)
    {
        if (count <= 0 || cachedTrailerIds.Count == 0)
        {
            return Enumerable.Empty<IntroInfo>();
        }

        var shuffled = cachedTrailerIds.OrderBy(_ => Random.Shared.Next()).ToList();
        return shuffled
            .Take(count)
            .Select(id => new IntroInfo
            {
                ItemId = id.GetMD5()
            });
    }
}
