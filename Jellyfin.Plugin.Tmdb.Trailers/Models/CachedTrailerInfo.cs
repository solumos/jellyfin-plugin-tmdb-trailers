namespace Jellyfin.Plugin.Tmdb.Trailers.Models;

/// <summary>
/// Cached trailer metadata for intro selection.
/// </summary>
public class CachedTrailerInfo
{
    /// <summary>
    /// Gets or sets the YouTube video key.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the trailer name (movie title + "Trailer").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TMDb movie ID for genre/year matching.
    /// </summary>
    public int TmdbMovieId { get; set; }

    /// <summary>
    /// Gets or sets the genre IDs from TMDb.
    /// </summary>
    public int[] GenreIds { get; set; } = System.Array.Empty<int>();

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the poster URL.
    /// </summary>
    public string PosterUrl { get; set; } = string.Empty;
}
