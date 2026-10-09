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
    }
}
