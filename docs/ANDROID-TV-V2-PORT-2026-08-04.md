# Android TV — Browse Modes v2.0 Port Implementation Log

## Session: 2026-08-04

Porting v2.0 features from jellyfin-web to jellyfin-androidtv (branch `browse-modes`).
This session covers tile cleanup, date cutoffs, 5 tag-based discovery modes, decades,
age rating, ribbon shelves framework, and locale cleanup.

---

## Phase 1 — Core v2.0 Port (morning)

### 1. Tile cleanup

**Removed:** HIGHEST_RATED, LONGEST, FAVORITES, UNWATCHED

**Renamed (keys preserved for backward compat with display preferences):**
- BEST_UNSEEN("bestunseen") → HIDDEN_GEMS("bestunseen")
- RECENTLY_PLAYED("recentlyplayed") → WATCH_AGAIN("recentlyplayed")

**Tile order — Movies (18 tiles):**
all, trending, topRated, genres, mood, storyThemes, plotElements, worlds, styles,
hiddenGems, justAdded, newReleases, random, criticsPicks, watchAgain, decades,
studios, ageRating

**Tile order — TV (17 tiles):**
all, trending, topRated, genres, mood, storyThemes, plotElements, worlds, styles,
hiddenGems, justAdded, newReleases, random, watchAgain, decades, networks, ageRating

### 2. 5 tag-based discovery modes

Added MOOD, STORY_THEMES, PLOT_ELEMENTS, WORLDS, STYLES to BrowseMode enum.
Each opens TagPickerFragment — a grid of curated tags intersected with the library's
available tags. Tag → TagItemsFragment shows matching items.

6,378 curated TMDb keywords converted from `browseTags.ts` to `BrowseTags.kt`.

### 3. Date cutoff support

Added `minDateLastSaved`/`minPremiereDate` fields to `BrowsePreset` and
`LibraryPreferences`. Just Added and New Releases seeded with 9-month windows.

### 4. Strings, colors, drawables

5 new vector drawable icons: ic_mood, ic_book, ic_timeline, ic_world, ic_palette.
7 new strings, 7 new colors. 4 removed strings/colors.

---

## Phase 2 — Date Cutoff Plumbing (afternoon)

Research found the complete display-preferences-to-API-query chain:

```
BrowseModesFragment.seed() → LibraryPreferences (server-side display prefs)
                                    ↓
BrowseGridFragment.buildAdapter() → reads prefs → FilterOptions
                                    ↓
ItemRowAdapter.setFilters() → copies FilterOptions → GetItemsRequest
                                    ↓
ItemRowAdapterHelper → copy() applies filter params to query
                                    ↓
itemsApi.getItems() → actual HTTP request
```

### Changes made

**FilterOptions.kt** — added `minDateLastSaved: String?` and `minPremiereDate: String?`
fields with parsed `LocalDateTime` getters. Parser handles ISO 8601 strings with
optional Z suffix.

**ItemRowAdapterHelper.kt** — added `setItemsDateCutoffs(request, minDateLastSaved?,
minPremiereDate?)` that calls `request.copy(minDateLastSaved = ..., minPremiereDate =
...)`.

**ItemRowAdapter.java** (setFilters, default case) — after setItemsFilter, also calls
setItemsDateCutoffs to propagate date fields into the query.

**BrowseGridFragment.java** (buildAdapter) — reads `filterMinDateLastSaved` and
`filterMinPremiereDate` from libraryPreferences and sets them on FilterOptions, right
beside the existing favoriteOnly/unwatchedOnly lines.

The SDK's `GetItemsRequest` already has `minDateLastSaved: LocalDateTime` and
`minPremiereDate: LocalDateTime` fields (confirmed in jellyfin-model 1.7.1 JAR).

---

## Phase 3 — Decades & Age Rating Tiles

### Added to BrowseModes.kt

**New enum values:** DECADES("decades"), AGE_RATING("agerating")

**New destinations:** DECADES_PICKER, AGE_RATING_PICKER

**Mode definitions:**
- `decadesMode` — icon: ic_calendar, tint: #7E9CD8, destination=DECADES_PICKER
- `ageRatingMode` — icon: ic_flask, tint: #9CCC65, destination=AGE_RATING_PICKER

Both added to movie and series arrays matching web spec position.

### New fragments (4 files)

