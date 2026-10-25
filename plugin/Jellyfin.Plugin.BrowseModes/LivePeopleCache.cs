using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.BrowseModes.Data;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// A short-lived, in-memory copy of one user's people of one library, used when the people index
/// cannot serve a request (not built yet, or the user sees only part of the library). Without it,
/// every page of an infinite scroll would collect the whole library again.
/// </summary>
public sealed class LivePeopleCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 8;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the people for a key, collecting them with <paramref name="collect"/> on a miss. Callers
    /// asking while a collection is under way share it.
    /// </summary>
    /// <param name="key">Identifies user, library and item types.</param>
    /// <param name="collect">Collects the rows.</param>
    /// <returns>The people.</returns>
    public async Task<LivePeople> GetOrCollectAsync(string key, Func<List<PersonIndexRow>> collect)
    {
        var now = DateTime.UtcNow;
        if (_entries.TryGetValue(key, out var existing) && existing.Expires > now)
        {
            try
            {
                return await existing.Task.ConfigureAwait(false);
            }
            catch
            {
                _entries.TryRemove(key, out _);
                throw;
            }
        }

        foreach (var stale in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
        {
            _entries.TryRemove(stale, out _);
        }

        while (_entries.Count >= MaxEntries && _entries.OrderBy(e => e.Value.Expires).Select(e => e.Key).FirstOrDefault() is { } oldest)
        {
            _entries.TryRemove(oldest, out _);
        }

        var entry = new CacheEntry(now + Lifetime, Task.Run(() => new LivePeople(collect())));
        _entries[key] = entry;
        try
        {
            return await entry.Task.ConfigureAwait(false);
        }
        catch
        {
            _entries.TryRemove(key, out _);
            throw;
        }
    }

    private sealed record CacheEntry(DateTime Expires, Task<LivePeople> Task);
}

/// <summary>
/// The people of one library for one viewer, held in memory and paged like the stored index.
/// </summary>
public sealed class LivePeople
{
    private readonly Dictionary<IndexedPersonKind, List<Row>> _byKind = new();
    private readonly Dictionary<(IndexedPersonKind, PersonIndexSort), Row[]> _sorted = new();
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePeople"/> class.
    /// </summary>
    /// <param name="rows">The collected rows.</param>
    public LivePeople(IEnumerable<PersonIndexRow> rows)
    {
        foreach (var kind in Enum.GetValues<IndexedPersonKind>())
        {
            _byKind[kind] = new List<Row>();
        }

        foreach (var (kind, entry) in rows)
        {
            _byKind[kind].Add(new Row(entry, PersonIndexStore.ToSortKey(entry.Name), entry.PersonId.ToString("N")));
        }
    }

    /// <summary>
    /// Gets one page, ordered and filtered the way <see cref="PersonIndexStore.QueryPage"/> does.
    /// </summary>
    /// <param name="kind">The credit kind.</param>
    /// <param name="sort">The ordering.</param>
    /// <param name="startIndex">How many rows to skip.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="nameStartsWith">An optional name prefix, or "#".</param>
    /// <returns>The total number of matching people and the page.</returns>
    public (int Total, List<PersonIndexEntry> Items) Page(
        IndexedPersonKind kind,
        PersonIndexSort sort,
        int startIndex,
        int limit,
        string? nameStartsWith)
    {
        IEnumerable<Row> rows = Sorted(kind, sort);
        if (!string.IsNullOrWhiteSpace(nameStartsWith))
        {
            var trimmed = nameStartsWith.Trim();
            if (trimmed == "#")
            {
                rows = rows.Where(r => PersonIndexStore.ToLetter(r.SortKey) == "#");
            }
            else
            {
                var prefix = PersonIndexStore.ToSortKey(trimmed);
                if (prefix.Length > 0)
                {
                    rows = rows.Where(r => r.SortKey.StartsWith(prefix, StringComparison.Ordinal));
                }
            }
        }

        var filtered = rows as Row[] ?? rows.ToArray();
        var page = filtered.Skip(startIndex).Take(limit).Select(r => r.Entry).ToList();
        return (filtered.Length, page);
    }

    /// <summary>
    /// Counts people per jump-bar letter.
    /// </summary>
    /// <param name="kind">The credit kind.</param>
    /// <returns>Letter to count.</returns>
    public Dictionary<string, int> Letters(IndexedPersonKind kind)
    {
        var letters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in _byKind[kind])
        {
            var letter = PersonIndexStore.ToLetter(row.SortKey);
            letters[letter] = letters.TryGetValue(letter, out var n) ? n + 1 : 1;
        }

        return letters;
    }

    private Row[] Sorted(IndexedPersonKind kind, PersonIndexSort sort)
    {
        lock (_lock)
        {
            if (_sorted.TryGetValue((kind, sort), out var cached))
            {
                return cached;
            }

            var rows = _byKind[kind].ToArray();
            Comparison<Row> byName = (a, b) =>
            {
                var c = string.CompareOrdinal(a.SortKey, b.SortKey);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            };

            Comparison<Row> comparison = sort switch
            {
                PersonIndexSort.NameDescending => (a, b) => byName(b, a),
                PersonIndexSort.MostTitles => (a, b) =>
                {
                    var c = b.Entry.TitleCount.CompareTo(a.Entry.TitleCount);
                    return c != 0 ? c : byName(a, b);
                },
                PersonIndexSort.FewestTitles => (a, b) =>
                {
                    var c = a.Entry.TitleCount.CompareTo(b.Entry.TitleCount);
                    return c != 0 ? c : byName(a, b);
                },
                _ => byName
            };

            Array.Sort(rows, comparison);
            _sorted[(kind, sort)] = rows;
            return rows;
        }
    }

    private sealed record Row(PersonIndexEntry Entry, string SortKey, string Id);
}
