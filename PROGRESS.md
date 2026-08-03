# Progress log

Running notes for the Browse Modes public-release work. Newest entries at the top.
Design rationale and the architecture decision live in [docs/TECHNICAL.md](./docs/TECHNICAL.md).

## Status at a glance

| Phase | State |
|---|---|
| 1. Build the plugin | ✅ done, verified on a stock server |
| 2. Drop Most Watched, revert server fork | ✅ done |
| 3. Create GitHub repos | ✅ all three public |
| 4. CI and release artifacts | ✅ v1.0.1.0 released |
| 5. Deploy to production | ✅ live on the real server, all three clients |

**Next session:** see [docs/NEXT-SESSION.md](./docs/NEXT-SESSION.md) — Chunk 3 (Vault) or Chunk 5 (Polish).

## 2026-08-03 (continued — v2.0 release)

### Tag-based discovery — DONE

Five new browse modes backed by TMDb keywords stored as Jellyfin tags:

- **😊 Mood** — emotional/aesthetic qualities (suspenseful, heartwarming, whimsical…)
- **🎬 Story Themes** — narrative topics (time travel, heist, revenge…)
- **📋 Plot Elements** — story mechanics (based on a true story, unreliable narrator…)
- **🌍 Worlds** — settings and time periods (new york city, 1950s, medieval, space…)
- **🎨 Styles** — filmmaking techniques (stop motion, film noir, anime, musical…)

Each opens as stacked horizontal poster shelves (following the `GenresSectionContainer`
pattern), with a sort dropdown (Random, A–Z, Z–A, Most/Fewest items), a shuffle button,
and a grid/shelf view toggle. Only tags with matching items in the library appear.

Curated tag lists stored as TS constants in `browseTags.ts` — 9,700 keywords
AI-classified from the official TMDb daily keyword export (`keyword_ids_MM_DD_YYYY.json.gz`,
92,533 total).

### Date cutoffs — DONE

New Releases and Just Added now limited to last 9 months:
- New Releases: `MinPremiereDate` (12.x and 10.11)
- Just Added: `MinDateLastSaved` (12.x and 10.11)

Required adding `MinPremiereDate`/`MinDateLastSaved` to `LibraryViewSettings` and
`getFiltersQuery` in `utils/items.ts`. 10.11 already had `MinPremiereDate` from the
backport; 12.x got both in this session.

### v2.0.0 release — DONE

GitHub release created with plugin zip (426K) and web bundle zip (~26MB). Manifest
updated for the Jellyfin plugin catalog. Docs updated with apt install instructions,
current tile inventory, and CONTEXT.md glossary.

Deployed to real server (apt-based) and verified working.

### Infrastructure

- `docs/CONTEXT.md` — domain glossary
- `scripts/classify_keywords.py` — TMDb keyword classification pipeline
- Root `.gitignore` for toolchain and session files

## 2026-08-03 (morning)

### Web client backported to Jellyfin 10.11 — DONE

Branch `browse-modes-10.11` in `AvonWilliams/jellyfin-web`. 17 files, ~900 lines. All modes
ported. Four 10.11-specific bugs found and fixed:

- `layoutManager.experimental` defaults false → removed from `shouldShowBrowseModes` guard
- CollectionType absent from API response → fallback to `options.context` + path inference
- RootAppRouter gates experimental routes on localStorage → always load EXPERIMENTAL_APP_ROUTES
- Card clicks looped back to tiles → gate interception on `item.Type === 'CollectionFolder'`

Plus: `MinPremiereDate` 9-month cutoff on New Releases, `IsPlayed` filter on Watch Again,
Studios added to useFetchItems switch/allowlist. Built and tested in Docker `jf-test-10`.

### v2.0 refactor started — Chunks 1a/1b/1c complete

Based on `category-upgrades.md`. Created `docs/PLAN.md` with 5 chunks by difficulty.

Chunk 1a: Removed Highest Rated (local sort), Longest, Favorites, Unwatched
Chunk 1b: Renamed Best Unseen → Hidden Gems, Recently Played → Watch Again
Chunk 1c: Reordered tiles to spec order

