using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using TMDbLib.Client;
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

    private static int CacheDurationHours => Math.Max(1, Plugin.Instance?.Configuration.CacheDurationHours ?? 6);

    /// <summary>
    /// Gets the TMDb ids of the currently trending movies, in TMDb's trending order.
    /// </summary>
    /// <param name="timeWindow">The window over which popularity is measured.</param>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The trending movie TMDb ids.</returns>
    public Task<IReadOnlyList<int>> GetTrendingMovieIdsAsync(TimeWindow timeWindow, int pages, CancellationToken cancellationToken)
    {
        return GetListIdsAsync(
            $"trending-movies-{timeWindow}-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetTrendingMoviesAsync(timeWindow, page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => result.Id),
            pages);
    }

    /// <summary>
    /// Gets the TMDb ids of the currently trending tv shows, in TMDb's trending order.
    /// </summary>
    /// <param name="timeWindow">The window over which popularity is measured.</param>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The trending tv show TMDb ids.</returns>
    public Task<IReadOnlyList<int>> GetTrendingSeriesIdsAsync(TimeWindow timeWindow, int pages, CancellationToken cancellationToken)
    {
        return GetListIdsAsync(
            $"trending-series-{timeWindow}-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetTrendingTvAsync(timeWindow, page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => result.Id),
            pages);
    }

    /// <summary>
    /// Gets the TMDb ids of the highest rated movies of all time, best first.
    /// </summary>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The top rated movie TMDb ids.</returns>
    public Task<IReadOnlyList<int>> GetTopRatedMovieIdsAsync(int pages, CancellationToken cancellationToken)
    {
        return GetListIdsAsync(
            $"toprated-movies-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetMovieTopRatedListAsync(page: page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => result.Id),
            pages);
    }

    /// <summary>
    /// Gets the TMDb ids of the highest rated tv shows of all time, best first.
    /// </summary>
    /// <param name="pages">The number of result pages to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The top rated tv show TMDb ids.</returns>
    public Task<IReadOnlyList<int>> GetTopRatedSeriesIdsAsync(int pages, CancellationToken cancellationToken)
    {
        return GetListIdsAsync(
            $"toprated-series-{pages.ToString(CultureInfo.InvariantCulture)}",
            async page => (await TmDbClient.GetTvShowTopRatedAsync(page: page, cancellationToken: cancellationToken)
                .ConfigureAwait(false))?.Results?.Select(result => result.Id),
            pages);
    }

    /// <summary>
    /// Populates the cache for every discover list, so that the first user to open one does not
    /// have to wait for TMDb.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the warm-up.</returns>
    public async Task WarmDiscoverListsAsync(CancellationToken cancellationToken)
    {
        var pages = PagesToScan;

        await GetTrendingMovieIdsAsync(TimeWindow.Week, pages, cancellationToken).ConfigureAwait(false);
        await GetTrendingSeriesIdsAsync(TimeWindow.Week, pages, cancellationToken).ConfigureAwait(false);
        await GetTopRatedMovieIdsAsync(pages, cancellationToken).ConfigureAwait(false);
        await GetTopRatedSeriesIdsAsync(pages, cancellationToken).ConfigureAwait(false);
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
    /// Fetches an ordered, de-duplicated list of TMDb ids by paging a TMDb list endpoint.
    /// </summary>
    private async Task<IReadOnlyList<int>> GetListIdsAsync(
        string cacheKey,
        Func<int, Task<IEnumerable<int>?>> fetchPage,
        int pages)
    {
        if (_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<int>? cachedIds) && cachedIds is not null)
        {
            return cachedIds;
        }

        if (!HasApiKey)
        {
            return Array.Empty<int>();
        }

        // Fetched concurrently: paging these one at a time costs roughly ten seconds for a
        // fifteen page list, which is long enough for a user to notice on a cold cache.
        var pageTasks = new Task<IEnumerable<int>?>[pages];
        for (var page = 0; page < pages; page++)
        {
            pageTasks[page] = fetchPage(page + 1);
        }

        var pageResults = await Task.WhenAll(pageTasks).ConfigureAwait(false);

        // Ranking depends on order, so results are consumed in page order rather than completion
        // order.
        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var pageIds in pageResults)
        {
            if (pageIds is null)
            {
                continue;
            }

            foreach (var id in pageIds)
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        if (ids.Count > 0)
        {
            _memoryCache.Set(cacheKey, (IReadOnlyList<int>)ids, TimeSpan.FromHours(CacheDurationHours));
        }

        return ids;
    }
}
