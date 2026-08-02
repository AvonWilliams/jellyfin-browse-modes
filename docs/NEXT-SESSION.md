# Handoff — 2026-08-02 session (final)

Read this first; it points at everything else.

## What shipped this session

### Plugin backported to Jellyfin 10.11.x (stable) — v1.0.2.1

The plugin now has **two build targets** sharing one codebase:

| | 12.x | 10.11 |
|---|---|---|
| Directory | `plugin/Jellyfin.Plugin.BrowseModes/` | `plugin/Jellyfin.Plugin.BrowseModes.10_11/` |
| Framework | `net10.0` | `net9.0` |
| Jellyfin pkgs | `12.0.0-rc3` | `10.11.11` |
| targetAbi | `12.0.0.0` | `10.11.0.0` |
| Source | Own `.cs` files | Links to 12.x sources via `**/*.cs` glob |

**Three changes from the 12.x plugin:**

1. **`DiscoverController.cs`** — Replaced `HasAnyProviderIds` (doesn't exist in 10.11) with in-memory matching. Fetches all items of the given `BaseItemKind`, ranks them locally. Works on both 10.11 and 12.x.
2. **`TmdbDiscoverClient.cs`** — TMDbLib 3.0.0 throws `ArgumentException` on empty API key. The `TMDbClient` is now created lazily (only when an API key is actually configured). Every code path already checks `HasApiKey` before use.
3. **`manifest.json`** — Two entries per version (one `targetAbi: 10.11.0.0`, one `targetAbi: 12.0.0.0`). Same manifest URL serves both Jellyfin versions.

**CI** builds both projects, packages two zips (`browse-modes_X.Y.Z.W_12.x.zip` and `browse-modes_X.Y.Z.W_10.11.zip`), attaches both to the GitHub Release.

**git:** All on `main`, pushed. Tags: `v1.0.2.0` (first backport), `v1.0.2.1` (TMDb constructor fix).

### Bugs found and fixed this session

1. **Manifest checksums didn't match CI builds** — Local builds produce different byte output (different PDB paths, timestamps). Jellyfin silently rejects the download. Fixed by pulling checksums from CI step summary. **See release procedure below.**
2. **`TMDbClient("")` constructor throws** — Fixed in v1.0.2.1. Client is now lazy-initialized.

### Global CLAUDE.md updates

- §10: Sensitive Data (never commit keys, emails, passwords)
- §11: Release Artifacts (never use local checksums for CI-built artifacts)

### Graphify

Ran on `jellyfin-browse-modes/`. Output in `graphify-out/`: `graph.html`, `graph.json`, `GRAPH_REPORT.md`. 296 nodes, 507 edges, 24 communities.

---

## Next: Web client backport to Jellyfin 10.11 — ✅ DONE (2026-08-02)

**Branch:** `browse-modes-10.11` in `AvonWilliams/jellyfin-web` (pushed).
**Directory:** `jellyfin-web-10.11/` (fresh clone from `v10.11.0-rc9`).

Built and deployed to Docker `jf-test-10` (port 8196). `tsc --noEmit` clean, eslint clean
(pre-existing `useCallback` missing-dep warning only), webpack production build passes.

The 12.x web bundle was installed on a 10.11.11 server. Observations:
- The **tile page mostly works** — the `modern` app routes seemed to resolve
- The **Plugins admin page crashes** with a React error in `plugins.*.chunk.js`
- The catalog/filtering worked (v1.0.2.1 showed up and installed successfully)

Scoped in `docs/BACKPORT-10.11.md` at 2–3 days. Key differences from 12.x:

| Concern | 12.x | 10.11 |
|---|---|---|
| App structure | `modern` / `legacy` | `experimental` / `stable` |
| Tab definitions | `apps/modern/features/libraries/constants/views/{movies,tvshows}.ts` | Inline in `apps/experimental/routes/{movies,shows}/index.tsx` |
| Settings key + defaults | `apps/modern/features/libraries/utils/settings.ts` | `src/utils/items.ts` (identical signatures) |
| Per-view localStorage | `hooks/useLibrary.tsx` | `apps/experimental/components/library/ItemsView.tsx:74` |
| Router interception | `components/router/appRouter.js:410` | `appRouter.js:403` (same pattern) |
| Route registration | `apps/modern/routes/asyncRoutes/user.ts` | `apps/experimental/routes/asyncRoutes/user.ts` (`AppType.Experimental`) |
| `LibraryProvider` context | Present | **Absent** — settings logic folds into `ItemsView.tsx` |

**What aligns:** Settings keys, router interception, rank badge (`CardImageContainer.tsx` + `card.scss` at same paths), `LibraryTab`, `cardOptions.ts`, `useFetchItems.ts`, `browseMode.ts` types. The preset-seeding mechanism transplants directly.

**What's different:** The `patches/jellyfin-web-browse-modes.patch` will NOT apply to 10.11. Treat it as reference only. This is a genuine re-port, not a patch rebase.

**Strategy:** Make a **separate 10.11 web branch** (`browse-modes-10.11`) in the `jellyfin-web` fork. Keep the existing `browse-modes` branch for 12.x. Two distinct bundles, like we did for the plugin.

---

## Docker test environment

A Jellyfin 10.11.11 container is running locally for testing:

```
Container: jf-test-10
Image:     jellyfin/jellyfin:10.11.11
Port:      8196 → container 8096
Config:    /tmp/jf-test/config
Cache:     /tmp/jf-test/cache
```

The plugin (v1.0.2.1, CI-built) is installed and working. A TMDb API key is configured in the plugin config at `/tmp/jf-test/config/plugins/configurations/Jellyfin.Plugin.BrowseModes.xml`.

**Commands:**
```bash
# Check it's running
docker ps --filter name=jf-test-10

# View logs
docker logs jf-test-10 --tail 50

# Restart
docker restart jf-test-10

# Shell in
docker exec -it jf-test-10 sh

# Remove when done
docker rm -f jf-test-10

# Update plugin (after rebuilding)
docker exec jf-test-10 rm -rf /config/plugins/BrowseModes_1.0.2.1
docker cp plugin/Jellyfin.Plugin.BrowseModes.10_11/bin/Release/net9.0/. jf-test-10:/config/plugins/BrowseModes_1.0.2.1/
# (also copy meta.json)
docker restart jf-test-10
```

**Note:** The container has NO media libraries set up — it's for plugin load/API testing only. To test the Discover endpoints, use the Web UI at `http://localhost:8196` to complete setup wizard and create a library, then query the API.

---

## Release procedure — READ BEFORE CUTTING A RELEASE

**Critical: local builds and CI builds produce different checksums.** Jellyfin validates the md5 checksum against the downloaded zip. Mismatch = silent install failure.

1. Commit code. Tag: `git tag -a vX.Y.Z.W -m "..."` and push with `--tags`.
2. **Wait for CI** (Actions → Plugin workflow).
3. Open the CI run → expand "Report checksums" step → copy both checksums.
4. Add new entries to `manifest.json` with the CI checksums (put newest at top).
5. Commit and push the manifest update.

Never put locally-computed checksums into the manifest. See also `~/.claude/CLAUDE.md` §11.

---

## Other parked items

- **Tile reordering** — first judgement call, never revisited. Group by intent.
- **Additional tiles** — candidates range from cheap (Shortest, Continue Watching) to expensive (Most Watched needs ranked endpoint).
- **CI in web/Android TV repos** — only the plugin is automated. Web zip and APK are built/uploaded manually.
- **Light-theme contrast** — icon colours chosen against dark theme.
- **APK signing** — debug-signed. Installs beside official app.

## Quick reference

| What | Where |
|---|---|
| Plugin repo | `jellyfin-browse-modes/` (this repo) |
| Web fork | `jellyfin-web/` (branch `browse-modes`) |
| Android TV fork | `jellyfin-androidtv/` (branch `browse-modes`) |
| Manifest URL | `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json` |
| Docker test | `jf-test-10` on port 8196 |
| Backport scoping | `docs/BACKPORT-10.11.md` |
| Full technical ref | `docs/TECHNICAL.md` |
| Web patch (12.x ref) | `patches/jellyfin-web-browse-modes.patch` |