Both 12.x (`browse-modes`) and 10.11 (`browse-modes-10.11`) updated, built, deployed
to Docker test containers. Current: 13 movie tiles, 12 TV tiles.

### Infrastructure

- `AvonWilliams/claude-config` private repo created with CLAUDE.md, skills, commands
- 12.x web bundle deployed to Docker `jellyfin` container for local testing

**Live:**
- https://github.com/AvonWilliams/jellyfin-browse-modes — plugin, docs, CI
- https://github.com/AvonWilliams/jellyfin-web — fork, default branch `browse-modes`
- https://github.com/AvonWilliams/jellyfin-androidtv — fork, default branch `browse-modes`
- Plugin repo URL: `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json`

**Resolved:** TMDb API key — the user supplies their own; the bundled key lives in
`MediaBrowser.Providers`, which the plugin deliberately does not reference. APK signing — shipping
debug-signed for now, which keeps the `.debug` suffix so it installs *beside* the official app
rather than replacing it. That is arguably the better default; revisit if Play-style updates are
ever wanted.

**Still open:** nothing blocking. Nice-to-haves are CI in the two fork repos (both artifacts are
currently built locally and uploaded by hand), light-theme contrast on web, and Decades / Age
Rating on the TV client.

---

## 2026-07-26

### ✅ Live in production — real server upgraded and running

Proxmox LXC `JellyClone` (CT 1056, Ubuntu 24.04) taken from **10.11.11 → 12.0-rc3**, then Browse
Modes installed. Confirmed working on **web, Android phone and Android TV**.

vzdump taken first: 11.75 GB, snapshot mode, clean finish.

**There is no apt suite for preview builds** — `dists/unstable` and `dists/preview` both 404. RCs
are direct `.deb` downloads from `repo.jellyfin.org/files/server/ubuntu/preview/v12.0-rc3/amd64/`.
The Proxmox community-scripts installer only does stable and must not be run on this container
again.

12.0-rc3 depends on **`jellyfin-ffmpeg8`**, not the `jellyfin-ffmpeg7` that was installed. It is
in the ordinary stable apt repo, so `apt install ./*.deb` resolved it and removed 7 cleanly.

Two things that cost time, both mine:

1. `curl -O` saves the URL-encoded filename literally, so the files landed as
   `jellyfin_12.0-rc3%2Bubu2404_all.deb` while the install command referenced `+`. apt reported
   "Unsupported file" for all three. Use `curl -o <cleanname>`.
2. A transient DNS failure in the container silently produced a zero-byte `jellyfin-server.deb`.
   Always verify with `dpkg-deb -f <file> Package Version` before installing — the md5 of the
   good file is `26c744a2b94534756da0565b28c7bf57`.

Post-upgrade the journal was full of errors from **third-party plugins**, not the migration:
Meilisearch could not reach its backend, and `JavaScriptInjector` failed writing to
`/usr/share/jellyfin/web/index.html` — the latter being exactly the fragile "plugin rewrites the
web root" approach ruled out during research. Both were disabled to rule out interference.

`apt-mark hold jellyfin-web` applied, since the package would otherwise replace the bundle.

### Decade and age rating pick tiles now have icons

The sub-grids behind **Decades** and **Age Rating** rendered as bare text — the mode tiles had
icons, the values behind them did not. New `constants/pickTiles.ts` supplies both.

Decades use an era-appropriate icon with a warm-to-cool colour ramp so the grid reads as a
timeline. Age ratings are graded by restrictiveness (green child → red `Explicit`), parsed from
the country-prefixed codes Jellyfin returns.

**A bug the test caught.** The country-prefix stripper `^[A-Z]{2}-` also ate the `TV-` from US
television ratings, so `TV-MA` normalised to `MA` and came out *mature* instead of *adult*, and
`TV-Y7` became an unrecognised `Y7` → *unrated*. Fixed with a negative lookahead. Worth noting
that `tsc` and `eslint` were both clean throughout — only running real inputs through the
function surfaced it. All 21 cases now correct, including `AU-MA15+`, `GB-12A`, `US-NC-17` and
bare `16+`.

