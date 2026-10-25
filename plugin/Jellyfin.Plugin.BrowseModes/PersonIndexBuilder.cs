using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Plugin.BrowseModes.Data;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Works out who is credited in a library and how many titles each person appears in, and keeps
/// the people index in <see cref="PersonIndexStore"/> up to date.
/// </summary>
/// <remarks>
/// The same collection code serves the index build (no user, the whole library) and the live
/// fallback for users who cannot see the whole library (their own item set), so the two can only
/// differ by the items they are given.
/// </remarks>
public sealed class PersonIndexBuilder
{
    /// <summary>
    /// How long a built index is trusted without a rebuild when nothing is known to have changed.
    /// </summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(12);

    private static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly PersonIndexStore _store;
    private readonly TmdbDiscoverClient _discoverClient;
    private readonly ILogger<PersonIndexBuilder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _changeVersion;
    private int _requested;
    private long _lastRequestEndTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonIndexBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="imageProcessor">Instance of the <see cref="IImageProcessor"/> interface.</param>
    /// <param name="store">Instance of <see cref="PersonIndexStore"/>.</param>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PersonIndexBuilder}"/> interface.</param>
    public PersonIndexBuilder(
        ILibraryManager libraryManager,
        IImageProcessor imageProcessor,
        PersonIndexStore store,
        TmdbDiscoverClient discoverClient,
        ILogger<PersonIndexBuilder> logger)
    {
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _store = store;
        _discoverClient = discoverClient;
        _logger = logger;
    }

    /// <summary>
    /// Gets the item types a library of the given collection type is indexed as, or null when the
    /// library holds no movies or series.
    /// </summary>
    /// <param name="collectionType">The library's collection type.</param>
    /// <returns>The item types, or null.</returns>
    public static BaseItemKind[]? ItemTypesFor(CollectionTypeOptions? collectionType) => collectionType switch
    {
        CollectionTypeOptions.movies => [BaseItemKind.Movie],
        CollectionTypeOptions.tvshows => [BaseItemKind.Series],
        _ => null
    };

    /// <summary>
    /// Records that something in the library changed, so a build already under way does not clear
    /// the dirty flag for changes it may have missed.
    /// </summary>
    public void NotifyChange() => Interlocked.Increment(ref _changeVersion);

    /// <summary>
    /// Collects every credited person in a library with the number of titles they appear in.
    /// </summary>
    /// <param name="libraryId">The library, or null for all libraries.</param>
    /// <param name="itemTypes">The title types counted (movies or series).</param>
    /// <param name="user">The user whose view to collect, or null for the whole library.</param>
    /// <param name="tagCache">Image tags already looked up, shared across libraries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One row per person per credit kind they hold.</returns>
    public List<PersonIndexRow> Collect(
        Guid? libraryId,
        BaseItemKind[] itemTypes,
        User? user,
        Dictionary<Guid, string?> tagCache,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = itemTypes,
            Recursive = true
        };

        if (libraryId.HasValue && !libraryId.Value.IsEmpty())
        {
            query.ParentId = libraryId.Value;
        }

        var items = _libraryManager.GetItemList(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Credits on seasons and episodes roll up to their series, same as /Discover/Counts.
        var known = new HashSet<Guid>(items.Count);
        foreach (var item in items)
        {
            known.Add(item.Id);
        }

        var creditsByTitle = TmdbDiscoverClient.GetPeopleAcrossItems(items, libraryId, _libraryManager, user);
        cancellationToken.ThrowIfCancellationRequested();

        var people = new Dictionary<Guid, PersonAggregate>();
        var seen = new HashSet<Guid>();
        foreach (var (titleId, credits) in creditsByTitle)
        {
            if (!known.Contains(titleId))
            {
                continue;
            }

            // A title counts once per person however many credits (kinds, episodes) they hold on it.
            seen.Clear();
            foreach (var credit in credits)
            {
                if (string.IsNullOrWhiteSpace(credit.Name))
                {
                    continue;
                }

                // The id of the person's own item (what a card links to), not the credit's id.
                var personId = _libraryManager.GetPersonId(credit.Name.Trim());
                if (!people.TryGetValue(personId, out var aggregate))
                {
                    aggregate = new PersonAggregate(credit.Name.Trim());
                    people[personId] = aggregate;
                }

                if (seen.Add(personId))
                {
                    aggregate.TitleCount++;
                }

                switch (credit.Type)
                {
                    case PersonKind.Actor:
                        aggregate.Kinds |= 1;
                        break;
                    case PersonKind.Director:
                        aggregate.Kinds |= 2;
                        break;
                    case PersonKind.Writer:
                        aggregate.Kinds |= 4;
                        break;
                }
            }
        }

        FillImageTags(people.Keys, tagCache, cancellationToken);

        var rows = new List<PersonIndexRow>(people.Count + (people.Count / 2));
        foreach (var (personId, aggregate) in people)
        {
            // A credit without a person item (not created yet, or removed) has nothing to open.
            if (!tagCache.TryGetValue(personId, out var tag))
            {
                continue;
            }

            var entry = new PersonIndexEntry(personId, aggregate.Name, aggregate.TitleCount, tag);
            if ((aggregate.Kinds & 1) != 0)
            {
                rows.Add(new PersonIndexRow(IndexedPersonKind.Actor, entry));
            }

            if ((aggregate.Kinds & 2) != 0)
            {
                rows.Add(new PersonIndexRow(IndexedPersonKind.Director, entry));
            }

            if ((aggregate.Kinds & 4) != 0)
            {
                rows.Add(new PersonIndexRow(IndexedPersonKind.Writer, entry));
            }
        }

        return rows;
    }

