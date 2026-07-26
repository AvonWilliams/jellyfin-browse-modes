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
}
