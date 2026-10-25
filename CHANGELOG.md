# Changelog

Versions cover the project as a whole — plugin, web client and Android TV client are released
together under one number, even when only one of them changed. Each entry says which parts moved.

## 12.3.1 — Fixed tile order

**Plugin** and **web client**.

- Browse Modes tiles are no longer configurable. Every category is always shown, in one fixed
  order (Categories, Mood & Themes, People, Discover after the primary tiles). The "Browse mode
  tiles" section is removed from the plugin's Display settings page.
- The web client no longer requests `/Discover/TileLayout`, so a stale or hand-edited saved
  order can no longer hide or reshuffle tiles.
- Compatibility: the `BrowseModeOrder` setting is deprecated and ignored. It is still read from
  older saved configurations without error and left as it is when settings are saved, and
  `/Discover/TileLayout` still exists but always returns an empty list, so older web builds fall
  back to the full built-in order.
- Android TV is unchanged (it never read the tile layout).
- Fixed: the snapshot Sources list grew on every settings save and server restart (9 entries
  became 18, 27, ...), so lists were fetched repeatedly. The saved list is now loaded as-is, and
  lists already duplicated are collapsed on load (an entry you changed is kept over an untouched
  default). The saved XML format is unchanged.
- Fixed: saving the Sources tab merged rows by source name, so a source's second list (for example
  IMDb Top Rated, Trakt Top Rated) and any extra entries were dropped. Every configured list now
  has its own row and is saved.

**Installer** (`install-browse-modes.sh`, served from `main`).

