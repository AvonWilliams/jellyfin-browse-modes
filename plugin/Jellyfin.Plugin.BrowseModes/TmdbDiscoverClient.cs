using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Caching.Memory;
using TMDbLib.Client;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.Trending;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Fetches and caches the TMDb lists that back the discover endpoints.
/// </summary>
/// <remarks>
/// Holds its own <see cref="TMDbClient"/> and <see cref="IMemoryCache"/> rather than borrowing the
/// server's TMDb metadata provider, which lives in an unpublished assembly.
/// </remarks>
public sealed class TmdbDiscoverClient : IDisposable
{
    private readonly MemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private TMDbClient? _tmDbClient;

    // Per-studio data (name → count), populated by scheduled task or lazily by the controller.
    private Dictionary<string, int>? _studioCounts;
    private DateTime _studioCountsLastRefreshed = DateTime.MinValue;
    private static readonly TimeSpan StudioCountsCacheDuration = TimeSpan.FromHours(24);

    // General per-value counts (genre/rating/tag/decade/studio), keyed by a caller-defined key.
    private readonly Dictionary<string, Dictionary<string, int>> _countsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _countsRefreshed = new(StringComparer.Ordinal);
    private static readonly TimeSpan CountsCacheDuration = TimeSpan.FromHours(24);
    private readonly object _countsLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbDiscoverClient"/> class.
    /// </summary>
    /// <remarks>
    /// Does not construct a <see cref="TMDbClient"/> unless an API key has been configured,
    /// because TMDbLib 3.0.0 throws on an empty key. Every code path that touches the client
    /// already checks <see cref="HasApiKey"/> first.
    /// </remarks>
    public TmdbDiscoverClient()
    {
        var apiKey = Plugin.Instance?.Configuration.TmdbApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _tmDbClient = new TMDbClient(apiKey)
            {
                ThrowApiExceptions = false
            };
        }
    }

    /// <summary>
    /// Gets a value indicating whether a TMDb API key has been configured.
    /// </summary>
    public static bool HasApiKey => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbApiKey);

    /// <summary>
    /// Gets the TMDb client, creating it lazily so that setting the API key after plugin load
    /// (which requires a server restart) results in a working client on the next request.
    /// </summary>
    private TMDbClient TmDbClient
    {
        get
        {
            if (_tmDbClient is null && HasApiKey)
            {
                _tmDbClient = new TMDbClient(Plugin.Instance!.Configuration.TmdbApiKey!)
                {
                    ThrowApiExceptions = false
                };
            }

            return _tmDbClient!;
        }
    }

    /// <summary>
    /// Gets how many pages of each list to scan.
    /// </summary>
    public static int PagesToScan => Math.Max(1, Plugin.Instance?.Configuration.DiscoverPagesToScan ?? 15);

    /// <summary>
    /// Gets the lowest TMDb position to show in the Trending list, 0 meaning no cutoff.
    /// </summary>
    public static int TrendingMaxRank => Math.Max(0, Plugin.Instance?.Configuration.TrendingMaxRank ?? 0);

    /// <summary>
    /// Gets the lowest TMDb position to show in the Top Rated list, 0 meaning no cutoff.
    /// </summary>
    public static int TopRatedMaxRank => Math.Max(0, Plugin.Instance?.Configuration.TopRatedMaxRank ?? 0);

    private static int CacheDurationHours => Math.Max(1, Plugin.Instance?.Configuration.CacheDurationHours ?? 6);

    /// <summary>
    /// The TMDb image base URL. Poster paths returned by the API are relative to this.
    /// </summary>
    private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/";

    /// <summary>
    /// The single poster size requested for missing-title stubs, kept small so stubs stay lightweight.
    /// </summary>
    private const string TmdbPosterSize = "w342";

    /// <summary>
    /// Builds the single-size poster URL for a TMDb poster path, or null when there is no poster.
    /// </summary>
    /// <param name="posterPath">The source-relative poster path.</param>
    /// <returns>The absolute poster URL, or null.</returns>
    public static string? BuildPosterUrl(string? posterPath)
    {
        if (string.IsNullOrWhiteSpace(posterPath))
        {
            return null;
        }

        return TmdbImageBaseUrl + TmdbPosterSize + posterPath;
    }

    /// <summary>
    /// Finds a poster URL for a title by searching TMDb, or null when there is no API key, no
    /// match, or no poster on the match.
    /// </summary>
    /// <param name="title">The title to search for.</param>
    /// <param name="year">The release year, used to disambiguate, when known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The absolute poster URL, or null.</returns>
    public async Task<string?> FindPosterUrlAsync(string title, int? year, CancellationToken cancellationToken)
    {
        if (!HasApiKey || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = await SearchForPosterAsync(title, year, cancellationToken).ConfigureAwait(false);
        if (match is null && title.EndsWith(" netflix", StringComparison.OrdinalIgnoreCase))
        {
            // The "netflix" hint can be too restrictive for TMDb's literal search; retry bare.
            match = await SearchForPosterAsync(title.Substring(0, title.Length - " netflix".Length), year, cancellationToken).ConfigureAwait(false);
        }

        return match;
    }

    private async Task<string?> SearchForPosterAsync(string title, int? year, CancellationToken cancellationToken)
    {
        var results = await TmDbClient.SearchMovieAsync(
                title,
                0,
                false,
                year ?? 0,
                string.Empty,
                0,
                cancellationToken)
            .ConfigureAwait(false);

        var normalizedTitle = NormalizeTitle(title);
        var candidates = results?.Results?
            .Where(r => !string.IsNullOrWhiteSpace(r.PosterPath)
                && NormalizeTitle(r.Title) == normalizedTitle)
            .ToList();
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        if (year.HasValue)
        {
            var yearMatches = candidates
                .Where(r => r.ReleaseDate?.Year >= year.Value - 1 && r.ReleaseDate?.Year <= year.Value + 1)
                .ToList();
            if (yearMatches.Count > 0)
            {
                candidates = yearMatches;
            }
        }

        return BuildPosterUrl(candidates[0].PosterPath);
    }

    /// <summary>
    /// Finds a title's release year by searching TMDb, or null when there is no API key, no
    /// normalized-title match, or no release date on the match. Successful lookups are cached.
    /// </summary>
    /// <param name="title">The title to search for.</param>
    /// <param name="isSeries">Whether the title is a TV series rather than a movie.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release year, or null.</returns>
    public async Task<int?> SearchForYearAsync(string title, bool isSeries, CancellationToken cancellationToken)
    {
        if (!HasApiKey || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalizedTitle = NormalizeTitle(title);
        var cacheKey = $"year-{(isSeries ? "tv" : "movie")}-{normalizedTitle}";
        if (_memoryCache.TryGetValue(cacheKey, out var cachedObject) && cachedObject is int cachedYear)
        {
            return cachedYear;
        }

        int? year;
        if (isSeries)
        {
            var results = await TmDbClient.SearchTvShowAsync(
                    title,
                    0,
                    false,
                    0,
                    cancellationToken)
                .ConfigureAwait(false);
            year = results?.Results?
                .FirstOrDefault(r => NormalizeTitle(r.Name) == normalizedTitle)?
                .FirstAirDate?.Year;
        }
        else
        {
            var results = await TmDbClient.SearchMovieAsync(
                    title,
                    0,
                    false,
                    0,
                    string.Empty,
                    0,
                    cancellationToken)
                .ConfigureAwait(false);
            year = results?.Results?
                .FirstOrDefault(r => NormalizeTitle(r.Title) == normalizedTitle)?
                .ReleaseDate?.Year;
        }

        if (year.HasValue)
        {
            _memoryCache.Set(cacheKey, year.Value, TimeSpan.FromHours(CacheDurationHours));
        }

        return year;
    }

    /// <summary>
    /// Finds a movie's release year and poster path by its IMDb title id.
    /// </summary>
    /// <param name="ttId">The IMDb title id, e.g. "tt0111161".</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release year and poster path, or null when there is no API key, no match, or
    /// no release date on the match.</returns>
    public async Task<(int Year, string? PosterPath)?> FindByImdbIdAsync(string ttId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ttId))
        {
            return null;
        }

        var cacheKey = $"imdb-{ttId}";
        if (_memoryCache.TryGetValue(cacheKey, out var cachedObject) && cachedObject is ValueTuple<int, string?> cached)
        {
            return cached;
        }

        if (!HasApiKey)
        {
            return null;
        }

        var container = await TmDbClient.FindAsync(FindExternalSource.Imdb, ttId, cancellationToken)
            .ConfigureAwait(false);
        var movie = container?.MovieResults?.FirstOrDefault();
        var releaseDate = movie?.ReleaseDate;
        if (releaseDate is null)
        {
            return null;
        }

        var result = (releaseDate.Value.Year, movie?.PosterPath);
        _memoryCache.Set(cacheKey, result, TimeSpan.FromHours(CacheDurationHours));
        return result;
    }

    /// <summary>
    /// Normalizes a title for name matching: lowercase, alphanumeric only. Mirrors the
    /// controller's normalization so poster picks agree with library matching.
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

    /// <summary>
    /// Gets the currently trending movies, in TMDb's trending order.
    /// </summary>
    /// <param name="timeWindow">The window over which popularity is measured.</param>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to bypass the cache and refetch from TMDb.</param>
    /// <returns>The trending movies with their title, year and poster.</returns>
    public Task<IReadOnlyList<TmdbRankedTitle>> GetTrendingMovieTitlesAsync(TimeWindow timeWindow, int pages, CancellationToken cancellationToken, bool force = false)
    {
        return GetListTitlesAsync(
            $"trending-movies-{timeWindow}-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetTrendingMoviesAsync(timeWindow, page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => new TmdbRankedTitle
                {
                    Id = result.Id,
                    Title = result.Title,
                    Year = result.ReleaseDate?.Year,
                    PosterPath = result.PosterPath
                }),
            pages,
            force);
    }

    /// <summary>
    /// Gets the currently trending tv shows, in TMDb's trending order.
    /// </summary>
    /// <param name="timeWindow">The window over which popularity is measured.</param>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to bypass the cache and refetch from TMDb.</param>
    /// <returns>The trending tv shows with their title, year and poster.</returns>
    public Task<IReadOnlyList<TmdbRankedTitle>> GetTrendingSeriesTitlesAsync(TimeWindow timeWindow, int pages, CancellationToken cancellationToken, bool force = false)
    {
        return GetListTitlesAsync(
            $"trending-series-{timeWindow}-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetTrendingTvAsync(timeWindow, page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => new TmdbRankedTitle
                {
                    Id = result.Id,
                    Title = result.Name,
                    Year = result.FirstAirDate?.Year,
                    PosterPath = result.PosterPath
                }),
            pages,
            force);
    }

    /// <summary>
    /// Gets the highest rated movies of all time, best first.
    /// </summary>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to bypass the cache and refetch from TMDb.</param>
    /// <returns>The top rated movies with their title, year and poster.</returns>
    public Task<IReadOnlyList<TmdbRankedTitle>> GetTopRatedMovieTitlesAsync(int pages, CancellationToken cancellationToken, bool force = false)
    {
        return GetListTitlesAsync(
            $"toprated-movies-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetMovieTopRatedListAsync(page: page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => new TmdbRankedTitle
                {
                    Id = result.Id,
                    Title = result.Title,
                    Year = result.ReleaseDate?.Year,
                    PosterPath = result.PosterPath
                }),
            pages,
            force);
    }

    /// <summary>
    /// Gets the highest rated tv shows of all time, best first.
    /// </summary>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to bypass the cache and refetch from TMDb.</param>
    /// <returns>The top rated tv shows with their title, year and poster.</returns>
    public Task<IReadOnlyList<TmdbRankedTitle>> GetTopRatedSeriesTitlesAsync(int pages, CancellationToken cancellationToken, bool force = false)
    {
        return GetListTitlesAsync(
            $"toprated-series-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetTvShowTopRatedAsync(page: page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => new TmdbRankedTitle
                {
                    Id = result.Id,
                    Title = result.Name,
                    Year = result.FirstAirDate?.Year,
                    PosterPath = result.PosterPath
                }),
            pages,
            force);
    }

    /// <summary>
    /// Populates the cache for every discover list, so that the first user to open one does not
    /// have to wait for TMDb.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to bypass the cache and refetch every list from TMDb.</param>
    /// <returns>A task representing the warm-up.</returns>
    public async Task WarmDiscoverListsAsync(CancellationToken cancellationToken, bool force = false)
    {
        var pages = PagesToScan;
        var trendingWindow = TimeWindow.Week;

        await GetTrendingMovieTitlesAsync(trendingWindow, pages, cancellationToken, force).ConfigureAwait(false);
        await GetTrendingSeriesTitlesAsync(trendingWindow, pages, cancellationToken, force).ConfigureAwait(false);
        await GetTopRatedMovieTitlesAsync(pages, cancellationToken, force).ConfigureAwait(false);
        await GetTopRatedSeriesTitlesAsync(pages, cancellationToken, force).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets cached per-studio item counts, or null if the cache is stale or empty.
    /// </summary>
    public IReadOnlyDictionary<string, int>? GetStudioCounts()
    {
        if (_studioCounts is not null && DateTime.UtcNow - _studioCountsLastRefreshed < StudioCountsCacheDuration)
        {
            return _studioCounts;
        }

        return null;
    }

    /// <summary>
    /// Stores per-studio item counts in the cache, overwriting any previous entry.
    /// </summary>
    public void SetStudioCounts(Dictionary<string, int> counts)
    {
        _studioCounts = counts;
        _studioCountsLastRefreshed = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets cached per-value counts for an arbitrary key, or null when stale or absent.
    /// </summary>
    public IReadOnlyDictionary<string, int>? GetCounts(string key)
    {
        lock (_countsLock)
        {
            if (_countsByKey.TryGetValue(key, out var counts)
                && _countsRefreshed.TryGetValue(key, out var refreshed)
                && DateTime.UtcNow - refreshed < CountsCacheDuration)
            {
                return counts;
            }
        }

        return null;
    }

    /// <summary>
    /// Stores per-value counts under a key, overwriting any previous entry.
    /// </summary>
    public void SetCounts(string key, Dictionary<string, int> counts)
    {
        lock (_countsLock)
        {
            _countsByKey[key] = counts;
            _countsRefreshed[key] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Builds the cache key a picker's counts are stored under.
    /// </summary>
    private static string BuildCountsKey(string type, Guid? parentId, BaseItemKind[] itemTypes, PersonKind[]? personTypes)
    {
        var scopeId = parentId.HasValue && !parentId.Value.IsEmpty() ? parentId.Value : Guid.Empty;
        var personScope = type.Equals("personitems", StringComparison.OrdinalIgnoreCase)
            ? $":{string.Join(',', personTypes ?? [PersonKind.Actor, PersonKind.Director, PersonKind.Writer])}"
            : string.Empty;
        return $"{type}:{scopeId}:{string.Join(',', itemTypes)}{personScope}";
    }

    /// <summary>
    /// Stores the person counts the people index already knows, under the keys
    /// <see cref="GetOrBuildCounts"/> reads, so that /Discover/Counts answers the person pickers
    /// from memory instead of scanning the library once per person kind.
    /// </summary>
    /// <param name="libraryId">The library the counts are for.</param>
    /// <param name="itemTypes">The item types the library is counted as (movies or series).</param>
    /// <param name="peopleByKind">Name and title count of every person, per credit kind.</param>
    public void CachePersonCounts(
        Guid libraryId,
        BaseItemKind[] itemTypes,
        IReadOnlyDictionary<PersonKind, IReadOnlyList<(string Name, int TitleCount)>> peopleByKind)
    {
        var kinds = new[] { PersonKind.Actor, PersonKind.Director, PersonKind.Writer };

        // "person": how many people each kind has, keyed by the kind name.
        var kindTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (peopleByKind.TryGetValue(kind, out var people))
            {
                foreach (var (name, _) in people)
                {
                    names.Add(name);
                }
            }

            kindTotals[kind.ToString()] = names.Count;
        }

        SetCounts(BuildCountsKey("person", libraryId, itemTypes, null), kindTotals);

        // "personitems": name → number of titles, per single kind and for all three together.
        var union = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (peopleByKind.TryGetValue(kind, out var people))
            {
                foreach (var (name, titleCount) in people)
                {
                    // Two people can share a name; the name-keyed picker shows the larger count.
                    counts[name] = counts.TryGetValue(name, out var existing) ? Math.Max(existing, titleCount) : titleCount;
                    union[name] = union.TryGetValue(name, out var all) ? Math.Max(all, titleCount) : titleCount;
                }
            }

            SetCounts(BuildCountsKey("personitems", libraryId, itemTypes, [kind]), counts);
        }

        SetCounts(BuildCountsKey("personitems", libraryId, itemTypes, null), union);
    }

    /// <summary>
    /// Gets or builds value → count for a picker, cached for 24 hours.
    /// </summary>
    /// <remarks>
    /// Decades are keyed by their start year as a bare integer string (e.g. "1980"), matching
    /// the client's filter values. Shared by the controller and the pre-bake scheduled task.
    /// </remarks>
    public Dictionary<string, int> GetOrBuildCounts(string type, Guid? parentId, BaseItemKind[] itemTypes, ILibraryManager libraryManager, PersonKind[]? personTypes = null)
    {
        var cacheKey = BuildCountsKey(type, parentId, itemTypes, personTypes);
        var cached = GetCounts(cacheKey);
        if (cached is not null)
        {
            return new Dictionary<string, int>(cached, StringComparer.OrdinalIgnoreCase);
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = itemTypes,
            Recursive = true
        };

        if (parentId.HasValue && !parentId.Value.IsEmpty())
        {
            query.ParentId = parentId.Value;
        }

        var items = libraryManager.GetItemList(query);

        if (type.Equals("person", StringComparison.OrdinalIgnoreCase))
        {
            var personCounts = BuildPersonCounts(items, parentId, libraryManager);
            SetCounts(cacheKey, personCounts);
            return personCounts;
        }

        if (type.Equals("personitems", StringComparison.OrdinalIgnoreCase))
        {
            var personItemCounts = BuildPersonItemCounts(items, parentId, libraryManager, personTypes ?? [PersonKind.Actor, PersonKind.Director, PersonKind.Writer]);
            SetCounts(cacheKey, personItemCounts);
            return personItemCounts;
        }

        if (type.Equals("year", StringComparison.OrdinalIgnoreCase))
        {
            var yearCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var year = item.ProductionYear > 0 ? item.ProductionYear : item.PremiereDate?.Year;
                if (year.HasValue)
                {
                    Increment(yearCounts, year.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            SetCounts(cacheKey, yearCounts);
            return yearCounts;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            switch (type)
            {
                case "genre":
                    if (item.Genres is not null)
                    {
                        foreach (var genre in item.Genres)
                        {
                            Increment(counts, genre);
                        }
                    }

                    break;

                case "rating":
                    Increment(counts, item.OfficialRating);
                    break;

                case "tag":
                    if (item.Tags is not null)
                    {
                        foreach (var tag in item.Tags)
                        {
                            Increment(counts, tag);
                        }
                    }

                    break;

                case "decade":
                    var year = item.ProductionYear > 0 ? item.ProductionYear : item.PremiereDate?.Year;
                    if (year.HasValue)
                    {
                        Increment(counts, ((year.Value / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s");
                    }

                    break;

                case "studio":
                    if (item.Studios is not null)
                    {
                        foreach (var studio in item.Studios)
                        {
                            Increment(counts, studio);
                        }
                    }

                    break;
            }
        }

        SetCounts(cacheKey, counts);
        return counts;
    }

    /// <summary>
    /// Counts the distinct persons of the Actor, Director and Writer kinds across the given
    /// items and their child items, keyed by the person kind name.
    /// </summary>
    private static Dictionary<string, int> BuildPersonCounts(IReadOnlyList<BaseItem> items, Guid? parentId, ILibraryManager libraryManager)
    {
        var names = new Dictionary<PersonKind, HashSet<string>>
        {
            [PersonKind.Actor] = new(StringComparer.OrdinalIgnoreCase),
            [PersonKind.Director] = new(StringComparer.OrdinalIgnoreCase),
            [PersonKind.Writer] = new(StringComparer.OrdinalIgnoreCase)
        };

        var peopleByItem = GetPeopleAcrossItems(items, parentId, libraryManager);
        foreach (var people in peopleByItem.Values)
        {
            foreach (var person in people)
            {
                if (string.IsNullOrWhiteSpace(person.Name) || !names.TryGetValue(person.Type, out var kindNames))
                {
                    continue;
                }

                kindNames.Add(person.Name);
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, kindNames) in names)
        {
            counts[kind.ToString()] = kindNames.Count;
        }

        return counts;
    }

    /// <summary>
    /// Counts how many items each person appears in, keyed by person name. A person belongs to the
    /// picker when they hold at least one credit of the given kinds, but their count is the number
    /// of items they appear in under any kind, matching the person detail's "appears in N items".
    /// </summary>
    private static Dictionary<string, int> BuildPersonItemCounts(IReadOnlyList<BaseItem> items, Guid? parentId, ILibraryManager libraryManager, PersonKind[] personKinds)
    {
        var kinds = new HashSet<PersonKind>(personKinds);
        var eligibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appearancesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var peopleByItem = GetPeopleAcrossItems(items, parentId, libraryManager);
        foreach (var people in peopleByItem.Values)
        {
            // An item can list the same person under several kinds (actor and director) and on
            // several child items (episodes); the item should only count once per person.
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in people)
            {
                if (string.IsNullOrWhiteSpace(person.Name))
                {
                    continue;
                }

                if (kinds.Contains(person.Type))
                {
                    eligibleNames.Add(person.Name);
                }

                if (counted.Add(person.Name))
                {
                    Increment(appearancesByName, person.Name);
                }
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in eligibleNames)
        {
            counts[name] = appearancesByName[name];
        }

        return counts;
    }

    /// <summary>
    /// Gets the people credited on the given top-level items and their child items (seasons and
    /// episodes), keyed by the top-level item each credit belongs to.
    /// </summary>
    /// <remarks>
    /// <see cref="ILibraryManager.GetPeopleByItems"/> returns only the people attached directly to
    /// the queried items. TV show people are usually attached to the episodes, so querying only the
    /// series misses them; the child items are fetched here and their people attributed to the
    /// parent series.
    /// </remarks>
    internal static Dictionary<Guid, IReadOnlyList<PersonInfo>> GetPeopleAcrossItems(
        IReadOnlyList<BaseItem> items,
        Guid? parentId,
        ILibraryManager libraryManager,
        User? user = null)
    {
        var childQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
            Recursive = true
        };

        if (parentId.HasValue && !parentId.Value.IsEmpty())
        {
            childQuery.ParentId = parentId.Value;
        }

        var children = libraryManager.GetItemList(childQuery);

        // Map every item id to the top-level item (series or movie) it rolls up into.
        var topLevelByItemId = new Dictionary<Guid, Guid>();
        var allIds = new List<Guid>(items.Count + children.Count);
        foreach (var item in items)
        {
            topLevelByItemId[item.Id] = item.Id;
            allIds.Add(item.Id);
        }

        foreach (var child in children)
        {
            var topLevelId = child switch
            {
                IHasSeries seriesItem when !seriesItem.SeriesId.IsEmpty() => seriesItem.SeriesId,
                _ when !child.ParentId.IsEmpty() => child.ParentId,
                _ => child.Id
            };

            topLevelByItemId[child.Id] = topLevelId;
            allIds.Add(child.Id);
        }

        // Re-key the per-item people by their top-level item so episode and season credits count
        // toward their series.
        var peopleByTopLevelItem = new Dictionary<Guid, List<PersonInfo>>();
        var peopleByItem = libraryManager.GetPeopleByItems(allIds);
        foreach (var (itemId, people) in peopleByItem)
        {
            var topLevelId = topLevelByItemId.TryGetValue(itemId, out var mapped) ? mapped : itemId;
            if (!peopleByTopLevelItem.TryGetValue(topLevelId, out var bucket))
            {
                bucket = new List<PersonInfo>();
                peopleByTopLevelItem[topLevelId] = bucket;
            }

            bucket.AddRange(people);
        }

        return peopleByTopLevelItem.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<PersonInfo>)kvp.Value);
    }

    private static void Increment(Dictionary<string, int> counts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        counts.TryGetValue(value, out var current);
        counts[value] = current + 1;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Only the cache this instance created is disposed. The server's shared IMemoryCache is
        // deliberately not used, so nothing outside the plugin is affected.
        _memoryCache.Dispose();
        _tmDbClient?.Dispose();
    }

    /// <summary>
    /// Fetches an ordered, de-duplicated list of ranked titles by paging a TMDb list endpoint.
    /// </summary>
    private async Task<IReadOnlyList<TmdbRankedTitle>> GetListTitlesAsync(
        string cacheKey,
        Func<int, Task<IEnumerable<TmdbRankedTitle>?>> fetchPage,
        int pages,
        bool force = false)
    {
        // "Show missing titles" and the missing cap shape the ranked response, so they belong in
        // the key: toggling either must not hand back a list cached under the previous setting.
        var showMissing = Plugin.Instance?.Configuration.ShowMissing ?? true;
        var maxMissing = Math.Max(0, Plugin.Instance?.Configuration.MaxMissing ?? 10);
        var listCacheKey = $"{cacheKey}-missing-{(showMissing ? 1 : 0)}-{maxMissing.ToString(CultureInfo.InvariantCulture)}";

        if (!force && _memoryCache.TryGetValue(listCacheKey, out IReadOnlyList<TmdbRankedTitle>? cachedTitles) && cachedTitles is not null)
        {
            return cachedTitles;
        }

        if (!HasApiKey)
        {
            return Array.Empty<TmdbRankedTitle>();
        }

        // Fetched concurrently: paging these one at a time costs roughly ten seconds for a
        // fifteen page list, which is long enough for a user to notice on a cold cache.
        var pageTasks = new Task<IEnumerable<TmdbRankedTitle>?>[pages];
        for (var page = 0; page < pages; page++)
        {
            pageTasks[page] = fetchPage(page + 1);
        }

        var pageResults = await Task.WhenAll(pageTasks).ConfigureAwait(false);

        // Ranking depends on order, so results are consumed in page order rather than completion
        // order.
        var titles = new List<TmdbRankedTitle>();
        var seen = new HashSet<int>();
        foreach (var pageTitles in pageResults)
        {
            if (pageTitles is null)
            {
                continue;
            }

            foreach (var title in pageTitles)
            {
                if (seen.Add(title.Id))
                {
                    titles.Add(title);
                }
            }
        }

        if (titles.Count > 0)
        {
            _memoryCache.Set(listCacheKey, (IReadOnlyList<TmdbRankedTitle>)titles, TimeSpan.FromHours(CacheDurationHours));
        }

        return titles;
    }
}
