using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.BrowseModes.Data;

/// <summary>
/// Reads and writes the snapshot source lists in the plugin-owned SQLite database.
/// </summary>
public sealed class SourceListStore
{
    private readonly DbContextOptions<SourceListDbContext> _options;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceListStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public SourceListStore(IApplicationPaths applicationPaths)
    {
        var dbPath = Path.Combine(applicationPaths.DataPath, "browse-modes.db");
        _options = new DbContextOptionsBuilder<SourceListDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    /// <summary>
    /// Replaces the stored list for a source and kind, or inserts it on the first refresh.
    /// </summary>
    /// <param name="source">The normalized source key.</param>
    /// <param name="kind">The list kind.</param>
    /// <param name="title">The display name.</param>
    /// <param name="items">The ranked titles, with <see cref="SourceListItem.Rank"/> already set.</param>
    public void ReplaceList(string source, SourceListKind kind, string title, IReadOnlyList<SourceListItem> items)
    {
        using var db = CreateContext();
        var existing = db.SourceLists
            .Include(l => l.Items)
            .FirstOrDefault(l => l.Source == source && l.Kind == kind);

        if (existing is null)
        {
            existing = new SourceList
            {
                Source = source,
                Kind = kind,
                Title = title,
                LastRefreshedUtc = DateTime.UtcNow,
                Items = new List<SourceListItem>(items)
            };
            db.SourceLists.Add(existing);
        }
        else
        {
            existing.Title = title;
            existing.LastRefreshedUtc = DateTime.UtcNow;
            existing.Items.Clear();
            existing.Items.AddRange(items);
        }

        db.SaveChanges();
    }

    /// <summary>
    /// Returns the stored titles for a source and kind, ordered by rank, or an empty list.
    /// </summary>
    /// <param name="source">The normalized source key.</param>
    /// <param name="kind">The list kind.</param>
    /// <returns>The ranked titles.</returns>
    public IReadOnlyList<SourceListItem> GetList(string source, SourceListKind kind)
    {
        using var db = CreateContext();
        return db.SourceListItems
            .Where(i => i.SourceList.Source == source && i.SourceList.Kind == kind)
            .OrderBy(i => i.Rank)
            .AsNoTracking()
            .ToList();
    }

    private SourceListDbContext CreateContext()
    {
        var context = new SourceListDbContext(_options);
        if (!_initialized)
        {
            context.Database.EnsureCreated();
            _initialized = true;
        }

        return context;
    }
}