- The old plugin and web directories are now **renamed into a `.bm-trash` folder (beside the plugins
  or web directory, same filesystem) instead of being deleted first**, for native and Docker
  installs. On a `/config` volume on ntfs-3g/FUSE, `rm -rf` of the plugin folder fails ("Directory
  not empty", `.fuse_hidden*`) while Jellyfin has the DLLs open, which broke the update, the
  restore (it used the same delete) and left the plugin folder empty. Now the update, the rollback
  (new folder renamed aside, old one renamed back) and the restore all use renames; the trash is
  deleted at the end on a best-effort basis, kept and reported if that fails, and cleaned by the
  next run. If a restore still fails, the script prints the exact state of the files and the manual
  steps.
- Before installing, every existing `Browse Modes_*` folder is moved aside (older, newer, or an
  empty leftover without `meta.json`), with a log line each, and the install checks that exactly
  one folder with a `meta.json` remains. An empty leftover used to make Jellyfin drop the freshly
  installed plugin at the next restart. For Docker, an empty folder is no longer mistaken for the
  installed plugin.
- A `systemctl start` / `docker restart` that fails at once now rolls back immediately instead of
  first waiting out the whole health-check window (also for the start after the rollback). A start
  that worked but is slow still gets the full window.

- The web bundle now counts as installed only while its files still match what was installed
  (SHA-256 of `index.html`, `runtime.bundle.js`, `main.jellyfin.bundle.js`, recorded in
  `.browse-modes-files`). A `jellyfin-web` package reinstall or same-minor upgrade used to leave the
  version marker behind, so the daily job never re-applied the bundle. A bundle installed by an older
  script is installed once more to record the fingerprint.
- A failing `systemctl start` (ExecStartPre failure, start-limit-hit) or `docker restart` no longer
  ends the run before the health check: it rolls back, records the result, exits 1 and does not log
  the failure twice. The rollback runs `systemctl reset-failed` first. Backup folders created in the
  same second no longer nest.
- Exit code 3 for "no tested release" is the same in the interactive flow, `--all`, `--yes`,
  `--target` and `--docker`: every chosen server skipped for that reason gives 3 (`--auto` stays 0);
  a failure gives 1; any installed or up-to-date server gives 0.
- "Next steps" and the backup hint appear only when they apply; dry runs are counted separately from
  skips; the confirmation names the chosen server(s); raw `sha256sum`/`md5sum` warnings are hidden.
- Discovery marks a native entry that is the same server as a running container ("same server as
  docker:NAME") and a native web directory with no service, package or listener ("stale files"); both
  are left out of `all`, `--all` and `--yes`.

## 12.3.0 — Jellyfin 12.3 support

**Versioning change.** Releases are now numbered `<Jellyfin major>.<Jellyfin minor>.<patch>`. The
first two numbers say which Jellyfin release the build was tested against; the third is ours, and
goes up for fix releases within the same Jellyfin minor (12.3.1, 12.3.2, ...). The previous scheme
ended at 2.4.2. The 12.0/12.1/12.2 plugin builds stay frozen at their 2.4.2 manifest entries; a
tag now builds only the ABI of its own Jellyfin major.minor.

**Plugin** — Jellyfin 12.3 support.

- New 12.3.0.0 build, `browse-modes_12.3.0_12.3.0.zip`. Built against the `Jellyfin.*` 12.3.0
  packages with no source changes; `TMDbLib` stays at 3.0.0 and the server's EF Core /
  Microsoft.Extensions packages moved 10.0.11 -> 10.0.12 (compile-time only).
- CI (`plugin.yml`) derives the Jellyfin ABI from the tag and fails if the tag's major.minor is not
  in its supported list.

**Installer** — `install-browse-modes.sh` now follows `compat.json` instead of "latest".

- Installs only the release listed for the running Jellyfin major.minor; with no entry it changes
  nothing. Never downgrades and never installs a bundle for another Jellyfin version.
- Verifies the plugin MD5 (`manifest.json`) and web SHA-256 (`compat.json`) before touching any
  file, backs up what it replaces, health-checks the server after the restart and rolls back on
  failure. Takes a lock so cron and manual runs cannot overlap.
- New flags `--dry-run`, `--status`, `--auto`, `--enable-auto-update`, `--disable-auto-update`.
  Auto-update (off by default) installs a local copy of the script and one daily cron line per
  server; see the README.
- Docker support is now complete: `--docker NAME` reads the version from inside the container,
  backs up the container's web root and plugin to the host, copies the verified bundle into the
  running container, restarts it, health-checks and rolls back on failure. `--auto --docker NAME`
  and `--enable-auto-update --docker NAME` (one marked cron line per container, `PATH` set for
  docker) work, and `--disable-auto-update [--docker NAME | --all]` removes only the matching jobs.
  Recreating a container discards the web bundle; the next scheduled run re-applies it when
  `compat.json` has an entry for the new version.
- Interactive mode: with no target the script discovers the native install and Docker containers
  (official and linuxserver layouts, running or stopped), shows version, compatibility and what is
  installed, lets you pick servers (`1`, `1,3`, `1-3`, `all`, `q`), shows a plan and asks before
  changing anything. New flags `--target`, `--native`, `--yes`, `--all`. Several servers run
  independently with their own backup, health check and rollback. Every install run ends with a
  per-server summary and a nonzero exit code if any server failed. Questions are read from the
  terminal, so `curl | bash` works.
- New `compat.json` (Jellyfin 12.2 -> 2.4.2 listed; 12.3 is added once 12.3.0 is released),
  `RELEASING.md` runbook, a daily workflow that opens an issue when an unlisted Jellyfin release
  appears, and the test scripts `tests/test-installer.sh`, `tests/test-docker.sh` and
  `tests/test-interactive.sh`.

## 1.0.2.0 — 2026-08-02

**Plugin** — backported to Jellyfin 10.11.x (stable).

- Retargeted from `net10.0` to `net9.0` and Jellyfin packages `12.0.0-rc3` → `10.11.11`.
- Replaced `HasAnyProviderIds` SQL filtering with in-memory matching, so one plugin build serves
  both 10.11.x and 12.x. The provider-id plural API doesn't exist in 10.11; the in-memory approach
  works on both.
- `targetAbi` lowered to `10.11.0.0` so the plugin appears in the 10.11 catalog.

**Web client** — no change since 1.0.1.0. The 12.x web bundle is not compatible with 10.11; a
separate backport is still needed.

**Android TV** — no change since 1.0.0.0. Already targets 0.19.9 / 10.x.

## 1.0.1.0 — 2026-07-26

**Web client**

- Decade and age rating pick tiles now have icons and colours. The sub-grids behind **Decades**
  and **Age Rating** previously rendered as bare text.
  - Decades get an era-appropriate icon with a warm-to-cool colour ramp, so the grid reads as a
    timeline: film reel for the 1940s through to `4K` for the 2020s.
  - Age ratings are graded by how restrictive they are, from a green child icon for G/U to a red
    `Explicit` for R18+/NC-17, with grey for unrated.
- Fixed US television ratings being mis-graded. The country-prefix stripper also removed the
  `TV-` from `TV-MA` and `TV-Y7`, so `TV-MA` was treated as `MA` (mature) rather than adult.

**Plugin** — no functional change. Rebuilt at 1.0.1.0 so all artifacts in a release share a
version.

**Android TV** — no change since 1.0.0.0.

**Build** — the plugin's `AssemblyVersion` is now injected from the git tag rather than hardcoded
in the `.csproj`, so a release cannot ship a DLL whose version disagrees with its package.

## 1.0.0.0 — 2026-07-26

First release.

- **Plugin** — `/Discover/{Trending,TopRated}/{Movies,Shows}`, backed by cached TMDb lists and a
  scheduled task that keeps them warm. Installs into a stock Jellyfin 12.0-rc3; no server fork.
- **Web client** — tile grid in front of Movies and TV libraries, 17 movie / 16 series modes,
  per-mode persisted sort, coloured icons.
- **Android TV client** — the same grid natively, 15 movie / 14 series modes, coloured icons and
  rank badges on Discover results.

> **Correction.** The `jellyfin-web-browse-modes.zip` attached to this release was replaced in
> place after publication, to pick up the icon work that later became 1.0.1.0. That was a
> mistake: a published tag should be immutable. It is recorded here rather than quietly left,
> and from 1.0.1.0 onward every change gets its own version.