**DecadesPickerFragment** — fetches available years from `/Items/Filters`,
  groups into decades via `(year / 10) * 10`, shows decade labels ("1980s").
  On click → DecadesItemsFragment with `years = (1980..1989).toSet()`.

**DecadesItemsFragment** — shows items for a decade filtered by `years` set.
  Sort: PREMIERE_DATE DESCENDING.

**AgeRatingPickerFragment** — fetches available ratings from `/Items/Filters`,
  shows as grid sorted alphabetically. On click → AgeRatingItemsFragment.

**AgeRatingItemsFragment** — shows items filtered by `officialRatings = setOf(rating)`.

All four follow the TagPickerFragment/TagItemsFragment pattern with identical
conventions (Koin DI, lifecycleScope.launch, withContext(Dispatchers.IO),
MutableObjectAdapter, CardPresenter, OnItemViewClickedListener).

### Wiring

- `Extras.kt` — added `Decade = "decade"`, `Rating = "rating"`
- `Destinations.kt` — added 4 new destinations: decadesPicker, libraryByDecadeItems,
  ageRatingPicker, libraryByAgeRatingItems
- `BrowseModesFragment.kt` — added DECADES_PICKER and AGE_RATING_PICKER cases
- `strings.xml` — added lbl_browse_mode_decades, lbl_browse_mode_age_rating
- `colors.xml` — added browse_mode_decades (#7E9CD8), browse_mode_age_rating (#9CCC65)

---

## Phase 4 — Ribbon Shelves Framework (Chunk 5c)

### Design

The web shows tag-based modes as stacked horizontal poster shelves (one per tag)
with sort/shuffle controls and a grid/shelf toggle. Android TV gets a fully
implemented alternative behind a compile-time toggle.

### Toggle mechanism

```kotlin
// BrowseModes.kt
const val USE_TAG_RIBBON_SHELVES = false  // true = shelves, false = grid picker
```

When `false` (default): tag modes open `TagPickerFragment` (flat grid of tag names).
When `true`: tag modes open `TagBrowseRowsFragment` (per-tag horizontal poster shelves).

### TagBrowseRowsFragment

Extends `RowsSupportFragment`. For each curated tag with items in the library:
- Creates a `ListRow` with `HeaderItem(tagName)`
- Creates an `ItemRowAdapter` with a `GetItemsRequest` filtered by that tag
- Items load independently per-row via `Retrieve()`
- Maximum 30 rows (MAX_ROWS constant), items sorted randomly per row
- Card height: 260dp (poster)

Pattern follows `BrowseViewFragment` row construction with `ItemRowAdapter` +
`ListRow` + `HeaderItem`. Lazy loading is handled by ItemRowAdapter's built-in
pagination.

### Reversibility

To revert to the grid picker: set `USE_TAG_RIBBON_SHELVES = false` in
`BrowseModes.kt`. Both paths (`TagPickerFragment` and `TagBrowseRowsFragment`)
are fully independent. Neither shares state with the other.

---

## Phase 5 — Locale Cleanup

Removed dead `lbl_unwatched` and `lbl_favorites` string entries from all 55
locale-specific `values-*/strings.xml` files. These resources were orphaned
when the Unwatched and Favorites tiles were removed. No Kotlin source references
either string.

---

## Complete File Inventory

### Modified (12 files)

| File | Changes |
|---|---|
| `browsemodes/BrowseModes.kt` | Enums (+5, -4, rename 2), definitions, arrays, date preset fields, destinations (+3), ribbon toggle |
| `res/values/strings.xml` | Added 9 strings, removed 4, renamed 2 |
| `res/values/colors.xml` | Added 7 colors, removed 4, renamed 2 |
| `preference/LibraryPreferences.kt` | Added filterMinDateLastSaved, filterMinPremiereDate |
| `constant/Extras.kt` | Added Tag, Decade, Rating |
| `navigation/Destinations.kt` | Added 7 destinations, imported 5 new fragments |
| `browsemodes/BrowseModesFragment.kt` | Added DECADES_PICKER, AGE_RATING_PICKER, TAG_PICKER+ribbon toggle, date seed lines |
| `browsing/BrowseGridFragment.java` | Read date prefs → FilterOptions (2 lines) |
| `itemhandling/ItemRowAdapter.java` | Apply date cutoffs in setFilters (2 lines) |
| `itemhandling/ItemRowAdapterHelper.kt` | Added setItemsDateCutoffs helper |
| `data/model/FilterOptions.kt` | Added date fields + LocalDateTime parsing |
| `jellyfin-browse-modes/PROGRESS.md` | Session summary entry |

### Created (13 files)

| File | Lines | Purpose |
|---|---|---|
| `browsemodes/BrowseTags.kt` | 6,410 | 6,378 curated TMDb keywords |
| `browsemodes/TagPickerFragment.kt` | 128 | Tag picker grid |
| `browsemodes/TagItemsFragment.kt` | 100 | Items filtered by tag |
| `browsemodes/TagBrowseRowsFragment.kt` | 155 | Ribbon shelves (alternative to picker) |
| `browsemodes/DecadesPickerFragment.kt` | 120 | Decade picker grid |
| `browsemodes/DecadesItemsFragment.kt` | 100 | Items filtered by decade |
| `browsemodes/AgeRatingPickerFragment.kt` | 110 | Rating picker grid |
| `browsemodes/AgeRatingItemsFragment.kt` | 95 | Items filtered by rating |
| `res/drawable/ic_mood.xml` | 9 | Smiley face vector icon |
| `res/drawable/ic_book.xml` | 9 | Book vector icon |
| `res/drawable/ic_timeline.xml` | 9 | Branch graph vector icon |
| `res/drawable/ic_world.xml` | 9 | Globe vector icon |
| `res/drawable/ic_palette.xml` | 9 | Palette vector icon |
| `docs/ANDROID-TV-V2-PORT-2026-08-04.md` | — | This log |

### Cleaned (55 locale files)

Removed `lbl_unwatched` and `lbl_favorites` from all `values-*/strings.xml`.

---

## Build Verification

⚠️ **Not built.** Requires Android SDK + Gradle toolchain.

```bash
cd jellyfin-androidtv
./gradlew assembleDebug
```

### Expected potential issues on first build

1. **`QueryFiltersLegacy`** — if not present in SDK 1.7.1, the `.tags`, `.years`,
   `.officialRatings` access in the picker fragments will fail. The type is referenced
   as `org.jellyfin.sdk.model.api.QueryFiltersLegacy`. If unavailable, fall back to
   a raw `Map` deserialization from the GET call.

2. **`ItemRowAdapter` constructor** — `TagBrowseRowsFragment` passes `cardPresenter as
   Presenter`. If this cast fails, use `CardPresenter`'s parent type directly.

3. **`MutableObjectAdapter<Row>`** — may have generic variance issues when
   `ItemRowAdapter` expects `MutableObjectAdapter<Row>` (Java) vs Kotlin's type
   inference. Check the exact constructor signature matched.

4. **Koin injection in `RowsSupportFragment`** — `TagBrowseRowsFragment` extends
   `RowsSupportFragment` which is a `Fragment` subclass. Koin `by inject()` should
   work, but if not, switch to embedding `RowsSupportFragment` as a child fragment
   inside a plain `Fragment` (following `EnhancedBrowseFragment` pattern).

5. **`R.string.lbl_all_items`, `R.string.random`** — verify these still exist.
   These are upstream Jellyfin strings, not added by Browse Modes.

---

## Deferred Items

### Tag picker card sizing
Tag names are much wider than studio names. Grid columns (6) and card height (200dp)
may need adjustment after on-TV testing.

### Sort / shuffle controls
Tag mode ribbon shelves on web have sort dropdown + shuffle button. Not yet
implemented on Android TV. Sort options to add: Random, A-Z, Z-A, Most items,
Fewest items.

### Grid / shelf toggle
Web has a toggle between ribbon shelves and flat grid view. Android TV treats
them as separate fragments switched via `USE_TAG_RIBBON_SHELVES` constant.
A runtime toggle could be added later.

### Decades per-tile styling
Web has era-appropriate icons and a warm→cool color ramp for decade picker tiles.
Android TV uses a single ic_calendar icon for all decades. Per-tile styling
requires a custom Presenter.

### Age rating per-tile styling
Web has severity-graded icons and colors for each rating level. Android TV uses
a single ic_flask icon. Per-tile styling requires a custom Presenter.

### Critics Picks icon tint
`criticsPicksMode` has `iconTint = null` — the drawable (`ic_rt_fresh`) carries
its own red tint. This is intentional and matches the web.
