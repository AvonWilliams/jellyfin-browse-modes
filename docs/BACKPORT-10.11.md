# Back-porting Browse Modes to Jellyfin 10.11 (stable)

**Status: plugin done (1.0.2.0, 2026-08-02), web done (2026-08-02).** This is a scoping document from
research done on 2026-07-26, written so the work can be picked up without repeating the
investigation. The plugin half was completed and released; the web half remains.

## Why

Everything currently shipped targets **Jellyfin 12.0-rc3**. The stable channel — what almost
everyone actually runs, and what the Proxmox community-scripts installer gives you — is
**10.11.11**. On 10.11 the current release fails twice over:

1. The plugin is filtered out of the catalog before it is ever shown.
   `InstallationManager.cs:269` applies `Version.Parse(TargetAbi) <= appVer`, and our
   `targetAbi` is `12.0.0.0`. It disappears silently, with no error.
2. The web bundle is built from `jellyfin-web` v12.0-rc3, whose tile page lives in
   `src/apps/modern/` — an app 10.11 does not have. Installing it would likely break the web
   client outright, not merely omit the tiles.

## What already lines up

Most of what the patch hooks into exists in 10.11, at different paths. This is the good news and
the reason a back-port is viable at all.

| Hook | 12.0-rc3 | 10.11.11 |
|---|---|---|
| Settings key + defaults | `apps/modern/features/libraries/utils/settings.ts` | `src/utils/items.ts` — **identical signatures** |
| Per-view localStorage | `hooks/useLibrary.tsx` | `apps/experimental/components/library/ItemsView.tsx:74` — same `useLocalStorage` call |
| Router interception | `components/router/appRouter.js:410` | `appRouter.js:403` — same `#/movies?topParentId=` construction |
| Route registration | `apps/modern/routes/asyncRoutes/user.ts` | same file under `apps/experimental`, `AppType.Experimental` |
| Rank badge | `cardbuilder/Card/CardImageContainer.tsx` + `card.scss` | **same paths** |
| `LibraryTab`, `cardOptions.ts`, `useFetchItems.ts` | present | present |
| App split | `modern` / `legacy` | `experimental` / `stable` |

The preset-seeding mechanism transplants directly, because 10.11 calls the same
`getSettingsKey(viewType, parentId)` that the patch extends with a mode suffix.

## What has to be rewritten

### 1. Plugin item matching — the real blocker

12.x has **`HasAnyProviderIds`** (`Dictionary<string, string[]>`, many ids per provider) at
`InternalItemsQuery.cs:459`. 10.11 has only **`HasAnyProviderId`** (singular,
`Dictionary<string, string>`) at line 338. The Discover endpoints hand it ~300 TMDb ids at once,
which the singular form cannot express.

**Suggested fix:** drop the provider-id query entirely. Fetch the library's items of the relevant
`BaseItemKind` once with `ItemFields.ProviderIds`, then build the rank map in memory. This is
simpler than the current code and works on **both** 10.11 and 12.x, so it would let a single
plugin source serve both — worth considering even for the 12.x build.

Trade-off: loads the item list instead of filtering in SQL. Fine for typical libraries, slower on
very large ones.

### 2. Tab definitions moved

12.x declares tab content in `apps/modern/features/libraries/constants/views/{movies,tvshows}.ts`.
10.11 declares `LibraryTabContent` **inline** in `apps/experimental/routes/movies/index.tsx` and
`shows/index.tsx`.

Trending and Top Rated are view-backed tiles, so each needs:
- a new member in `src/types/libraryTab.ts`
- a `case` in `useGetItemsViewByType` in `src/hooks/useFetchItems.ts`
- an entry in the inline tab mapping in each route

Mechanical, but spread across three more places than in 12.x.

### 3. No `useLibrary` context

10.11 has no `LibraryProvider`. The browse-mode seeding logic folds back into `ItemsView.tsx`,
which is arguably where it belonged.

## Effort

| Part | Estimate | Notes |
|---|---|---|
| Plugin | ~half a day | Retarget `Jellyfin.Controller` 10.11.x, `targetAbi 10.11.0.0`, rewrite item matching |
| Web | 2–3 days | A genuine re-port, not a patch rebase |
| Android TV | none | 0.19.9 already targets 10.x; needs only the 10.11 plugin |

**The patches in `patches/` will not apply.** Treat them as reference.

## Compatibility notes already verified

- 10.11 targets **net9.0**, so `.Index()` and collection expressions still compile. No downgrade
  needed there.
- `TaskTriggerInfoType` **does** exist in 10.11 (`MediaBrowser.Model/Tasks/TaskTriggerInfo.cs:15`),
  so the scheduled task ports unchanged.
- `Jellyfin.Controller` / `Model` / `Data` / `Extensions` are all published on nuget.org at
  `10.11.11`.
- `ExcludeProviderIds` exists in both, if an exclusion approach is ever wanted.

## Recommended sequencing

1. **Plugin first, on its own.** Half a day, and the in-memory matching would let one build serve
   both channels. That alone gives stable users Trending and Top Rated data.
2. **Web port second**, and ideally only after the 12.x version has real users shaking bugs out —
   port something proven rather than maintaining two immature branches.

## The strategic question

12.0 will eventually go stable, at which point the 10.11 branch becomes dead work. Doing this
back-port means carrying two web branches, two plugin targets and two release tracks until then.
Worth weighing against how long 12.x is expected to stay in RC.
