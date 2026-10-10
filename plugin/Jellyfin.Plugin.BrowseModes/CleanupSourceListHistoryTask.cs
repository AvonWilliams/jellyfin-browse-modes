using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BrowseModes.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Deletes source-list history rows older than the retention window.
/// </summary>
public class CleanupSourceListHistoryTask : IScheduledTask
{
    /// <summary>
    /// How many days of daily snapshots to keep.
    /// </summary>
    private const int RetentionDays = 35;

    private readonly SourceListHistoryStore _historyStore;
    private readonly ILogger<CleanupSourceListHistoryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupSourceListHistoryTask"/> class.
    /// </summary>
    /// <param name="historyStore">Instance of <see cref="SourceListHistoryStore"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{CleanupSourceListHistoryTask}"/> interface.</param>
    public CleanupSourceListHistoryTask(SourceListHistoryStore historyStore, ILogger<CleanupSourceListHistoryTask> logger)
    {
        _historyStore = historyStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Clean up source list history";

    /// <inheritdoc />
    public string Description => "Deletes daily source-list snapshots older than 35 days.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CleanupSourceListHistory";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // On startup, so a fresh install starts clean.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // Daily; history rows age out slowly.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        try
        {
            var removed = await _historyStore.CleanupAsync(RetentionDays, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Removed {Count} expired source-list history rows", removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to clean up source list history");
        }

        progress.Report(100);
    }
}
