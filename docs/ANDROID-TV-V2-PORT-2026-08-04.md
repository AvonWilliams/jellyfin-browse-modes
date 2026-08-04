# Android TV — Browse Modes v2.0 Port Implementation Log

## Session: 2026-08-04

Full port of v2.0 features from jellyfin-web to jellyfin-androidtv (branch `browse-modes`).
Emulator-tested on Android TV API 26 against Jellyfin 10.11.11 (Docker, port 8196).

---

## Completed features

### Tile cleanup
- **Removed:** Highest Rated, Longest, Favorites, Unwatched
- **Renamed:** Best Unseen → Hidden Gems, Recently Played → Watch Again
- **Reordered** to match web spec

### 5 tag-based discovery modes
Mood, Story Themes, Plot Elements, Worlds, Styles — each backed by a curated list
of TMDb keywords (6,378 total, converted from `browseTags.ts` to `BrowseTags.kt`).
Tags are intersected with the library's available tags at render time; only tags with
matching items appear.

Grid picker (`TagPickerFragment`) and ribbon shelves (`TagBrowseRowsFragment`) both
implemented. Default is ribbon shelves; toggle in Library Settings → Display Preferences.

Tag names are title-cased for display ("feel good" → "Feel Good"), with original
lowercase preserved for Jellyfin API filtering.

### Decades and Age Rating tiles
- **Decades:** groups years from `/Items/Filters` into decade tiles (1980s, 1990s...).
  Natural chronological order, no sort needed.
- **Age Rating:** fetches `officialRatings` from `/Items/Filters`, shows as grid.
  Sort button (A–Z / Z–A / Random) available.

### Date cutoff plumbing
`minDateLastSaved`/`minPremiereDate` on `BrowsePreset`, seeded as 9-month windows
for Just Added and New Releases. Full chain wired: `LibraryPreferences` →
`FilterOptions` → `ItemRowAdapter.setFilters()` → `GetItemsRequest`.

### Sort controls
Sort button as first grid tile (80dp, smaller than 200dp tag tiles). Cycles A–Z →
Z–A → Random. Uses `PresenterSelector` for smaller visual size. Available in tag
picker, age rating picker, and ribbon shelves. Not applied to Decades (chronological).

### Grid/shelf toggle
`enableTagRibbonShelves` library preference (default: `true`). Checkbox in
Display Preferences screen. Read on each tile click, so switching is immediate.
Available at: Library → Settings → Display Preferences → "Tag ribbon shelves."

### Drawables
5 new vector drawables: `ic_mood`, `ic_book`, `ic_timeline`, `ic_world`, `ic_palette`.

### Locale cleanup
Removed `lbl_unwatched` and `lbl_favorites` from 55 locale files.

---

## Files created (13)

| File | Purpose |
|---|---|
| `browsemodes/BrowseTags.kt` | 6,378 curated TMDb keywords (6,410 lines) |
| `browsemodes/TagPickerFragment.kt` | Tag picker grid with sort + title-case |
| `browsemodes/TagItemsFragment.kt` | Items filtered by a single tag |
| `browsemodes/TagBrowseRowsFragment.kt` | Ribbon shelves (RowsSupportFragment) with sort |
| `browsemodes/DecadesPickerFragment.kt` | Decade picker grid (chronological) |
| `browsemodes/DecadesItemsFragment.kt` | Items filtered by decade year range |
| `browsemodes/AgeRatingPickerFragment.kt` | Rating picker grid with sort |
| `browsemodes/AgeRatingItemsFragment.kt` | Items filtered by official rating |
| `res/drawable/ic_mood.xml` | Smiley face icon |
| `res/drawable/ic_book.xml` | Book icon |
| `res/drawable/ic_timeline.xml` | Timeline/branch icon |
| `res/drawable/ic_world.xml` | Globe icon |
| `res/drawable/ic_palette.xml` | Palette icon |

## Files modified (12)

