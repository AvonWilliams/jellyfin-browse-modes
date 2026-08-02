# Browse Modes v2.0 — Implementation Plan

Based on `category-upgrades.md`. Tracked in chunks by difficulty.

**Status key:** ⬜ not started | 🔵 in progress | ✅ done

---

## Chunk 1: Easy — Tile cleanup & toggles

⬜ **1a. Remove tiles:** Highest Rated (local sort), Longest, Favorites, Unwatched  
⬜ **1b. Rename tiles:** Best Unseen → Hidden Gems, Recently Played → Watch Again  
⬜ **1c. Reorder tiles:** Match the spec order in `category-upgrades.md`  
⬜ **1d. Per-library toggle:** Plugin config option to enable/disable browse modes per library type  
⬜ **1e. Per-tile toggle:** Plugin config option to show/hide individual tiles; client filters enabled list

Files: `browseModes.ts`, `en-us.json`, `PluginConfiguration.cs`, `config.html`

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

## Sessions log

| Date | Chunk | What |
|---|---|---|
| 2026-08-03 | — | Plan created |
