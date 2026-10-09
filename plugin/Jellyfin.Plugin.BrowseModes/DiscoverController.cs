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
using Jellyfin.Plugin.BrowseModes.Data;
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

    /// <summary>
    /// The default ranked data source, and the only one implemented in this phase.
    /// </summary>
    private const string DefaultSource = "tmdb";

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly TmdbDiscoverClient _discoverClient;
    private readonly SourceListStore _sourceListStore;
    private readonly PosterStore _posterStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>.</param>
    /// <param name="sourceListStore">Instance of <see cref="SourceListStore"/>.</param>
    /// <param name="posterStore">Instance of <see cref="PosterStore"/>.</param>
    public DiscoverController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        TmdbDiscoverClient discoverClient,
        SourceListStore sourceListStore,
        PosterStore posterStore)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _discoverClient = discoverClient;
        _sourceListStore = sourceListStore;
        _posterStore = posterStore;
    }

    /// <summary>
    /// Gets trending movies that are present in the library, plus optional missing-title stubs.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="source">Optional. The ranked data source; only "tmdb" is implemented.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Trending movies returned.</response>
    /// <returns>The trending movies available locally, plus missing-title stubs.</returns>
    [HttpGet("Trending/Movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoverRankedResult>> GetTrendingMovies(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        if (!IsSupportedSource(resolvedSource, SourceListKind.Trending))
        {
            return Ok(new DiscoverRankedResult { Source = resolvedSource });
        }

        if (resolvedSource.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase))
        {
            var timeWindow = TimeWindow.Week;
            var titles = await _discoverClient
                .GetTrendingMovieTitlesAsync(timeWindow, TmdbDiscoverClient.PagesToScan, cancellationToken)
                .ConfigureAwait(false);

            return BuildRankedResult(resolvedSource, titles, BaseItemKind.Movie, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TrendingMaxRank);
        }

        return BuildSnapshotRankedResult(resolvedSource, SourceListKind.Trending, BaseItemKind.Movie, userId, parentId, ParseFields(fields), limit);
    }

    /// <summary>
    /// Gets trending shows that are present in the library, plus optional missing-title stubs.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="source">Optional. The ranked data source; only "tmdb" is implemented.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Trending shows returned.</response>
    /// <returns>The trending shows available locally, plus missing-title stubs.</returns>
    [HttpGet("Trending/Shows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoverRankedResult>> GetTrendingShows(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        // Snapshot sources are movie lists only; shows are served by TMDb alone.
        if (!resolvedSource.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase)
            || !IsSupportedSource(resolvedSource, SourceListKind.Trending))
        {
            return Ok(new DiscoverRankedResult { Source = resolvedSource });
        }

        var timeWindow = TimeWindow.Week;
        var titles = await _discoverClient
            .GetTrendingSeriesTitlesAsync(timeWindow, TmdbDiscoverClient.PagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return BuildRankedResult(resolvedSource, titles, BaseItemKind.Series, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TrendingMaxRank);
    }

    /// <summary>
    /// Gets the highest rated movies of all time that are present in the library, plus stubs.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="source">Optional. The ranked data source; only "tmdb" is implemented.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Top rated movies returned.</response>
    /// <returns>The top rated movies available locally, plus missing-title stubs.</returns>
    [HttpGet("TopRated/Movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoverRankedResult>> GetTopRatedMovies(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        if (!IsSupportedSource(resolvedSource, SourceListKind.TopRated))
        {
            return Ok(new DiscoverRankedResult { Source = resolvedSource });
        }

        if (resolvedSource.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase))
        {
            var titles = await _discoverClient
                .GetTopRatedMovieTitlesAsync(TmdbDiscoverClient.PagesToScan, cancellationToken)
                .ConfigureAwait(false);

            return BuildRankedResult(resolvedSource, titles, BaseItemKind.Movie, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TopRatedMaxRank);
        }

        return BuildSnapshotRankedResult(resolvedSource, SourceListKind.TopRated, BaseItemKind.Movie, userId, parentId, ParseFields(fields), limit);
    }

    /// <summary>
    /// Gets the highest rated shows of all time that are present in the library, plus stubs.
    /// </summary>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library.</param>
    /// <param name="fields">Optional. Comma delimited list of fields to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="source">Optional. The ranked data source; only "tmdb" is implemented.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Top rated shows returned.</response>
    /// <returns>The top rated shows available locally, plus missing-title stubs.</returns>
    [HttpGet("TopRated/Shows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoverRankedResult>> GetTopRatedShows(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? fields,
        [FromQuery] int limit = 24,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        // Snapshot sources are movie lists only; shows are served by TMDb alone.
        if (!resolvedSource.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase)
            || !IsSupportedSource(resolvedSource, SourceListKind.TopRated))
        {
            return Ok(new DiscoverRankedResult { Source = resolvedSource });
        }

        var titles = await _discoverClient
            .GetTopRatedSeriesTitlesAsync(TmdbDiscoverClient.PagesToScan, cancellationToken)
            .ConfigureAwait(false);

        return BuildRankedResult(resolvedSource, titles, BaseItemKind.Series, userId, parentId, ParseFields(fields), limit, TmdbDiscoverClient.TopRatedMaxRank);
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
    /// Gets the stored snapshot lists, in full, with their last-refresh time.
    /// </summary>
    /// <remarks>
    /// Backs the admin settings page that shows each source's current list. The lists are returned
    /// in rank order, exactly as fetched, without any library matching.
    /// </remarks>
    /// <response code="200">The stored source lists returned.</response>
    /// <returns>The stored source lists.</returns>
    [HttpGet("SourceLists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<SourceListSummaryDto>> GetSourceLists()
    {
        var lists = _sourceListStore.GetAllLists();
        var result = lists
            .Select(list => new SourceListSummaryDto
            {
                Source = list.Source,
                Kind = list.Kind == SourceListKind.Trending ? "trending" : "toprated",
                Title = list.Title,
                LastRefreshedUtc = list.LastRefreshedUtc,
                Items = list.Items
                    .OrderBy(item => item.Rank)
                    .Select(item => new SourceListTitleDto { Rank = item.Rank, Title = item.Title, Year = item.Year })
                    .ToList()
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Serves a cached poster image.
    /// </summary>
    /// <param name="key">The poster key from the stored URL.</param>
    /// <response code="200">The poster image returned.</response>
    /// <response code="404">The poster is not cached.</response>
    /// <returns>The poster image.</returns>
    [HttpGet("Posters/{key}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPoster([FromRoute] string key)
    {
        var path = _posterStore.GetFilePath(key);
        if (path is null)
        {
            return NotFound();
        }

        _posterStore.Touch(key);
        return PhysicalFile(path, "image/jpeg");
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
    /// Resolves the requested source name to a normalized value, defaulting to "tmdb".
    /// </summary>
    private static string ResolveSource(string? source)
    {
        var value = source?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? DefaultSource : value;
    }

    /// <summary>
    /// Returns whether a source serves the given list kind. TMDb is available when a key is
    /// configured; every other source is available when its matching list is enabled.
    /// </summary>
    private static bool IsSupportedSource(string source, SourceListKind kind)
    {
        if (source.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase))
        {
            return TmdbDiscoverClient.HasApiKey;
        }

        var kindKey = kind == SourceListKind.Trending ? "trending" : "toprated";
        return Plugin.Instance?.Configuration.Sources
            .Any(s => s.Enabled
                && s.Key.Equals(source, StringComparison.OrdinalIgnoreCase)
                && s.Kind.Equals(kindKey, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool ShowMissing => Plugin.Instance?.Configuration.ShowMissing ?? true;

    private static int MaxMissing => Math.Max(0, Plugin.Instance?.Configuration.MaxMissing ?? 10);

    /// <summary>
    /// Resolves a set of ranked titles to a discover result: the local library items in source
    /// order, plus missing-title stubs for titles not owned locally.
    /// </summary>
    /// <remarks>
    /// <paramref name="maxRank"/> is the lowest 1-based source position to include; 0 means no
    /// cutoff. Stubs honour the same cutoff and the global missing cap, and are never written to
    /// the library.
    /// </remarks>
    private DiscoverRankedResult BuildRankedResult(
        string source,
        IReadOnlyList<TmdbRankedTitle> titles,
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

        if (titles.Count == 0)
        {
            return new DiscoverRankedResult { Source = source };
        }

        // Position in the source's response is the ranking, and it is what the caller expects.
        var rankByTmdbId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (position, title) in titles.Index())
        {
            rankByTmdbId.TryAdd(title.Id.ToString(CultureInfo.InvariantCulture), position);
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

        var matched = items
            .Select(item => (Item: item, Rank: GetRank(item, rankByTmdbId)))
            .Where(entry => entry.Rank >= 0)
            .ToArray();

        // Every library item that matches the source is "owned", regardless of the rank cutoff
        // or the item limit, so a stub is only ever emitted for a title genuinely absent.
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, _) in matched)
        {
            if (item.TryGetProviderId(MetadataProviders.Tmdb, out var tmdbId))
            {
                owned.Add(tmdbId);
            }
        }

        var ranked = matched
            .Where(entry => maxRank <= 0 || entry.Rank < maxRank)
            .OrderBy(entry => entry.Rank)
            .Take(limit)
            .ToArray();

        var dtos = _dtoService.GetBaseItemDtos(Array.ConvertAll(ranked, entry => entry.Item), dtoOptions, user);

        // Surface each item's position in the source list rather than its position among the
        // items that happened to match. IndexNumber is unused for movies and series, so carrying
        // it there keeps the response a plain list the client already understands.
        for (var i = 0; i < dtos.Count && i < ranked.Length; i++)
        {
            dtos[i].IndexNumber = ranked[i].Rank + 1;
        }

        return new DiscoverRankedResult
        {
            Source = source,
            Items = dtos.ToList(),
            Missing = BuildMissingTitles(source, titles, owned, rankByTmdbId, maxRank)
        };
    }

    /// <summary>
    /// Builds the missing-title stubs: source-ranked titles not owned locally, under the global
    /// cap and the same rank cutoff applied to local items.
    /// </summary>
    private static List<MissingTitleDto> BuildMissingTitles(
        string source,
        IReadOnlyList<TmdbRankedTitle> titles,
        HashSet<string> owned,
        Dictionary<string, int> rankByTmdbId,
        int maxRank)
    {
        if (!ShowMissing || MaxMissing <= 0)
        {
            return new List<MissingTitleDto>();
        }

        var missing = new List<MissingTitleDto>(MaxMissing);
        foreach (var title in titles)
        {
            if (missing.Count >= MaxMissing)
            {
                break;
            }

            var id = title.Id.ToString(CultureInfo.InvariantCulture);
            if (owned.Contains(id))
            {
                continue;
            }

            if (!rankByTmdbId.TryGetValue(id, out var rank) || (maxRank > 0 && rank >= maxRank))
            {
                continue;
            }

            missing.Add(new MissingTitleDto
            {
                Source = source,
                Rank = rank + 1,
                Title = title.Title ?? string.Empty,
                Year = title.Year,
                ProviderIds = new Dictionary<string, string> { { MetadataProviders.Tmdb.ToString(), id } },
                PosterUrl = TmdbDiscoverClient.BuildPosterUrl(title.PosterPath) ?? string.Empty
            });
        }

        return missing;
    }

    /// <summary>
    /// Builds a ranked result for a snapshot source, matching the stored titles against the library
    /// by normalized name and year.
    /// </summary>
    private DiscoverRankedResult BuildSnapshotRankedResult(
        string source,
        SourceListKind kind,
        BaseItemKind itemKind,
        Guid? userId,
        Guid? parentId,
        ItemFields[] fields,
        int limit)
    {
        var effectiveUserId = ResolveUserId(userId);
        var user = effectiveUserId.IsEmpty() ? null : _userManager.GetUserById(effectiveUserId);
        var dtoOptions = new DtoOptions { Fields = fields };

        var titles = _sourceListStore.GetList(source, kind);
        if (titles.Count == 0)
        {
            return new DiscoverRankedResult { Source = source };
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [itemKind],
            Recursive = true
        };

        if (parentId.HasValue && !parentId.Value.IsEmpty())
        {
            query.ParentId = parentId.Value;
        }

        var items = _libraryManager.GetItemList(query);

        // Group library items by normalized name so each stored title can be looked up once.
        var byName = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = NormalizeTitle(item.Name);
            if (!byName.TryGetValue(key, out var list))
            {
                byName[key] = list = new List<BaseItem>();
            }

            list.Add(item);
        }

        var used = new HashSet<Guid>();
        var ownedRanks = new HashSet<int>();
        var matched = new List<(BaseItem Item, int Rank)>();

        foreach (var title in titles)
        {
            if (!byName.TryGetValue(NormalizeTitle(title.Title), out var candidates))
            {
                continue;
            }

            BaseItem? best = null;
            if (title.Year is not null)
            {
                best = candidates.FirstOrDefault(c => !used.Contains(c.Id) && c.ProductionYear == title.Year)
                    ?? candidates.FirstOrDefault(c => !used.Contains(c.Id)
                        && c.ProductionYear is not null
                        && Math.Abs(c.ProductionYear.Value - title.Year.Value) <= 1);
            }

            best ??= candidates.FirstOrDefault(c => !used.Contains(c.Id));

            if (best is null)
            {
                continue;
            }

            used.Add(best.Id);
            ownedRanks.Add(title.Rank);
            matched.Add((best, title.Rank));
        }

        var ranked = matched
            .OrderBy(entry => entry.Rank)
            .Take(limit)
            .ToArray();

        var dtos = _dtoService.GetBaseItemDtos(Array.ConvertAll(ranked, entry => entry.Item), dtoOptions, user);

        for (var i = 0; i < dtos.Count && i < ranked.Length; i++)
        {
            dtos[i].IndexNumber = ranked[i].Rank;
        }

        return new DiscoverRankedResult
        {
            Source = source,
            Items = dtos.ToList(),
            Missing = BuildSnapshotMissingTitles(source, titles, ownedRanks)
        };
    }

    /// <summary>
    /// Builds the missing-title stubs for a snapshot source: stored titles that did not match a
    /// library item, under the global cap.
    /// </summary>
    private static List<MissingTitleDto> BuildSnapshotMissingTitles(
        string source,
        IReadOnlyList<SourceListItem> titles,
        HashSet<int> ownedRanks)
    {
        if (!ShowMissing || MaxMissing <= 0)
        {
            return new List<MissingTitleDto>();
        }

        var missing = new List<MissingTitleDto>(MaxMissing);
        foreach (var title in titles)
        {
            if (missing.Count >= MaxMissing)
            {
                break;
            }

            if (ownedRanks.Contains(title.Rank))
            {
                continue;
            }

            missing.Add(new MissingTitleDto
            {
                Source = source,
                Rank = title.Rank,
                Title = title.Title,
                Year = title.Year,
                PosterUrl = title.PosterUrl ?? string.Empty
            });
        }

        return missing;
    }

    /// <summary>
    /// Normalizes a title for name matching: lowercase, alphanumeric only.
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
    /// Gets per-value item counts for a browse-mode picker, cached for 24 hours.
    /// </summary>
    /// <param name="type">The picker: genre, rating, tag, decade, or studio.</param>
    /// <param name="parentId">Optional. Localize the count to a specific library.</param>
    /// <param name="itemTypes">Optional. Comma delimited item types; defaults to Movie and Series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">A map of value to item count.</response>
    /// <returns>Per-value item counts.</returns>
    [HttpGet("Counts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, int>> GetCounts(
        [FromQuery] string type,
        [FromQuery] Guid? parentId,
        [FromQuery] string? itemTypes,
        CancellationToken cancellationToken = default)
    {
        var kinds = ParseItemKinds(itemTypes);
        return Ok(_discoverClient.GetOrBuildCounts(type, parentId, kinds, _libraryManager));
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

    /// <summary>
    /// Parses the comma delimited itemTypes query parameter, defaulting to Movie and Series.
    /// </summary>
    private static BaseItemKind[] ParseItemKinds(string? itemTypes)
    {
        if (string.IsNullOrWhiteSpace(itemTypes))
        {
            return [BaseItemKind.Movie, BaseItemKind.Series];
        }

        var kinds = new List<BaseItemKind>();
        foreach (var value in itemTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<BaseItemKind>(value, true, out var kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds.ToArray();
    }
}
