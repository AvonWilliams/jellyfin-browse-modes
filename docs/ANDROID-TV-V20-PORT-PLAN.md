# Android TV Browse Modes — Port Plan: upstream `v0.20.0-beta.1` (Compose migration)

- **Date:** 2026-09-12
- **Fork repo:** `jellyfin-androidtv` branch `browse-modes`
- **Fork base:** `14a5e160efbb33d471ef2efe7ed9073fc2206785` ("perf: Defer LiveTV check…", 2026-05-09, merge-base of the fork with `v0.19.9`)
- **Port target:** `v0.20.0-beta.1` = commit `2a5936994638e5415361f400b6c3a0209f4aec33` (2026-09-08)
- **Constraint honored:** this plan was produced with read-only git (`git fetch origin v0.20.0-beta.1`, `git show <tag>:<path>`, `git diff v0.19.9 v0.20.0-beta.1`, `git grep`). The `browse-modes` branch/working tree was never modified; a concurrent rebase owns it.

---

## 0. Executive summary

The fork is ~15 new Leanback/Compose files under `ui/browsing/browsemodes/` plus ~15 small edits to shared files. Upstream `v0.20` replaced the entire Options-DSL settings stack and the legacy image-card stack with Compose. The port survives **without touching the 15 `browsemodes/*.kt` fragments in substance** — they are plain Leanback (`VerticalGridSupportFragment` / `RowsSupportFragment`) and Leanback is still a dependency in v0.20. What breaks is concentrated in six shared surfaces:

1. `ui/navigation/Destinations.kt` — 11 fork-added destination helpers use the deleted `fragmentDestination<T>(Extras.X to y)` API.
2. `ui/itemhandling/ItemLauncher.java` — the browse-modes interception must move into the new `getUserViewDestination` method and the new `libraryBrowser(baseItem, null)` signature.
3. `ui/presentation/CardPresenter.java` + `ui/card/LegacyImageCardView.java` + `res/layout/view_card_legacy_image.xml` — **deleted**; the rank badge must be re-implemented in Compose.
4. `ui/browsing/DisplayPreferencesScreen.kt` — **deleted**; the two new toggles move into the Compose `SettingsLibrariesDisplayScreen`.
5. `preference/LibraryPreferences.kt`, `data/model/FilterOptions.kt`, `constant/Extras.kt` — fork-added fields must be re-applied (upstream versions are otherwise unchanged).
6. Date-cutoff plumbing (`ItemRowAdapterHelper.kt`, `ItemRowAdapter.java`, `BrowseGridFragment.java`) — verified to re-apply cleanly.

Total effort: **~4–5 engineer-days** (see §3).

---

## 1. Inventory: what breaks, feature by feature

