# Browse Modes v2.0 — Implementation Plan

Based on `category-upgrades.md`. Tracked in chunks by difficulty.

**Status key:** ⬜ not started | 🔵 in progress | ✅ done

---

## Chunk 1: Easy — Tile cleanup & toggles

✅ **1a. Remove tiles:** Highest Rated (local sort), Longest, Favorites, Unwatched  
✅ **1b. Rename tiles:** Best Unseen → Hidden Gems, Recently Played → Watch Again  
✅ **1c. Reorder tiles:** Match the spec order in `category-upgrades.md`  
⬜ **1d. Per-library toggle:** Plugin config option to enable/disable browse modes per library type  
⬜ **1e. Per-tile toggle:** Plugin config option to show/hide individual tiles; client filters enabled list

**1d/1e plan (not yet implemented):**
- Add `EnableForMovies`, `EnableForTvShows`, `DisabledTiles` to `PluginConfiguration.cs`
- Update `config.html` with checkboxes grouped by library type
- Client fetches plugin config via `GET /Plugins/{id}/Configuration`, caches it, filters arrays
- New client hook: `hooks/useBrowseConfig.ts`
- Server-side config (not per-user) — one admin sets it, all users get same tiles

Files: `browseModes.ts`, `en-us.json`, `PluginConfiguration.cs`, `config.html`

**Icon/color notes:** Renamed tiles keep their existing icons (Recommend → Hidden Gems, History → Watch Again). Clean up unused icon imports from removed tiles. New tiles in later chunks will need icon + color assignments.

## Chunk 2: Medium — More menu

⬜ **2a. New `#/more` route** — Secondary tile page for low-frequency modes  
⬜ **2b. Move low-priority tiles** — Age Rating, Release Year etc. into More  
⬜ **2c. Web + Android TV implementations**

Files: new route/page, `browse/index.tsx` (add "More" tile), TV `BrowseModesFragment.kt`

## Chunk 3: Medium — Vault

⬜ **3a. Multi-level picker** — Extend the picker pattern to support sub-levels  
⬜ **3b. Vault categories** — Awards, Seasonal, Franchises, Studios, Adaptations, etc.  
⬜ **3c. Data model** — Define award/franchise/studio groupings (TMDb collections, genre filters)

Files: new Vault components, `browseModes.ts`, possibly plugin endpoints

## Chunk 4: Hard — Mood & Story Themes

⬜ **4a. Tag infrastructure** — How to map moods/themes to actual items. Options:
- TMDb keywords API (needs per-item lookup, heavy on API calls)
- Genre→mood mapping (e.g. Comedy+Romance → "Feel Good")
- User-managed tags (manual, but no external dependencies)
⬜ **4b. Mood picker** — Randomized subset, weighted probabilities  
⬜ **4c. Story Themes picker** — Same pattern, larger list

## Chunk 5: Polish

⬜ **5a. Icons** — Update tile icons to match new naming  
⬜ **5b. Icon colors** — Ensure consistency with new/renamed tiles  
⬜ **5c. Android TV parity** — Port new tiles to TV client

---

## Current tile inventory (post-Chunk 1)

### Movies (13 tiles)
1. All
2. Trending
3. Top Rated
4. Genres
5. Hidden Gems
6. Just Added
7. New Releases
8. Random
9. Critics' Picks
10. Watch Again
11. Decades
12. Studios
13. Age Rating

### TV Shows (12 tiles)
1. All
2. Trending
3. Top Rated
4. Genres
5. Hidden Gems
6. Just Added
7. New Releases
8. Random
9. Watch Again
10. Decades
11. Networks
12. Age Rating

---

## Two codebases

| | 12.x | 10.11 |
|---|---|---|
| Repo | `jellyfin-web/` | `jellyfin-web-10.11/` |
| Branch | `browse-modes` | `browse-modes-10.11` |
| App dir | `apps/modern/` | `apps/experimental/` |
| Settings | `utils/settings.ts` | `utils/items.ts` |
| Layout guard | `layoutManager.modern` | removed (RootAppRouter always loads experimental) |

**Changes must be made in both.** The 10.11 backport has additional adaptations (no LibraryProvider, path-based CollectionType inference, etc.).

## Test environments

| | 10.11 | 12.0 |
|---|---|---|
| Container | `jf-test-10` | `jellyfin` |
| Port | 8196 | host networking |
| Web dist | bind-mounted | docker cp |
| Media | /video (prod mount) | /video + /media |

## Sessions log

| Date | Chunk | What |
|---|---|---|
| 2026-08-03 | — | Plan created |
| 2026-08-03 | 1a+1b | Removed 4 tiles + renamed 2. Both codebases updated, built, pushed |
| 2026-08-03 | 1c | Reordered tiles to match spec. Both codebases updated, built, pushed |
