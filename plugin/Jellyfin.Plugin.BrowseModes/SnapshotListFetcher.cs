using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BrowseModes.Configuration;
using Jellyfin.Plugin.BrowseModes.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Downloads and parses the snapshot ranked lists for the non-TMDb sources.
/// </summary>
/// <remarks>
/// Each source is fetched by a configured URL (or a built-in default) and parsed into a flat list
/// of titles, then handed to <see cref="SourceListStore"/> so the controller can serve it from
/// SQLite. A failed refresh keeps the last stored list, so a transient error never empties a view.
/// </remarks>
public sealed class SnapshotListFetcher
{
    private static readonly IReadOnlyDictionary<(string Key, string Kind), string> DefaultUrls =
        new Dictionary<(string, string), string>
        {
            [("imdb", "trending")] = "https://raw.githubusercontent.com/crazyuploader/IMDb-Top-50/main/data/popular/movies.json",
            [("imdb", "toprated")] = "https://raw.githubusercontent.com/crazyuploader/IMDb-Top-50/main/data/top250/movies.json",
            [("letterboxd", "toprated")] = "https://raw.githubusercontent.com/L-Dot/Letterboxd-list-scraper/master/example_output/json/lb_top250.json",
            [("rottentomatoes", "toprated")] = "https://raw.githubusercontent.com/minakdr/Rotten-Tomatoes-Certified-Fresh-Movies-Dataset-September-2024-Edition-/main/MoviesRatingRottenTomato.csv",
            [("netflix", "trending")] = "https://raw.githubusercontent.com/dbready/netflix_top10/main/all-weeks-global.tsv",
            [("netflix-au", "trending")] = "https://raw.githubusercontent.com/dbready/netflix_top10/main/all-weeks-countries.tsv",
            [("netflix-ph", "trending")] = "https://raw.githubusercontent.com/dbready/netflix_top10/main/all-weeks-countries.tsv"
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SourceListStore _store;
    private readonly SourceListHistoryStore _historyStore;
    private readonly TmdbDiscoverClient _discoverClient;
    private readonly ILogger<SnapshotListFetcher> _logger;

    // Caps the parallel TMDb year lookups so a refresh cannot hammer the TMDb rate limit.
    private static readonly SemaphoreSlim EnrichmentSemaphore = new(8);

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotListFetcher"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="store">Instance of <see cref="SourceListStore"/>.</param>
    /// <param name="historyStore">Instance of <see cref="SourceListHistoryStore"/>.</param>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>, used for TMDb year lookups.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{SnapshotListFetcher}"/> interface.</param>
    public SnapshotListFetcher(
        IHttpClientFactory httpClientFactory,
        SourceListStore store,
        SourceListHistoryStore historyStore,
        TmdbDiscoverClient discoverClient,
        ILogger<SnapshotListFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _historyStore = historyStore;
        _discoverClient = discoverClient;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes every enabled snapshot list.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when every list has been fetched and stored.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var sources = Plugin.Instance?.Configuration.Sources ?? new List<SourceListConfig>();
        foreach (var source in sources.Where(s => s.Enabled))
        {
            await RefreshOneAsync(source, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshOneAsync(SourceListConfig source, CancellationToken cancellationToken)
    {
        var url = ResolveUrl(source);
        if (string.IsNullOrEmpty(url))
        {
            _logger.LogInformation("No snapshot URL for source {Source} ({Kind}); skipping", source.Key, source.Kind);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var body = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var (items, imdbIds) = Parse(source.Key, body);
            await EnrichImdbYearsAsync(items, imdbIds, cancellationToken).ConfigureAwait(false);
            await EnrichYearsByTitleAsync(source.Key, items, cancellationToken).ConfigureAwait(false);
            ResolvePosterUrls(source.Key, items);
            var kind = source.Kind.Equals("trending", StringComparison.OrdinalIgnoreCase)
                ? SourceListKind.Trending
                : SourceListKind.TopRated;
            var title = source.Kind.Equals("trending", StringComparison.OrdinalIgnoreCase)
                ? $"{DisplayName(source.Key)} Trending"
                : $"{DisplayName(source.Key)} Top Rated";

            _store.ReplaceList(source.Key, kind, title, items);

            try
            {
                await _historyStore
                    .AppendSnapshotAsync(source.Key, (int)kind, items, DateTime.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Losing a history row only costs a future aggregation window, so it must not
                // fail the refresh.
                _logger.LogDebug(ex, "Unable to append history for {Source} ({Kind})", source.Key, source.Kind);
            }

            _logger.LogInformation("Refreshed {Source} ({Kind}): {Count} titles", source.Key, source.Kind, items.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to refresh snapshot list {Source} ({Kind})", source.Key, source.Kind);
        }
    }

    /// <summary>
    /// Fills in missing years on IMDb entries by resolving each row's tt id on TMDb. IMDb's
    /// snapshot JSON carries no year, and the year-aware library matching in the controller only
    /// applies when one is present. Lookup failures are swallowed so a title that cannot be
    /// enriched simply stays yearless.
    /// </summary>
    private async Task EnrichImdbYearsAsync(IReadOnlyList<SourceListItem> items, IReadOnlyList<(int Index, string TtId)> imdbIds, CancellationToken cancellationToken)
    {
        if (imdbIds.Count == 0)
        {
            return;
        }

        var lookups = new Task[imdbIds.Count];
        for (var i = 0; i < imdbIds.Count; i++)
        {
            lookups[i] = LookupImdbYearAsync(items, imdbIds[i], cancellationToken);
        }

        await Task.WhenAll(lookups).ConfigureAwait(false);
    }

    private async Task LookupImdbYearAsync(IReadOnlyList<SourceListItem> items, (int Index, string TtId) imdbId, CancellationToken cancellationToken)
    {
        await EnrichmentSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var match = await _discoverClient.FindByImdbIdAsync(imdbId.TtId, cancellationToken).ConfigureAwait(false);
            var item = items[imdbId.Index];
            if (match.HasValue && item.Year is null)
            {
                item.Year = match.Value.Year;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to resolve year for IMDb title {TtId}", imdbId.TtId);
        }
        finally
        {
            EnrichmentSemaphore.Release();
        }
    }

    /// <summary>
    /// Fills in missing years on Rotten Tomatoes and Netflix entries by searching each title on
    /// TMDb. Both sources carry no year, and the year-aware library matching in the controller
    /// only applies when one is present. Lookup failures are swallowed so a title that cannot be
    /// enriched simply stays yearless.
    /// </summary>
    private async Task EnrichYearsByTitleAsync(string sourceKey, IReadOnlyList<SourceListItem> items, CancellationToken cancellationToken)
    {
        if (!sourceKey.Equals("rottentomatoes", StringComparison.Ordinal)
            && !sourceKey.StartsWith("netflix", StringComparison.Ordinal))
        {
            return;
        }

        var lookups = new List<Task>();
        foreach (var item in items)
        {
            if (item.Year is not null || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            lookups.Add(LookupYearByTitleAsync(item, cancellationToken));
        }

        if (lookups.Count == 0)
        {
            return;
        }

        await Task.WhenAll(lookups).ConfigureAwait(false);
    }

    private async Task LookupYearByTitleAsync(SourceListItem item, CancellationToken cancellationToken)
    {
        await EnrichmentSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var year = await _discoverClient.SearchForYearAsync(item.Title, item.IsSeries, cancellationToken).ConfigureAwait(false);
            if (year.HasValue && item.Year is null)
            {
                item.Year = year.Value;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to resolve year for title {Title}", item.Title);
        }
        finally
        {
            EnrichmentSemaphore.Release();
        }
    }

    private static IReadOnlyList<SourceListItem> ResolvePosterUrls(string sourceKey, IReadOnlyList<SourceListItem> items)
    {
        // Netflix titles are often generic ("UNABOMBER", "The Widower"), so hint the search with
        // the word "netflix" to avoid mismatching an unrelated film of the same name.
        var hint = sourceKey.StartsWith("netflix", StringComparison.Ordinal) ? " netflix" : string.Empty;

        foreach (var item in items)
        {
            // Look every title up on TMDb so all posters share the w342 size, regardless of source.
            item.PosterUrl = PosterStore.EncodeProxyUrl(
                PosterStore.SearchPrefix + item.Title + hint + '\u001f' + (item.Year?.ToString() ?? string.Empty));
        }

        return items;
    }

    private static string? ResolveUrl(SourceListConfig source)
    {
        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            return source.Url.Trim();
        }

        return DefaultUrls.TryGetValue((source.Key, source.Kind), out var url) ? url : null;
    }

    private static string DisplayName(string key) => key switch
    {
        "imdb" => "IMDb",
        "netflix" => "Netflix Global",
        "netflix-au" => "Netflix Australia",
        "netflix-ph" => "Netflix Philippines",
        "letterboxd" => "Letterboxd",
        "rottentomatoes" => "Rotten Tomatoes",
        _ => key
    };

    private static (IReadOnlyList<SourceListItem> Items, IReadOnlyList<(int Index, string TtId)> ImdbIds) Parse(string key, string body)
    {
        if (key.Equals("imdb", StringComparison.Ordinal))
        {
            return ParseImdb(body);
        }

        if (key.Equals("letterboxd", StringComparison.Ordinal))
        {
            return (ParseLetterboxd(body), Array.Empty<(int Index, string TtId)>());
        }

        if (key.Equals("rottentomatoes", StringComparison.Ordinal))
        {
            return (ParseRottenTomatoes(body), Array.Empty<(int Index, string TtId)>());
        }

        if (key.StartsWith("netflix", StringComparison.Ordinal))
        {
            return (ParseNetflix(key, body), Array.Empty<(int Index, string TtId)>());
        }

        return (Array.Empty<SourceListItem>(), Array.Empty<(int Index, string TtId)>());
    }

    private static (IReadOnlyList<SourceListItem> Items, IReadOnlyList<(int Index, string TtId)> ImdbIds) ParseImdb(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = new List<SourceListItem>();
        var imdbIds = new List<(int Index, string TtId)>();
        var position = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            position++;

            var title = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var rank = position;
            if (element.TryGetProperty("Rank", out var rankElement) && rankElement.TryGetInt32(out var explicitRank))
            {
                rank = explicitRank;
            }

            var posterUrl = element.TryGetProperty("image", out var imageElement) ? imageElement.GetString() : null;
            var link = element.TryGetProperty("link", out var linkElement) ? linkElement.GetString() : null;
            // IMDb "link" is a full URL (https://www.imdb.com/title/tt31450459/); TMDb /find wants the bare tt id.
            var ttIdMatch = link is null ? null : Regex.Match(link, @"tt\d+", RegexOptions.IgnoreCase);
            if (ttIdMatch?.Success == true)
            {
                // Track the tt id alongside the entry's index so the year can be backfilled
                // after parsing; stored entries have no id column of their own.
                imdbIds.Add((items.Count, ttIdMatch.Value));
            }

            items.Add(new SourceListItem
            {
                Rank = rank,
                Title = title.Trim(),
                Year = null,
                PosterUrl = string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl
            });
        }

        return (items, imdbIds);
    }

    private static IReadOnlyList<SourceListItem> ParseLetterboxd(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = new List<SourceListItem>();
        var position = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            position++;

            var title = element.TryGetProperty("Film_title", out var titleElement) ? titleElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var year = element.TryGetProperty("Release_year", out var yearElement) && yearElement.TryGetInt32(out var y)
                ? (int?)y
                : null;

            items.Add(new SourceListItem
            {
                Rank = position,
                Title = title.Trim(),
                Year = year
            });
        }

        return items;
    }

    private static IReadOnlyList<SourceListItem> ParseRottenTomatoes(string csv)
    {
        // The dataset is a flat Certified Fresh dump, not an ordered chart, so the list is ranked
        // by critic score and de-duplicated by title.
        var entries = new List<(string Title, int Score)>();
        var lines = csv.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var fields = SplitCsvLine(lines[i]);
            if (fields.Count < 3)
            {
                continue;
            }

            if (i == 0 && fields[0].Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = fields[0].Trim();
            if (string.IsNullOrWhiteSpace(title) || !int.TryParse(fields[2].Trim().TrimEnd('%'), out var score))
            {
                continue;
            }

            entries.Add((title, score));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranked = entries
            .Where(e => seen.Add(e.Title))
            .OrderByDescending(e => e.Score)
            .Take(250)
            .Select((e, index) => new SourceListItem
            {
                Rank = index + 1,
                Title = e.Title,
                Year = null
            })
            .ToList();

        return ranked;
    }

    private static IReadOnlyList<SourceListItem> ParseNetflix(string key, string tsv)
    {
        // The netflix_top10 dataset is tab-separated. A country key ("netflix-<iso2>") selects one
        // country from all-weeks-countries.tsv; the bare "netflix" reads all-weeks-global.tsv.
        // The last ten weeks are unioned per title: the best weekly_rank wins, ties broken by
        // cumulative_weeks_in_top_10 (higher first) and then the most recent week.
        var iso2 = key.StartsWith("netflix-", StringComparison.Ordinal)
            ? key.Substring("netflix-".Length).ToUpperInvariant()
            : null;

        var rows = new List<(int Rank, string Title, string Week, bool IsSeries, int CumulativeWeeks)>();
        var lines = tsv.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('\t');

            string week;
            string category;
            string title;
            int rank;
            int cumulativeWeeks;
            if (iso2 is null)
            {
                // week, category, weekly_rank, show_title, ..., cumulative_weeks_in_top_10
                if (fields.Length < 9)
                {
                    continue;
                }

                if (i == 0 && fields[0].Equals("week", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                week = fields[0];
                category = fields[1];
                if (!int.TryParse(fields[2], out rank) || !int.TryParse(fields[8], out cumulativeWeeks))
                {
                    continue;
                }

                title = fields[3];
            }
            else
            {
                // country_name, country_iso2, week, category, weekly_rank, show_title, ...
                // cumulative_weeks_in_top_10
                if (fields.Length < 8)
                {
                    continue;
                }

                if (i == 0 && fields[0].Equals("country_name", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!fields[1].Equals(iso2, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                week = fields[2];
                category = fields[3];
                if (!int.TryParse(fields[4], out rank) || !int.TryParse(fields[7], out cumulativeWeeks))
                {
                    continue;
                }

                title = fields[5];
            }

            if (!category.StartsWith("Film", StringComparison.OrdinalIgnoreCase)
                && !category.StartsWith("TV", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var isSeries = category.StartsWith("TV", StringComparison.OrdinalIgnoreCase);
            rows.Add((rank, title.Trim(), week, isSeries, cumulativeWeeks));
        }

        var weeks = rows
            .Select(r => r.Week)
            .Distinct()
            .OrderByDescending(w => w, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var window = rows.Where(r => weeks.Contains(r.Week));

        // Re-rank within each series/movie group so English and non-English rows do not share a
        // rank (the dataset ranks them independently per language). Films come first, then TV, the
        // same group order the previous per-week re-ranking produced; rank is the position in the
        // merged list and the final list is capped at 50 entries.
        return UnionAndRank(window, isSeries: false)
            .Concat(UnionAndRank(window, isSeries: true))
            .Select((u, index) => new SourceListItem { Rank = index + 1, Title = u.Title, Year = null, IsSeries = u.IsSeries })
            .Take(50)
            .ToList();
    }

    private static IReadOnlyList<(string Title, bool IsSeries)> UnionAndRank(
        IEnumerable<(int Rank, string Title, string Week, bool IsSeries, int CumulativeWeeks)> rows, bool isSeries)
    {
        // Union per title: best weekly_rank, highest cumulative weeks on the chart, newest week.
        var best = new Dictionary<string, (int Rank, int CumulativeWeeks, string Week)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.IsSeries != isSeries)
            {
                continue;
            }

            if (best.TryGetValue(row.Title, out var agg))
            {
                best[row.Title] = (
                    Math.Min(agg.Rank, row.Rank),
                    Math.Max(agg.CumulativeWeeks, row.CumulativeWeeks),
                    string.CompareOrdinal(row.Week, agg.Week) > 0 ? row.Week : agg.Week);
            }
            else
            {
                best[row.Title] = (row.Rank, row.CumulativeWeeks, row.Week);
            }
        }

        return best
            .OrderBy(kv => kv.Value.Rank)
            .ThenByDescending(kv => kv.Value.CumulativeWeeks)
            .ThenByDescending(kv => kv.Value.Week, StringComparer.Ordinal)
            .Take(25)
            .Select(kv => (kv.Key, isSeries))
            .ToList();
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
