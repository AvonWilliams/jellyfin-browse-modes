using System;

namespace Jellyfin.Plugin.BrowseModes.Data;

/// <summary>
/// One daily snapshot row: a title's position in one source's list on one day.
/// </summary>
/// <remarks>
/// Rows are appended by the scheduled refreshers and trimmed by the cleanup task, so a later
/// slice can aggregate month windows from them. A title appears at most once per source, kind
/// and day.
/// </remarks>
public sealed class SourceListHistory
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the normalized source key, e.g. "imdb" or "tmdb".
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list kind, matching <see cref="SourceList.Kind"/> semantics.
    /// </summary>
    public int Kind { get; set; }

    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year, when the source provides one.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this title is a series (TV) rather than a movie.
    /// </summary>
    public int IsSeries { get; set; }

    /// <summary>
    /// Gets or sets the 1-based rank within the source's list.
    /// </summary>
    public int Rank { get; set; }

    /// <summary>
    /// Gets or sets the snapshot day, truncated to midnight UTC by the store.
    /// </summary>
    public DateTime SnapshotUtc { get; set; }
}
