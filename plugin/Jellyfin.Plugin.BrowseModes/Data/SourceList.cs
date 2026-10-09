using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.BrowseModes.Data;

/// <summary>
/// Which ranked list a source contributes: a trending-style list or an all-time top-rated list.
/// </summary>
public enum SourceListKind
{
    /// <summary>
    /// A trending list (currently popular).
    /// </summary>
    Trending,

    /// <summary>
    /// An all-time top-rated list.
    /// </summary>
    TopRated
}

/// <summary>
/// A persisted ranked list from one source, refreshed at runtime by the snapshot task.
/// </summary>
public sealed class SourceList
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the normalized source key, e.g. "imdb".
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is a trending list or a top-rated list.
    /// </summary>
    public SourceListKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the display name, e.g. "IMDb Top 250".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this list was last refreshed.
    /// </summary>
    public DateTime LastRefreshedUtc { get; set; }

    /// <summary>
    /// Gets or sets the ranked titles in this list.
    /// </summary>
    public List<SourceListItem> Items { get; set; } = new();
}

/// <summary>
/// One ranked title inside a <see cref="SourceList"/>.
/// </summary>
public sealed class SourceListItem
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the owning <see cref="SourceList"/> id.
    /// </summary>
    public int SourceListId { get; set; }

    /// <summary>
    /// Gets or sets the owning list.
    /// </summary>
    public SourceList SourceList { get; set; } = null!;

    /// <summary>
    /// Gets or sets the 1-based rank within the source's list.
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
    /// Gets or sets a poster URL, when the source provides one.
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this title is a series (TV) rather than a movie.
    /// </summary>
    public bool IsSeries { get; set; }
}
