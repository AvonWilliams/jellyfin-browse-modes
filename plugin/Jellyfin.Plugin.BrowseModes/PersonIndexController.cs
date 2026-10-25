using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Plugin.BrowseModes.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Serves the People categories (actors, directors, writers) of a library one page at a time.
/// </summary>
/// <remarks>
/// Pages come from the people index when it is built and the caller may see the whole library.
/// Otherwise they are computed from the library as the caller sees it. See
/// <see cref="CanUseIndex"/> for the rule.
/// </remarks>
[ApiController]
[Authorize]
[Route("Discover")]
[Produces(MediaTypeNames.Application.Json)]
public class PersonIndexController : ControllerBase
{
    private const string UserIdClaimType = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";
    private const int MaxLimit = 500;
    private const int MaxPrefixLength = 32;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PersonIndexStore _store;
    private readonly PersonIndexBuilder _builder;
    private readonly LivePeopleCache _liveCache;
    private readonly ILogger<PersonIndexController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonIndexController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="store">Instance of <see cref="PersonIndexStore"/>.</param>
    /// <param name="builder">Instance of <see cref="PersonIndexBuilder"/>.</param>
    /// <param name="liveCache">Instance of <see cref="LivePeopleCache"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PersonIndexController}"/> interface.</param>
    public PersonIndexController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        PersonIndexStore store,
        PersonIndexBuilder builder,
        LivePeopleCache liveCache,
        ILogger<PersonIndexController> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _store = store;
        _builder = builder;
        _liveCache = liveCache;
        _logger = logger;
    }

    /// <summary>
    /// Gets one page of a library's people of one kind, sorted and optionally filtered by name.
    /// </summary>
    /// <param name="parentId">The library. Without it the people of all movie and series libraries are computed live.</param>
    /// <param name="kind">Actor, Director or Writer.</param>
    /// <param name="sort">az, za, most (most titles first) or fewest.</param>
    /// <param name="startIndex">How many people to skip.</param>
    /// <param name="limit">The page size, at most 500.</param>
    /// <param name="nameStartsWith">Optional. Only names starting with this text; "#" for names not starting with A to Z.</param>
    /// <param name="includeLetters">Optional. Also return the people count per starting letter, for a jump bar.</param>
    /// <param name="userId">Optional. Administrators may ask on behalf of another user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The page of people.</response>
    /// <response code="400">A parameter is invalid.</response>
    /// <returns>The page.</returns>
    [HttpGet("People")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PersonIndexPageDto>> GetPeople(
        [FromQuery] Guid? parentId,
        [FromQuery] string? kind,
        [FromQuery] string? sort = "az",
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 100,
        [FromQuery] string? nameStartsWith = null,
        [FromQuery] bool includeLetters = false,
        [FromQuery] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<IndexedPersonKind>(kind, true, out var personKind) || !Enum.IsDefined(personKind))
        {
            return BadRequest("kind must be Actor, Director or Writer.");
        }

        if (!TryParseSort(sort, out var order))
        {
            return BadRequest("sort must be az, za, most or fewest.");
        }

        if (startIndex < 0 || limit < 1)
        {
            return BadRequest("startIndex must be 0 or more and limit at least 1.");
        }

        limit = Math.Min(limit, MaxLimit);
        var prefix = CleanPrefix(nameStartsWith);

        var user = ResolveUser(userId);
        var library = parentId.HasValue && !parentId.Value.IsEmpty() ? parentId : null;

        // A library the caller may not open has no people for them. The item queries below do not
        // refuse a parent the user cannot access, so this must be checked here.
        if (library.HasValue && user is not null && !CanSeeLibrary(user, library.Value))
        {
            return Ok(new PersonIndexPageDto
            {
                StartIndex = startIndex,
                Letters = includeLetters ? new Dictionary<string, int>() : null,
                Source = "live"
            });
        }

        if (library.HasValue && CanUseIndex(user, library.Value))
        {
            var page = TryReadIndex(library.Value, personKind, order, startIndex, limit, prefix, includeLetters);
            if (page is not null)
            {
                return Ok(page);
            }

            // Not built yet (or the database is unavailable): answer live now, build for next time.
            _builder.RequestBuild();
        }

        var live = await GetLiveAsync(user, library, cancellationToken).ConfigureAwait(false);
        var (total, items) = live.Page(personKind, order, startIndex, limit, prefix);
        return Ok(new PersonIndexPageDto
        {
            Total = total,
            StartIndex = startIndex,
            Items = items.Select(ToDto).ToList(),
            Letters = includeLetters ? OrderLetters(live.Letters(personKind)) : null,
            Source = "live"
        });
    }

    /// <summary>
    /// Decides whether the stored index, which is built from the whole library, may answer for
    /// this caller.
    /// </summary>
    /// <remarks>
    /// The index may be used only when nothing is hidden from the caller: no user (an API key), or
    /// a user with no parental-rating limit, no blocked or allowed tags, no unrated-content block
    /// and access to the requested library. Anyone else gets the live path, which collects people
    /// and counts from only the items that user can see, so a name or a count never reveals a title
    /// they cannot open. The check errs towards the live path: an unknown library is treated as
    /// restricted, and so is a user flagged by the server as having content restrictions.
    /// </remarks>
    private bool CanUseIndex(User? user, Guid libraryId)
    {
        if (user is null)
        {
            return true;
        }

        if (new InternalItemsQuery(user).UserHasContentRestrictions
            || user.MaxParentalRatingScore is not null
            || user.MaxParentalRatingSubScore is not null)
        {
            return false;
        }

        return CanSeeLibrary(user, libraryId);
    }

    private bool CanSeeLibrary(User user, Guid libraryId)
    {
        var library = _libraryManager.GetItemById(libraryId);
        return library is not null && library.IsVisible(user);
    }

    private PersonIndexPageDto? TryReadIndex(
        Guid libraryId,
        IndexedPersonKind kind,
        PersonIndexSort sort,
        int startIndex,
        int limit,
        string? prefix,
        bool includeLetters)
    {
        try
        {
            if (_store.GetMeta(libraryId) is null)
            {
                return null;
            }

            var (total, items) = _store.QueryPage(libraryId, kind, sort, startIndex, limit, prefix);
            return new PersonIndexPageDto
            {
                Total = total,
                StartIndex = startIndex,
                Items = items.Select(ToDto).ToList(),
                Letters = includeLetters ? OrderLetters(_store.GetLetters(libraryId, kind)) : null,
                Source = "index"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The people index could not be read; using the live path");
            return null;
        }
    }

    private async Task<LivePeople> GetLiveAsync(User? user, Guid? libraryId, CancellationToken cancellationToken)
    {
        BaseItemKind[] itemTypes = [BaseItemKind.Movie, BaseItemKind.Series];
        if (libraryId.HasValue)
        {
            var folder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var id) && id == libraryId.Value);
            itemTypes = PersonIndexBuilder.ItemTypesFor(folder?.CollectionType) ?? itemTypes;
        }

        // Someone who sees the whole library shares one cache entry with everyone else who does.
        var key = $"{(user is null ? "all" : user.Id.ToString("N"))}:{libraryId?.ToString("N") ?? "none"}:{string.Join(',', itemTypes)}";
        var tags = new Dictionary<Guid, string?>();
        return await _liveCache
            .GetOrCollectAsync(key, () => _builder.Collect(libraryId, itemTypes, user, tags, CancellationToken.None))
            .ConfigureAwait(false);
    }

    private User? ResolveUser(Guid? requestedUserId)
    {
        var claimValue = User.FindFirstValue(UserIdClaimType);
        var authenticated = string.IsNullOrEmpty(claimValue) ? default : Guid.Parse(claimValue);
        var effective = requestedUserId.IsNullOrEmpty() || (!requestedUserId.Value.Equals(authenticated) && !User.IsInRole(AdministratorRole))
            ? authenticated
            : requestedUserId.Value;

        return effective.IsEmpty() ? null : _userManager.GetUserById(effective);
    }

    private static bool TryParseSort(string? sort, out PersonIndexSort order)
    {
        switch ((sort ?? "az").Trim().ToLowerInvariant())
        {
            case "az":
                order = PersonIndexSort.NameAscending;
                return true;
            case "za":
                order = PersonIndexSort.NameDescending;
                return true;
            case "most":
                order = PersonIndexSort.MostTitles;
                return true;
            case "fewest":
                order = PersonIndexSort.FewestTitles;
                return true;
            default:
                order = PersonIndexSort.NameAscending;
                return false;
        }
    }

    private static string? CleanPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value.Trim().Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, MaxPrefixLength)];
    }

    private static Dictionary<string, int> OrderLetters(Dictionary<string, int> letters)
    {
        var ordered = new Dictionary<string, int>();
        foreach (var letter in letters.Keys.Where(k => k != "#").OrderBy(k => k, StringComparer.Ordinal))
        {
            ordered[letter] = letters[letter];
        }

        if (letters.TryGetValue("#", out var other))
        {
            ordered["#"] = other;
        }

        return ordered;
    }

    private static PersonIndexItemDto ToDto(PersonIndexEntry entry) => new()
    {
        Id = entry.PersonId,
        Name = entry.Name,
        Count = entry.TitleCount,
        ImageTag = entry.ImageTag
    };
}