| File | Changes |
|---|---|
| `browsemodes/BrowseModes.kt` | Enums +5/-4, 2 renames, 3 new destinations, date presets |
| `browsemodes/BrowseModesFragment.kt` | TAG_PICKER/DECADES/AGE_RATING routing, pref-based ribbon toggle |
| `navigation/Destinations.kt` | 7 new destinations (tagPicker, libraryByTagItems, decadesPicker, etc.) |
| `constant/Extras.kt` | Added Tag, Decade, Rating constants |
| `preference/LibraryPreferences.kt` | Added filterMinDateLastSaved, filterMinPremiereDate, enableTagRibbonShelves |
| `data/model/FilterOptions.kt` | Added date fields + LocalDateTime parsing |
| `browsing/BrowseGridFragment.java` | Reads date prefs into FilterOptions |
| `itemhandling/ItemRowAdapter.java` | Applies date cutoffs in setFilters() |
| `itemhandling/ItemRowAdapterHelper.kt` | Added setItemsDateCutoffs helper |
| `browsing/DisplayPreferencesScreen.kt` | Tag ribbon shelves checkbox |
| `res/values/strings.xml` | 11 new strings, 4 removed, 2 renamed |
| `res/values/colors.xml` | 7 new colors, 4 removed, 2 renamed |

---

## Bugs found and fixed during emulator testing

1. **BaseItemDto `MissingFieldException`** — synthetic JSON `{"Name":"tag"}` was
   missing required `Id` (UUID) and `Type` (BaseItemKind) fields. Fixed by adding
   `put("Id", UUID.randomUUID())` and `put("Type", "Folder")` to `buildJsonObject`.

2. **JSON injection** — tag names containing `"` or `\` (e.g. `"country western" soundtrack`)
   would produce malformed JSON. Fixed by using `buildJsonObject { put() }` instead
   of string interpolation.

3. **Duplicate `curatedTagsFor` functions** — merged into single `internal` function
   shared between TagPickerFragment and TagBrowseRowsFragment.

4. **TitleView click handler** — the title bar isn't in Leanback's DPAD focus flow.
   Reverted to sort button as first grid tile. Used `PresenterSelector` for smaller
   (80dp) visual size to distinguish from content tiles.

---

## Emulator verification

Tested on Android TV API 26 emulator against Jellyfin 10.11.11 (jf-test-10:8196).

- ✅ All 16 browse mode tiles render correctly in grid
- ✅ Removed tiles (Highest Rated, Longest, Favorites, Unwatched) absent
- ✅ New tiles (Mood, Story Themes, Plot Elements, Worlds, Styles, Decades, Age Rating) present
- ✅ Renamed tiles (Hidden Gems, Watch Again) show correct text
- ✅ Tag picker opens, fetches tags from `/Items/Filters`, shows title-cased tags
- ✅ Tag click navigates to filtered items
- ✅ Sort button cycles A–Z → Z–A → Random, re-sorts grid
- ✅ Sort tile renders smaller (80dp) than tag tiles (200dp)
- ✅ Ribbon shelves show per-tag poster rows (when toggled via preference)
- ✅ Decades show chronological list, click navigates to decade-filtered items
- ✅ Age ratings show alphabetical list with sort
- ✅ Trending/Top Rated work (with Browse Modes plugin installed)
- ✅ Date cutoff preferences stored and applied
- ✅ No crashes in logcat during extended testing
- ✅ Grid/shelf toggle preference visible in Display Preferences screen
- ✅ Plugin installed on test server (TMDb API key configured)

---

## Build

```bash
cd jellyfin-androidtv
./gradlew assembleDebug
# Output: app/build/outputs/apk/debug/jellyfin-androidtv-v0.0.0-dev.1-debug.apk

# Emulator
export ANDROID_HOME=$PWD/../.toolchain/android-sdk
$ANDROID_HOME/emulator/emulator -avd tv-test -no-audio -no-window -gpu swiftshader_indirect &
$ANDROID_HOME/platform-tools/adb -s emulator-5554 install -r <apk>
```

---

## Deferred / future work

- **Most/Fewest items sort** — requires per-tag GET /Items calls, expensive with 50+ tags
- **Decades per-tile styling** — era-appropriate icons and warm→cool color ramp (web has this)
- **Age rating per-tile styling** — severity-graded icons (web has `getRatingStyle`)
- **Runtime sort/shuffle in ribbon shelves** — per-row item sort (currently random)
- **Empty-state messages** — grid pickers show blank when API returns 0 tags