| # | Fork feature | Fork files | Upstream change that affects it | Severity |
|---|---|---|---|---|
| 1 | Launcher interception ("open library → browse modes") | `ui/itemhandling/ItemLauncher.java` | Logic moved into extracted `getUserViewDestination()`; `libraryBrowser(baseItem)` → `libraryBrowser(baseItem, null)` (new `includeType` param). `Destinations.browseModes` gone. | Medium — small, but must be placed in the new method |
| 2 | Tile grid (`BrowseModesFragment` + `BrowseModeTilePresenter` + `BrowseModes.kt`) | `browsemodes/BrowseModesFragment.kt`, `BrowseModeTilePresenter.kt`, `BrowseModes.kt` | `Destinations.kt` old API; `Extras.BrowseMode` gone. Otherwise Leanback + Compose still available. | Low-Medium (mostly mechanical) |
| 3 | Picker fragments (Tag/Decades/AgeRating/ByStudio) | `TagPickerFragment.kt`, `DecadesPickerFragment.kt`, `AgeRatingPickerFragment.kt`, `ByStudioFragment.kt` | `Destinations.*` old API; `Extras.*` constants. | Low |
| 4 | Items fragments + ribbon shelves | `StudioItemsFragment.kt`, `DecadesItemsFragment.kt`, `AgeRatingItemsFragment.kt`, `TagItemsFragment.kt`, `TagBrowseRowsFragment.kt` | `Destinations.*` old API; `Extras.*`. | Low |
| 5 | DiscoverFragment + rank badge | `DiscoverFragment.kt`, `DiscoverCardPresenter.kt` | **`LegacyImageCardView.java`, `CardPresenter.java`, `view_card_legacy_image.xml` all DELETED** → replaced by Compose `ItemCard`/`ItemCardBaseItemOverlay` and Kotlin `CardPresenter.kt`. | **High** — the only genuinely re-designed piece |
| 6 | Sort/shuffle controls (sort toggle, reshuffle, interleaved random, Most/Fewest) | inside `TagPickerFragment.kt` (+ `AgeRatingPickerFragment.kt`) | Self-contained (custom `PresenterSelector`, synthetic `BaseItemDto` JSON); only `Destinations.libraryByTagItems`/`libraryByAgeRatingItems` break. | Low |
| 7 | Grid/shelf toggle (tag ribbon shelves) | `preference/LibraryPreferences.kt` + `DisplayPreferencesScreen.kt` | **`DisplayPreferencesScreen.kt` deleted**; settings now Compose `SettingsLibrariesDisplayScreen`. | Medium — new Compose surface |
| 8 | Browse-modes on/off toggle | `LibraryPreferences.kt` + `DisplayPreferencesScreen.kt` | Same as #7. | Medium |
| 9 | Date-cutoff sort presets (Just Added / New Releases) | `FilterOptions.kt`, `ItemRowAdapterHelper.kt`, `ItemRowAdapter.java`, `BrowseGridFragment.java`, `LibraryPreferences.kt` | Upstream versions of `FilterOptions`/`ItemRowAdapter.java` unchanged; `ItemRowAdapterHelper` changed only in `retrieveUserViews` (far from `setItemsFilter`); `BrowseGridFragment` filter block unchanged. | Low (clean re-apply) |
| 10 | 11 destination helpers | `ui/navigation/Destinations.kt` | Whole-file rewrite to lambda/`Bundle` builder. | Medium |
| 11 | `Extras` constants (`BrowseMode`, `Studio`, `Tag`, `Decade`, `Rating`) | `constant/Extras.kt` | File unchanged upstream → clean re-add. | Low |
| 12 | Resources: 8 drawables, 17 colors, ~25 strings, manifest | `res/**` | New drawables/colors/strings are additive. **`AndroidManifest.xml` `configChanges` is already present in v0.20 (line 128)** → fork change is a no-op. | Low |
| 13 | Incidental fork tweaks | `KnownDefects.kt` (add `AFTMM`), `RecommendedServerIssueExtensions.kt` (string rename), 90 translation files (`-2` each: `lbl_favorites`, `lbl_unwatched`), `README.md` | Not browse-mode features. Upstream may already contain some; triage individually (see §4). | N/A — decide per item |

### Verified non-breakages (important — do not "fix" these)

- **Leanback is still a dependency** (`app/build.gradle.kts`: `libs.androidx.leanback.core` + `libs.androidx.leanback.preference`), and `VerticalGridSupportFragment` / `RowsSupportFragment` are still used by `BrowseFolderFragment.kt`, `EnhancedBrowseFragment.java`, `HomeRowsFragment.kt`, etc. All 15 fork fragments compile against them.
- **`CardPresenter(true, staticHeight)`** constructor still exists (`CardPresenter.kt` secondary constructor `(showInfo: Boolean, staticHeight: Int)`). All picker/items fragments keep compiling.
- **`MutableObjectAdapter`** still at `ui/presentation/MutableObjectAdapter.kt`.
- **`BaseItemDtoBaseRowItem(item)`** constructor signature unchanged (`item, preferParentThumb, staticHeight, selectAction, preferSeriesPoster`).
- **`ItemRepository.itemFields`** still present.
- **`NavigationRepository.navigate(destination: Destination)`** signature unchanged.
- **`copyWithDisplayPreferencesId`** still exists in package `org.jellyfin.androidtv.util.sdk.compat` (moved into `JavaCompat.kt` with `@file:JvmName("JavaCompat")`; the Kotlin import is unchanged).
- **Koin `by inject<ApiClient>()`** delegate still used in v0.20 (`ByGenreFragment.kt`).
- **`Destinations.libraryByGenres(item, includeType)`** and **`Destinations.libraryBrowser(item, includeType = null)`** still exist — the fork's own calls to *upstream* destinations keep working.
- **Fragment registration is by class name.** `ui/browsing/DestinationFragmentView.kt` instantiates fragments reflectively (`fragmentManager.fragmentFactory.instantiate(classLoader, entry.name.name)`). There is **no** registry/map to update for the 11 new fragments — referencing them from `Destinations.kt` is sufficient.

