using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TMDbLib.Objects.Trending;

// ControllerBase exposes a MetadataProvider property, which would otherwise win name resolution.
using MetadataProviders = MediaBrowser.Model.Entities.MetadataProvider;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Discover controller.
/// </summary>
/// <remarks>
/// Surfaces curated TMDb lists narrowed to the items actually present in the library. TMDb's
/// ordering is preserved; anything not owned locally simply drops out.
/// </remarks>
[ApiController]
[Authorize]
[Route("Discover")]
[Produces(MediaTypeNames.Application.Json)]
public class DiscoverController : ControllerBase
{
    /// <summary>
    /// The claim the server stores the authenticated user id under. Mirrors
    /// Jellyfin.Api's InternalClaimTypes.UserId, which is not a published type.
    /// </summary>
    private const string UserIdClaimType = "Jellyfin-UserId";

    /// <summary>
    /// Mirrors Jellyfin.Api's UserRoles.Administrator, likewise unpublished.
    /// </summary>
    private const string AdministratorRole = "Administrator";

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly TmdbDiscoverClient _discoverClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>.</param>
    public DiscoverController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        TmdbDiscoverClient discoverClient)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _discoverClient = discoverClient;
    }

    /// <summary>
    /// Gets trending movies that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
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
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        [FromQuery] bool weekly = true,
        CancellationToken cancellationToken = default)
    {
        var timeWindow = weekly ? TimeWindow.Week : TimeWindow.Day;
        var tmdbIds = await _discoverClient
            .GetTrendingMovieIdsAsync(timeWindow, TmdbDiscoverClient.PagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Movie, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TrendingMaxRank);
    }

    /// <summary>
    /// Gets trending shows that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
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
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        [FromQuery] bool weekly = true,
        CancellationToken cancellationToken = default)
    {
        var timeWindow = weekly ? TimeWindow.Week : TimeWindow.Day;
        var tmdbIds = await _discoverClient
            .GetTrendingSeriesIdsAsync(timeWindow, TmdbDiscoverClient.PagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Series, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TrendingMaxRank);
    }

    /// <summary>
    /// Gets the highest rated movies of all time that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Top rated movies returned.</response>
    /// <returns>The top rated movies available locally.</returns>
    [HttpGet("TopRated/Movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetTopRatedMovies(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        CancellationToken cancellationToken = default)
    {
        var tmdbIds = await _discoverClient
            .GetTopRatedMovieIdsAsync(TmdbDiscoverClient.PagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Movie, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TopRatedMaxRank);
    }

    /// <summary>
    /// Gets the highest rated shows of all time that are present in the library.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Top rated shows returned.</response>
    /// <returns>The top rated shows available locally.</returns>
    [HttpGet("TopRated/Shows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetTopRatedShows(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        CancellationToken cancellationToken = default)
    {
        var tmdbIds = await _discoverClient
            .GetTopRatedSeriesIdsAsync(TmdbDiscoverClient.PagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return GetLocalItemsForTmdbIds(tmdbIds, BaseItemKind.Series, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TopRatedMaxRank);
    }

    /// <summary>
    /// Gets the browse-mode tile keys in display order, as configured by the administrator.
    /// </summary>
    /// <remarks>
    /// The client applies this order and hides any mode whose key is not listed. The plugin
    /// itself only stores the list; the meaning of each key lives in the client.
    /// </remarks>
    /// <response code="200">The ordered tile keys returned.</response>
    /// <returns>The ordered browse-mode tile keys.</returns>
    [HttpGet("TileLayout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetTileLayout()
    {
        return Ok(Plugin.Instance?.Configuration.BrowseModeOrder ?? new List<string>());
    }

    /// <summary>
    /// Parses the comma delimited fields query parameter.
    /// </summary>
    /// <remarks>
    /// The server binds this with CommaDelimitedCollectionModelBinder, which lives in Jellyfin.Api
    /// and is not available to plugins, so the same shape is parsed by hand. Unrecognised names
    /// are ignored rather than failing the request.
    /// </remarks>
    private static ItemFields[] ParseFields(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
        {
            return Array.Empty<ItemFields>();
        }

        var parsed = new List<ItemFields>();
        foreach (var value in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ItemFields>(value, true, out var field))
            {
                parsed.Add(field);
            }
        }

        return parsed.ToArray();
    }

    /// <summary>
    /// Resolves the effective user id, mirroring Jellyfin.Api's RequestHelpers.GetUserId.
    /// </summary>
    /// <remarks>
    /// That helper is internal to Jellyfin.Api. Reimplemented here so the endpoints keep the same
    /// behaviour: fall back to the authenticated user, and only allow impersonating another user
    /// when the caller is an administrator.
    /// </remarks>
    private Guid ResolveUserId(Guid? requestedUserId)
    {
        var claimValue = User.FindFirstValue(UserIdClaimType);
        var authenticatedUserId = string.IsNullOrEmpty(claimValue) ? default : Guid.Parse(claimValue);

        if (requestedUserId.IsNullOrEmpty())
        {
            return authenticatedUserId;
        }

        if (!requestedUserId.Value.Equals(authenticatedUserId) && !User.IsInRole(AdministratorRole))
        {
            return authenticatedUserId;
        }

        return requestedUserId.Value;
    }

    /// <summary>
    /// Resolves a set of TMDb ids to local library items, preserving the order they were given in.
    /// <paramref name="maxRank"/> is the lowest 1-based TMDb position to include; 0 means no cutoff.
    /// </summary>
    private ActionResult<QueryResult<BaseItemDto>> GetLocalItemsForTmdbIds(
        IReadOnlyList<int> tmdbIds,
        BaseItemKind itemKind,
        Guid? userId,
        Guid? parentId,
        ItemFields[] fields,
        int limit,
        int maxRank)
    {
        var effectiveUserId = ResolveUserId(userId);
        var user = effectiveUserId.IsEmpty()
            ? null
            : _userManager.GetUserById(effectiveUserId);
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
            .Where(entry => entry.Rank >= 0 && (maxRank <= 0 || entry.Rank < maxRank))
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

    /// <summary>
    /// Gets available studios by extracting them from item metadata.
    /// </summary>
    /// <remarks>
    /// On Jellyfin 12.x, studios are materialized as entities and the standard /Studios API works.
    /// On 10.11, studios only exist as metadata strings, so we aggregate them here. This endpoint
    /// works on both versions.
    /// </remarks>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Studio items returned.</response>
    /// <returns>Studio items with generated IDs and item counts.</returns>
    [HttpGet("Studios")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetStudios(
        [FromQuery] Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        var counts = GetOrBuildStudioCounts(parentId);
        var items = new List<BaseItemDto>(counts.Count);
        foreach (var (name, count) in counts)
        {
            items.Add(new BaseItemDto
            {
                Name = name,
                Id = StudioNameToGuid(name),
                ChildCount = count
            });
        }

        return Ok(new QueryResult<BaseItemDto>(items));
    }

    /// <summary>
    /// Gets per-studio item counts, cached for 24 hours.
    /// </summary>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Studio counts returned.</response>
    /// <returns>A map of studio name to item count.</returns>
    [HttpGet("StudioCounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, int>> GetStudioCounts(
        [FromQuery] Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        return Ok(GetOrBuildStudioCounts(parentId));
    }

    /// <summary>
    /// Builds studio name → count by enumerating items and reading their studio metadata.
    /// </summary>
    /// <remarks>
    /// <see cref="ILibraryManager.GetStudios"/> depends on materialized Studio entities, which
    /// only exist in Jellyfin 12.x. On 10.11, studios are metadata strings on the items
    /// themselves, so this method extracts them directly instead.
    /// </remarks>
    private Dictionary<string, int> GetOrBuildStudioCounts(Guid? parentId)
    {
        var cached = _discoverClient.GetStudioCounts();
        if (cached is not null)
        {
            // cached is IReadOnlyDictionary; copy to mutable for consistency.
            return new Dictionary<string, int>(cached, StringComparer.OrdinalIgnoreCase);
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true
        };

        if (parentId.HasValue && !parentId.Value.IsEmpty())
        {
            query.ParentId = parentId.Value;
        }

        var items = _libraryManager.GetItemList(query);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            // BaseItem.Studios is string[] on both 10.11 and 12.x.
            var studios = item.Studios;
            if (studios is null || studios.Length == 0)
            {
                continue;
            }

            foreach (var studio in studios)
            {
                if (string.IsNullOrWhiteSpace(studio))
                {
                    continue;
                }

                counts.TryGetValue(studio, out var current);
                counts[studio] = current + 1;
            }
        }

        _discoverClient.SetStudioCounts(counts);
        return counts;
    }

    /// <summary>
    /// Generates a deterministic <see cref="Guid"/> from a studio name.
    /// </summary>
    /// <remarks>
    /// The same name always produces the same id, so the client can use these ids for sorting
    /// and filtering without them changing between restarts.
    /// </remarks>
    private static Guid StudioNameToGuid(string name)
    {
        Span<byte> hash = stackalloc byte[16];
        var input = System.Text.Encoding.UTF8.GetBytes(name);
        // Simple FNV-1a-like hash into 16 bytes — good enough for a deterministic Guid.
        uint h = 2166136261;
        foreach (var b in input)
        {
            h ^= b;
            h *= 16777619;
        }

        // Seed the Guid bytes with the hash and the name bytes for uniqueness.
        for (var i = 0; i < 16; i++)
        {
            hash[i] = (byte)(input.Length > i ? input[i] ^ (byte)(h >> ((i % 4) * 8)) : (byte)(h >> ((i % 4) * 8)));
        }

        return new Guid(hash);
    }

    private static int GetRank(BaseItem item, Dictionary<string, int> rankByTmdbId)
    {
        return item.TryGetProviderId(MetadataProviders.Tmdb, out var tmdbId)
            && rankByTmdbId.TryGetValue(tmdbId, out var rank)
                ? rank
                : -1;
    }
}
