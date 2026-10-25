using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.BrowseModes.Data;

/// <summary>
/// The credit kinds the people index covers. The numeric values are stored in the database, so
/// they must never be renumbered.
/// </summary>
public enum IndexedPersonKind
{
    /// <summary>Actors.</summary>
    Actor = 1,

    /// <summary>Directors.</summary>
    Director = 2,

    /// <summary>Writers.</summary>
    Writer = 3
}

/// <summary>
/// The orderings the people index can serve.
/// </summary>
public enum PersonIndexSort
{
    /// <summary>Name, A to Z.</summary>
    NameAscending,

    /// <summary>Name, Z to A.</summary>
    NameDescending,

    /// <summary>Most titles first, ties by name.</summary>
    MostTitles,

    /// <summary>Fewest titles first, ties by name.</summary>
    FewestTitles
}

/// <summary>
/// One person in one library, as stored in the people index.
/// </summary>
/// <param name="PersonId">The person's item id.</param>
/// <param name="Name">The display name.</param>
/// <param name="TitleCount">How many movies or series the person appears in.</param>
/// <param name="ImageTag">The cache tag of the person's primary image, or null when there is none.</param>
public readonly record struct PersonIndexEntry(Guid PersonId, string Name, int TitleCount, string? ImageTag);

/// <summary>
/// One person-in-library row ready to be written, before it is split into per-kind rows.
/// </summary>
/// <param name="Kind">The credit kind the row lists the person under.</param>
/// <param name="Entry">The person.</param>
public readonly record struct PersonIndexRow(IndexedPersonKind Kind, PersonIndexEntry Entry);

/// <summary>
/// Describes how current the index for one library is.
/// </summary>
/// <param name="BuiltUtc">When the index was last built.</param>
/// <param name="Fingerprint">The item-count fingerprint recorded at build time.</param>
/// <param name="Dirty">True when the library changed since the build.</param>
/// <param name="PersonCount">How many distinct people were indexed.</param>
public sealed record PersonIndexMeta(DateTime BuiltUtc, string Fingerprint, bool Dirty, int PersonCount);

/// <summary>
/// The people index: a per-library, per-kind list of people with their title counts, kept in the
/// plugin's own <c>browse-modes.db</c> so that a People category can be paged, sorted and filtered
/// by SQLite instead of by walking the whole library on every request.
/// </summary>
/// <remarks>
/// The tables share the file with the snapshot source lists. The source-list schema is created by
/// <c>EnsureCreated</c>, which does nothing once the file has any table, so it is always created
/// first and the people tables are added afterwards by <see cref="Migrate"/>, which is versioned
/// through the <c>BmSchema</c> table. The index only holds derived data: deleting it, or the whole
/// file, loses nothing and the endpoint falls back to the live path until the next build.
/// </remarks>
public sealed class PersonIndexStore
{
    private const string SchemaName = "PersonIndex";
    private const int CurrentSchemaVersion = 1;
    private static readonly object InitLock = new();

    private readonly string _connectionString;
    private readonly DbContextOptions<SourceListDbContext> _sourceListOptions;
    private bool _ready;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonIndexStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public PersonIndexStore(IApplicationPaths applicationPaths)
    {
        var dbPath = Path.Combine(applicationPaths.DataPath, "browse-modes.db");
        _connectionString = $"Data Source={dbPath}";
        _sourceListOptions = new DbContextOptionsBuilder<SourceListDbContext>()
            .UseSqlite(_connectionString)
            .Options;
    }