Note the colours land in `browse.<hash>.chunk.js`, not the main bundle, since the browse route is
an async chunk — grep the chunk, not `main.jellyfin.bundle.js`, when verifying a build.

### Install-from-catalog gotchas (hit during first real install)

Two things bit the first real install, neither a defect:

1. **GitHub Pages had not finished its first build** when the repo URL was added, so Jellyfin
   logged `404 (Not Found)` against the manifest. It resolved on its own about a minute later.
   If a repo looks broken, check the server log — `An error occurred while accessing the plugin
   manifest` names the exact cause.
2. **The browser cached the plugin list.** The catalog kept showing the old response until the
   cache was cleared manually; a normal reload was not enough.

Also worth knowing: `routes/plugins/index.tsx:51` defaults the status filter to **Installed**, and
uninstalled catalog entries have no `status`, so they are filtered out. Switch it to *Available*
when looking for something not yet installed.

### ✅ Published — v1.0.0.0 live, install chain verified end to end

Three public repos, plus a v1.0.0.0 release carrying the plugin zip, the web bundle and the APK.
GitHub Pages serves the plugin manifest.

The install chain was tested the way a stranger would hit it, not just assumed:

1. `manifest.json` over Pages → HTTP 200
2. followed its `sourceUrl` → HTTP 200
3. md5 of the download matched the manifest checksum
4. unpacked **that downloaded artifact** (CI-built, not the local build) into a stock
   `jellyfin/jellyfin:12.0-rc3` container → `Loaded plugin: Browse Modes 1.0.0.0`

Both fork default branches were switched to `browse-modes`, otherwise visitors would have landed
on upstream's README.

### ⚠️ Personal email leaked into the first public commits — remediated

The initial commits were authored with a personal address, and pushed. Recording it because the
remediation is not obvious:

- Rewriting history and force-pushing **was not sufficient**. The orphaned commits stayed
  reachable by SHA and still returned the address via the API — GitHub does not promptly
  garbage-collect unreachable objects.
- The token lacked `delete_repo`, so the repo could not be deleted.
- Fix: renamed the repo to `jellyfin-browse-modes-leaked-delete-me` and made it **private**, which
  immediately 404s the orphans to anonymous access. Then re-created the intended public repo from
  a **freshly initialised** `.git` rather than a rewritten one.

`git config user.email` is now set to the GitHub noreply address in all three repos. Verified
every pushed commit across all three uses it. **The old private repo should still be deleted.**

Docs had already been genericised of LAN IPs and `/home/avon` paths before the first commit, so
the only leak was commit metadata.

### Phase 3/4 — repo assembled locally, ready to publish

Local git repo initialised and committed. Contents: plugin source, `docs/TECHNICAL.md` and
`docs/USER-GUIDE.md` (both rewritten for the plugin architecture), `patches/` for the two client
forks, a GitHub Actions workflow, MIT licence, and `reference/server-fork-original/`.

The docs were **genericised before commit** — they previously carried LAN IPs (`192.168.1.6/.7`)
and `/home/avon/...` paths, which have no place in a public repo. Replaced with `<TV-IP>`,
`<SERVER-IP>` and relative paths.

