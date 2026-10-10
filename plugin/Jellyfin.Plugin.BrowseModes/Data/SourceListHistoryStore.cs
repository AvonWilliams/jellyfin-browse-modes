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
    /// recorded for the same source, kind, title and day.
    /// </summary>
    /// <param name="source">The normalized source key.</param>
    /// <param name="kind">The list kind, matching <see cref="SourceListKind"/> values.</param>
    /// <param name="items">The ranked titles, with <see cref="SourceListItem.Rank"/> already set.</param>
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
                $@"INSERT OR IGNORE INTO ""SourceListHistory"" (""Source"", ""Kind"", ""Title"", ""Rank"", ""SnapshotUtc"")
VALUES ({source}, {kind}, {item.Title}, {item.Rank}, {snapshotDate})");
        }

        return Task.CompletedTask;
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
