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

## Chunk 2: Medium — Showcase (next priority)

⬜ **2a. Multi-level picker** — Extend the picker pattern to support sub-levels ✅ <em>navigation done</em>  
⬜ **2b. Showcase categories** — Awards, Seasonal, Franchises, Studios, Adaptations, etc. ✅ <em>tree in place; Studios wired, rest placeholder</em>  
⬜ **2c. Data model** — Define award/franchise/studio groupings (TMDb collections, genre filters)  

Files: `showcaseCategories.ts`, `browse/index.tsx`, possibly plugin endpoints

> ~~Chunk 2 was originally "More menu" (secondary tile page for low-frequency modes). Removed — changed direction.~~

## Chunk 4: Hard — Mood & Story Themes ✅

✅ **4a. Tag infrastructure** — Downloaded official TMDb keyword export (92,533 keywords),
AI-classified into 5 categories (mood: 510, theme: 1,517, plot: 1,749, world: 1,975,
style: 627). Stored as TypeScript constants in `browseTags.ts`. At render time,
`filters.Tags` from the API is intersected against the curated lists — only tags with
matching items appear.

✅ **4b. Mood picker** — `BrowseMode.Mood` tile with randomized infinite-scroll picker.
✅ **4c. Story Themes picker** — Same pattern.
✅ **Bonus: 3 additional categories** — Plot Elements, Worlds, Styles.

## Chunk 5: Polish

✅ **5a. Icons** — Update tile icons to match new naming  
✅ **5b. Icon colors** — Ensure consistency with new/renamed tiles  
✅ **5c. Android TV parity** — Port new tiles to TV client (done 2026-08-04)

---

## Current tile inventory (post-Chunk 1)

### Movies (19 tiles)
1. All
2. Trending
3. Top Rated
4. Genres
5. 😊 Mood
6. 🎬 Story Themes
7. 📋 Plot Elements
8. 🌍 Worlds
9. 🎨 Styles
10. Hidden Gems
11. Just Added
12. New Releases
13. Random
14. Critics' Picks
15. Watch Again
16. Decades
17. Studios
18. Age Rating
19. ✨ Showcase

### TV Shows (18 tiles)
1. All
2. Trending
3. Top Rated
4. Genres
5. 😊 Mood
6. 🎬 Story Themes
7. 📋 Plot Elements
8. 🌍 Worlds
9. 🎨 Styles
10. Hidden Gems
11. Just Added
12. New Releases
13. Random
14. Watch Again
15. Decades
16. Networks
17. Age Rating
18. ✨ Showcase

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
| 2026-08-03 | 4 | Tag infrastructure + 5 new browse modes. AI-classified 92K TMDb keywords. Infinite-scroll ribbon shelves with sort/shuffle. Grid/shelf toggle. 9-month date cutoffs on New Releases and Just Added. Both codebases built, deployed, released as v2.0.0 |
| 2026-08-04 | 5c | Android TV v2.0 port complete — all tiles, tag discovery, ribbon shelves, decades, age rating, date cutoffs, sort, grid/shelf toggle, 5 new drawables, 55 locale files cleaned |
