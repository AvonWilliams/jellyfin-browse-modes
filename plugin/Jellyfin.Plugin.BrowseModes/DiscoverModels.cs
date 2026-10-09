using System;
using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// The ranked discover response returned by the Trending and Top Rated endpoints.
/// </summary>
/// <remarks>
/// <see cref="Items"/> holds the titles that are already in the library, ordered by rank with
/// <see cref="BaseItemDto.IndexNumber"/> carrying the 1-based source position. <see cref="Missing"/>
/// holds external titles not in the library, returned only when show-missing is enabled and under
/// the global cap; they are never library items and never written to the database.
/// </remarks>
public sealed class DiscoverRankedResult
{
    /// <summary>
    /// Gets or sets the resolved data source (e.g. "tmdb").
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the in-library items, ranked.
    /// </summary>
    public List<BaseItemDto> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets the external titles not in the library.
    /// </summary>
    public List<MissingTitleDto> Missing { get; set; } = new();
}

/// <summary>
/// An external title surfaced inside a ranked discover response that is not in the library.
/// </summary>
public sealed class MissingTitleDto
{
    /// <summary>
    /// Gets or sets the data source this title came from.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the 1-based rank within the source's list, matching the badge clients show.
    /// </summary>
    public int Rank { get; set; }

    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year, when the source provides one.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets provider ids for later re-matching, keyed the same way library items are.
    /// </summary>
    public Dictionary<string, string> ProviderIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a single lightweight poster URL, empty when the source has no poster.
    /// </summary>
    public string PosterUrl { get; set; } = string.Empty;
}

/// <summary>
/// A ranked title fetched from a discover source, before it is resolved against the library.
/// </summary>
public sealed class TmdbRankedTitle
{
    /// <summary>
    /// Gets the source's numeric id (the TMDb id for the current source).
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Gets the display title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the release year, when the source provides one.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Gets the source-relative poster path, when the source provides one.
    /// </summary>
    public string? PosterPath { get; init; }
}

/// <summary>
/// One stored source list, surfaced to the admin source-lists page.
/// </summary>
public sealed class SourceListSummaryDto
{
    /// <summary>
    /// Gets or sets the normalized source key, e.g. "imdb".
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list kind, "trending" or "toprated".
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name, e.g. "IMDb Top 250".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this list was last fetched and stored.
    /// </summary>
    public DateTime LastRefreshedUtc { get; set; }

    /// <summary>
    /// Gets or sets the ranked titles in this list.
    /// </summary>
    public List<SourceListTitleDto> Items { get; set; } = new();
}

/// <summary>
/// One ranked title inside a <see cref="SourceListSummaryDto"/>.
/// </summary>
public sealed class SourceListTitleDto
{
    /// <summary>
    /// Gets or sets the 1-based rank within the list.
    /// </summary>
    public int Rank { get; set; }

    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year, when the source provides one.
    /// </summary>
    public int? Year { get; set; }
}