Doc sections rewritten because the plugin invalidated them: §1 (three forks → plugin + two
forks, plus the evidence table for why the UI cannot be a plugin), §2 (server fork → plugin,
including the two packaging requirements), §4.9, §5, §6 (Most Watched now "dropped, not
deferred", with a note on how to bring it back as a ranked endpoint if ever wanted), §7.3, §7.4,
§8 and §9.

The CI workflow includes a guard worth keeping: it fails the build if any `Jellyfin.*` or
`MediaBrowser.*` assembly appears in the plugin output, since that silently reintroduces the
AssemblyLoadContext type-identity problem.

Full stack re-verified after the Most Watched removal: web rebuilt (`mostwatched` absent from
every chunk) and deployed onto the **stock + plugin** container. Server, plugin and web bundle
all working with no forked server anywhere.

### Phase 2 — Most Watched dropped, server fork reverted

**Web.** Removed `GLOBAL_PLAY_COUNT` and `BrowseMode.MostWatched` from `types/browseMode.ts`, the
`mostWatchedMode` definition and both list entries from `browseModes.ts`, and
`BrowseModeMostWatched` from `en-us.json`. Also dropped two imports the removal orphaned — the
`ItemSortBy` type import in `browseMode.ts` and `LocalFireDepartment` in `browseModes.ts`.
`tsc --noEmit` and `eslint` both clean; `en-us.json` still parses.

Tile counts are now **17 movie / 16 series** on web (was 18/17).

**Server reverted to stock.** Rather than hand-editing (error-prone, and the tree has no git
baseline to diff against), the four modified files were replaced with pristine copies fetched
from upstream, and the two added files deleted:

| File | Action |
|---|---|
| `Jellyfin.Data/Enums/ItemSortBy.cs` | restored from upstream |
| `Jellyfin.Server.Implementations/Item/OrderMapper.cs` | restored from upstream |
| `Jellyfin.Api/Jellyfin.Api.csproj` | restored from upstream |
| `MediaBrowser.Providers/Plugins/Tmdb/TmdbClientManager.cs` | restored from upstream |
| `Jellyfin.Api/Controllers/DiscoverController.cs` | deleted |
| `MediaBrowser.Providers/Plugins/Tmdb/RefreshDiscoverListsTask.cs` | deleted |

The upstream tag is **`v12.0-rc3`** — note *not* `v12.0.0-rc3`, which 404s.

Because that tree is not a git repo and the revert is irreversible, all six originals were copied
to `reference/server-fork-original/` in this repo first. They are superseded, but they are the
only record of the pre-plugin implementation.

`grep` for `GlobalPlayCount|DiscoverPagesToScan|RefreshDiscoverLists|WarmDiscoverLists|
GetTrendingMovieIds` across the server tree now returns nothing.

### ✅ Plugin verified on a STOCK Jellyfin server — the fork is no longer needed

Ran `jellyfin/jellyfin:12.0-rc3` **unmodified** (container `jellyfin-stock-plugintest`, port 8098)
with only the plugin dropped into `config/plugins/BrowseModes_1.0.0.0/`.

Server log, with no errors anywhere:
```
Loaded assembly TMDbLib ... from /config/plugins/BrowseModes_1.0.0.0/TMDbLib.dll
Loaded assembly Newtonsoft.Json ... from /config/plugins/BrowseModes_1.0.0.0/Newtonsoft.Json.dll
Loaded assembly Jellyfin.Plugin.BrowseModes ...
Loaded plugin: Browse Modes 1.0.0.0
Refresh TMDb discover lists Completed after 0 minute(s) and 4 seconds
```

The scheduled task self-registered and ran on startup unprompted — confirming plugin
`IScheduledTask` discovery works with no explicit registration.

All four endpoints return 200 with TMDb rank preserved in `IndexNumber`:

| Endpoint | Result |
|---|---|
| `/Discover/Trending/Movies` | 5 items — 20.The Devil Wears Prada, 52.Hokum, 82.Master of the Universe, 133.F1 |
| `/Discover/TopRated/Movies` | 10 items — 5.The Godfather, 20.Pulp Fiction, 31.Fight Club, 49.Inception |
| `/Discover/Trending/Shows` | 8 items — 11.Rick and Morty, 36.The Boys, 45.The Lord of the Rings, 84.South Park |
| `/Discover/TopRated/Shows` | 5 items — 3.Breaking Bad, 20.Rick and Morty, 38.INVINCIBLE, 192.The Boys |

Match counts line up with what the forked server produced, so behaviour is preserved.

Also confirmed: plugin shows as `Active` in `GET /Plugins`, and its config page serves at
`/web/ConfigurationPage?name=Browse%20Modes` (HTTP 200, renders the API key field).

**Test API key note:** the check used Jellyfin's own bundled TMDb key, which is a public constant
in the server source. That is fine for testing but is *not* settled for release — see open
decisions at the top.

### Plugin code complete, builds clean

All seven files written; `dotnet build -c Release` succeeds with **0 warnings, 0 errors**.

Two packaging details that were not obvious and are worth keeping:

1. **`CopyLocalLockFileAssemblies=true` is required.** A library build does not copy NuGet
   dependencies to its output, and a plugin is loaded from a folder rather than through
   `deps.json` — so the first build produced only `Jellyfin.Plugin.BrowseModes.dll` and TMDbLib
   would have been missing at runtime.
2. **`ExcludeAssets="runtime"` on all four Jellyfin packages.** With (1) turned on they would
   otherwise be copied into the plugin folder, and `PluginManager` loads every DLL there into the
   plugin's own AssemblyLoadContext — giving duplicate type identities for server-provided types.

Output is now exactly right: `Jellyfin.Plugin.BrowseModes.dll`, `TMDbLib.dll`,
`Newtonsoft.Json.dll` (TMDbLib's own dependency) and nothing else.

Package versions: `Jellyfin.Controller/Model/Data/Extensions` all exist on nuget.org at
**`12.0.0-rc3`**, exactly matching the server — no version guessing needed.

Adjustments made while porting, versus the in-tree original:

- `BaseJellyfinApiController` → `ControllerBase` plus explicit `[ApiController] [Route("Discover")]
  [Produces] [Authorize]`, keeping the literal route so existing client paths are unchanged.
- `RequestHelpers.GetUserId` is `internal` to Jellyfin.Api → reimplemented as `ResolveUserId`,
  reading the `Jellyfin-UserId` claim with the same admin check. One deliberate behaviour change:
  the original throws `SecurityException` when a non-admin names another user; this falls back to
  the authenticated user instead, since an unhandled throw from a plugin is worse than a
  degraded result.
- `CommaDelimitedCollectionModelBinder` lives in Jellyfin.Api → `fields` is taken as `string?` and
  split by hand, ignoring unrecognised names.
- `TmdbClientManager.Dispose` disposed the **shared** container `IMemoryCache` (a latent upstream
  bug). `TmdbDiscoverClient` owns a private `MemoryCache` and disposes only that.
- `DiscoverPagesToScan` and cache duration are now plugin config rather than constants.

### Phase 1 started — scaffolding

Created `jellyfin-browse-modes/` as the umbrella repo, with `plugin/Jellyfin.Plugin.BrowseModes/`
inside it. Deliberately a sibling of the existing repos rather than nested in any of them, so the
existing tree stays untouched until the plugin is proven.

Environment checks before starting:
- `dotnet` 10.0.110 present system-wide — no local toolchain needed for the plugin, unlike the
  Android and web builds.
- nuget.org is the only configured source (`nuget.config` does `<clear/>` first), which is fine:
  every package the plugin needs is published there.

### Research conclusions carried in from planning

Two findings drive the whole design and are worth not re-deriving:

1. **The server half can be a plugin.** `IUserManager`, `ILibraryManager`, `IDtoService` are all in
   the published `Jellyfin.Controller` package; `IScheduledTask` is in `Jellyfin.Model` and plugin
   tasks are auto-discovered (`ApplicationHost.cs:424`); plugin controllers get routed
   (`ApiServiceCollectionExtensions.cs:158-161`).
2. **The UI cannot be.** Plugin pages render only via `ServerContentPage`, used once at
   `apps/dashboard/routes/routes.tsx:49` behind `ConnectionRequired level='admin'`. No `CustomJs`
   counterpart to `CustomCss`. The route table is a module-local const built at import
   (`RootAppRouter.tsx:22`). `pluginManager.js:97` is a build-time webpack context import. So the
   tile page needs a `jellyfin-web` fork, permanently.

Consequence for users: TV needs **plugin + APK**; browser/phone needs **plugin + forked web
bundle**. Server stays stock in both cases.

Only Trending and Top Rated actually depend on the plugin — every other tile is a plain sort or
filter that stock Jellyfin already serves. `DiscoverFragment` catches a failed fetch and shows the
empty-state message, so an APK without the plugin degrades gracefully rather than crashing.