    /// <summary>
    /// Builds the key a person is sorted by: upper case with accents removed, so "Émile" sorts
    /// with the E names and the letter jump bar needs no collation support from SQLite.
    /// </summary>
    /// <param name="name">The person's name.</param>
    /// <returns>The sort key.</returns>
    public static string ToSortKey(string name)
    {
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Gets the jump-bar letter a sort key belongs under: A to Z, or "#" for everything else.
    /// </summary>
    /// <param name="sortKey">The sort key from <see cref="ToSortKey"/>.</param>
    /// <returns>The single-character letter.</returns>
    public static string ToLetter(string sortKey)
    {
        return sortKey.Length > 0 && sortKey[0] >= 'A' && sortKey[0] <= 'Z' ? sortKey[..1] : "#";
    }

    /// <summary>
    /// Replaces everything indexed for a library in one transaction, so a reader sees either the
    /// old index or the new one, never a half-built one.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="rows">The rows to store.</param>
    /// <param name="fingerprint">The item-count fingerprint of the library at build time.</param>
    /// <param name="personCount">The number of distinct people in the rows.</param>
    public void ReplaceLibrary(Guid libraryId, IEnumerable<PersonIndexRow> rows, string fingerprint, int personCount)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var library = Key(libraryId);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM PersonIndex WHERE LibraryId = $lib";
            delete.Parameters.AddWithValue("$lib", library);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR REPLACE INTO PersonIndex (LibraryId, Kind, PersonId, Name, SortName, TitleCount, ImageTag) " +
                "VALUES ($lib, $kind, $person, $name, $sort, $count, $tag)";
            var pKind = insert.Parameters.Add("$kind", SqliteType.Integer);
            var pPerson = insert.Parameters.Add("$person", SqliteType.Text);
            var pName = insert.Parameters.Add("$name", SqliteType.Text);
            var pSort = insert.Parameters.Add("$sort", SqliteType.Text);
            var pCount = insert.Parameters.Add("$count", SqliteType.Integer);
            var pTag = insert.Parameters.Add("$tag", SqliteType.Text);
            insert.Parameters.AddWithValue("$lib", library);
            insert.Prepare();

            foreach (var (kind, entry) in rows)
            {
                pKind.Value = (int)kind;
                pPerson.Value = Key(entry.PersonId);
                pName.Value = entry.Name;
                pSort.Value = ToSortKey(entry.Name);
                pCount.Value = entry.TitleCount;
                pTag.Value = (object?)entry.ImageTag ?? DBNull.Value;
                insert.ExecuteNonQuery();
            }
        }

        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText =
                "INSERT OR REPLACE INTO PersonIndexMeta (LibraryId, BuiltUtc, Fingerprint, Dirty, PersonCount) " +
                "VALUES ($lib, $built, $fp, 0, $people)";
            meta.Parameters.AddWithValue("$lib", library);
            meta.Parameters.AddWithValue("$built", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            meta.Parameters.AddWithValue("$fp", fingerprint);
            meta.Parameters.AddWithValue("$people", personCount);
            meta.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Removes the index of every library not in the given set (a deleted library).
    /// </summary>
    /// <param name="keepLibraries">The libraries that still exist.</param>
    public void RemoveLibrariesExcept(IReadOnlyCollection<Guid> keepLibraries)
    {
        using var connection = Open();
        var keep = new HashSet<string>();
        foreach (var id in keepLibraries)
        {
            keep.Add(Key(id));
        }

        var stale = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT LibraryId FROM PersonIndexMeta";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!keep.Contains(id))
                {
                    stale.Add(id);
                }
            }
        }