---

## 2. Re-implementation approach, per broken piece

### 2.1 Launcher interception — `app/src/main/java/org/jellyfin/androidtv/ui/itemhandling/ItemLauncher.java`

In v0.20 the logic the fork patched lives in `getUserViewDestination(@Nullable BaseItemDto)` (returns `Destination.Fragment`), MOVIES/TVSHOWS case:

```java
LibraryPreferences displayPreferences = preferencesRepository.getValue().getLibraryPreferences(baseItem.getDisplayPreferencesId());
boolean enableSmartScreen = displayPreferences.get(LibraryPreferences.Companion.getEnableSmartScreen());
if (!enableSmartScreen) return Destinations.INSTANCE.libraryBrowser(baseItem, null);
else return Destinations.INSTANCE.librarySmartScreen(baseItem);
```

Re-apply the fork's interception here (preserving the new `libraryBrowser(baseItem, null)` call):

```java
boolean enableBrowseModes = displayPreferences.get(LibraryPreferences.Companion.getEnableBrowseModes());
boolean enableSmartScreen = displayPreferences.get(LibraryPreferences.Companion.getEnableSmartScreen());

if (enableBrowseModes) return Destinations.INSTANCE.browseModes(baseItem);
else if (!enableSmartScreen) return Destinations.INSTANCE.libraryBrowser(baseItem, null);
else return Destinations.INSTANCE.librarySmartScreen(baseItem);
```

`Destinations.INSTANCE.browseModes(baseItem)` returns `Destination.Fragment`, which is assignable to the method's `Destination.Fragment` return type.

### 2.2 Destination helpers — `app/src/main/java/org/jellyfin/androidtv/ui/navigation/Destinations.kt`

New builder API (from `ui/navigation/Destination.kt`):

```kotlin
sealed interface Destination {
    data class Fragment(
        val fragment: KClass<out androidx.fragment.app.Fragment>,
        val arguments: Bundle = createBundle(),
    ) : Destination
}

inline fun <reified T : Fragment> fragmentDestination(
    noinline arguments: (Bundle.() -> Unit)? = null,
) = Destination.Fragment(fragment = T::class, arguments = createBundle(arguments))
```

The fork's old form `fragmentDestination<BrowseModesFragment>(Extras.Folder to Json.encodeToString(item))` becomes a lambda of `Bundle` `put*` calls. Rewrite the 11 fork-added helpers (and the imports at the top of the file):

```kotlin
// TODO only pass item id instead of complete JSON to browsing destinations
fun browseModes(item: BaseItemDto) = fragmentDestination<BrowseModesFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
}

fun discover(item: BaseItemDto, browseMode: String) = fragmentDestination<DiscoverFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.BrowseMode, browseMode)
}

fun libraryByStudio(item: BaseItemDto, includeType: String) = fragmentDestination<ByStudioFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.IncludeType, includeType)
}

fun libraryByStudioItems(item: BaseItemDto, studio: String) = fragmentDestination<StudioItemsFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.Studio, studio)
}

fun tagPicker(item: BaseItemDto, browseMode: String) = fragmentDestination<TagPickerFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.BrowseMode, browseMode)
}

fun libraryByTagItems(item: BaseItemDto, tag: String, includeType: String) = fragmentDestination<TagItemsFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.Tag, tag)
    putString(Extras.IncludeType, includeType)
}

fun decadesPicker(item: BaseItemDto) = fragmentDestination<DecadesPickerFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
}

fun libraryByDecadeItems(item: BaseItemDto, decadeStart: Int, includeType: String) = fragmentDestination<DecadesItemsFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.Decade, decadeStart.toString())   // or putInt(Extras.Decade, decadeStart)
    putString(Extras.IncludeType, includeType)
}

fun ageRatingPicker(item: BaseItemDto) = fragmentDestination<AgeRatingPickerFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
}

fun libraryByAgeRatingItems(item: BaseItemDto, rating: String, includeType: String) = fragmentDestination<AgeRatingItemsFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.Rating, rating)
    putString(Extras.IncludeType, includeType)
}

fun tagBrowseRows(item: BaseItemDto, browseMode: String) = fragmentDestination<TagBrowseRowsFragment> {
    putString(Extras.Folder, Json.encodeToString(item))
    putString(Extras.BrowseMode, browseMode)
}
```

