using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Plugin.Tmdb.Trailers.CinemaMode;
using Jellyfin.Plugin.Tmdb.Trailers.Config;
using Jellyfin.Plugin.Tmdb.Trailers.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;
using Video = TMDbLib.Objects.General.Video;

namespace Jellyfin.Plugin.Tmdb.Trailers;

/// <summary>
/// The TMDb manager.
/// </summary>
public class TmdbManager : IDisposable
{
    /// <summary>
    /// Gets the page size.
    /// TMDb always returns 20 items.
    /// </summary>
    public const int PageSize = 20;

    private readonly TimeSpan _defaultCacheTime = TimeSpan.FromDays(1);
    private readonly List<string> _cacheIds = new();
    private readonly ConcurrentDictionary<string, CachedTrailerInfo> _cachedTrailers = new();

    private readonly ILogger<TmdbManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;

    private TMDbClient _client;
    private IntroManager _introManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbManager"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TmdbManager}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    public TmdbManager(
        ILogger<TmdbManager> logger,
        ILoggerFactory loggerFactory,
        IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _memoryCache = memoryCache;

        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
    }

    private string CachePath => Path.Join(_applicationPaths.CachePath, "tmdb-intro-trailers");

    private PluginConfiguration Configuration => TmdbTrailerPlugin.Instance.Configuration;

    /// <summary>
    /// Gets the cached trailer metadata for smart selection.
    /// </summary>
    public IReadOnlyDictionary<string, CachedTrailerInfo> CachedTrailers => _cachedTrailers;

    private TMDbClient Client => _client ??= new TMDbClient(Configuration.ApiKey);

    /// <summary>
    /// Get channel items.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel item result.</returns>
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            ChannelItemResult result = null;
            // Initial entry
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return GetChannelTypes();
            }

            if (_memoryCache.TryGetValue(query.FolderId, out ChannelItemResult cachedValue))
            {
                _logger.LogDebug("Function={Function} FolderId={FolderId} Cache Hit", nameof(GetChannelItems), query.FolderId);
                return cachedValue;
            }

            // Get upcoming movies.
            if (query.FolderId.Equals("upcoming", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetUpcomingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.ComingSoonToTheaters);
            }

            // Get now playing movies.
            else if (query.FolderId.Equals("nowplaying", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetNowPlayingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.ComingSoonToTheaters);
            }

            // Get popular movies.
            else if (query.FolderId.Equals("popular", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetPopularMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.Archive);
            }

            // Get top rated movies.
            else if (query.FolderId.Equals("toprated", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetTopRatedMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.Archive);
            }

            // Get video streams for item.
            else if (int.TryParse(query.FolderId, out var movieId))
            {
                var searchMovie = new SearchMovie { Id = movieId };
                var videos = await GetMovieStreamsAsync(searchMovie, cancellationToken).ConfigureAwait(false);
                result = GetVideoItem(videos.Movie, videos.Result, false);
            }

            if (result != null)
            {
                _memoryCache.Set(query.FolderId, result, _defaultCacheTime);
            }

            return result ?? new ChannelItemResult();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelItems));
            throw;
        }
    }

    /// <summary>
    /// Get All Channel Items.
    /// </summary>
    /// <param name="ignoreCache">Ignore cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel item result.</returns>
    public async Task<ChannelItemResult> GetAllChannelItems(bool ignoreCache, CancellationToken cancellationToken)
    {
        try
        {
            if (!ignoreCache && _memoryCache.TryGetValue("all-trailer", out ChannelItemResult cachedValue))
            {
                _logger.LogDebug("Function={Function} Cache Hit", nameof(GetAllChannelItems));
                return cachedValue;
            }

            var query = new InternalChannelItemQuery();

            var channelItemsResult = new ChannelItemResult();
            var movieTasks = new List<Task<(SearchMovie Movie, ResultContainer<Video> Result)>>();

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersUpcoming)
            {
                var upcomingMovies = await GetUpcomingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(upcomingMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersNowPlaying)
            {
                var nowPlayingMovies = await GetNowPlayingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(nowPlayingMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersPopular)
            {
                var popularMovies = await GetPopularMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(popularMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersTopRated)
            {
                var topRatedMovies = await GetTopRatedMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(topRatedMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            await Task.WhenAll(movieTasks).ConfigureAwait(false);
            var resultList = new List<ChannelItemInfo>();
            foreach (var task in movieTasks)
            {
                var result = await task.ConfigureAwait(false);
                resultList.AddRange(GetVideoItem(result.Movie, result.Result, true).Items);
            }

            channelItemsResult.Items = resultList;
            _memoryCache.Set("all-trailer", channelItemsResult);
            return channelItemsResult;
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetAllChannelItems));
            throw;
        }
    }

    /// <summary>
    /// Get channel image.
    /// </summary>
    /// <param name="type">Image type.</param>
    /// <returns>The image response.</returns>
    public Task<DynamicImageResponse> GetChannelImage(ImageType type)
    {
        try
        {
            _logger.LogDebug(nameof(GetChannelImage));
            if (type == ImageType.Thumb)
            {
                var name = GetType().Namespace + ".Images.jellyfin-plugin-tmdb.png";
                var response = new DynamicImageResponse
                {
                    Format = ImageFormat.Png,
                    HasImage = true,
                    Stream = GetType().Assembly.GetManifestResourceStream(name)
                };

                return Task.FromResult(response);
            }

            return Task.FromResult<DynamicImageResponse>(null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelImage));
            throw;
        }
    }

    /// <summary>
    /// Get supported channel images.
    /// </summary>
    /// <returns>The supported channel images.</returns>
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        yield return ImageType.Thumb;
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    /// <param name="disposing">Dispose everything.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Client?.Dispose();
        }
    }

    /// <summary>
    /// Calculate page size from start index.
    /// </summary>
    /// <param name="startIndex">Start index.</param>
    /// <returns>The page number.</returns>
    private static int GetPageNumber(int? startIndex)
    {
        var start = startIndex ?? 0;

        return (int)Math.Floor(start / (double)PageSize);
    }

    /// <summary>
    /// Gets the original image url.
    /// </summary>
    /// <param name="imagePath">The image resource path.</param>
    /// <returns>The full image path.</returns>
    private string GetImageUrl(string imagePath)
    {
        try
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "https://image.tmdb.org/t/p/original/{0}",
                imagePath.TrimStart('/'));
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetImageUrl));
            throw;
        }
    }

    /// <summary>
    /// Get types of trailers.
    /// </summary>
    /// <returns><see cref="ChannelItemResult"/> containing the types of trailers.</returns>
    private ChannelItemResult GetChannelTypes()
    {
        _logger.LogDebug("Get Channel Types");
        return new ChannelItemResult
        {
            Items = new List<ChannelItemInfo>
            {
                new ChannelItemInfo
                {
                    Id = "upcoming",
                    FolderType = ChannelFolderType.Container,
                    Name = "Upcoming",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                },
                new ChannelItemInfo
                {
                    Id = "nowplaying",
                    FolderType = ChannelFolderType.Container,
                    Name = "Now Playing",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                },
                new ChannelItemInfo
                {
                    Id = "popular",
                    FolderType = ChannelFolderType.Container,
                    Name = "Popular",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                },
                new ChannelItemInfo
                {
                    Id = "toprated",
                    FolderType = ChannelFolderType.Container,
                    Name = "Top Rated",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                }
            },
            TotalRecordCount = 4
        };
    }

    /// <summary>
    /// Get playback url from site and key.
    /// </summary>
    /// <param name="site">Site to play from.</param>
    /// <param name="key">Video key.</param>
    /// <returns>Stream information for playback.</returns>
    private async Task<StreamInfo> GetPlaybackUrlAsync(string site, string key)
    {
        try
        {
            if (site.Equals("youtube", StringComparison.OrdinalIgnoreCase))
            {
                var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
                var youTubeClient = new YoutubeClient(httpClient);
                var streamManifest = await youTubeClient.Videos.Streams.GetManifestAsync(key).ConfigureAwait(false);

                // Try muxed streams first (may still work for some videos)
                var muxedStreams = streamManifest.GetMuxedStreams();
                if (muxedStreams.Any())
                {
                    var bestMuxed = muxedStreams.GetWithHighestVideoQuality();
                    if (bestMuxed != null)
                    {
                        _logger.LogDebug("Found muxed stream for {Key}: {Quality}", key, bestMuxed.VideoQuality);
                        return new StreamInfo
                        {
                            Url = bestMuxed.Url,
                            Bitrate = bestMuxed.Bitrate,
                            Container = bestMuxed.Container,
                            IsMuxed = true
                        };
                    }
                }

                // Fallback: Get best video and audio streams separately
                // YouTube no longer provides muxed streams for most videos as of 2024
                var videoStream = streamManifest
                    .GetVideoOnlyStreams()
                    .Where(s => s.Container == Container.Mp4)
                    .OrderByDescending(s => s.VideoQuality.MaxHeight)
                    .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                    .FirstOrDefault();

                var audioStream = streamManifest
                    .GetAudioOnlyStreams()
                    .Where(s => s.Container == Container.Mp4)
                    .OrderByDescending(s => s.Bitrate.BitsPerSecond)
                    .FirstOrDefault();

                if (videoStream != null && audioStream != null)
                {
                    _logger.LogDebug(
                        "Using separate streams for {Key}: Video={VideoQuality}, Audio={AudioBitrate}bps",
                        key,
                        videoStream.VideoQuality,
                        audioStream.Bitrate.BitsPerSecond);
                    return new StreamInfo
                    {
                        VideoUrl = videoStream.Url,
                        AudioUrl = audioStream.Url,
                        Bitrate = new Bitrate(videoStream.Bitrate.BitsPerSecond + audioStream.Bitrate.BitsPerSecond),
                        Container = videoStream.Container,
                        IsMuxed = false
                    };
                }

                // Last resort: video-only (no audio)
                if (videoStream != null)
                {
                    _logger.LogWarning("No audio stream available for video {Key}, using video-only stream", key);
                    return new StreamInfo
                    {
                        Url = videoStream.Url,
                        Bitrate = videoStream.Bitrate,
                        Container = videoStream.Container,
                        IsMuxed = true
                    };
                }

                _logger.LogWarning("No suitable streams found for video {Key}", key);
            }

            // TODO other sites.
            return null;
        }
        catch (Exception e)
        {
            // Check if the video is unavailable (deleted, private, region-locked, etc.)
            // These are expected cases and should be logged as warnings, not errors
            var message = e.Message;
            var isUnavailable = message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("cipher", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("removed", StringComparison.OrdinalIgnoreCase);

            if (isUnavailable)
            {
                _logger.LogWarning("Video {Key} is unavailable on YouTube: {Message}", key, message);
            }
            else
            {
                _logger.LogError(e, "GetPlaybackUrlAsync failed for video {Key} (https://youtube.com/watch?v={Key})", key, key);
            }

            return null;
        }
    }

    /// <summary>
    /// Create channel item result from search result.
    /// </summary>
    /// <param name="movies">Search container of movies.</param>
    /// <param name="trailerType">The trailer type.</param>
    /// <returns>The channel item result.</returns>
    private ChannelItemResult GetChannelItemResult(IEnumerable<SearchMovie> movies, TrailerType trailerType)
    {
        try
        {
            var channelItems = new List<ChannelItemInfo>();
            foreach (var item in movies)
            {
                var posterUrl = GetImageUrl(item.PosterPath);
                _memoryCache.Set($"{item.Id}-item", item, _defaultCacheTime);
                _memoryCache.Set($"{item.Id}-poster", posterUrl, _defaultCacheTime);
                _memoryCache.Set($"{item.Id}-trailer", trailerType, _defaultCacheTime);
                channelItems.Add(new ChannelItemInfo
                {
                    Id = item.Id.ToString(CultureInfo.InvariantCulture),
                    Name = item.Title,
                    FolderType = ChannelFolderType.Container,
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = posterUrl
                });
            }

            return new ChannelItemResult
            {
                Items = channelItems
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelItemResult));
            throw;
        }
    }

    /// <summary>
    /// Get upcoming movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upcoming movies.</returns>
    private async Task<List<SearchMovie>> GetUpcomingMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetUpcomingMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMovieUpcomingListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetUpcomingMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get now playing movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The now playing movies.</returns>
    private async Task<List<SearchMovie>> GetNowPlayingMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetNowPlayingMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMovieNowPlayingListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetNowPlayingMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get popular movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The popular movies.</returns>
    private async Task<List<SearchMovie>> GetPopularMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetPopularMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMoviePopularListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetPopularMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get top rated movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The top rated movies.</returns>
    private async Task<List<SearchMovie>> GetTopRatedMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetTopRatedMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMovieTopRatedListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetTopRatedMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get available movie streams.
    /// </summary>
    /// <param name="movie">The movie.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movie streams.</returns>
    private async Task<(SearchMovie Movie, ResultContainer<Video> Result)> GetMovieStreamsAsync(SearchMovie movie, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("{Function} Id={Id}", nameof(GetMovieStreamsAsync), movie.Id);
            var response = await Client.GetMovieVideosAsync(movie.Id, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Function} Response={@Response}", nameof(GetMovieStreamsAsync), response);
            return (movie, response);
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetMovieStreamsAsync));
            throw;
        }
    }

    private ChannelItemResult GetVideoItem(SearchMovie searchMovie, ResultContainer<Video> videoResult, bool trailerChannel)
    {
        try
        {
            _logger.LogDebug("{Function} VideoResult={@VideoResult}", nameof(GetVideoItem), videoResult);
            var channelItems = new List<ChannelItemInfo>(videoResult.Results.Count);
            foreach (var video in videoResult.Results)
            {
                // Only add first trailer
                if (trailerChannel && channelItems.Count != 0)
                {
                    break;
                }

                var channelItemInfo = GetVideoChannelItem(videoResult.Id, video, trailerChannel);
                if (channelItemInfo == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(searchMovie.Title))
                {
                    channelItemInfo.Name = $"{searchMovie.Title} - {channelItemInfo.Name}";
                }

                channelItems.Add(channelItemInfo);
            }

            return new ChannelItemResult
            {
                Items = channelItems,
                TotalRecordCount = channelItems.Count
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetVideoItem));
            throw;
        }
    }

    private ChannelItemInfo GetVideoChannelItem(int id, Video video, bool trailerChannel)
    {
        try
        {
            // Returning only trailers
            if (trailerChannel && !string.Equals(video.Type, "trailer", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _logger.LogDebug("{Function} Id={Id} Video={@Video}", nameof(GetVideoChannelItem), id, video);
            _memoryCache.TryGetValue($"{id}-poster", out string posterUrl);
            _memoryCache.TryGetValue($"{id}-trailer", out TrailerType? trailerType);
            _memoryCache.Set($"{video.Id}-video", video, _defaultCacheTime);

            trailerType ??= TrailerType.Archive;

            var channelItemInfo = GetChannelItemInfo(video);
            if (channelItemInfo == null)
            {
                return null;
            }

            channelItemInfo.Name = video.Name;
            if (!string.IsNullOrEmpty(posterUrl))
            {
                channelItemInfo.ImageUrl = posterUrl;
            }

            // only add additional properties if sourced from trailer channel.
            if (trailerChannel)
            {
                channelItemInfo.ExtraType = ExtraType.Trailer;
                channelItemInfo.TrailerTypes = new List<TrailerType>
                {
                    trailerType.Value
                };
                channelItemInfo.ProviderIds = new Dictionary<string, string>
                {
                    {
                        MetadataProvider.Tmdb.ToString(), id.ToString(CultureInfo.InvariantCulture)
                    }
                };
            }

            return channelItemInfo;
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetVideoChannelItem));
            throw;
        }
    }

    /// <summary>
    /// Get stream information from video item.
    /// </summary>
    /// <param name="item">Video item.</param>
    /// <returns>Stream information.</returns>
    private ChannelItemInfo GetChannelItemInfo(Video item)
    {
        try
        {
            return new ChannelItemInfo
            {
                Id = item.Id,
                Name = item.Name,
                OriginalTitle = item.Name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelItemInfo));
            throw;
        }
    }

    /// <summary>
    /// Get media source from video.
    /// </summary>
    /// <param name="id">video id.</param>
    /// <returns>Media source info.</returns>
    public async Task<MediaSourceInfo> GetMediaSource(string id)
    {
        try
        {
            _memoryCache.TryGetValue($"{id}-video", out Video video);
            if (video == null)
            {
                return null;
            }

            // Check if we have a cached version first
            Directory.CreateDirectory(CachePath);
            var cachedPath = Path.Combine(CachePath, $"{id}.mp4");
            if (File.Exists(cachedPath))
            {
                _logger.LogDebug("Using cached video for {Id}: {Path}", id, cachedPath);
                var fileInfo = new FileInfo(cachedPath);
                return new MediaSourceInfo
                {
                    Name = video.Name,
                    Path = cachedPath,
                    TranscodingUrl = video.Key,
                    Protocol = MediaProtocol.File,
                    Id = video.Id,
                    IsRemote = false,
                    Container = "mp4",
                    Size = fileInfo.Length
                };
            }

            var streamInfo = await GetPlaybackUrlAsync(video.Site, video.Key).ConfigureAwait(false);

            if (streamInfo == null)
            {
                // Video is unavailable on YouTube - remove it from the library
                _logger.LogInformation("Removing unavailable trailer {Id} ({Key}) from library", id, video.Key);

                // Clear from memory cache
                _memoryCache.Remove($"{id}-video");

                try
                {
                    var guid = id.GetMD5();
                    var libraryItem = _libraryManager.GetItemById(guid);
                    if (libraryItem is not null)
                    {
                        var deleteOptions = new DeleteOptions { DeleteFileLocation = true };
                        _libraryManager.DeleteItem(libraryItem, deleteOptions);
                        _logger.LogInformation("Successfully removed unavailable trailer {Id} from library", id);
                    }
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx, "Failed to remove unavailable trailer {Id} from library", id);
                }

                return null;
            }

            // If we have a muxed stream, return it directly for streaming
            if (streamInfo.IsMuxed && !string.IsNullOrEmpty(streamInfo.Url))
            {
                return new MediaSourceInfo
                {
                    Name = video.Name,
                    Path = streamInfo.Url,
                    TranscodingUrl = video.Key,
                    Protocol = MediaProtocol.Http,
                    Id = video.Id,
                    IsRemote = true,
                    Bitrate = Convert.ToInt32(streamInfo.Bitrate.BitsPerSecond),
                    Container = streamInfo.Container.Name
                };
            }

            // For non-muxed streams, we need to download and mux using FFmpeg
            // This is required because YouTube no longer provides muxed streams
            _logger.LogInformation("Downloading and muxing video {Id} ({Key}) for playback", id, video.Key);

            var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            var youTubeClient = new YoutubeClient(httpClient);

            try
            {
                await youTubeClient.Videos.DownloadAsync(
                    video.Key,
                    cachedPath,
                    cfg => cfg
                        .SetContainer(Container.Mp4)
                        .SetPreset(ConversionPreset.Fast)
                        .SetFFmpegPath(_mediaEncoder.EncoderPath)).ConfigureAwait(false);

                if (File.Exists(cachedPath))
                {
                    var fileInfo = new FileInfo(cachedPath);
                    _logger.LogInformation("Successfully cached video {Id} at {Path} ({Size} bytes)", id, cachedPath, fileInfo.Length);
                    return new MediaSourceInfo
                    {
                        Name = video.Name,
                        Path = cachedPath,
                        TranscodingUrl = video.Key,
                        Protocol = MediaProtocol.File,
                        Id = video.Id,
                        IsRemote = false,
                        Container = "mp4",
                        Size = fileInfo.Length
                    };
                }
            }
            catch (Exception downloadEx)
            {
                _logger.LogError(downloadEx, "Failed to download and mux video {Id} ({Key})", id, video.Key);
            }

            return null;
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetMediaSource));
            throw;
        }
    }

    /// <summary>
    /// Update the intro metadata cache (fast, no video downloads).
    /// This caches trailer metadata for smart selection without downloading video files.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="forceRefresh">Whether to force refresh from TMDb API.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task UpdateIntroMetadataCache(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting metadata cache update (forceRefresh={ForceRefresh})...", forceRefresh);

        var channelItems = await GetAllChannelItems(forceRefresh, cancellationToken).ConfigureAwait(false);

        _cachedTrailers.Clear();
        foreach (var item in channelItems.Items)
        {
            if (_memoryCache.TryGetValue($"{item.Id}-item", out SearchMovie movie))
            {
                _cachedTrailers[item.Id] = new CachedTrailerInfo
                {
                    Id = item.Id,
                    Name = item.Name,
                    TmdbMovieId = movie.Id,
                    GenreIds = movie.GenreIds?.ToArray() ?? Array.Empty<int>(),
                    Year = movie.ReleaseDate?.Year,
                    PosterUrl = !string.IsNullOrEmpty(movie.PosterPath)
                        ? $"https://image.tmdb.org/t/p/w500{movie.PosterPath}"
                        : string.Empty
                };
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Metadata cache updated: {Count} trailers cached in {Elapsed}ms",
            _cachedTrailers.Count,
            sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Update the intro cache.
    /// Downloads video files for intro playback, limited to avoid long processing times.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task UpdateIntroCache(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(CachePath);

        // First, update metadata cache (fast) - uses existing channel item cache
        await UpdateIntroMetadataCache(cancellationToken, forceRefresh: false).ConfigureAwait(false);

        var channelItems = await GetAllChannelItems(false, cancellationToken).ConfigureAwait(false);

        // Clean up trailers no longer in channel
        var deleteOptions = new DeleteOptions { DeleteFileLocation = true };
        var existingCache = Directory.GetFiles(CachePath);
        var existingIds = existingCache.Select(c => Path.GetFileNameWithoutExtension(c)).ToArray();
        var deletedCount = 0;

        for (var i = 0; i < existingCache.Length; i++)
        {
            var existingId = existingIds[i];
            if (!string.IsNullOrEmpty(existingId)
                && !channelItems.Items.Any(c => string.Equals(c.Id, existingId, StringComparison.OrdinalIgnoreCase)))
            {
                var guid = existingId.GetMD5();
                var item = _libraryManager.GetItemById(guid);
                if (item is not null)
                {
                    _libraryManager.DeleteItem(item, deleteOptions);
                    deletedCount++;
                }
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Removed {Count} outdated trailers from cache", deletedCount);
        }

        // Refresh existing cache list
        existingCache = Directory.GetFiles(CachePath);
        existingIds = existingCache.Select(c => Path.GetFileNameWithoutExtension(c)).ToArray();

        // Determine how many new downloads we need
        var introCount = Configuration.IntroCount;
        var targetCacheSize = Math.Max(introCount * 3, 15); // At least 3x intros or 15 trailers
        var maxNewDownloads = Math.Max(5, introCount); // Download at least 5, or IntroCount per run
        var currentCacheCount = existingIds.Length;

        _logger.LogInformation(
            "Intro cache status: {Current} cached, target {Target}, will download up to {Max} new",
            currentCacheCount,
            targetCacheSize,
            maxNewDownloads);

        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        var youTubeClient = new YoutubeClient(httpClient);
        _cacheIds.Clear();

        var newDownloads = 0;
        var skippedExisting = 0;
        var failedDownloads = 0;

        foreach (var item in channelItems.Items)
        {
            // Check if already cached
            if (existingIds.Any(i => string.Equals(i, item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _cacheIds.Add(item.Id);
                skippedExisting++;
                continue;
            }

            // Check if we've reached target or max downloads
            if (_cacheIds.Count >= targetCacheSize || newDownloads >= maxNewDownloads)
            {
                continue; // Skip further downloads this run
            }

            var destinationPath = Path.Combine(CachePath, $"{item.Id}.mp4");
            var mediaSource = await GetMediaSource(item.Id).ConfigureAwait(false);
            if (mediaSource is null)
            {
                failedDownloads++;
                continue;
            }

            try
            {
                _logger.LogInformation(
                    "Downloading trailer {Index}/{Max}: {Name}",
                    newDownloads + 1,
                    maxNewDownloads,
                    item.Name);

                await youTubeClient.Videos.DownloadAsync(
                    mediaSource.TranscodingUrl,
                    destinationPath,
                    cfg => cfg.SetFFmpegPath(_mediaEncoder.EncoderPath),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                _cacheIds.Add(item.Id);
                _libraryManager.CreateItem(
                    new Trailer
                    {
                        Id = item.Id.GetMD5(),
                        Name = item.Name,
                        Path = destinationPath
                    },
                    null);
                newDownloads++;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to cache {Name}", item.Name);
                failedDownloads++;
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Intro cache update complete in {Elapsed}s: {Total} total cached, {New} new downloads, {Skipped} already cached, {Failed} failed",
            sw.Elapsed.TotalSeconds,
            _cacheIds.Count,
            newDownloads,
            skippedExisting,
            failedDownloads);
    }

    /// <summary>
    /// Get random intros (legacy method for backward compatibility).
    /// </summary>
    /// <returns>The list of intros.</returns>
    public IEnumerable<IntroInfo> GetIntros()
    {
        return GetIntros(null, null);
    }

    /// <summary>
    /// Get intros for a specific item and user.
    /// Uses cinema mode selection when enabled, falls back to random selection otherwise.
    /// </summary>
    /// <param name="item">The item being played.</param>
    /// <param name="user">The user watching.</param>
    /// <returns>The list of intros.</returns>
    public IEnumerable<IntroInfo> GetIntros(BaseItem item, User user)
    {
        _logger.LogInformation(
            "GetIntros called: Item={ItemName}, ItemType={ItemType}, User={User}, CinemaMode={CinemaMode}, IntroCount={IntroCount}, CachedTrailers={CachedCount}",
            item?.Name ?? "null",
            item?.GetType().Name ?? "null",
            user?.Username ?? "null",
            Configuration.EnableCinemaMode,
            Configuration.IntroCount,
            _cacheIds.Count);

        var introCount = TmdbTrailerPlugin.Instance.Configuration.IntroCount;
        if (introCount <= 0 || _cacheIds.Count == 0)
        {
            _logger.LogWarning("No intros returned: IntroCount={IntroCount}, CachedCount={CachedCount}", introCount, _cacheIds.Count);
            return Enumerable.Empty<IntroInfo>();
        }

        // Use cinema mode if enabled and we have item context
        if (Configuration.EnableCinemaMode && item is Movie movie)
        {
            // Check if cinema mode is restricted to a specific library
            if (!string.IsNullOrEmpty(Configuration.CinemaModeLibrary))
            {
                var movieLibraryId = GetLibraryId(movie);
                if (movieLibraryId != Configuration.CinemaModeLibrary)
                {
                    _logger.LogInformation(
                        "Cinema Mode skipped for {MovieName}: not in target library (movie library: {MovieLib}, target: {TargetLib})",
                        movie.Name,
                        movieLibraryId ?? "unknown",
                        Configuration.CinemaModeLibrary);
                    goto RandomSelection;
                }
            }

            _logger.LogInformation("Using Cinema Mode for movie: {MovieName}", movie.Name);
            EnsureIntroManager();
            var result = _introManager.GetIntroSequence(item, user, _cacheIds, introCount);
            _logger.LogInformation("Cinema Mode returned {Count} intros", result.Count());
            return result;
        }

        if (Configuration.EnableCinemaMode && item is not Movie)
        {
            _logger.LogInformation("Cinema Mode enabled but item is not a Movie: {ItemType}", item?.GetType().Name);
        }

        RandomSelection:

        // Fall back to random selection
        _logger.LogInformation("Using random trailer selection");
        var tmp = new List<string>(_cacheIds);
        tmp.Shuffle();
        var intros = new List<IntroInfo>(introCount);
        for (var i = 0; i < introCount && i < tmp.Count; i++)
        {
            intros.Add(new IntroInfo { ItemId = tmp[i].GetMD5() });
        }

        _logger.LogInformation("Returning {Count} random intros", intros.Count);
        return intros;
    }

    private void EnsureIntroManager()
    {
        _introManager ??= new IntroManager(
            _libraryManager,
            _memoryCache,
            _loggerFactory.CreateLogger<IntroManager>(),
            _loggerFactory.CreateLogger<PreRollSelector>(),
            _loggerFactory.CreateLogger<TrailerSelector>());
    }

    private string GetLibraryId(BaseItem item)
    {
        // Walk up the parent chain to find the library root
        var current = item;
        while (current != null)
        {
            var parent = current.GetParent();
            if (parent == null || parent is AggregateFolder)
            {
                // current is the library root
                return current.Id.ToString("N");
            }

            current = parent;
        }

        return null;
    }

    /// <summary>
    /// Stream information for playback.
    /// </summary>
    private sealed class StreamInfo
    {
        /// <summary>
        /// Gets or sets the primary URL (muxed or video-only).
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Gets or sets the video-only URL when not muxed.
        /// </summary>
        public string VideoUrl { get; set; }

        /// <summary>
        /// Gets or sets the audio-only URL when not muxed.
        /// </summary>
        public string AudioUrl { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is a muxed stream.
        /// </summary>
        public bool IsMuxed { get; set; }

        /// <summary>
        /// Gets or sets the combined bitrate.
        /// </summary>
        public Bitrate Bitrate { get; set; }

        /// <summary>
        /// Gets or sets the container format.
        /// </summary>
        public Container Container { get; set; }
    }
}
