# Handoff — 2026-08-02 session

Read this first; it points at everything else.

## What happened this session

### Plugin backported to Jellyfin 10.11.x (stable) — SHIPPED as v1.0.2.0

The plugin now has **two build targets** sharing one codebase:

| | 12.x | 10.11 |
|---|---|---|
| Directory | `plugin/Jellyfin.Plugin.BrowseModes/` | `plugin/Jellyfin.Plugin.BrowseModes.10_11/` |
| Framework | `net10.0` | `net9.0` |
| Jellyfin pkgs | `12.0.0-rc3` | `10.11.11` |
| targetAbi | `12.0.0.0` | `10.11.0.0` |
| Source | Own `.cs` files | Links to 12.x sources via `**/*.cs` glob |

**The fix that made it work:** `DiscoverController.cs` no longer uses `HasAnyProviderIds` (doesn't exist in 10.11). Instead it fetches all items of the given `BaseItemKind` and ranks them in memory. Simpler, and works on both versions.

**CI** (`.github/workflows/plugin.yml`) builds both, packages both zips, and attaches both to the GitHub Release. The manifest on GitHub Pages lists both with distinct `targetAbi` values so each Jellyfin version sees its own entry.

**Commit:** `4f88c64` on `main`, tagged `v1.0.2.0`, pushed to GitHub. CI should produce the release artifacts.

### Global CLAUDE.md updated

Added §10 (Sensitive Data) — never commit email addresses, API keys, tokens, passwords, hostnames, or personal info to git.

### Graphify run

Ran `/graphify` on `jellyfin-browse-modes/` (41 files). Output in `graphify-out/`: `graph.html`, `graph.json`, `GRAPH_REPORT.md`. 296 nodes, 507 edges, 24 communities.

## What still needs doing

### 1. Web client backport to 10.11 — NOT STARTED

The user installed the 12.x web bundle on their 10.11.11 server. The tile page mostly works but the **Plugins admin page crashes** with a React error in `plugins.3feb857a0c22366a9944.chunk.js`. This is expected — the 12.x `modern` app doesn't exist in 10.11.

Scoped in `docs/BACKPORT-10.11.md` at 2–3 days. The key differences:
- No `modern` app — everything lives in `experimental`
- Tab definitions are inline, not in constants files
- No `LibraryProvider` context
- Settings live in `src/utils/items.ts` instead of `apps/modern/`

**Important:** When doing the web backport, make two distinct versions (like we did for the plugin) rather than modifying the existing 12.x web bundle to be universal. The user explicitly wants separate builds.

### 2. Verify the plugin actually loads on 10.11.11

Built and packaged locally, but not tested on a live 10.11 server. The zip is at `dist/browse-modes_1.0.2.0_10.11.zip`. Install by unpacking into `<config>/plugins/BrowseModes_1.0.2.0/`. The manifest entry on GitHub Pages should make it appear in the catalog once the CI release is published.

### 3. Tile reordering and additional tiles

Still parked from the previous session. See the previous NEXT-SESSION.md or TECHNICAL.md §6.

## Operational notes (carried forward)

- **The web bundle does not survive updates.** On the user's LXC install, `apt-mark hold jellyfin-web` is set.
- **Browser cache is the usual culprit.** Hard-refresh after deploying.
- **Committing.** All repos configured with noreply email. Do not set `user.email` per-commit.
- **Plugin repository URL:** `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json`
- **Two graphify-out directories exist** — one in `browse-modes/` parent (mostly empty, safe to delete) and the real one in `jellyfin-browse-modes/graphify-out/`.

## Release procedure — READ BEFORE CUTTING A RELEASE

**Critical: local builds and CI builds produce different checksums.** Jellyfin validates the md5 checksum in the manifest against the downloaded zip. If they don't match, installation silently fails.

Correct order:
1. Commit code changes
2. Tag: `git tag -a vX.Y.Z.W -m "..."` and push: `git push origin main --tags`
3. **Wait for CI to complete** (Actions → Plugin workflow)
4. Open the CI run → expand "Report checksums" → copy both checksums
5. Update `manifest.json` with the CI checksums (new entry at top of versions array)
6. Commit and push the manifest update

The CI step summary at `https://github.com/AvonWilliams/jellyfin-browse-modes/actions` has the checksums for each run.
