using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Keeps the snapshot source lists fresh.
/// </summary>
/// <remarks>
/// These lists change slowly and are identical for every user, so they are fetched ahead of time
/// and stored in SQLite; opening a source then reads the stored list instead of downloading it.
/// </remarks>
public class RefreshSourceListsTask : IScheduledTask
{
    private readonly SnapshotListFetcher _fetcher;
    private readonly ILogger<RefreshSourceListsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshSourceListsTask"/> class.
    /// </summary>
    /// <param name="fetcher">Instance of <see cref="SnapshotListFetcher"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RefreshSourceListsTask}"/> interface.</param>
    public RefreshSourceListsTask(SnapshotListFetcher fetcher, ILogger<RefreshSourceListsTask> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh snapshot source lists";

    /// <inheritdoc />
    public string Description => "Downloads the IMDb, Netflix, Letterboxd and Rotten Tomatoes ranked lists and stores them locally.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "RefreshSnapshotSourceLists";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // On startup, so a freshly installed plugin has lists ready to serve.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // At midnight, after the source datasets have refreshed for the day.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.Zero.Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        try
        {
            await _fetcher.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A refresh failure only costs stale lists, so it must not surface as a failed task.
            _logger.LogWarning(ex, "Unable to refresh the snapshot source lists");
        }

        progress.Report(100);
    }
}