    /// <summary>
    /// Builds, or refreshes, the index of every movie and series library. Libraries whose index is
    /// current (built recently, not changed, same item count) are left alone unless forced.
    /// </summary>
    /// <param name="force">Whether to rebuild even current indexes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the build has finished.</returns>
    public async Task BuildAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => BuildCore(force, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a background build if none is under way, for a request that found no index. Calls
    /// that arrive while one runs, or shortly after one ended, are ignored.
    /// </summary>
    public void RequestBuild()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRequestEndTicks) < RequestCooldown.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _requested, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await BuildAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background people index build failed");
            }
            finally
            {
                Interlocked.Exchange(ref _lastRequestEndTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _requested, 0);
            }
        });
    }

    private void BuildCore(bool force, CancellationToken cancellationToken)
    {
        var folders = _libraryManager.GetVirtualFolders();
        var existing = new List<Guid>();
        var tagCache = new Dictionary<Guid, string?>();
        var total = Stopwatch.StartNew();

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Guid.TryParse(folder.ItemId, out var libraryId) || libraryId == Guid.Empty)
            {
                continue;
            }

            existing.Add(libraryId);
            var itemTypes = ItemTypesFor(folder.CollectionType);
            if (itemTypes is null)
            {
                continue;
            }

            try
            {
                BuildLibrary(libraryId, folder.Name, itemTypes, force, tagCache, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to build the people index for library {Library}", folder.Name);
            }
        }

        try
        {
            _store.RemoveLibrariesExcept(existing);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to remove the people index of deleted libraries");
        }

        _logger.LogInformation("People index pass finished in {Elapsed} ms", total.ElapsedMilliseconds);
    }

    private void BuildLibrary(
        Guid libraryId,
        string libraryName,
        BaseItemKind[] itemTypes,
        bool force,
        Dictionary<Guid, string?> tagCache,
        CancellationToken cancellationToken)
    {
        var versionBefore = Interlocked.Read(ref _changeVersion);
        var fingerprint = Fingerprint(libraryId, itemTypes);
        var meta = _store.GetMeta(libraryId);
        if (!force
            && meta is not null
            && !meta.Dirty
            && meta.Fingerprint == fingerprint
            && DateTime.UtcNow - meta.BuiltUtc < FreshFor)
        {
            // Nothing changed: keep the index, but make sure the name-keyed count caches
            // (lost on restart) are warm again.
            WarmCounts(libraryId, itemTypes);
            return;
        }

        var watch = Stopwatch.StartNew();
        var rows = Collect(libraryId, itemTypes, null, tagCache, cancellationToken);
        var collected = watch.ElapsedMilliseconds;
        var distinct = rows.Select(r => r.Entry.PersonId).Distinct().Count();

        _store.ReplaceLibrary(libraryId, rows, fingerprint, distinct);

        // Changes that arrived while this library was being read may not be in the rows.
        if (Interlocked.Read(ref _changeVersion) != versionBefore)
        {
            _store.MarkAllDirty();
        }

        WarmCounts(libraryId, itemTypes);
        _logger.LogInformation(
            "Built the people index for {Library}: {People} people, {Rows} rows (read {Read} ms, total {Total} ms)",
            libraryName,
            distinct,
            rows.Count,
            collected,
            watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// A cheap summary of the library's size, to notice changes made while the server was off.
    /// </summary>
    private string Fingerprint(Guid libraryId, BaseItemKind[] itemTypes)
    {
        var query = new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IncludeItemTypes = [.. itemTypes, BaseItemKind.Season, BaseItemKind.Episode],
            Limit = 1,
            EnableTotalRecordCount = true
        };

        return _libraryManager.GetItemsResult(query).TotalRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void WarmCounts(Guid libraryId, BaseItemKind[] itemTypes)
    {
        var byKind = new Dictionary<PersonKind, IReadOnlyList<(string Name, int TitleCount)>>
        {
            [PersonKind.Actor] = _store.GetNamesAndCounts(libraryId, IndexedPersonKind.Actor),
            [PersonKind.Director] = _store.GetNamesAndCounts(libraryId, IndexedPersonKind.Director),
            [PersonKind.Writer] = _store.GetNamesAndCounts(libraryId, IndexedPersonKind.Writer)
        };

        _discoverClient.CachePersonCounts(libraryId, itemTypes, byKind);
    }

    /// <summary>
    /// Looks up the primary image tag of every person not seen yet, in chunks.
    /// </summary>
    private void FillImageTags(IEnumerable<Guid> personIds, Dictionary<Guid, string?> tagCache, CancellationToken cancellationToken)
    {
        var missing = personIds.Where(id => !tagCache.ContainsKey(id)).ToList();
        foreach (var chunk in missing.Chunk(1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var persons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Person],
                ItemIds = chunk
            });

            foreach (var person in persons)
            {
                var image = person.GetImageInfo(ImageType.Primary, 0);
                tagCache[person.Id] = image is null ? null : _imageProcessor.GetImageCacheTag(person, image);
            }
        }
    }

    private sealed class PersonAggregate(string name)
    {
        public string Name { get; } = name;

        public int TitleCount { get; set; }

        public int Kinds { get; set; }
    }
}
