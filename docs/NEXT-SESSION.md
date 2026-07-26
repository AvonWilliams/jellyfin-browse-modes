# Handoff — where things stand and what's next

Written 2026-07-26. Read this first; it points at everything else.

## State: shipped and running in production

Browse Modes is **live on the real server** (Proxmox LXC `JellyClone`, CT 1056) and confirmed
working on **web, Android phone and Android TV**.

| Piece | Version | Where |
|---|---|---|
| Plugin | 1.0.1.0 | [jellyfin-browse-modes](https://github.com/AvonWilliams/jellyfin-browse-modes) |
| Web client | 1.0.1.0 | [jellyfin-web](https://github.com/AvonWilliams/jellyfin-web) fork, `browse-modes` branch |
| Android TV | 1.0.1.0 | [jellyfin-androidtv](https://github.com/AvonWilliams/jellyfin-androidtv) fork, `browse-modes` branch |

Plugin repository URL: `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json`

The server runs **stock Jellyfin 12.0-rc3** — no forked server anywhere. See
[TECHNICAL.md §1](./TECHNICAL.md) for why the server half could become a plugin and the UI half
could not.

## Next up (what this session ended on)

Two pieces of work, neither started:

### 1. Reorder the tiles

The current order was a judgement call and has never been revisited. It is defined in two places
that must be kept in step:

- Web: `src/apps/modern/features/libraries/constants/browseModes.ts` — `movieBrowseModes` and
  `tvBrowseModes`
- Android TV: `ui/browsing/browsemodes/BrowseModes.kt` — `movieBrowseModes` and
  `seriesBrowseModes`

Current order (movies): All, Unwatched, Just Added, Best Unseen, Random, Favorites, Genres,
Highest Rated, Top Rated, Trending, New Releases, Decades, Studios, Recently Played, Age Rating,
Critics' Picks, Longest.

Worth deciding on a *principle* rather than shuffling by feel. One option is grouping by intent:

| Group | Tiles |
|---|---|
| Escape hatch | All |
| "What should I watch?" | Unwatched, Best Unseen, Random |
| Curated / external | Trending, Top Rated, Critics' Picks, Highest Rated |
| Recency | Just Added, New Releases, Recently Played |
| Browse by attribute | Genres, Studios/Networks, Decades, Age Rating |
| Personal | Favorites |
| Odds and ends | Longest |

Note the TV grid is 4 columns, so order changes how things land in rows — the first four tiles are
the ones actually seen without scrolling.

### 2. Brainstorm additional tiles

Candidates below are grouped by how much work they'd be. Nothing here is decided.

**Cheap — a plain sort or filter, same shape as existing presets:**

| Idea | Backing | Note |
|---|---|---|
| Shortest | `Runtime` asc | Mirror of Longest. "I have 90 minutes" |
| Continue Watching | `IsResumable` filter | Genuinely useful; currently only on the TV smart screen |
| Watched | `IsPlayed` filter | Mirror of Unwatched; good for re-watching |
| Oldest / Classics | `PremiereDate` asc | Mirror of New Releases |
| Just Added & Unwatched | `DateCreated` desc + `IsUnplayed` | Combines two existing tiles; arguably the most-wanted view |

**Moderate — needs a new filter or picker:**

| Idea | Backing | Note |
|---|---|---|
| 4K / HD | quality filter | Jellyfin exposes `isHd` / `is4K` |
| With Subtitles | `hasSubtitles` | Useful for accessibility |
| By Year | picker, like Decades but finer | Reuses the picker machinery |
| Tags | picker over tags | Only useful if the library is tagged |
| Kids-safe | curated age-rating filter | A pre-filtered Age Rating; `pickTiles.ts` already grades ratings by restrictiveness |

**Expensive — needs plugin or server work:**

| Idea | Why |
|---|---|
| Most Watched | Dropped. Needs a ranked endpoint — see [TECHNICAL.md §6](./TECHNICAL.md) |
| Because You Watched… | Recommendation logic; no existing endpoint |
| People / Actors | Jellyfin can browse by person, but the TV client has this deliberately disabled upstream ("screen doesn't behave properly") |
| Collections | Exists as a tab in the stock client; would need wiring as a tile |

**Still missing on TV specifically:** Decades and Age Rating. Both are pickers, and
`BrowseGridFragment` has no way to carry a picked value — see [TECHNICAL.md §6](./TECHNICAL.md).

## Also parked

- **[10.11 stable back-port](./BACKPORT-10.11.md)** — fully scoped, not started. Currently only
  12.x is supported.
- **CI in the two fork repos.** The web zip and APK are built locally and uploaded by hand; only
  the plugin is automated.
- **Light-theme contrast on web.** Icon colours were chosen against the dark theme, which is the
  default. The palest — `#B0BEC5` (All), `#F2C14E` (Best Unseen) — will be weak on light.
- **APK signing.** Debug-signed, which is why it installs beside the official app rather than
  replacing it. Arguably the right default; revisit only if Play-style updates are wanted.
- **Delete `AvonWilliams/jellyfin-browse-modes-leaked-delete-me`.** Private, harmless, but should
  go. Needs an account with `delete_repo` scope.

## Operational notes worth not rediscovering

**Releases.** Every change gets a version — tag `vX.Y.Z.W`, CI builds the plugin and injects the
version from the tag, then attach the web zip and APK and add a `manifest.json` entry. Never
replace an asset on a published tag; that mistake is recorded in `CHANGELOG.md`.

**The web bundle does not survive updates.** `jellyfin-web` is a real apt package on an LXC
install, so any upgrade replaces `/usr/share/jellyfin/web`. It is currently held with
`apt-mark hold jellyfin-web`. On Docker the same applies because the web root is baked into the
image.

**Browser cache is the usual culprit.** A plain reload is not enough for either the plugin
catalog or the web bundle — clear the cache. This cost time twice in one session.

**Plugin not in the catalog?** Check, in order: the status filter defaults to *Installed* and
uninstalled entries are hidden (`routes/plugins/index.tsx:51`); the browser cached the list; the
manifest 404'd when it was added.

**Third-party plugin conflicts.** `JavaScriptInjector` rewrites `/usr/share/jellyfin/web/index.html`
in place and will fight the bundle — install the bundle first, let it inject after. Meilisearch
and others were disabled on the production box to rule out interference.

**Committing.** All three repos are configured with the GitHub noreply email. Do not set
`user.email` per-commit; a personal address leaked that way once, and force-pushing did **not**
remove it — see the incident note in `PROGRESS.md`.
