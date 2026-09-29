using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Pre-computes picker item counts so the browse-mode pickers load instantly.
/// </summary>
/// <remarks>
/// Backs the /Discover/Counts endpoint consumed by both the web and Android TV clients. Counts
/// are cached in memory for 24 hours; this task rebuilds them ahead of expiry so the first picker
/// open never has to wait.
/// </remarks>
public class RefreshBrowseModeCountsTask : IScheduledTask
{
    private static readonly string[] Types = ["genre", "rating", "tag", "decade", "studio"];

    private readonly TmdbDiscoverClient _discoverClient;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RefreshBrowseModeCountsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshBrowseModeCountsTask"/> class.
    /// </summary>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>.</param>
    /// <param name="libraryManager">Instance of <see cref="ILibraryManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RefreshBrowseModeCountsTask}"/> interface.</param>
    public RefreshBrowseModeCountsTask(
        TmdbDiscoverClient discoverClient,
        ILibraryManager libraryManager,
        ILogger<RefreshBrowseModeCountsTask> logger)
    {
        _discoverClient = discoverClient;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh browse mode counts";

    /// <inheritdoc />
    public string Description => "Pre-computes picker item counts (genre, rating, tag, decade, studio) for every library.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "RefreshBrowseModeCounts";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // On startup, because the cache is in memory and does not survive a restart.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // Daily, matching the 24-hour cache the counts are stored under.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        try
        {
            var folders = new List<VirtualFolderInfo>(_libraryManager.GetVirtualFolders());

            // Warm the per-library scopes the clients request, then the un-scoped (all libraries)
            // fallback. Counts are keyed by (type, scope, itemTypes), so both scopes are needed.
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Guid.TryParse(folder.ItemId, out var folderId) || folderId == Guid.Empty)
                {
                    continue;
                }

                var itemTypes = folder.CollectionType switch
                {
                    CollectionTypeOptions.movies => new[] { BaseItemKind.Movie },
                    CollectionTypeOptions.tvshows => new[] { BaseItemKind.Series },
                    _ => null
                };

                if (itemTypes is null)
                {
                    continue;
                }

                foreach (var type in Types)
                {
                    _discoverClient.GetOrBuildCounts(type, folderId, itemTypes, _libraryManager);
                }
            }

            foreach (var type in Types)
            {
                _discoverClient.GetOrBuildCounts(type, null, [BaseItemKind.Movie, BaseItemKind.Series], _libraryManager);
            }

            _logger.LogInformation("Refreshed browse mode counts across {Count} libraries", folders.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to refresh browse mode counts");
        }

        progress.Report(100);
    }
}
