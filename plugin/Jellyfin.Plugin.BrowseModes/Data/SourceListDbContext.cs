using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.BrowseModes.Data;

/// <summary>
/// EF Core context for the plugin-owned SQLite file that holds the snapshot source lists.
/// </summary>
/// <remarks>
/// The file lives at <c>&lt;DataPath&gt;/browse-modes.db</c>, separate from Jellyfin's own
/// <c>jellyfin.db</c>, so the plugin's schema cannot collide with the server's migrations.
/// </remarks>
public sealed class SourceListDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceListDbContext"/> class.
    /// </summary>
    /// <param name="options">The <see cref="DbContextOptions{SourceListDbContext}"/> instance.</param>
    public SourceListDbContext(DbContextOptions<SourceListDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets the stored source lists.
    /// </summary>
    public DbSet<SourceList> SourceLists => Set<SourceList>();

    /// <summary>
    /// Gets the stored ranked titles.
    /// </summary>
    public DbSet<SourceListItem> SourceListItems => Set<SourceListItem>();

    /// <summary>
    /// Gets the daily snapshot history rows.
    /// </summary>
    public DbSet<SourceListHistory> SourceListHistory => Set<SourceListHistory>();

    /// <summary>
    /// Creates the schema on first use, and adds the history table to databases that predate it.
    /// </summary>
    /// <remarks>
    /// <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.EnsureCreated"/> builds the whole schema only when the database
    /// file does not exist, so the history table is also created explicitly for existing files.
    /// Both stores go through this path, serialized so the startup tasks cannot race the creation.
    /// </remarks>
    /// <param name="context">The context whose database to prepare.</param>
    public static void EnsureSchema(SourceListDbContext context)
    {
        lock (SchemaLock)
        {
            context.Database.EnsureCreated();
            context.Database.ExecuteSqlRaw(HistoryTableSql);
        }
    }

    private static readonly object SchemaLock = new();

    // Mirrors the EF model for the history entity, so a pre-existing database gets the same
    // table and unique index a fresh one receives from EnsureCreated.
    private const string HistoryTableSql = @"
CREATE TABLE IF NOT EXISTS ""SourceListHistory"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SourceListHistory"" PRIMARY KEY AUTOINCREMENT,
    ""Source"" TEXT NOT NULL,
    ""Kind"" INTEGER NOT NULL,
    ""Title"" TEXT NOT NULL,
    ""Rank"" INTEGER NOT NULL,
    ""SnapshotUtc"" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SourceListHistory_Source_Kind_Title_SnapshotUtc""
    ON ""SourceListHistory"" (""Source"", ""Kind"", ""Title"", ""SnapshotUtc"");";

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceList>()
            .HasIndex(l => new { l.Source, l.Kind })
            .IsUnique();

        modelBuilder.Entity<SourceListItem>()
            .HasIndex(i => new { i.SourceListId, i.Rank });

        modelBuilder.Entity<SourceListItem>()
            .HasOne(i => i.SourceList)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.SourceListId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SourceListHistory>()
            .HasIndex(h => new { h.Source, h.Kind, h.Title, h.SnapshotUtc })
            .IsUnique();
    }
}