Note: the fork used `Extras.Tag` for both decades and age-rating (both decoded via `requireArguments().getString(Extras.Tag)`). It also declared `Extras.Decade`/`Extras.Rating` but never used them. Keep the fork's actual `Extras.Tag` keys in `libraryByDecadeItems`/`libraryByAgeRatingItems` to avoid touching the fragment decode logic (the fragments read `Extras.Tag`); alternatively normalize to `Extras.Decade`/`Extras.Rating` and update the two fragments in the same commit. Either is fine — the plan assumes **preserve `Extras.Tag`** to keep the fragments byte-identical.

### 2.3 Rank badge — Compose reimplementation

Deleted upstream: `ui/card/LegacyImageCardView.java` (`setRankBadge(int)`), `ui/presentation/CardPresenter.java` (`mCardView.setRankBadge(0)` reset), `res/layout/view_card_legacy_image.xml` (`rankIndicator`/`rankNumber`). Replaced by Compose `CardPresenter.kt` → `ItemCard`/`ItemCardBaseItemOverlay`.

The rank source is unchanged: `DiscoverFragment` reads server-ranked items where `baseItem.indexNumber` carries the position. The `DiscoverCardPresenter` in the fork extended `CardPresenter(true, staticHeight)` and applied the badge in `onBindViewHolder`. In v0.20, `CardPresenter` is a Kotlin `class` (final) whose `CardViewHolder`/`CardViewHolderContent` are `private`, so **subclassing to inject a badge is not viable**. The correct approach is to make the badge an opt-in on `CardPresenter` itself and drive it from `indexNumber`.

Concrete plan:

1. Add a constructor param to `CardPresenter` (`ui/presentation/CardPresenter.kt`):

   ```kotlin
   class CardPresenter(
       val showInfo: Boolean,
       val imageType: ImageType,
       val staticHeight: Int,
       val uniformAspect: Boolean,
       val showRankBadge: Boolean = false,   // NEW
   ) : Presenter() {
   ```

   (Keep the existing secondary constructors; only the primary gains the new defaulted param.)

2. In `CardViewHolderContent`, the overlay slot already wraps `ItemCardBaseItemOverlay(item, footer = …)`. Render the badge there, guarded by the flag and the item's rank:

   ```kotlin
   overlay = {
       val showInfo = !usePreview && item.showCardInfoOverlay
       item.baseItem?.let { baseItem ->
           ItemCardBaseItemOverlay(item = baseItem, footer = { /* existing footer */ })
       }
       if (showRankBadge && (item.baseItem?.indexNumber ?: 0) > 0) {
           RankBadge(rank = item.baseItem!!.indexNumber!!, modifier = Modifier.align(Alignment.TopStart))
       }
   }
   ```

   `Badge` and `Text` already exist and are used by `ItemCardBaseItemOverlay` (`ui/base/Badge`, `ui/base/Text`). Add a small composable (new file `ui/composable/item/RankBadge.kt`, or inline in `CardPresenter.kt`):

   ```kotlin
   @Composable
   fun RankBadge(rank: Int, modifier: Modifier = Modifier) {
       if (rank <= 0) return
       Badge(modifier = modifier.sizeIn(minWidth = 24.dp, minHeight = 24.dp)) {
           Text(text = rank.toString(), fontSize = 10.sp)
       }
   }
   ```

   This reproduces the deleted 20dp circle + number at top-start, using the fork's existing `circle_accent` color via the `Badge` styling (or a `Box` with `background` if exact color parity with the old XML is required).

