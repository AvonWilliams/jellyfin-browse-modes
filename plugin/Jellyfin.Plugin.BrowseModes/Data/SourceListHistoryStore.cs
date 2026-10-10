using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.BrowseModes.Data;

/// <summary>
/// Reads and writes the daily snapshot history in the plugin-owned SQLite database.
/// </summary>
public sealed class SourceListHistoryStore
{
    private readonly DbContextOptions<SourceListDbContext> _options;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceListHistoryStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public SourceListHistoryStore(IApplicationPaths applicationPaths)
    {
        var dbPath = Path.Combine(applicationPaths.DataPath, "browse-modes.db");
        _options = new DbContextOptionsBuilder<SourceListDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    /// <summary>
    /// Appends one history row per ranked title, skipping blank titles and any row already
    /// recorded for the same source, kind, title and day. Each row carries the title's year and
    /// movie/series flag so later aggregations can hand them to the year-aware library matching
    /// and the month kind filter.
    /// </summary>
    /// <param name="source">The normalized source key.</param>
    /// <param name="kind">The list kind, matching <see cref="SourceListKind"/> values.</param>
    /// <param name="items">The ranked titles, with <see cref="SourceListItem.Rank"/>,
    /// <see cref="SourceListItem.Year"/> and <see cref="SourceListItem.IsSeries"/> already set.</param>
    /// <param name="snapshotUtc">The snapshot time; the UTC calendar day is what is stored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the inserts.</returns>
    public Task AppendSnapshotAsync(
        string source,
        int kind,
        IReadOnlyList<SourceListItem> items,
        DateTime snapshotUtc,
        CancellationToken cancellationToken)
    {
        using var db = CreateContext();

        // Truncated to the calendar day so repeated refreshes within one day share the same
        // unique key and the first rows of the day win.
        var snapshotDate = snapshotUtc.Date;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            db.Database.ExecuteSqlInterpolated(
                $@"INSERT OR IGNORE INTO ""SourceListHistory"" (""Source"", ""Kind"", ""Title"", ""Rank"", ""SnapshotUtc"", ""Year"", ""IsSeries"")
VALUES ({source}, {kind}, {item.Title}, {item.Rank}, {snapshotDate}, {item.Year}, {(item.IsSeries ? 1 : 0)})");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Aggregates the snapshot rows within a rolling window into one ranked list per title.
    /// </summary>
    /// <remarks>
    /// Locked rule: each row scores points from its position — rank 1 = 10, rank 2 = 9, down to
    /// rank 10 = 1, and rank 11+ = 1. Points are summed per title over the window. Titles are
    /// ordered by points descending, then by their most recent rank ascending (a better recent
    /// position wins), then by title for stability. Rows are grouped by normalized title
    /// (lowercase, alphanumeric only), and the display title is the most common original casing
    /// in the group; the year is the most recent non-null one seen.
    /// </remarks>
    /// <param name="source">The normalized source key.</param>
    /// <param name="kind">The list kind, matching <see cref="SourceListKind"/> values.</param>
    /// <param name="days">How many days of snapshots the rolling window covers.</param>
    /// <param name="isSeries">Whether to aggregate series (TV) rows; false aggregates movies.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregated titles, best first.</returns>
    public async Task<IReadOnlyList<(string Title, int? Year, int Points, int MostRecentRank)>> GetMonthRanksAsync(
        string source,
        int kind,
        int days,
        bool isSeries,
        CancellationToken cancellationToken)
    {
        using var db = CreateContext();
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var isSeriesValue = isSeries ? 1 : 0;
        var rows = await db.SourceListHistory
            .AsNoTracking()
            .Where(h => h.Source == source && h.Kind == kind && h.IsSeries == isSeriesValue && h.SnapshotUtc >= cutoff)
            .OrderByDescending(h => h.SnapshotUtc)
            .ThenByDescending(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Rows arrive newest first, so the first row of a group is its most recent appearance.
        var groups = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = NormalizeTitle(row.Title);
            if (key.Length == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var accumulator))
            {
                groups[key] = accumulator = new Accumulator();
            }

            accumulator.Points += PointsForRank(row.Rank);
            accumulator.TitleCounts.TryGetValue(row.Title, out var count);
            accumulator.TitleCounts[row.Title] = count + 1;

            if (!accumulator.MostRecentDay.HasValue)
            {
                accumulator.MostRecentDay = row.SnapshotUtc;
                accumulator.MostRecentRank = row.Rank;
            }
            else if (row.SnapshotUtc == accumulator.MostRecentDay && row.Rank < accumulator.MostRecentRank)
            {
                accumulator.MostRecentRank = row.Rank;
            }

            if (!accumulator.Year.HasValue && row.Year.HasValue)
            {
                accumulator.Year = row.Year;
            }
        }

        return groups.Values
            .OrderByDescending(a => a.Points)
            .ThenBy(a => a.MostRecentRank)
            .ThenBy(a => a.DisplayTitle(), StringComparer.Ordinal)
            .Select(a => (a.DisplayTitle(), a.Year, a.Points, a.MostRecentRank))
            .ToList();
    }

    /// <summary>
    /// Scores one row's rank by the locked rule: 10 points for rank 1 down to 1 point for rank
    /// 10, and 1 point for every lower rank.
    /// </summary>
    private static int PointsForRank(int rank)
    {
        if (rank < 1)
        {
            return 0;
        }

        return Math.Max(1, 11 - rank);
    }

    /// <summary>
    /// Normalizes a title for grouping: lowercase, alphanumeric only. Mirrors the controller's
    /// normalization so month rows group exactly as library matching will.
    /// </summary>
    private static string NormalizeTitle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private sealed class Accumulator
    {
        /// <summary>
        /// Points summed across the window.
        /// </summary>
        public int Points;

        /// <summary>
        /// The rank on the most recent snapshot day the title appeared on.
        /// </summary>
        public int MostRecentRank;

        /// <summary>
        /// The most recent snapshot day seen so far.
        /// </summary>
        public DateTime? MostRecentDay;

        /// <summary>
        /// The most recent non-null year seen so far.
        /// </summary>
        public int? Year;

        /// <summary>
        /// Original title casing counts, to pick the most common display casing.
        /// </summary>
        public readonly Dictionary<string, int> TitleCounts = new(StringComparer.Ordinal);

        /// <summary>
        /// The most common original casing, ties broken by ordinal title order for stability.
        /// </summary>
        public string DisplayTitle() => TitleCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First()
            .Key;
    }

    /// <summary>
    /// Deletes history rows older than the retention window.
    /// </summary>
    /// <param name="retentionDays">How many days of snapshots to keep.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted rows.</returns>
    public async Task<int> CleanupAsync(int retentionDays, CancellationToken cancellationToken)
    {
        using var db = CreateContext();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        return await db.SourceListHistory
            .Where(h => h.SnapshotUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private SourceListDbContext CreateContext()
    {
        var context = new SourceListDbContext(_options);
        if (!_initialized)
        {
            SourceListDbContext.EnsureSchema(context);
            _initialized = true;
        }

        return context;
    }
}
