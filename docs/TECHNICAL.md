# Browse Modes — Technical Reference

Everything in this project that differs from stock Jellyfin, and why. Written for whoever picks
this up next, human or model. Assume no memory of how it was built.

> **Related documents**
> - [USER-GUIDE.md](./USER-GUIDE.md) — plain-language description of the feature and step-by-step
>   install instructions.
> - [README](../README.md) — one-page overview and install summary.
>
> **Upgrading to a newer Jellyfin release? Start at [§7](#7-applying-this-to-a-new-release).**

---

## 1. Shape of the system

One plugin and two client forks. **The server itself is stock** — there is no forked server.

```
plugin/                Server plugin, C#.  Custom /Discover API. Serves every client.
jellyfin-web/          Web client, React.  Browser, desktop, and the Android PHONE app.
jellyfin-androidtv/    TV client, Kotlin.  Android TV / Google TV only.
```

### Why the server is a plugin but the UI is not

The split is not a style choice; it is what Jellyfin's extension points allow.

**The server half can be a plugin.** Everything `DiscoverController` needs — `IUserManager`,
`ILibraryManager`, `IDtoService` — lives in the published `Jellyfin.Controller` package.
`IScheduledTask` is in `Jellyfin.Model` and plugin tasks are discovered automatically
(`ApplicationHost.cs:424` calls `AddTasks(GetExports<IScheduledTask>())` over all loaded
assemblies). Plugin controllers get routed because `ApiServiceCollectionExtensions.cs:158-161`
calls `mvcBuilder.AddApplicationPart(pluginAssembly)` for each one.

**The UI half cannot be, and no amount of effort changes that.** Verified exhaustively:

| Vector | Verdict | Evidence |
|---|---|---|
| Plugin page in the user app | No | `IHasWebPages` pages render only via `ServerContentPage`, used once at `apps/dashboard/routes/routes.tsx:49`, under `ConnectionRequired level='admin'` |
| Inject JavaScript | No | `BrandingOptions` has `CustomCss` and no JS field; CSS lands in a React `<style>` child, so no breakout |
| Register a route at runtime | No | `RootAppRouter.tsx:22` builds the router once from statically-imported arrays; no `patchRoutesOnNavigation` |
| Client plugin loading an external URL | No | `pluginManager.js:97` is a webpack context import rooted at `src/plugins/`, resolved at build time; `PluginType` has no page/route member |
| Server rewrites `index.html` | No | Raw `PhysicalFileProvider` passthrough (`Startup.cs:212`); only a `Cache-Control` header is set |
| Plugin static files shadow the bundle | No | `UseStaticFiles` precedes `UseRouting`, so disk always wins |

The only remaining route is a plugin writing directly into the web directory with raw file I/O —
unsupported, and destroyed by every web-client update. Not a foundation to build on.

**So the tile page requires a `jellyfin-web` fork, permanently.**

### What each client needs

| Client | Needs |
|---|---|
| Browser / desktop | stock server + plugin + forked web bundle |
| Jellyfin **phone** app | same (it is a WebView over the bundle) |
| Jellyfin **TV** app | stock server + plugin + APK — the web fork is irrelevant to it |

Only **Trending** and **Top Rated** actually call the plugin. Every other tile is a plain sort or
filter that stock Jellyfin already answers, so an APK without the plugin still works —
`DiscoverFragment` catches the failed fetch and shows its empty-state message.

### Why there are two client implementations

`jellyfin-web` is a bundle the server hosts from `AppContext.BaseDirectory/jellyfin-web`
(overridable with `--webdir`). The Android **phone** app (`Jellyfin Android`, app id
`org.jellyfin.mobile`) is a WebView over that bundle, so it inherits web changes with no work.

The Android **TV** app (`Jellyfin Android TV`, app id `org.jellyfin.androidtv`) is native
Leanback/Compose. It talks only to the REST API. **No change to `jellyfin-web` can ever appear on
it.** That is the single most important fact here; it was the original bug report ("works on
mobile and web, not on the TV client").

A related trap: even a *web-based* TV client would not have shown the tiles. `LayoutMode.Tv` is
listed in `LegacyLayoutModes` (`src/constants/layoutMode.ts`), so `layoutManager.modern` is false
and `RootAppRouter.tsx:26` serves `LEGACY_APP_ROUTES`, which has no `browse` route. The
`shouldShowBrowseModes` guard checks `layoutManager.modern` first, so it fails closed rather than
routing to a dead page.

### Identifying which client a device is

The server records every client in `Devices` in `jellyfin.db`:

```sql
SELECT AppName, AppVersion, DeviceName, DateLastActivity FROM Devices
ORDER BY DateLastActivity DESC;
```

`Jellyfin Web` = the bundle. `Jellyfin Android` = phone WebView (also the bundle).
`Jellyfin Android TV` = native, needs the APK.

---

## 2. The plugin (`plugin/Jellyfin.Plugin.BrowseModes/`)

Replaces what was originally a forked server. Installs into stock Jellyfin and survives Jellyfin
updates.

### 2.1 Files

| File | Role |
|---|---|
| `Plugin.cs` | `BasePlugin<PluginConfiguration>` + `IHasWebPages`. GUID `0d5f6a1e-3b7c-4c62-9a4d-2e8f1b6c9a37` |
| `Configuration/PluginConfiguration.cs` + `config.html` | TMDb API key, pages to scan, cache duration |
| `TmdbDiscoverClient.cs` | Fetches and caches the four TMDb lists |
| `RefreshDiscoverListsTask.cs` | `IScheduledTask` that keeps them warm |
| `DiscoverController.cs` | The four `/Discover/*` endpoints |
| `PluginServiceRegistrator.cs` | Registers `TmdbDiscoverClient` as a singleton |

### 2.2 Endpoints

```
GET /Discover/Trending/Movies
GET /Discover/Trending/Shows
GET /Discover/TopRated/Movies
GET /Discover/TopRated/Shows
```

Query parameters: `userId`, `parentId`, `fields` (comma-delimited), `limit` (default 24),
`weekly` (default true, trending only). Returns `QueryResult<BaseItemDto>`.

Maps TMDb ids onto local items via `InternalItemsQuery.HasAnyProviderIds`, preserves TMDb's
ordering, and **writes each item's position in the TMDb list into `IndexNumber`** (1-based). That
is what both clients render as the rank badge.

> The number is the item's absolute position in TMDb's global ranking, not its position within
> your results. A badge reading `28` means "28th most trending film worldwide, and you own it".

### 2.3 Packaging — two non-obvious requirements

Both are in the `.csproj` and both are load-bearing:

1. **`CopyLocalLockFileAssemblies=true`.** A library build does not copy NuGet dependencies to its
   output, and a plugin is loaded from a folder rather than through `deps.json`. Without this the
   output is just `Jellyfin.Plugin.BrowseModes.dll` and TMDbLib is missing at runtime.
2. **`ExcludeAssets="runtime"` on every Jellyfin package.** With (1) enabled they would otherwise
   be copied into the plugin folder, and `PluginManager.cs:136` loads *every* DLL there into the
   plugin's own `AssemblyLoadContext` — producing duplicate type identities for server-provided
   types and failing casts.

Correct output is exactly three files: `Jellyfin.Plugin.BrowseModes.dll`, `TMDbLib.dll`,
`Newtonsoft.Json.dll`.

Package versions are `12.0.0-rc3` for all four Jellyfin packages, matching the server exactly.
**`MediaBrowser.Providers` and `Jellyfin.Api` are deliberately not referenced** — neither is
published to nuget.org, and referencing the shipped DLLs would reintroduce the TMDbLib type
identity problem.

### 2.4 Three Jellyfin.Api internals reimplemented

The original controller leaned on things plugins cannot reach:

| Original | Replacement |
|---|---|
| `BaseJellyfinApiController` | plain `ControllerBase` + explicit `[ApiController] [Route("Discover")] [Produces] [Authorize]`. The literal route keeps existing client paths working |
| `RequestHelpers.GetUserId` (`internal`) | local `ResolveUserId`, reading the `Jellyfin-UserId` claim with the same admin check |
| `CommaDelimitedCollectionModelBinder` | `fields` taken as `string?` and split by hand; unrecognised names ignored |

One deliberate behaviour change: the original `GetUserId` throws `SecurityException` when a
non-admin names another user. `ResolveUserId` falls back to the authenticated user instead — an
unhandled throw out of a plugin is worse than a degraded result.

### 2.5 TMDb client

`TmdbDiscoverClient` is a port of the four list-fetching methods that were added to the server's
`TmdbClientManager`. Pages are fetched **concurrently** (11.2s sequential → 1.6s parallel for a
15-page list) and consumed in page order, since ranking depends on order.

It owns a private `MemoryCache`. The in-tree `TmdbClientManager.Dispose` disposes the **shared**
container `IMemoryCache` — a latent upstream bug that was deliberately not copied.

`DiscoverPagesToScan` (default 15) and cache duration (default 6h) are plugin configuration
rather than constants. TMDb returns 20 results per page and most will not be in any given
library, so a wide net is cast.

### 2.6 TMDb API key

The server bundles a key for its own TMDb metadata provider, but that constant lives in
`MediaBrowser.Providers`, which the plugin cannot reference. **The user must supply their own key**
in plugin settings. Keys are free. Without one, `TmdbDiscoverClient` returns empty lists and the
scheduled task logs and skips — Trending and Top Rated come up empty, and nothing else is
affected.

### 2.7 Scheduled task

Appears in Dashboard → Scheduled Tasks as "Refresh TMDb discover lists". Two triggers:

- `StartupTrigger` — the cache is in-memory and does not survive a restart.
- `IntervalTrigger` every **4 hours** — deliberately shorter than the 6 hour cache expiry, so a
  live entry is replaced rather than lapsing and leaving a user waiting on a cold fetch.

No registration is needed; `ApplicationHost` discovers `IScheduledTask` implementations in plugin
assemblies automatically.

### 2.8 Gotcha inherited from the server

**Provider ids are not loaded unless asked for.** `BaseItemRepository.QueryBuilding.cs:227` gates
`Include(e => e.Provider)` on `ItemFields.ProviderIds`. Without it every Discover result is
silently discarded during ranking, because the ranking reads provider ids off each item. The
controller adds `ProviderIds` to the query's `DtoOptions` regardless of what the caller asked to
have returned.

---

## 3. Web client (`jellyfin-web/`, tag `v12.0-rc3`)

Also covers the Android phone app.

### 3.1 New files

| File | Role |
|---|---|
| `src/types/browseMode.ts` | `BrowseMode` enum, `BrowseModeDefinition`, `BrowsePicker`, `GLOBAL_PLAY_COUNT` |
| `src/apps/modern/features/libraries/constants/browseModes.ts` | The 18 mode definitions and per-collection-type lists |
| `src/apps/modern/routes/browse/index.tsx` | The tile page, including picker sub-grids |

### 3.2 Modified files

`appRouter.js`, `useLibrary.tsx`, `useFetchItems.ts`, `libraries/utils/{path,settings}.ts`,
`constants/views/{movies,tvshows}.ts`, `libraryRoutes.ts`, `ItemsView.tsx`,
`CardImageContainer.tsx` + `card.scss` (rank badge), `cardOptions.ts`, `libraryTab.ts`,
`asyncRoutes/user.ts`, `strings/en-us.json`.

### 3.3 Interception point

`src/components/router/appRouter.js:410`, inside `showItem`:

```js
if (!options.section && shouldShowBrowseModes(item.CollectionType, item.Id)) {
    return `#/browse?topParentId=${item.Id}&collectionType=${item.CollectionType}`;
}
```

The `!options.section` check is what lets deep links (such as the latest-media rows) bypass the
tile page and go straight where they intended.

### 3.4 How a tile resolves

- **View-backed** (Genres, Studios, Favorites, Trending, Top Rated) → navigates to that tab index.
- **Preset-backed** (Just Added, Random, Highest Rated…) → `?browseMode=<mode>`; `useLibrary`
  seeds `LibraryViewSettings` from the definition.
- **Picker** (Decades, Age Rating) → sub-grid of values, then `?browseMode=<mode>&pick=<values>`.

### 3.5 Preset persistence

`getSettingsKey(viewType, parentId, browseMode)` produces
`{viewType} - {parentId} - {browseMode}`, so changing the sort inside "Just Added" does not
disturb the plain library view. The preset is supplied as the *default* for a fresh key, so a
user's later adjustment inside a mode survives.

### 3.6 Escape hatch

An explicit `landing-<libraryId>` user setting bypasses the tile page entirely
(`shouldShowBrowseModes` in `libraries/utils/path.ts`). There is also an "All" tile.

### 3.7 Icon colours

`BrowseModeDefinition.iconColor` carries a hex string, applied in `browse/index.tsx` via the MUI
icon's `sx`. The 15 modes the TV also has use **identical hex values** to the Android palette, so
the clients genuinely match. Three are web-only: Critics' Picks `#E0533D`, Decades `#7E9CD8`,
Age Rating `#9CCC65`.

> Chosen against the dark theme, which is the web default (`RootAppRouter` sets
> `defaultMode='dark'`). On a light theme the palest — `#B0BEC5` (All) and `#F2C14E` (Best
> Unseen) — will be low contrast. Not yet addressed.

### 3.8 Web gotchas

- **`useFetchItems.ts` has an `enabled` allowlist of view types.** A view type missing from it
  leaves react-query permanently `pending`, which renders as an infinite spinner with no request
  and no error. Add new view types there.
- **The service worker caches the bundle.** Hard-refresh (Ctrl+Shift+R) after deploying or
  changes appear absent.
- **`GlobalPlayCount` is not in the generated `@jellyfin/sdk` enum.** Asserted once in
  `types/browseMode.ts` as `GLOBAL_PLAY_COUNT`. TypeScript permits this; Kotlin does not — see
  §4.9.

---

## 4. Android TV client (`jellyfin-androidtv/`, tag `v0.19.9`)

The tag matches the app version that was already installed on the TV.

### 4.1 New files — `ui/browsing/browsemodes/`

| File | Role |
|---|---|
| `BrowseModes.kt` | `BrowseMode` enum, `BrowsePreset`, `BrowseModeDefinition`, `BrowseModeDestination`, the per-collection-type lists, `getBrowseModes()` |
| `BrowseModesFragment.kt` | The tile grid. 4-column `VerticalGridSupportFragment` |
| `BrowseModeTilePresenter.kt` | Draws one tile (icon + label) |
| `DiscoverFragment.kt` | Trending / Top Rated results |
| `DiscoverCardPresenter.kt` | `CardPresenter` subclass that badges cards with their rank |
| `ByStudioFragment.kt` | Grid of studios to pick from |
| `StudioItemsFragment.kt` | Items for one chosen studio |

New drawables: `ic_trending_up.xml`, `ic_medal.xml`, `ic_new_releases.xml`.

### 4.2 Modified files

`constant/Extras.kt`, `preference/LibraryPreferences.kt`, `ui/browsing/DisplayPreferencesScreen.kt`,
`ui/card/LegacyImageCardView.java`, `ui/itemhandling/ItemLauncher.java`,
`ui/navigation/Destinations.kt`, `ui/presentation/CardPresenter.java`,
`res/layout/view_card_legacy_image.xml`, `res/values/colors.xml`, `res/values/strings.xml`.

### 4.3 Interception point

`ui/itemhandling/ItemLauncher.java`, in `getUserViewDestination` — the direct analogue of
`appRouter.js:410`:

```java
case MOVIES:
case TVSHOWS:
    LibraryPreferences displayPreferences = ...;
    boolean enableBrowseModes = displayPreferences.get(LibraryPreferences.Companion.getEnableBrowseModes());
    boolean enableSmartScreen  = displayPreferences.get(LibraryPreferences.Companion.getEnableSmartScreen());

    if (enableBrowseModes)      return Destinations.INSTANCE.browseModes(baseItem);
    else if (!enableSmartScreen) return Destinations.INSTANCE.libraryBrowser(baseItem);
    else                         return Destinations.INSTANCE.librarySmartScreen(baseItem);
```

`enableBrowseModes` defaults **true**, so tiles are the landing screen for every movie/TV library.

> **Known consequence.** This displaces the stock "smart screen" (Continue Watching, Next Up,
> Latest rows) for anyone who had `enableSmartScreen` on — it becomes reachable only by turning
> browse modes off. `enableSmartScreen` defaults false, so most users are unaffected. If this
> matters, the fix is a tile that routes to `librarySmartScreen`.

### 4.4 Mode-scoped display preferences

The equivalent of web's localStorage key scoping, and it reuses an idiom the app already had.
`EnhancedBrowseFragment:366` scopes music preferences with
`copyWithDisplayPreferencesId(mFolder, mFolder.getId() + "AL")`. The same trick here:

```kotlin
val scoped = folder.copyWithDisplayPreferencesId("${folder.id}-${definition.mode.key}")
```

`PreferencesRepository.getLibraryPreferences(String)` accepts any id, and `LibraryPreferences`
is a `DisplayPreferencesStore` synced to the server. **`BrowseGridFragment` needed no changes at
all.** Without this, opening a preset would overwrite the user's real library sort.

`BrowseMode.key` is therefore load-bearing: changing a key orphans users' saved settings for
that mode.

### 4.5 Seed-once semantics

`LibraryPreferences.browseModeSeeded` (a boolean in the scoped store) marks that a preset has
been applied. `BrowseModesFragment.seed()` returns early if set. This matches web, where the
preset is a *default* for a fresh key rather than something reapplied on every visit.

The store is fetched from the server, so `seed()` runs inside `withContext(Dispatchers.IO)`.

### 4.6 Rank badges

Additive change, invisible unless used:

- `view_card_legacy_image.xml` — new `rankIndicator` FrameLayout aligned to the **start** of
  `main_image`, mirroring the existing end-aligned `watchedIndicator`. `visibility="gone"`.
- `LegacyImageCardView.setRankBadge(int)` — shows the badge; `<= 0` hides it.
- `CardPresenter.resetCardView()` — calls `setRankBadge(0)` so recycled cards cannot leak a
  stale rank.
- `DiscoverCardPresenter` — the only thing that sets it, from `baseItem.indexNumber`.

To show 1,2,3,4 (rank *within the library*) instead of TMDB's absolute position, use the loop
index in `DiscoverFragment` — but web would then disagree unless changed too.

### 4.7 Studios: a deliberate divergence from `ByGenreFragment`

`ByGenreFragment` builds one row per value, and `EnhancedBrowseFragment:276` calls
`rowAdapter.Retrieve()` **eagerly for every row** inside the loop. Fine for ~20 genres.

A sample library of 56 films carries **209 studios**. That shape would fire 209 concurrent
requests on opening the screen.

So studios use a two-step flow instead, matching what web's Studios tab does: `ByStudioFragment`
(grid of studio cards) → `StudioItemsFragment` (that studio's items). Fetches on demand.

**Do not "fix" this back to the ByGenre shape.**

### 4.8 Icon tinting

Twelve of the fifteen icons are pre-existing app drawables (`ic_grid`, `ic_heart`, `ic_time`…)
shared with the rest of the client. Tint is therefore applied **at draw time** via a Compose
`ColorFilter` in `BrowseModeTilePresenter`, driven by `BrowseModeDefinition.iconTint`. Editing
the drawables would have recoloured the Favourites heart everywhere else in the app.

`ic_rt_fresh` (Critics' Picks) has `iconTint = null` on purpose: it is a 3-path multi-colour
drawable and a flat tint would destroy it.

### 4.9 `GlobalPlayCount` — why Most Watched never landed on TV

Historical, since the tile was dropped entirely (§6) — but the constraint is worth knowing before
anyone tries to add a sort key from a client.

`ItemSortBy` in the Kotlin SDK is a real `enum class`, and `LibraryPreferences.sortBy` is an
`enumPreference`. There is no way to express a value the generated enum does not contain.
TypeScript let web assert past this (`'GlobalPlayCount' as ItemSortBy`); Kotlin will not.

The server-side constraint is the harder one: `ItemSortBy` there is also a closed compiled enum
and `OrderMapper` is static with no registration hook, so **no plugin can add a sort key**.

### 4.10 Raw requests for custom endpoints

`/Discover/*` is not in the generated SDK either. `DiscoverFragment` uses the SDK's raw helper:

```kotlin
apiClient.get<BaseItemDtoQueryResult>(
    pathTemplate = "/Discover/Trending/Movies",
    queryParameters = mapOf(
        "userId"   to userRepository.currentUser.value?.id,
        "parentId" to folder.id,
        "fields"   to ItemRepository.itemFields.joinToString(",") { it.serialName },
        "limit"    to LIMIT,
    ),
).content.items
```

`fields` is joined manually rather than passed as a collection, because the server binds it with
`CommaDelimitedCollectionModelBinder`.

### 4.11 Screen metrics

The test TV reports 1280x720 at an effective density of about 1.33 — roughly **960dp wide**.
Four 220dp tiles plus leanback's padding came to ~1284px against a 1280px screen and clipped the
last column. Tiles are **200 x 112dp**. Keep that budget in mind for any grid work.

### 4.12 Android gotchas

- `gridPresenter` is not settable as a Kotlin property on `VerticalGridSupportFragment`; call
  `setGridPresenter(...)`.
- `ItemLauncher.launch` wants `MutableObjectAdapter<Any>`, not `ArrayObjectAdapter`.
- `PreferencesRepository.getLibraryPreferences` does blocking network I/O via `runBlocking`.
  Keep it off the main dispatcher.

---

## 5. Tile inventory

| Tile | Web | TV | Backing |
|---|:--:|:--:|---|
| All | ✅ | ✅ | plain grid |
| Unwatched | ✅ | ✅ | filter |
| Just Added | ✅ | ✅ | `DateCreated` desc |
| Best Unseen | ✅ | ✅ | unplayed + `CommunityRating` desc |
| Random | ✅ | ✅ | `Random` |
| Favorites | ✅ | ✅ | web: view · TV: filter |
| Genres | ✅ | ✅ | `ByGenreFragment` on TV |
| Highest Rated | ✅ | ✅ | `CommunityRating` desc |
| Top Rated | ✅ | ✅ | `/Discover/TopRated/*` |
| Trending | ✅ | ✅ | `/Discover/Trending/*` |
| New Releases | ✅ | ✅ | `PremiereDate` desc |
| Studios / Networks | ✅ | ✅ | two-step on TV (§4.7) |
| Recently Played | ✅ | ✅ | `DatePlayed`; series use `SeriesDatePlayed` |
| Longest | ✅ | ✅ | `Runtime` desc |
| Critics' Picks | ✅ | ✅ | `CriticRating` desc. **Movies only** — 48/58 films have a critic rating, only 1/21 series |
| Decades | ✅ | ❌ | picker |
| Age Rating | ✅ | ❌ | picker |

Web: 17 movie modes, 16 series. TV: 15 movie, 14 series.

**Most Watched was dropped**, see §6.

---

## 6. Remaining work

**Most Watched — dropped, not deferred.** It summed `UserData.PlayCount` across all users via a
new `ItemSortBy.GlobalPlayCount` sort key. `ItemSortBy` is a compiled, closed enum and
`OrderMapper` is a static class with no registration hook, so **a plugin cannot add a sort key**.
Keeping it would have meant keeping a forked server, which is exactly what this architecture
exists to avoid.

If it is ever wanted back, it does not need a fork — reimplement it as a *ranked endpoint* like
Trending, computing the order in the plugin and returning position in `IndexNumber`. Two routes:
query `JellyfinDbContext.UserData` directly (`Jellyfin.Database.Implementations` *is* a published
package) for identical semantics; or use `IUserManager.GetUsers()` +
`IUserDataManager.GetUserDataBatch` per user, which is fully supported API but costs one query
per user. On both clients it would become a view-backed tile rather than a sort preset.

**Decades / Age Rating** — hardest of the three. `LibraryPreferences` has no years or
officialRatings fields, so a picked value cannot travel through preferences the way presets do.
Needs a new `Extras` key and real surgery on `BrowseGridFragment`'s query building — the one
place all work so far has avoided touching. Filter values come from `/Items/Filters`
(`getQueryFiltersLegacy`), which *is* in the SDK.

**Rank badge semantics** — decide between TMDB absolute position (current) and 1..n.

**Light theme contrast** on web (§3.7).

**Smart screen displacement** on TV (§4.3).

---

## 7. Applying this to a new release

Ready-made patches live in `patches/` in this repo. They were generated with `git add -N .` so they
include the new files as well as the edits, and both were verified to match the working trees at
the time of writing.

```
patches/jellyfin-web-browse-modes.patch        ~1060 lines
patches/jellyfin-androidtv-browse-modes.patch  ~1100 lines
```

### 7.1 Web client

```bash
git clone https://github.com/jellyfin/jellyfin-web.git
cd jellyfin-web
git checkout <new-tag>                       # baseline was v12.0-rc3

git apply --3way ../patches/jellyfin-web-browse-modes.patch
```

`--3way` is what you want: on conflict it leaves ordinary conflict markers you can resolve,
rather than refusing the whole patch. Use `git apply --check` first for a dry run.

If it applies cleanly, rebuild and you are done. If not, §7.4 lists the places most likely to
have moved.

### 7.2 Android TV client

```bash
git clone https://github.com/jellyfin/jellyfin-androidtv.git
cd jellyfin-androidtv
git checkout <new-tag>                       # baseline was v0.19.9

git apply --3way ../patches/jellyfin-androidtv-browse-modes.patch
echo "sdk.dir=<toolchain>/android-sdk" > local.properties
```

`local.properties` is gitignored, so it is not in the patch and must be recreated.

Seven of the thirteen files are wholly new (`ui/browsing/browsemodes/` and the three drawables)
and cannot conflict. The risk is concentrated in the six touched files.

### 7.3 The plugin

Usually nothing to do. The plugin is ordinary source in this repo and is not a patch against
anything, so a new Jellyfin release only matters if it breaks compatibility.

On a new Jellyfin version:

1. Bump the four `Jellyfin.*` `PackageReference` versions in the `.csproj` to match, and check
   they exist on nuget.org first — the tag naming is inconsistent (`v12.0-rc3` on GitHub,
   `12.0.0-rc3` on nuget).
2. Bump `targetAbi` in `manifest.json` and `meta.json`.
3. Rebuild and confirm the output is still exactly three DLLs (§2.3).
4. Install into a stock container and check the log says `Loaded plugin: Browse Modes`.

**There is no forked server to merge.** That was the whole point of moving to a plugin — the
server tree used to be un-versioned, with the changes recoverable only from this document. The
originals are preserved at `reference/server-fork-original/` for historical interest only.

### 7.4 Where breakage is most likely

Ranked by how exposed each is to upstream churn.

| Risk | Location | What to re-check |
|---|---|---|
| High | `appRouter.js` `showItem` | Web's router is actively refactored. The hook must stay before the `isModernLayout` branch and keep the `!options.section` guard. |
| High | `ItemLauncher.getUserViewDestination` | The MOVIES/TVSHOWS branch must return `browseModes(...)` ahead of the smart-screen check. |
| Low | The plugin (§7.3) | Ordinary source, not a patch. Only package versions and `targetAbi` normally need bumping. |
| Medium | `useFetchItems.ts` `enabled` allowlist | New view types silently hang as an infinite spinner (§3.8). |
| Medium | `EnhancedBrowseFragment:276` eager `Retrieve()` | If upstream makes rows lazy, the two-step studios flow (§4.7) could be simplified back. |
| Medium | `view_card_legacy_image.xml` | The `rankIndicator` block must stay a sibling of `watchedIndicator` inside the same RelativeLayout. |
| Low | `browsemodes/` package | Self-contained; only breaks if SDK signatures change. |
| Low | Reused drawables (§4.8) | An icon could be renamed or dropped upstream. Build failure is obvious and the fix trivial. |

### 7.5 Verifying a port

In this order — each step isolates a different layer:

1. **Server API, no client.** Use the `curl` recipe in §9. If the Discover endpoints return 200
   with items, the server port is good.
2. **Web.** `npx tsc --noEmit` then `npx eslint <changed files>`, then build. Hard-refresh the
   browser (§3.8) and open a movie library.
3. **Android TV.** `./gradlew assembleDebug`, install, then `adb exec-out screencap` (§9).
   Check the tile grid, then open Trending — it exercises the raw request path, which is the most
   fragile part.

### 7.6 Regenerating the patches

After making further changes:

```bash
cd jellyfin-web            # or jellyfin-androidtv
git add -N .
git diff --binary > ../patches/jellyfin-web-browse-modes.patch
git reset
```

`git add -N` is intent-to-add: it makes untracked files visible to `git diff` without staging
their contents. The `git reset` afterwards returns the index to how it was.

---

## 8. Building

The plugin needs only the .NET SDK (10.0.x). The two clients need a JDK, the Android SDK and
Node >= 24, none of which are typically on `PATH` — the reference setup unpacks them as tarballs
into a `.toolchain/` directory (JDK 21.0.12, Android SDK platform-36 + build-tools 36,
Node v24.9.0 / npm 11.6.0), so no sudo is needed and deleting the directory reverses the lot.

```bash
# Plugin  (needs only the dotnet SDK, no local toolchain)
cd plugin/Jellyfin.Plugin.BrowseModes
dotnet build -c Release                     # -> bin/Release/net10.0/ (3 DLLs)

# Web  (also updates the phone app)
cd jellyfin-web
export PATH=$PWD/../.toolchain/node-v24.9.0-linux-x64/bin:$PATH
npm ci && npm run build:production          # ~46s -> dist/

# Android TV
cd jellyfin-androidtv
export JAVA_HOME=$PWD/../.toolchain/jdk-21.0.12+8
export ANDROID_HOME=$PWD/../.toolchain/android-sdk
./gradlew assembleDebug --no-daemon         # cold ~8 min, incremental ~30s
```

> **Piping gradle into `grep` masks its exit code.** `./gradlew ... | grep BUILD` reports grep's
> status, which cost a false "build succeeded" during development. Redirect to a log and test
> `$?`.

Large downloads on this connection get reset partway; retry curl with `-C - --retry 8`.

---

## 9. Deploying and testing

### Test container

Run **stock** `jellyfin/jellyfin:12.0-rc3` — no custom image is needed any more. Install the
plugin by dropping the three built DLLs plus a `meta.json` into
`<config>/plugins/BrowseModes_<version>/`, and set the TMDb key in
`<config>/plugins/configurations/Jellyfin.Plugin.BrowseModes.xml`.

```bash
docker run -d --name jellyfin-test -p 8098:8096 \
  -v <config>:/config -v <cache>:/cache -v <media>:/media:ro \
  jellyfin/jellyfin:12.0-rc3
```

For the web bundle, the web root is `/jellyfin/jellyfin-web`, **baked into the image, not
mounted**:

```bash
docker cp jellyfin-web/dist/. jellyfin-test:/jellyfin/jellyfin-web/
```

That is ephemeral — recreating the container reverts it. Rebuild the image, or bind-mount a
`--webdir`, for anything lasting.

Confirm the plugin loaded:

```bash
docker logs jellyfin-test 2>&1 | grep -E "Loaded plugin: Browse Modes|Refresh TMDb discover"
```

### TV over adb

With network ADB enabled on the TV (Settings -> Device Preferences -> About -> tap Build 7x,
then Developer options -> Network debugging):

```bash
export PATH=.toolchain/android-sdk/platform-tools:$PATH
adb connect <TV-IP>:5555
adb install -r jellyfin-androidtv/app/build/outputs/apk/debug/*.apk
```

- It answers **only on 5555**; a plain ping sweep misses it. `nmap -p 5555 --open <YOUR-SUBNET>.0/24`
  finds it.
- **It is invisible while a VPN is up on the build machine.** This produced two false "the TV
  is off" readings during development. Check the VPN first.
- Debug builds carry `applicationIdSuffix = ".debug"`, so they install *beside* the stock app as
  `org.jellyfin.androidtv.debug` and never disturb it. A release build would replace it.
- Driving the UI with `adb shell input keyevent` works, but `KEYCODE_BACK` exits the app rather
  than merely dismissing the on-screen keyboard.
- `adb exec-out screencap -p > shot.png` is the fastest way to actually verify a UI change.

### Verifying the API without a client

```bash
TOKEN=$(python3 -c "import sqlite3;print(list(sqlite3.connect('jellyfin.db').execute(
  \"SELECT AccessToken FROM Devices WHERE AppName LIKE '%Android TV%' LIMIT 1\"))[0][0])")
curl -s -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "http://localhost:8097/Discover/Trending/Movies?parentId=<libraryId>&limit=60"
```

All four Discover endpoints, `/Studios`, and studio-filtered `/Items` were confirmed this way
before any client work — worth repeating first when something looks broken, to establish whether
the fault is client-side or API-side.

---

## 10. Measured on the sample library (56 movies / 20 series)

Trending: 5 movie / 11 series matches at 15 pages (was 1 / 4 at 5 pages).
Top Rated: 10 movie / 4 series matches.
Cold list build 11.2s sequential → 1.6s parallel → 30–300ms from the pre-warmed cache.

---

## 11. Deliberately not built

**Showing trending titles the library does not own.** Dead clicks in a media server; Jellyseerr
already solves discovery-then-request properly.

**IMDb Top 250.** No free API, and scraping breaks their terms. TMDB Top Rated was used instead.
