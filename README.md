# Jellyfin Browse Modes

Open a Movies or TV library in Jellyfin and you land in one long alphabetical list. Fine if you
know what you want; not much help if you don't.

Browse Modes puts a menu in front of that list — a grid of tiles, each a different way in:
**Trending**, **Top Rated**, **Mood**, **Genres**, **Story Themes**, **Worlds**, **Hidden Gems**,
**Just Added**, **Random**, and more. Tag-based discovery categories show stacked poster shelves
with sort, shuffle, and grid/shelf toggle controls.

Nothing is hidden. The first tile, **All**, is the ordinary list exactly as it was.

---

## What you need

Browse Modes has two halves, because Jellyfin clients do not share a UI layer.

| Piece | What it is | Needed for |
|---|---|---|
| **Browse Modes plugin** | Installs into stock Jellyfin from a repository URL | Trending and Top Rated on every client |
| **Web client build** | A patched `jellyfin-web` bundle | Tiles in the browser and the Jellyfin **phone** app |
| **Android TV app** | A patched APK, installs beside the official app | Tiles on the **television** |

Your Jellyfin server itself stays **completely stock** — there is no custom server build.

Pick the clients you care about:

- **Browser / phone only** → plugin + web build
- **TV only** → plugin + APK
- **Everything** → all three

> The Jellyfin phone app is a WebView over the server's web bundle, so it inherits the web build
> automatically. The Android TV app is native and shares nothing with it, which is why it needs
> its own install.

## Install

### 1. The plugin

Dashboard → Plugins → Repositories → **+**, and add:

```
https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json
```

Then Catalog → **Browse Modes** → Install, and restart Jellyfin.

Open its settings and paste a [TMDb API key](https://www.themoviedb.org/settings/api) (free).
Only **Trending** and **Top Rated** need it — every other tile works without one.

### 2. The web client

Download `jellyfin-web-browse-modes.zip` from [Releases](../../releases).

**On Docker:**

```bash
unzip jellyfin-web-browse-modes.zip
docker cp dist/. <container>:/jellyfin/jellyfin-web/
```

**On apt / Debian / Ubuntu / LXC:**

The web directory is `/usr/share/jellyfin/web/`. Back it up first, then replace:

```bash
unzip jellyfin-web-browse-modes.zip
sudo cp -a /usr/share/jellyfin/web /usr/share/jellyfin/web.bak
sudo cp -a dist/. /usr/share/jellyfin/web/
```

Then **hard-refresh your browser (Ctrl+Shift+R)**. Jellyfin caches its own interface aggressively
and a normal reload is often not enough — clear the cache if it persists.

> ⚠️ **The web client does not survive a Jellyfin update.** A package upgrade or
> Docker image pull replaces the web directory and the tiles vanish. To prevent this:
>
> **Docker:** bind-mount the bundle over the web root:
> ```
> -v /path/to/dist:/jellyfin/jellyfin-web:ro
> ```
>
> **apt:** hold the `jellyfin-web` package so upgrades don't touch it:
> ```bash
> sudo apt-mark hold jellyfin-web
> ```
> When you do want to upgrade, re-apply the bundle from a matching release afterwards.
>
> **Other setups:** find the web root with `find / -name index.html -path "*web*" 2>/dev/null`. Common paths are `/jellyfin/jellyfin-web` (Docker) and `/usr/share/jellyfin/web` (apt/LXC).

### 3. The Android TV app

**Easiest:** open the **Downloader** app (by AFTVnews) on your TV and enter:

```
avonwilliams.github.io/jellyfin-browse-modes/tv
```

This redirects straight to the latest APK. No typing long URLs.

**Or sideload manually:**

```bash
adb connect <TV-IP>:5555
adb install -r jellyfin-androidtv-v0.0.0-dev.1-debug.apk
```

Full step-by-step instructions, including enabling ADB on the TV, are in the
[user guide](docs/USER-GUIDE.md).

It installs **alongside** the official app rather than replacing it, so your existing setup keeps
working. Source: [AvonWilliams/jellyfin-androidtv](https://github.com/AvonWilliams/jellyfin-androidtv)
(`browse-modes` branch).

## Documentation

| Document | For |
|---|---|
| [User guide](docs/USER-GUIDE.md) | What each tile does, install walkthrough, troubleshooting |
| [Technical reference](docs/TECHNICAL.md) | Every deviation from stock Jellyfin, and how to re-apply it to a new Jellyfin release |
| [10.11 back-port scoping](docs/BACKPORT-10.11.md) | What it would take to support the stable channel. Parked, not started |

## Building from source

You do not need to build anything — releases carry prebuilt artifacts. If you want to anyway, see
[the technical reference](docs/TECHNICAL.md#8-building).

## What's not included

**Most Watched** (play counts summed across every user) was dropped. It needed a new sort key in
the server's `ItemSortBy` enum, which is compiled and closed — a plugin cannot extend it, and
keeping it would have meant forking the server. Dropping one tile was the better trade.

**Decades** and **Age Rating** exist on web but not yet on the TV app.

## Compatibility

Built against **Jellyfin 12.0-rc3** and **Jellyfin 10.11.11** (plugin only). Android TV: **0.19.9**.

> **The plugin works on both 10.11.x (stable) and 12.x.** The web client currently targets 12.x
> only — a 10.11 web backport is scoped but not built. See the
> [back-port scoping](docs/BACKPORT-10.11.md) for details. A Jellyfin update will
overwrite the web build, and the TV app will not auto-update — see the technical reference for
how to re-apply to a newer release.

## Licence

The plugin is MIT. The web and TV clients are forks of Jellyfin's own projects and remain under
GPL-3.0 / GPL-2.0 respectively.