3. Replace the fork's `DiscoverCardPresenter` with a tiny factory (in `browsemodes/DiscoverCardPresenter.kt`):

   ```kotlin
   fun discoverCardPresenter(staticHeight: Int) =
       CardPresenter(true, ImageType.POSTER, staticHeight, true, showRankBadge = true)
   ```

   and change `DiscoverFragment` to use `discoverCardPresenter(CARD_HEIGHT)` instead of `DiscoverCardPresenter(CARD_HEIGHT)`. The old `setRankBadge(0)` reset is unnecessary: the overlay is derived from `indexNumber` per bind, so unbinding/reuse is automatically correct.

**Design note:** do **not** render a rank badge globally for every `indexNumber` — `indexNumber` is the episode number for `EPISODE` items and other semantics elsewhere. The `showRankBadge` flag keeps the behavior scoped to the Discover list, exactly as the fork intended.

### 2.4 Settings toggles — Compose `SettingsLibrariesDisplayScreen`

Deleted: `ui/browsing/DisplayPreferencesScreen.kt` (and the whole Options-DSL). New home for the two toggles is `app/src/main/java/org/jellyfin/androidtv/ui/settings/screen/library/SettingsLibrariesDisplayScreen.kt`.

The screen already imports `LibraryPreferences`, `rememberPreference`, `ListButton`, `Checkbox`, `Text`, `Modifier.focusKey`, and computes `allowViewSelection`. The fork's two checkboxes were gated behind `if (allowViewSelection)` in the old screen (browse modes only apply to Movies/TVShows). Re-express them as two `item { }` blocks inside the same `if (allowViewSelection) item { … }` region:

```kotlin
if (allowViewSelection) item {
    var enableSmartScreen by rememberPreference(libraryPreferences, LibraryPreferences.enableSmartScreen)
    ListButton( /* existing smart-screen row */ )
}

if (allowViewSelection) item {
    var enableBrowseModes by rememberPreference(libraryPreferences, LibraryPreferences.enableBrowseModes)
    ListButton(
        headingContent = { Text(stringResource(R.string.pref_enable_browse_modes)) },
        trailingContent = { Checkbox(checked = enableBrowseModes) },
        captionContent = { Text(stringResource(R.string.pref_enable_browse_modes_description)) },
        onClick = { enableBrowseModes = !enableBrowseModes },
        modifier = Modifier.focusKey("enable_browse_modes"),
    )
}

if (allowViewSelection) item {
    var enableTagRibbonShelves by rememberPreference(libraryPreferences, LibraryPreferences.enableTagRibbonShelves)
    ListButton(
        headingContent = { Text(stringResource(R.string.pref_enable_tag_ribbon_shelves)) },
        trailingContent = { Checkbox(checked = enableTagRibbonShelves) },
        captionContent = { Text(stringResource(R.string.pref_enable_tag_ribbon_shelves_description)) },
        onClick = { enableTagRibbonShelves = !enableTagRibbonShelves },
        modifier = Modifier.focusKey("enable_tag_ribbon_shelves"),
    )
}
```

The settings sheet is wired from `BrowseGridFragment` via `BrowseGridFragmentHelperKt.addSettings(...)` (already present in v0.20); no change needed there — the toggle rows simply appear once added to the screen.

### 2.5 Preferences / model / extras re-adds (mechanical)

**`app/src/main/java/org/jellyfin/androidtv/preference/LibraryPreferences.kt`** — v0.20 companion object is otherwise unchanged. Add back the fork's fields plus the `stringPreference` import:

```kotlin
import org.jellyfin.preference.stringPreference

// inside companion object:
val enableBrowseModes = booleanPreference("BrowseModes", true)
val browseModeSeeded = booleanPreference("BrowseModeSeeded", false)
val enableTagRibbonShelves = booleanPreference("TagRibbonShelves", true)
val filterMinDateLastSaved = stringPreference("FilterMinDateLastSaved", "")
val filterMinPremiereDate = stringPreference("FilterMinPremiereDate", "")
```

**`app/src/main/java/org/jellyfin/androidtv/data/model/FilterOptions.kt`** — unchanged upstream; re-apply the fork's `minDateLastSaved`/`minPremiereDate` fields, the `*Parsed` accessors, and the `parseDate` companion (with the `java.time.LocalDateTime` / `java.time.format.DateTimeFormatter` imports).

