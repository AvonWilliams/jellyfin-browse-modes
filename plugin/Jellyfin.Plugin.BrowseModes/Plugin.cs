using System;
using System.Collections.Generic;
using Jellyfin.Plugin.BrowseModes.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Serves the curated TMDb lists that back the Trending and Top Rated browse modes.
/// </summary>
/// <remarks>
/// Only the data half of the feature lives here. The tile grid itself is part of the client, and
/// cannot be delivered by a plugin: Jellyfin serves plugin pages solely inside the admin
/// dashboard, and the web client builds its route table at compile time.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => new Guid("0d5f6a1e-3b7c-4c62-9a4d-2e8f1b6c9a37");

    /// <inheritdoc />
    public override string Name => "Browse Modes";

    /// <inheritdoc />
    public override string Description =>
        "Adds trending and top rated discover lists, narrowed to the items your library already has.";

    /// <inheritdoc />
    public override string ConfigurationFileName => "Jellyfin.Plugin.BrowseModes.xml";

    /// <summary>
    /// Returns the plugin configuration page.
    /// </summary>
    /// <returns>The configuration page.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
        };
    }
}
