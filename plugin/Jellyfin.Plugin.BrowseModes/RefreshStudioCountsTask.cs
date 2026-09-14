using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Caches per-studio item counts so the Studios browse mode loads instantly.
/// </summary>
/// <remarks>
/// These counts answer "which studio has the most titles" without the client making
/// N parallel API calls. Refreshed daily; can also be triggered manually from
/// Dashboard → Scheduled Tasks.
/// </remarks>
public class RefreshStudioCountsTask : IScheduledTask
{
    private readonly TmdbDiscoverClient _discoverClient;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RefreshStudioCountsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshStudioCountsTask"/> class.
    /// </summary>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>.</param>
    /// <param name="libraryManager">Instance of <see cref="ILibraryManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RefreshStudioCountsTask}"/> interface.</param>
    public RefreshStudioCountsTask(
        TmdbDiscoverClient discoverClient,
        ILibraryManager libraryManager,
        ILogger<RefreshStudioCountsTask> logger)
    {
        _discoverClient = discoverClient;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh studio item counts";

    /// <inheritdoc />
    public string Description => "Caches how many titles each studio has so the Studios browse mode loads instantly.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "RefreshStudioItemCounts";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // On startup, because the cache is in memory and does not survive a restart.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // Once a day, since studio counts change slowly.
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
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true
            };

            // Use GetItemList instead of GetStudios: on 10.11, studios are not materialized
            // as entities, so GetStudios returns nothing. Extract studio names from item
            // metadata instead — BaseItem.Studios is string[] on both 10.11 and 12.x.
            var items = _libraryManager.GetItemList(query);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var studios = item.Studios;
                if (studios is null || studios.Length == 0)
                {
                    continue;
                }

                foreach (var studio in studios)
                {
                    if (string.IsNullOrWhiteSpace(studio))
                    {
                        continue;
                    }

                    counts.TryGetValue(studio, out var current);
                    counts[studio] = current + 1;
                }
            }

            _discoverClient.SetStudioCounts(counts);
            _logger.LogInformation("Refreshed studio counts: {Count} studios", counts.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to refresh studio counts");
        }

        progress.Report(100);
    }
}
