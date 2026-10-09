using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BrowseModes.Configuration;

/// <summary>
/// Configuration for the Browse Modes plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TMDb API key used to build the discover lists.
    /// </summary>
    /// <remarks>
    /// The server bundles a key for its own TMDb metadata provider, but that lives in an assembly
    /// this plugin deliberately does not reference, so a key has to be supplied here. Keys are
    /// free from themoviedb.org.
    /// </remarks>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many pages of each TMDb list to scan.
    /// </summary>
    /// <remarks>
    /// TMDb returns 20 results per page and most will not be in any given library, so a wide net
    /// is cast to land a usable number of hits. Raising this finds more matches at the cost of
    /// more requests against the API key.
    /// </remarks>
    public int DiscoverPagesToScan { get; set; } = 15;

    /// <summary>
    /// Gets or sets how long a built list is cached, in hours.
    /// </summary>
    public int CacheDurationHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets the lowest TMDb position to show in the Trending list. 0 means no cutoff.
    /// </summary>
    /// <remarks>
    /// Position is 1-based: a value of 50 keeps only titles ranked 1 through 50 on TMDb's
    /// trending list, and drops anything ranked lower regardless of whether it is in the library.
    /// </remarks>
    public int TrendingMaxRank { get; set; }

    /// <summary>
    /// Gets or sets the lowest TMDb position to show in the Top Rated list. 0 means no cutoff.
    /// </summary>
    /// <remarks>
    /// Position is 1-based, mirroring <see cref="TrendingMaxRank"/> for the all-time list.
    /// </remarks>
    public int TopRatedMaxRank { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ranked discover lists include titles that are
    /// not in the library, returned as external stubs clients render as "coming soon" tiles.
    /// </summary>
    /// <remarks>
    /// This is the server-side default; clients may still hide missing titles per user without
    /// changing this value.
    /// </remarks>
    public bool ShowMissing { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of missing titles to include per ranked view.
    /// </summary>
    /// <remarks>
    /// Global and applies to every ranked source, so a single view never floods with stubs. 0
    /// disables missing titles entirely.
    /// </remarks>
    public int MaxMissing { get; set; } = 10;

    /// <summary>
    /// Gets or sets the snapshot ranked lists the plugin serves, alongside TMDb.
    /// </summary>
    /// <remarks>
    /// TMDb is not listed here: it is always available when <see cref="TmdbApiKey"/> is set and is
    /// fetched live. Each entry is one list from one source (a source like IMDb contributes two
    /// lists — a trending list and a top-rated list). Fetched and refreshed at runtime by a
    /// scheduled task.
    /// </remarks>
    public List<SourceListConfig> Sources { get; set; } = new List<SourceListConfig>
    {
        new SourceListConfig { Key = "imdb", Kind = "trending" },
        new SourceListConfig { Key = "imdb", Kind = "toprated" },
        new SourceListConfig { Key = "netflix", Kind = "trending" },
        new SourceListConfig { Key = "netflix-au", Kind = "trending" },
        new SourceListConfig { Key = "netflix-ph", Kind = "trending" },
        new SourceListConfig { Key = "letterboxd", Kind = "toprated" },
        new SourceListConfig { Key = "rottentomatoes", Kind = "toprated" }
    };

    /// <summary>
    /// Gets or sets the browse-mode tile keys in display order. Keys not listed are hidden.
    /// </summary>
    /// <remarks>
    /// The keys are the client's <c>BrowseMode</c> enum values. An empty list means the client
    /// should use its built-in order and visibility. The plugin stores the list but does not
    /// interpret it; the web client reads it from <c>/Discover/TileLayout</c>.
    /// </remarks>
    public List<string> BrowseModeOrder { get; set; } = new List<string>();
}

/// <summary>
/// One snapshot ranked list: how to fetch it and whether scraping is an allowed fallback.
/// </summary>
public sealed class SourceListConfig
{
    /// <summary>
    /// Gets or sets the normalized source key, e.g. "imdb".
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list kind, "trending" or "toprated".
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this list is served.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the snapshot URL. Empty means the built-in default for the key and kind.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether scraping the source's site is an allowed fallback
    /// when the snapshot URL fails. Off by default; scraping is the last resort.
    /// </summary>
    public bool AllowScrape { get; set; }
}
