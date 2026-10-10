using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BrowseModes.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TMDbLib.Objects.Trending;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Keeps the TMDb discover lists warm.
/// </summary>
/// <remarks>
/// These lists are identical for every user and change slowly, but building one costs a sequence
/// of TMDb requests. Refreshing them in the background means opening Trending or Top Rated is
/// served from cache rather than making the user wait.
/// </remarks>
public class RefreshDiscoverListsTask : IScheduledTask
{
    private readonly TmdbDiscoverClient _discoverClient;
    private readonly SourceListHistoryStore _historyStore;
    private readonly ILogger<RefreshDiscoverListsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshDiscoverListsTask"/> class.
    /// </summary>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>.</param>
    /// <param name="historyStore">Instance of <see cref="SourceListHistoryStore"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RefreshDiscoverListsTask}"/> interface.</param>
    public RefreshDiscoverListsTask(
        TmdbDiscoverClient discoverClient,
        SourceListHistoryStore historyStore,
        ILogger<RefreshDiscoverListsTask> logger)
    {
        _discoverClient = discoverClient;
        _historyStore = historyStore;
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

        // Ahead of the cache expiry, so a live entry is always replaced rather than being allowed
        // to lapse and leave a user waiting.
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

        if (!TmdbDiscoverClient.HasApiKey)
        {
            _logger.LogInformation("No TMDb API key is configured, skipping the discover list refresh");
            progress.Report(100);
            return;
        }

        try
        {
            await _discoverClient.WarmDiscoverListsAsync(cancellationToken, force: true).ConfigureAwait(false);
            await AppendHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A warm-up failure only costs a slow first request later, so it must not surface as
            // a failed task.
            _logger.LogWarning(ex, "Unable to refresh the TMDb discover lists");
        }

        progress.Report(100);
    }

    /// <summary>
    /// Records the warmed lists as today's snapshot. The served trending lists use the week
    /// window, but month aggregation needs day-window positions, so the day lists are fetched
    /// fresh; the top rated lists are reused straight from the warm-up's cache.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task AppendHistoryAsync(CancellationToken cancellationToken)
    {
        var pages = TmdbDiscoverClient.PagesToScan;
        try
        {
            var trendMovies = await _discoverClient
                .GetTrendingMovieTitlesAsync(TimeWindow.Day, pages, cancellationToken, force: true)
                .ConfigureAwait(false);
            var trendSeries = await _discoverClient
                .GetTrendingSeriesTitlesAsync(TimeWindow.Day, pages, cancellationToken, force: true)
                .ConfigureAwait(false);
            var topMovies = await _discoverClient
                .GetTopRatedMovieTitlesAsync(pages, cancellationToken)
                .ConfigureAwait(false);
            var topSeries = await _discoverClient
                .GetTopRatedSeriesTitlesAsync(pages, cancellationToken)
                .ConfigureAwait(false);

            var snapshotUtc = DateTime.UtcNow;
            await _historyStore
                .AppendSnapshotAsync("tmdb", (int)SourceListKind.Trending, ToItems(trendMovies, isSeries: false), snapshotUtc, cancellationToken)
                .ConfigureAwait(false);
            await _historyStore
                .AppendSnapshotAsync("tmdb", (int)SourceListKind.Trending, ToItems(trendSeries, isSeries: true), snapshotUtc, cancellationToken)
                .ConfigureAwait(false);
            await _historyStore
                .AppendSnapshotAsync("tmdb", (int)SourceListKind.TopRated, ToItems(topMovies, isSeries: false), snapshotUtc, cancellationToken)
                .ConfigureAwait(false);
            await _historyStore
                .AppendSnapshotAsync("tmdb", (int)SourceListKind.TopRated, ToItems(topSeries, isSeries: true), snapshotUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing a history row only costs a future aggregation window, so it must not fail
            // the task.
            _logger.LogDebug(ex, "Unable to append TMDb discover history");
        }
    }

    private static IReadOnlyList<SourceListItem> ToItems(IReadOnlyList<TmdbRankedTitle> titles, bool isSeries)
    {
        var items = new List<SourceListItem>(titles.Count);
        for (var i = 0; i < titles.Count; i++)
        {
            items.Add(new SourceListItem
            {
                Rank = i + 1,
                Title = titles[i].Title ?? string.Empty,
                Year = titles[i].Year,
                IsSeries = isSeries
            });
        }

        return items;
    }
}
