using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Deletes cached poster images that have not been served for a week.
/// </summary>
public class CleanupPostersTask : IScheduledTask
{
    private readonly PosterStore _posterStore;
    private readonly ILogger<CleanupPostersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupPostersTask"/> class.
    /// </summary>
    /// <param name="posterStore">Instance of <see cref="PosterStore"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{CleanupPostersTask}"/> interface.</param>
    public CleanupPostersTask(PosterStore posterStore, ILogger<CleanupPostersTask> logger)
    {
        _posterStore = posterStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Clean up cached source posters";

    /// <inheritdoc />
    public string Description => "Deletes cached poster images that have not been served for a week.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CleanupBrowseModePosters";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // On startup, so a fresh install starts clean.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // Daily; posters go stale slowly.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        var removed = _posterStore.Cleanup(TimeSpan.FromDays(7));
        _logger.LogInformation("Removed {Count} unused poster images", removed);

        progress.Report(100);
        return Task.CompletedTask;
    }
}
