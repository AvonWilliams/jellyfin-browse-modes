using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Plugins.Tmdb
{
    /// <summary>
    /// Keeps the TMDb discover lists warm.
    /// </summary>
    /// <remarks>
    /// These lists are identical for every user and change slowly, but building one costs a
    /// sequence of TMDb requests. Refreshing them in the background means opening Trending or
    /// Top Rated is served from cache rather than making the user wait.
    /// </remarks>
    public class RefreshDiscoverListsTask : IScheduledTask
    {
        private readonly TmdbClientManager _tmdbClientManager;
        private readonly ILogger<RefreshDiscoverListsTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RefreshDiscoverListsTask"/> class.
        /// </summary>
        /// <param name="tmdbClientManager">Instance of <see cref="TmdbClientManager"/>.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{RefreshDiscoverListsTask}"/> interface.</param>
        public RefreshDiscoverListsTask(
            TmdbClientManager tmdbClientManager,
            ILogger<RefreshDiscoverListsTask> logger)
        {
            _tmdbClientManager = tmdbClientManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Refresh TMDb discover lists";

        /// <inheritdoc />
        public string Description => "Caches the TMDb trending and top rated lists so library browse modes load instantly.";

        /// <inheritdoc />
        public string Category => "Library";

        /// <inheritdoc />
        public string Key => "RefreshTmdbDiscoverLists";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // On startup, because the cache is in memory and does not survive a restart.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            };

            // Ahead of the six hour cache expiry, so a live entry is always replaced rather than
            // being allowed to lapse and leave a user waiting.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(4).Ticks
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress.Report(0);

            try
            {
                await _tmdbClientManager.WarmDiscoverListsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A warm-up failure only costs a slow first request later, so it must not surface
                // as a failed task.
                _logger.LogWarning(ex, "Unable to refresh the TMDb discover lists");
            }

            progress.Report(100);
        }
    }
}
