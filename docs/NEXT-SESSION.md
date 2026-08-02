# Handoff — 2026-08-02

Read this first; it points at everything else.

## What shipped this session

### Plugin backported to Jellyfin 10.11.x (stable) — v1.0.2.1

Two build targets sharing one codebase: 12.x (`net10.0`) and 10.11 (`net9.0`).
CI builds both and attaches both zips to the GitHub Release.

Three changes from the 12.x plugin:
1. **`DiscoverController.cs`** — In-memory provider-id matching (works on both versions)
2. **`TmdbDiscoverClient.cs`** — Lazy `TMDbClient` init (avoids throw on empty API key)
3. **`manifest.json`** — Two targetAbi entries per version

### Web client backported to Jellyfin 10.11 — ✅

**Branch:** `browse-modes-10.11` in `AvonWilliams/jellyfin-web`
**Directory:** `jellyfin-web-10.11/` (fresh clone from `v10.11.0-rc9`)
**Deployed to:** Docker `jf-test-10` (port 8196) with media libraries from production

17 files, ~900 lines. All 17 movie / 16 series modes ported.
Build: `tsc --noEmit` clean, eslint clean, webpack production passes.

### Four 10.11-specific bugs found and fixed

| Bug | Root cause | Fix |
|---|---|---|
| Tiles never appeared | `layoutManager.experimental` defaults false in 10.11 | Removed from `shouldShowBrowseModes` guard |
| CollectionType always undefined | 10.11 sidebar passes type via `options.context`, not on item | Fall back to options.context + path inference |
| "Page not found" on #/browse | RootAppRouter only loads experimental routes when localStorage layout='experimental' | Always load EXPERIMENTAL_APP_ROUTES (matches 12.x) |
| Clicking movies looped back to tiles | Card click handlers pass collectionType via `options.context`; interception picked it up | Gate entire interception on `item.Type === 'CollectionFolder'` |

### Improvements added during port

- **New Releases**: Now filters to 9-month cutoff (`MinPremiereDate`)
- **Recently Played**: Now filtered to `IsPlayed` items only
- **Studios**: Added to switch case and enabled allowlist (was missing vs 12.x)

### Claude config repo

Private repo `AvonWilliams/claude-config` with CLAUDE.md, settings.json, commands/, and skills/.
Install by copying files into `~/.claude/`.

---

## Next session: category-upgrades.md

The user mentioned working on `docs/category-upgrades.md` — this file doesn't exist yet.
Create it and scope the work at the start of next session.

---

## Known issues (not yet resolved)

- **Trending / Top Rated** may not load items in the client. The Discover API is verified
  working server-side (returns items via curl). Client-side `fetchDiscoverList` in
  `useFetchItems.ts` might have a URL or auth issue in 10.11.
- **Shows** library on the test container has `CollectionType: null` — needs proper setup
  via the Jellyfin library wizard, not just config file copy. Path inference handles it in
  the router, but it may affect other things.

## Docker test environment

```
Container: jf-test-10
Image:     jellyfin/jellyfin:10.11.11
Port:      8196 → container 8096
Config:    /tmp/jf-test/config (bind-mounted)
Cache:     /tmp/jf-test/cache (bind-mounted)
Media:     /mnt/208C2E0C8C2DDCD2/Video → /video (ro, from production)
           /mnt/media → /media (ro)
Web dist:  jellyfin-web-10.11/dist → /jellyfin/jellyfin-web (ro, bind-mounted)
           changes picked up live — rebuild and hard-refresh (Ctrl+Shift+R)
```

### Commands

```bash
# Build
cd jellyfin-web-10.11
export PATH=$PWD/../.toolchain/node-v24.9.0-linux-x64/bin:$PATH
npm run build:production

# Check container
docker ps --filter name=jf-test-10

# View logs
docker logs jf-test-10 --tail 50

# Restart
docker restart jf-test-10

# API test (get token from DB first)
TOKEN=$(python3 -c "import sqlite3; print(list(sqlite3.connect('/tmp/jf-test/config/data/jellyfin.db').execute('SELECT AccessToken FROM Devices LIMIT 1'))[0][0])")
curl -s "http://localhost:8196/Discover/Trending/Movies?limit=5" \
  -H "Authorization: MediaBrowser Token=\"$TOKEN\""
```

## Quick reference

| What | Where |
|---|---|
| Plugin repo | `jellyfin-browse-modes/` (branch `main`) |
| Web 12.x fork | `jellyfin-web/` (branch `browse-modes`) |
| Web 10.11 fork | `jellyfin-web-10.11/` (branch `browse-modes-10.11`) |
| Android TV fork | `jellyfin-androidtv/` (branch `browse-modes`) |
| Manifest URL | `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json` |
| Docker test | `jf-test-10` on port 8196 |
| Production | `jellyfin` Docker container (host networking, 12.0-rc3) |
| Claude config | `AvonWilliams/claude-config` (private) |
