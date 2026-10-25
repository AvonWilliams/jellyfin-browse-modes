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

        // Singleton fetcher that downloads and parses the snapshot lists.
        serviceCollection.AddSingleton<SnapshotListFetcher>();

        // Singleton poster cache; the fetcher writes to it and the controller serves from it.
        serviceCollection.AddSingleton<PosterStore>();

        // The people index: stored per library in the same SQLite file, built by the counts task
        // and rebuilt in the background after library changes.
        serviceCollection.AddSingleton<PersonIndexStore>();
        serviceCollection.AddSingleton<PersonIndexBuilder>();
        serviceCollection.AddSingleton<LivePeopleCache>();
        serviceCollection.AddHostedService<PersonIndexService>();
    }
}