**`app/src/main/java/org/jellyfin/androidtv/constant/Extras.kt`** — unchanged upstream; re-add `BrowseMode`, `Studio`, `Tag`, `Decade`, `Rating`.

### 2.6 Date-cutoff plumbing (verified clean re-apply)

- **`ui/itemhandling/ItemRowAdapterHelper.kt`**: `setItemsFilter` still at line 667; add the fork's `setItemsDateCutoffs` after it. Upstream's only change in this file is in `retrieveUserViews` (lines ~235–244), so no conflict.
- **`ui/itemhandling/ItemRowAdapter.java`**: unchanged upstream (`git diff v0.19.9 v0.20.0-beta.1` is empty). The fork's two added lines in `setFilters` (`mQuery = setItemsDateCutoffs(mQuery, filters.getMinDateLastSavedParsed(), filters.getMinPremiereDateParsed())`) apply verbatim after the existing `setItemsFilter` line at ~line 412.
- **`ui/browsing/BrowseGridFragment.java`**: the fork's two lines (`filters.setMinDateLastSaved(...)` / `setMinPremiereDate(...)`) slot in after `filters.setUnwatchedOnly(...)` at lines 662–664, whose context is unchanged.

### 2.7 Resources (additive)

- **8 drawables** (`ic_book`, `ic_medal`, `ic_mood`, `ic_new_releases`, `ic_palette`, `ic_timeline`, `ic_trending_up`, `ic_world`): new files, carry over verbatim. The existing icons they reference (`ic_grid`, `ic_masks`, `ic_clapperboard`, `ic_tv`, `ic_add`, `ic_lightbulb`, `ic_rt_fresh`, `ic_resume`, `ic_calendar`, `ic_flask`, `ic_shuffle`, `circle_accent`) all still exist in v0.20.
- **colors.xml**: add the 17 `browse_mode_*` entries; `button_default_normal_background` / `button_default_normal_text` (used by `BrowseModeTilePresenter`) still exist.
- **strings.xml**: add the 25 browse-mode strings + 4 pref strings. **Drop** the fork's `lbl_favorites`/`lbl_unwatched` deletions in the 90 translation files unless the upstream v0.20 strings still need them removed (triage, see §4).
- **AndroidManifest.xml**: **no-op** — `android:configChanges="…"` is already present on `MainActivity` in v0.20 (line 128).

### 2.8 The 15 `browsemodes/*.kt` files

No substantive rewrite. Carry over as-is; the only edits that land inside them are the ones already implied above (nothing beyond what compiles against the re-added `Extras` and rewritten `Destinations`). `BrowseTags.kt` (6,410 lines of curated tags) is a pure data file with no upstream counterpart — copy it. `BrowseModeTilePresenter.kt`'s `ComposeViewWrapper.onMeasure` hack (to avoid a Compose/Leanback measure crash) is self-contained and stays.

---

## 3. Phasing + effort estimate

Ordered so each chunk compiles against the previous, and the risky/design pieces land while the mechanical pieces are fresh.

| Phase | Work | Effort |
|---|---|---|
| **1. Foundation (prefs/model/plumbing)** | Re-add `LibraryPreferences` (5 prefs + `stringPreference` import), `FilterOptions` date fields, `Extras` constants; `ItemRowAdapterHelper.setItemsDateCutoffs`, `ItemRowAdapter.java` date wiring, `BrowseGridFragment.java` date wiring. Verify `org.jellyfin.preference.stringPreference` exists. | 0.5 day |
| **2. Destinations rewrite** | Rewrite the 11 fork-added helpers to the `fragmentDestination<T> { putString(…) }` builder; add the 11 fragment imports. | 0.25 day |
| **3. Launcher interception** | `ItemLauncher.getUserViewDestination` MOVIES/TVSHOWS case: add `enableBrowseModes` branch. | 0.25 day |
| **4. Port the fragments + resources** | Carry over all 15 `browsemodes/*.kt`; add drawables/colors/strings; (manifest no-op). Fold the picker/items fragments' `Destinations.*` calls against Phase 2. | 1.0–1.5 day |
| **5. Rank badge in Compose** | `CardPresenter.kt` `showRankBadge` param + `RankBadge` composable; rewrite `DiscoverCardPresenter` → factory; wire `DiscoverFragment`. Includes TV focus/measure verification in a running emulator. | 0.5–1.0 day |
| **6. Compose settings toggles** | Add two `item { }` rows to `SettingsLibrariesDisplayScreen` (browse modes + tag ribbon shelves). | 0.5 day |
| **7. Build, run, triage incidental changes** | Full `./gradlew assembleDebug` (or equivalent), smoke test Browse-by/Discover/ribbon shelves on device; decide fate of `KnownDefects` AFTMM, `RecommendedServerIssueExtensions` string rename, translation deletions, `README.md`. | 0.5–0.75 day |

