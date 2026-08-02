# Handoff — 2026-08-03

Read this first.

## Where we left off

Started **Browse Modes v2.0** refactor based on `category-upgrades.md`.
See [`docs/PLAN.md`](./PLAN.md) for the full chunked implementation plan.

**Chunks completed:** 1a (remove 4 tiles), 1b (rename 2 tiles), 1c (reorder tiles)
**Next:** 1d (per-library toggle), 1e (per-tile toggle)

The toggle architecture is designed but not yet coded:
- Server-side plugin config (`PluginConfiguration.cs`): `EnableForMovies`, `EnableForTvShows`, `DisabledTiles[]`
- Client fetches config via `GET /Plugins/{id}/Configuration`, caches, filters tile arrays
- `config.html` gets checkboxes grouped by library type
- New client hook: `hooks/useBrowseConfig.ts`

## Two codebases — remember to update both

| | 12.x | 10.11 |
|---|---|---|
| Directory | `jellyfin-web/` | `jellyfin-web-10.11/` |
| Branch | `browse-modes` | `browse-modes-10.11` |
| App | `apps/modern/` | `apps/experimental/` |
| Settings | `utils/settings.ts` | `utils/items.ts` |
| Layout | `layoutManager.modern` (default true) | RootAppRouter always loads experimental |

Key differences carried forward from 10.11 backport:
- No `LibraryProvider` — browse seeding inline in `ItemsView.tsx`
- `shouldShowBrowseModes` uses `layoutManager.experimental` check removed
- Path-based `CollectionType` inference for libraries missing the field
- Interception gated on `item.Type === 'CollectionFolder'`
- `MinPremiereDate` filter on New Releases, `IsPlayed` on Watch Again

## Docker test environments

| | 10.11 | 12.0 |
|---|---|---|
| Container | `jf-test-10` | `jellyfin` |
| Port | 8196 | host networking |
| Web dist | bind-mounted (changes live) | `docker cp` to deploy |

### Quick rebuild + deploy

```bash
# 10.11 (auto-deploys via bind mount)
cd jellyfin-web-10.11
export PATH=$PWD/../.toolchain/node-v24.9.0-linux-x64/bin:$PATH
npm run build:production            # live immediately at :8196

# 12.0
cd jellyfin-web
npm run build:production            # needs system node
docker cp dist/. jellyfin:/jellyfin/jellyfin-web/

# Revert either to stock:
docker exec <container> rm -rf /jellyfin/jellyfin-web/*
docker cp <container>:/jellyfin/jellyfin-web.bak/. <container>:/jellyfin/jellyfin-web/
```

## Current tile inventory (post-Chunk 1)

Movies: All, Trending, Top Rated, Genres, Hidden Gems, Just Added, New Releases,
Random, Critics' Picks, Watch Again, Decades, Studios, Age Rating (13)

TV: All, Trending, Top Rated, Genres, Hidden Gems, Just Added, New Releases,
Random, Watch Again, Decades, Networks, Age Rating (12)

## Repos

| What | Where |
|---|---|
| Plugin + docs | `AvonWilliams/jellyfin-browse-modes` (main) |
| Web 12.x | `AvonWilliams/jellyfin-web` (browse-modes) |
| Web 10.11 | `AvonWilliams/jellyfin-web` (browse-modes-10.11) |
| Android TV | `AvonWilliams/jellyfin-androidtv` (browse-modes) |
| Claude config | `AvonWilliams/claude-config` (private) |
| Manifest | `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json` |
