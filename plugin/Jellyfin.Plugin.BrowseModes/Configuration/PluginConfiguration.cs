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
    /// Gets or sets the browse-mode tile keys in display order. Keys not listed are hidden.
    /// </summary>
    /// <remarks>
    /// The keys are the client's <c>BrowseMode</c> enum values. An empty list means the client
    /// should use its built-in order and visibility. The plugin stores the list but does not
    /// interpret it; the web client reads it from <c>/Discover/TileLayout</c>.
    /// </remarks>
    public List<string> BrowseModeOrder { get; set; } = new List<string>();
}