**Total: ~3.5–4.75 engineer-days** (call it **~4–5 days** with CI/build-test round-trips and the emulator verification).

---

## 4. Risks / unknowns

1. **Jellyfin SDK bump (1.7.1 → 1.8.12)** — the fork's raw-API calls (`apiClient.get<BaseItemDtoQueryResult>(pathTemplate, queryParameters)`, `/Items/Filters` → `QueryFiltersLegacy`, `GetItemsRequest.minDateLastSaved/minPremiereDate`, `BaseItemDto.indexNumber`) must be re-verified against SDK 1.8.12. Also `compileSdk 36→37`, `minSdk 21→23`, media3 `1.8.0→1.9.0`. Low-medium risk; a compile spike will surface any removed fields.
2. **`org.jellyfin.preference.stringPreference` availability** — the v0.20 `LibraryPreferences` imports only `booleanPreference`/`enumPreference`; confirm the `stringPreference` factory still exists in the pinned `org.jellyfin.preference` library. If removed, fall back to storing the ISO string in a `enumPreference` wrapper or a custom `Preference<String>`.
3. **Compose rank badge inside Leanback grids** — the fork's `BrowseModeTilePresenter` needed an `onMeasure` hack to stop Compose crashing inside Leanback presenters. The new `CardPresenter` sidesteps that via `setParentCompositionContext`/lifecycle/savedstate owners, but the rank-badge overlay must be verified in an actual `VerticalGridPresenter` at TV densities (720p ≈ 960dp). Spike this before locking the visual.
4. **`v0.20` is still a beta** — the Compose migration is in flight. `CardPresenter`, `ItemCard`/`ItemCardBaseItemOverlay`, the settings screens, `DestinationFragmentView`, `router.kt`, and `focusKey` are all new and may shift before GA. Re-verify against each beta tag; treat §2.3/§2.4 as the most change-prone.
5. **`getUserViewDestination` nullability** — `baseItem` is `@Nullable`; `baseItem.getDisplayPreferencesId()` is dereferenced upstream in the same shape as the fork's original, so parity is fine, but keep the fork's null-guard semantics identical to upstream's to avoid NPE on unknown collection types.
6. **Concurrent rebase owns the working tree** — this plan must land as a follow-up commit/PR after the rebase settles; do not interleave edits with it.
7. **Incidental fork changes need a decision each**: `KnownDefects` `AFTMM` (upstream may already have added Fire TV models), `RecommendedServerIssueExtensions` `server_issue_ssl_handshake` rename (string may already exist upstream), the 90 translation files' `lbl_favorites`/`lbl_unwatched` deletions (likely artifacts of an earlier partial upstream sync — check whether v0.20 still references those keys before re-deleting), and `README.md` (already dirty in the working tree). None are browse-mode features; drop or re-derive rather than blindly carry.
8. **`Extras.Decade`/`Extras.Rating` are dead constants** in the fork (the fragments read `Extras.Tag`). Preserve `Extras.Tag` in the rewritten `libraryByDecadeItems`/`libraryByAgeRatingItems` helpers to keep the fragments byte-identical, or normalize both sides in one commit — do not half-migrate.
9. **Discover server endpoint** (`/Discover/{Trending|TopRated}/{Movies|Shows}`) is a custom server addition, unchanged by this port; the empty/error handling in `DiscoverFragment.load` (`lbl_no_trending_items`/`lbl_no_top_rated_items`) must not regress.
