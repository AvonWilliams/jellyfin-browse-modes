using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
            [("rottentomatoes", "toprated")] = "https://raw.githubusercontent.com/minakdr/Rotten-Tomatoes-Certified-Fresh-Movies-Dataset-September-2024-Edition-/main/MoviesRatingRottenTomato.csv"
            // Netflix has no reliable machine-readable collation, so it carries no default URL.
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SourceListStore _store;
    private readonly ILogger<SnapshotListFetcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotListFetcher"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="store">Instance of <see cref="SourceListStore"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{SnapshotListFetcher}"/> interface.</param>
    public SnapshotListFetcher(
        IHttpClientFactory httpClientFactory,
        SourceListStore store,
        ILogger<SnapshotListFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
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
            var items = Parse(source.Key, body);
            var kind = source.Kind.Equals("trending", StringComparison.OrdinalIgnoreCase)
                ? SourceListKind.Trending
                : SourceListKind.TopRated;
            var title = source.Kind.Equals("trending", StringComparison.OrdinalIgnoreCase)
                ? $"{DisplayName(source.Key)} Trending"
                : $"{DisplayName(source.Key)} Top Rated";

            _store.ReplaceList(source.Key, kind, title, items);
            _logger.LogInformation("Refreshed {Source} ({Kind}): {Count} titles", source.Key, source.Kind, items.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to refresh snapshot list {Source} ({Kind})", source.Key, source.Kind);
        }
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
        "netflix" => "Netflix",
        "letterboxd" => "Letterboxd",
        "rottentomatoes" => "Rotten Tomatoes",
        _ => key
    };

    private static IReadOnlyList<SourceListItem> Parse(string key, string body) => key switch
    {
        "imdb" => ParseImdb(body),
        "letterboxd" => ParseLetterboxd(body),
        "rottentomatoes" => ParseRottenTomatoes(body),
        "netflix" => ParseNetflix(body),
        _ => Array.Empty<SourceListItem>()
    };

    private static IReadOnlyList<SourceListItem> ParseImdb(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = new List<SourceListItem>();
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

            items.Add(new SourceListItem
            {
                Rank = rank,
                Title = title.Trim(),
                Year = null,
                PosterUrl = string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl
            });
        }

        return items;
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
            .Select((e, index) => new SourceListItem
            {
                Rank = index + 1,
                Title = e.Title,
                Year = null
            })
            .ToList();

        return ranked;
    }

    private static IReadOnlyList<SourceListItem> ParseNetflix(string csv)
    {
        // Netflix Top 10 weekly export: week,category,weekly_rank,show_title,season_title,...
        // Only the current week's global film rows are wanted; rank comes from weekly_rank.
        var items = new List<SourceListItem>();
        var lines = csv.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var fields = SplitCsvLine(lines[i]);
            if (fields.Count < 4)
            {
                continue;
            }

            if (i == 0 && fields[0].Equals("week", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(fields[2].Trim(), out var rank))
            {
                continue;
            }

            var title = fields[3].Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            items.Add(new SourceListItem
            {
                Rank = rank,
                Title = title,
                Year = null
            });
        }

        return items.OrderBy(i => i.Rank).ToList();
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
