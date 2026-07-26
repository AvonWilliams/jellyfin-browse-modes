# Jellyfin Browse Modes

Open a Movies or TV library in Jellyfin and you land in one long alphabetical list. Fine if you
know what you want; not much help if you don't.

Browse Modes puts a menu in front of that list — a grid of tiles, each a different way in:
**Just Added**, **Random**, **Unwatched**, **Best Unseen**, **Trending**, **Top Rated**,
**Genres**, **Studios**, **Critics' Picks**, and more.

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

Download `jellyfin-web-browse-modes.zip` from [Releases](../../releases) and unpack it over your
server's web directory (`/jellyfin/jellyfin-web` in the official Docker image, or wherever
`--webdir` points):

```bash
unzip jellyfin-web-browse-modes.zip
docker cp dist/. <your-jellyfin-container>:/jellyfin/jellyfin-web/
```

Source: [AvonWilliams/jellyfin-web](https://github.com/AvonWilliams/jellyfin-web) (`browse-modes` branch).

Then **hard-refresh your browser (Ctrl+Shift+R)**. Jellyfin caches its own interface aggressively,
and without this the tiles will not appear and you will think the install failed. A normal reload
is often not enough — clear the cache if it persists.

> ### ⚠️ The web client does not survive a Jellyfin update
>
> In the official Docker image the web root is **baked into the image, not a mounted volume**.
> Recreating the container or pulling a newer Jellyfin image silently restores the stock client
> and the tiles simply vanish, with no error anywhere. The same applies to a package upgrade on a
> bare-metal or LXC install, which replaces the web directory wholesale.
>
> **To make it stick,** mount the unpacked bundle over the web root instead of copying into it:
>
> ```
> -v /path/to/dist:/jellyfin/jellyfin-web:ro
> ```
>
> The mount then wins over whatever the image ships, so Jellyfin updates leave it alone. You will
> still want to re-download the bundle when you move to a new Jellyfin version, since the client
> and server are versioned together.
>
> **Not sure where your web root is?**
>
> ```bash
> # Is a --webdir flag set?
> docker exec <container> sh -c 'cat /proc/1/cmdline | tr "\0" " "'
> # Otherwise, find index.html
> docker exec <container> sh -c 'find / -name index.html -path "*web*" -not -path "*/config/*" 2>/dev/null'
> ```
>
> Common locations are `/jellyfin/jellyfin-web` (official Docker image) and
> `/usr/share/jellyfin/web` (Debian/Ubuntu packages, and most LXC installs).
>
> **Back up first**, so you can get the stock client back:
> ```bash
> docker exec <container> cp -a /jellyfin/jellyfin-web /jellyfin/jellyfin-web.bak
> ```
>
> The **plugin** is unaffected by all of this — it lives in your config directory and survives
> updates normally.

### 3. The Android TV app

Download the APK from [Releases](../../releases) and sideload it:

```bash
adb connect <TV-IP>:5555
adb install -r jellyfin-androidtv-browse-modes-1.0.0-debug.apk
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

Built against **Jellyfin 12.0-rc3** and **Jellyfin Android TV 0.19.9**.

> **Jellyfin 10.x (stable) is not supported.** The plugin is filtered out of the catalog by its
> `targetAbi`, and the web bundle targets an app that 10.x does not have. See the
> [back-port scoping](docs/BACKPORT-10.11.md) for what supporting it would involve. A Jellyfin update will
overwrite the web build, and the TV app will not auto-update — see the technical reference for
how to re-apply to a newer release.

## Licence

The plugin is MIT. The web and TV clients are forks of Jellyfin's own projects and remain under
GPL-3.0 / GPL-2.0 respectively.
