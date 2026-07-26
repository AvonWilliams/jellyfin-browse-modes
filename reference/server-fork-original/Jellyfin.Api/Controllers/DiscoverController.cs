using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TMDbLib.Objects.Trending;

// ControllerBase exposes a MetadataProvider property, which would otherwise win name resolution.
using MetadataProviders = MediaBrowser.Model.Entities.MetadataProvider;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Discover controller.
/// </summary>
/// <remarks>
/// Surfaces curated TMDb lists narrowed to the items actually present in the library. TMDb's
/// ordering is preserved; anything not owned locally simply drops out.
/// </remarks>
[Authorize]
[Tags("Discover")]
public class DiscoverController : BaseJellyfinApiController
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly TmdbClientManager _tmdbClientManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="tmdbClientManager">Instance of <see cref="TmdbClientManager"/>.</param>
    public DiscoverController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        TmdbClientManager tmdbClientManager)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _tmdbClientManager = tmdbClientManager;
    }

    /// <summary>
    /// Gets trending movies that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. The fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="weekly">Optional. Measure popularity over a week rather than a day.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Trending movies returned.</response>
    /// <returns>The trending movies available locally.</returns>
    [HttpGet("Trending/Movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetTrendingMovies(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int limit = 24,
        [FromQuery] bool weekly = true,
        CancellationToken cancellationToken = default)
    {
        var timeWindow = weekly ? TimeWindow.Week : TimeWindow.Day;
        var tmdbIds = await _tmdbClientManager
            .GetTrendingMovieIdsAsync(timeWindow, TmdbClientManager.DiscoverPagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Movie, userId, parentId, fields, limit);
    }

    /// <summary>
    /// Gets trending shows that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. The fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="weekly">Optional. Measure popularity over a week rather than a day.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Trending shows returned.</response>
    /// <returns>The trending shows available locally.</returns>
    [HttpGet("Trending/Shows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetTrendingShows(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int limit = 24,
        [FromQuery] bool weekly = true,
        CancellationToken cancellationToken = default)
    {
        var timeWindow = weekly ? TimeWindow.Week : TimeWindow.Day;
        var tmdbIds = await _tmdbClientManager
            .GetTrendingSeriesIdsAsync(timeWindow, TmdbClientManager.DiscoverPagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Series, userId, parentId, fields, limit);
    }

    /// <summary>
    /// Gets the highest rated movies of all time that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. The fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Top rated movies returned.</response>
    /// <returns>The top rated movies available locally.</returns>
    [HttpGet("TopRated/Movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetTopRatedMovies(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int limit = 24,
        CancellationToken cancellationToken = default)
    {
        var tmdbIds = await _tmdbClientManager
            .GetTopRatedMovieIdsAsync(TmdbClientManager.DiscoverPagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Movie, userId, parentId, fields, limit);
    }

    /// <summary>
    /// Gets the highest rated shows of all time that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. The fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Top rated shows returned.</response>
    /// <returns>The top rated shows available locally.</returns>
    [HttpGet("TopRated/Shows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetTopRatedShows(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int limit = 24,
        CancellationToken cancellationToken = default)
    {
        var tmdbIds = await _tmdbClientManager
            .GetTopRatedSeriesIdsAsync(TmdbClientManager.DiscoverPagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Series, userId, parentId, fields, limit);
    }

    /// <summary>
    /// Resolves a set of TMDb ids to local library items, preserving the order they were given in.
    /// </summary>
    private ActionResult<QueryResult<BaseItemDto>> GetLocalItemsForTmdbIds(
        IReadOnlyList<int> tmdbIds,
        BaseItemKind itemKind,
        Guid? userId,
        Guid? parentId,
        ItemFields[] fields,
        int limit)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var dtoOptions = new DtoOptions { Fields = fields };

        // The repository only loads provider ids when they are explicitly requested, and the
        // ranking below reads them off each item, so the query has to ask for them regardless
        // of what the caller wanted returned.
        var queryDtoOptions = new DtoOptions
        {
            Fields = fields.Contains(ItemFields.ProviderIds) ? fields : [.. fields, ItemFields.ProviderIds]
        };

        if (tmdbIds.Count == 0)
        {
            return Ok(new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>()));
        }

        // Position in TMDb's response is the ranking, and it is what the caller expects to see.
        var rankByTmdbId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (position, tmdbId) in tmdbIds.Index())
        {
            rankByTmdbId.TryAdd(tmdbId.ToString(CultureInfo.InvariantCulture), position);
        }

        var query = new InternalItemsQuery(user)
        {
            HasAnyProviderIds = new Dictionary<string, string[]>
            {
                { MetadataProviders.Tmdb.ToString(), rankByTmdbId.Keys.ToArray() }
            },
            IncludeItemTypes = [itemKind],
            Recursive = true,
            DtoOptions = queryDtoOptions
        };

        if (parentId.HasValue && !parentId.Value.IsEmpty())
        {
            query.ParentId = parentId.Value;
        }

        var items = _libraryManager.GetItemList(query);

        var ranked = items
            .Select(item => (Item: item, Rank: GetRank(item, rankByTmdbId)))
            .Where(entry => entry.Rank >= 0)
            .OrderBy(entry => entry.Rank)
            .Take(limit)
            .ToArray();

        var dtos = _dtoService.GetBaseItemDtos(Array.ConvertAll(ranked, entry => entry.Item), dtoOptions, user);

        // Surface each item's position in the source list rather than its position among the
        // items that happened to match. IndexNumber is unused for movies and series, so carrying
        // it there keeps the response a plain QueryResult the client already understands.
        for (var i = 0; i < dtos.Count && i < ranked.Length; i++)
        {
            dtos[i].IndexNumber = ranked[i].Rank + 1;
        }

        return Ok(new QueryResult<BaseItemDto>(dtos));
    }

    private static int GetRank(BaseItem item, Dictionary<string, int> rankByTmdbId)
    {
        return item.TryGetProviderId(MetadataProviders.Tmdb, out var tmdbId)
            && rankByTmdbId.TryGetValue(tmdbId, out var rank)
                ? rank
                : -1;
    }
}