        foreach (var id in stale)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "PersonIndex", "PersonIndexMeta" })
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE LibraryId = $lib";
                delete.Parameters.AddWithValue("$lib", id);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Flags every library's index as out of date; it keeps being served until rebuilt.
    /// </summary>
    public void MarkAllDirty()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PersonIndexMeta SET Dirty = 1 WHERE Dirty = 0";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets how current a library's index is, or null when it has never been built.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>The metadata, or null.</returns>
    public PersonIndexMeta? GetMeta(Guid libraryId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT BuiltUtc, Fingerprint, Dirty, PersonCount FROM PersonIndexMeta WHERE LibraryId = $lib";
        command.Parameters.AddWithValue("$lib", Key(libraryId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PersonIndexMeta(
            DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(1),
            reader.GetInt32(2) != 0,
            reader.GetInt32(3));
    }

    /// <summary>
    /// Reads one page of a library's people of one kind.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The credit kind.</param>
    /// <param name="sort">The ordering.</param>
    /// <param name="startIndex">How many rows to skip.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="nameStartsWith">An optional name prefix, or "#" for names not starting with A to Z.</param>
    /// <returns>The total number of matching people and the requested page.</returns>
    public (int Total, List<PersonIndexEntry> Items) QueryPage(
        Guid libraryId,
        IndexedPersonKind kind,
        PersonIndexSort sort,
        int startIndex,
        int limit,
        string? nameStartsWith)
    {
        using var connection = Open();

        var where = "LibraryId = $lib AND Kind = $kind";
        var filter = BuildPrefixFilter(nameStartsWith);
        if (filter is not null)
        {
            where += " AND " + filter.Value.Sql;
        }

        void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$lib", Key(libraryId));
            command.Parameters.AddWithValue("$kind", (int)kind);
            if (filter is not null)
            {
                foreach (var (name, value) in filter.Value.Parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }
            }
        }

        int total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM PersonIndex WHERE {where}";
            Bind(count);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        // The order text is chosen from a fixed set, never from caller input.
        var order = sort switch
        {
            PersonIndexSort.NameDescending => "SortName DESC, PersonId DESC",
            PersonIndexSort.MostTitles => "TitleCount DESC, SortName ASC, PersonId ASC",
            PersonIndexSort.FewestTitles => "TitleCount ASC, SortName ASC, PersonId ASC",
            _ => "SortName ASC, PersonId ASC"
        };

        var items = new List<PersonIndexEntry>(Math.Min(limit, Math.Max(0, total - startIndex)));
        using (var select = connection.CreateCommand())
        {
            select.CommandText =
                $"SELECT PersonId, Name, TitleCount, ImageTag FROM PersonIndex WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset";
            Bind(select);
            select.Parameters.AddWithValue("$limit", limit);
            select.Parameters.AddWithValue("$offset", startIndex);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new PersonIndexEntry(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return (total, items);
    }

    /// <summary>
    /// Counts a library's people of one kind per jump-bar letter (A to Z, then "#").
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The credit kind.</param>
    /// <returns>Letter to count, only for letters that have people.</returns>
    public Dictionary<string, int> GetLetters(Guid libraryId, IndexedPersonKind kind)
    {
        var letters = new Dictionary<string, int>(StringComparer.Ordinal);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CASE WHEN c BETWEEN 'A' AND 'Z' THEN c ELSE '#' END AS letter, COUNT(*) " +
            "FROM (SELECT substr(SortName, 1, 1) AS c FROM PersonIndex WHERE LibraryId = $lib AND Kind = $kind) " +
            "GROUP BY letter";
        command.Parameters.AddWithValue("$lib", Key(libraryId));
        command.Parameters.AddWithValue("$kind", (int)kind);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            letters[reader.GetString(0)] = reader.GetInt32(1);
        }

        return letters;
    }

    /// <summary>
    /// Reads every person of one kind in a library, for pre-warming the name-keyed count caches.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The credit kind.</param>
    /// <returns>Name and title count per person.</returns>
    public List<(string Name, int TitleCount)> GetNamesAndCounts(Guid libraryId, IndexedPersonKind kind)
    {
        var result = new List<(string, int)>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, TitleCount FROM PersonIndex WHERE LibraryId = $lib AND Kind = $kind";
        command.Parameters.AddWithValue("$lib", Key(libraryId));
        command.Parameters.AddWithValue("$kind", (int)kind);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return result;
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns a typed prefix into a range over the sort key, so the (library, kind, sort key)
    /// index answers it without scanning.
    /// </summary>
    private static (string Sql, (string Name, string Value)[] Parameters)? BuildPrefixFilter(string? nameStartsWith)
    {
        if (string.IsNullOrWhiteSpace(nameStartsWith))
        {
            return null;
        }

        var trimmed = nameStartsWith.Trim();
        if (trimmed == "#")
        {
            // Everything that is not A to Z: digits and punctuation sort before "A", and
            // everything after "Z" (non-Latin scripts) sorts after it.
            return ("(SortName < 'A' OR SortName >= '[')", Array.Empty<(string, string)>());
        }

        var prefix = ToSortKey(trimmed);
        if (prefix.Length == 0)
        {
            return null;
        }

        return ("(SortName >= $lo AND SortName < $hi)", [("$lo", prefix), ("$hi", prefix + char.MaxValue)]);
    }

    private SqliteConnection Open()
    {
        EnsureReady();
        return OpenRaw();
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureReady()
    {
        if (_ready)
        {
            return;
        }

        lock (InitLock)
        {
            if (_ready)
            {
                return;
            }

            // The source-list schema must exist first: EnsureCreated is a no-op once the file
            // holds any table, so creating ours first would leave the source lists without theirs.
            using (var context = new SourceListDbContext(_sourceListOptions))
            {
                SourceListDbContext.EnsureSchema(context);
            }

            using var connection = OpenRaw();
            Migrate(connection);
            _ready = true;
        }
    }

    /// <summary>
    /// Brings the people tables up to <see cref="CurrentSchemaVersion"/>. Each step is
    /// idempotent and the version is written in the same transaction as the step, so an
    /// interrupted migration is simply repeated.
    /// </summary>
    private static void Migrate(SqliteConnection connection)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS BmSchema (Name TEXT NOT NULL PRIMARY KEY, Version INTEGER NOT NULL)";
            create.ExecuteNonQuery();
        }

        int version;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Version FROM BmSchema WHERE Name = $name";
            read.Parameters.AddWithValue("$name", SchemaName);
            version = read.ExecuteScalar() is long stored ? (int)stored : 0;
        }

        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        if (version < 1)
        {
            Execute(
                connection,
                transaction,
                "CREATE TABLE IF NOT EXISTS PersonIndex (" +
                "LibraryId TEXT NOT NULL, Kind INTEGER NOT NULL, PersonId TEXT NOT NULL, Name TEXT NOT NULL, " +
                "SortName TEXT NOT NULL, TitleCount INTEGER NOT NULL, ImageTag TEXT NULL, " +
                "PRIMARY KEY (LibraryId, Kind, PersonId)) WITHOUT ROWID",
                "CREATE INDEX IF NOT EXISTS IX_PersonIndex_Name ON PersonIndex (LibraryId, Kind, SortName, PersonId)",
                "CREATE INDEX IF NOT EXISTS IX_PersonIndex_CountDesc ON PersonIndex (LibraryId, Kind, TitleCount DESC, SortName, PersonId)",
                "CREATE INDEX IF NOT EXISTS IX_PersonIndex_CountAsc ON PersonIndex (LibraryId, Kind, TitleCount, SortName, PersonId)",
                "CREATE TABLE IF NOT EXISTS PersonIndexMeta (" +
                "LibraryId TEXT NOT NULL PRIMARY KEY, BuiltUtc TEXT NOT NULL, Fingerprint TEXT NOT NULL, " +
                "Dirty INTEGER NOT NULL, PersonCount INTEGER NOT NULL)");
        }

        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = "INSERT OR REPLACE INTO BmSchema (Name, Version) VALUES ($name, $version)";
            write.Parameters.AddWithValue("$name", SchemaName);
            write.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            write.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
