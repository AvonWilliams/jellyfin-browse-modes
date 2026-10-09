using Jellyfin.Plugin.BrowseModes.Data;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Registers the plugin's services with the server's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singleton so the list cache is shared between the controller and the scheduled task
        // that warms it.
        serviceCollection.AddSingleton<TmdbDiscoverClient>();

        // Singleton store for the snapshot source lists; the controller reads it and the scheduled
        // task writes it, both against the same SQLite file.
        serviceCollection.AddSingleton<SourceListStore>();
    }
}
