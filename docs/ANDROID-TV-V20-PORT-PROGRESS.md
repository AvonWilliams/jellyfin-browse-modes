# Android TV v0.20 port — LIVE PROGRESS

> Source of truth for this port. **Update frequently.** Shorthand is fine.
> **NEVER delete lines — mark them `[x]` done or annotate them.**
> Full plan: [`ANDROID-TV-V20-PORT-PLAN.md`](ANDROID-TV-V20-PORT-PLAN.md)
> Source of the fork's changes: the `browse-modes` branch (v0.19.10, 16 commits) in `jellyfin-androidtv`.

## Branches
- `browse-modes-v20` — **lead agent**, Leanback plumbing + integration (Chunks 1–4). Based on `v0.20.0-beta.1`.
- `browse-modes-v20-compose` — **Compose agent**, rank badge + settings (Chunks 5–6). Based on `browse-modes-v20` (worktree `/tmp/androidtv-v20-compose`).
- Merge of the two + Chunk 7 (build/triage) is a **follow-up**.

## Chunk status (most important first)
- [x] **Chunk 1 — Foundation (plumbing)** — `preference/LibraryPreferences.kt` (5 prefs + `stringPreference`), `data/model/FilterOptions.kt` (date fields), `constant/Extras.kt`, `ui/itemhandling/ItemRowAdapterHelper.kt` (date cutoffs), `ItemRowAdapter.java` + `ui/browsing/BrowseGridFragment.java` (date wiring).
- [x] **Chunk 2 — Destinations** — rewrite the 11 destination helpers from `fragmentDestination<T>(Extras.X to y)` to the new `fragmentDestination<T> { putString(...) }` builder in `ui/navigation/Destination.kt`.
- [x] **Chunk 3 — Launcher** — `ItemLauncher.getUserViewDestination()`: `libraryBrowser(baseItem, null)` (new `includeType` param) for the browse-modes branch.
- [x] **Chunk 4 — Fragments + resources** — bring over the 15 `ui/browsing/browsemodes/*.kt` files (survive as Leanback) + drawables; verify constructors/imports compile.
- [x] **Chunk 5 — Compose rank badge** — `CardPresenter.kt` opt-in `showRankBadge` + `RankBadge` composable (driven by `baseItem.indexNumber`); turn `DiscoverCardPresenter` into a factory.
- [x] **Chunk 6 — Compose settings toggles** — two `ListButton`+`Checkbox` rows in `SettingsLibrariesDisplayScreen.kt` (grid/shelf + display transform) via `rememberPreference`.
- [x] **Chunk 7 — Build + run + triage** (FOLLOW-UP after merge).

## Log (newest last)
<!-- agents append here: `HH:MM [agent] chunk → status · commit <sha> · note` -->
10:55 [lead] chunk 1 → done · commit 3eaafbea7 · prefs/model/extras + date-cutoff plumbing; SDK 1.8.12 GetItemsRequest fields verified
11:00 [lead] chunk 2 → done · commit ad0c1b1a5 · 11 destination helpers rewritten to Bundle builder; Extras.Tag preserved for decades/rating
11:03 [lead] chunk 3 → done · commit 7f61e7cfd · launcher routes Movies/TVShows to browseModes via getUserViewDestination
11:50 [lead] chunk 4 → done · commit c76eef3c8 · 15 browsemodes/*.kt + 8 drawables + colors/strings. Static verify: all SDK 1.8.12 symbols + v0.20 ctors/imports OK. DiscoverCardPresenter/DiscoverFragment carried verbatim → Compose agent rework (Chunk 5). Full gradle compile blocked by infra (JDK21 + compileSdk37 platform missing) — see note.
12:05 [compose] chunk 5 → done · commit cdea10d10 · CardPresenter opt-in showRankBadge + new RankBadge composable (indexNumber); DiscoverCardPresenter→factory discoverCardPresenter(staticHeight); DiscoverFragment builds items staticHeight=true so v0.20 honors CARD_HEIGHT=260 (plan snippet would silently drop to 150dp). Static verify only — gradle blocked by infra.
12:09 [compose] chunk 6 → done · commit 007d93675 · two ListButton+Checkbox rows (enableBrowseModes, enableTagRibbonShelves) in SettingsLibrariesDisplayScreen allowViewSelection region via rememberPreference; prefs (Chunk 1) + 4 pref strings (Chunk 4) present. Static verify only.
12:11 [lead] chunk 4 · compile VERIFIED on browse-modes-v20 · resolved infra (JDK21→.toolchain/jdk21, ANDROID_HOME + auto-install android-37.0). `:app:compileDebugKotlin` result: exactly 4 errors, ALL in DiscoverCardPresenter.kt (final CardPresenter + LegacyImageCardView/setRankBadge deleted). Everything else in Chunks 1–4 compiles clean (Java+resources+13 fragments). The 4 errors are precisely compose chunk 5's rework.

EOD [chunk7] build + run -> done · merged compose->v20 (fast-forward), assembleDebug SUCCESS (5m15s, 222 tasks), AVD tv26 booted + APK installed + app launched. One interop fix c8d738c8b (4-arg CardPresenter overload). Port complete.
