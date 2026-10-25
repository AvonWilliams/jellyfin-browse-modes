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

### Quick install / update (script)

The repo ships [`install-browse-modes.sh`](install-browse-modes.sh), which detects your server's
Jellyfin version, looks up the plugin + web bundle release that was tested against it in
[`compat.json`](compat.json), checks both downloads against their published checksums, and applies
them — stopping Jellyfin, backing up what it replaces, applying the changes, then starting it again
and checking the server answers (it rolls back from the backup if it does not). If your Jellyfin
version has no tested release, it says so and changes nothing. Safe to re-run: it skips anything
already up to date and never downgrades.

Native installs need **root** — the script writes `/var/lib/jellyfin` and `/usr/share/jellyfin`,
so run it with `sudo`. Docker installs use `--docker` instead and don't need root.

> **Precaution:** the one-liners below pipe a remote script straight into `bash` — with `sudo`,
> as root. Never run a script you haven't read. The safe path is to download and inspect it first
> (the second example), then run it yourself.

**curl**

Run directly:

```bash
curl -fsSL https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh | sudo bash
```

Or download, inspect, then run:

```bash
curl -fsSL -o install-browse-modes.sh https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh
chmod +x install-browse-modes.sh
sudo ./install-browse-modes.sh
```

**wget**

Run directly:

```bash
wget -qO- https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh | sudo bash
```

On Docker, append `--docker <container-name>` (and drop `sudo`). Use `--plugin-only` / `--web-only`
to apply just one half, and `--dry-run` to download and verify without changing anything. See
`--help` for the rest.

### Automatic updates (optional)

Off by default. If you want fixes for your Jellyfin version applied without remembering to run the
script, the installer can set itself up as a daily job. **Native installs only** (not Docker, see
below). Run it from a saved copy of the script, not piped from `curl`, because that saved copy is
what gets installed:

```bash
curl -fsSL -o install-browse-modes.sh https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh
less install-browse-modes.sh          # read it first
sudo bash install-browse-modes.sh --enable-auto-update
```

It prints exactly what it added. Afterwards:

```bash
sudo bash /usr/local/lib/browse-modes/updater.sh --status             # versions, cron state, last run result
sudo bash /usr/local/lib/browse-modes/updater.sh --disable-auto-update
```

If the local copy is missing or broken, remove it by hand. The job is the single line in root's
crontab that ends in `# browse-modes-auto-update`:

```bash
sudo crontab -l | grep -vF '# browse-modes-auto-update' | sudo crontab -
sudo rm -rf /usr/local/lib/browse-modes
```

**What it does.** Once a day, at a random time between 02:00 and 05:59, it runs the stored local
copy of the installer with `--auto`. If `compat.json` lists a tested release for your running
Jellyfin version and your installed plugin or web bundle is older, or the web bundle was replaced
by a Jellyfin upgrade, it downloads the release, verifies the checksums, backs up the current files
under `/var/lib/browse-modes-updater/backups/` (last three kept), restarts Jellyfin, and rolls back
if the server does not come back. Results go to `/var/lib/browse-modes-updater/updater.log` and
syslog.

**What it does not do.**

- It does not upgrade Jellyfin itself. Update Jellyfin the way you normally do.
- It does nothing when there is no tested release for your Jellyfin version. Right after a Jellyfin
  release, you stay on what you have until a matching Browse Modes release is published.
- It never downgrades, and never installs a release meant for a different Jellyfin version.
- It does not update itself. Newer installer logic only arrives if you run
  `--enable-auto-update` again from a fresh download.
- It restarts Jellyfin when it installs something (nothing is restarted on a no-op day).

**Trust.** This is a root cron job that runs a script stored on your machine, and that script
downloads and installs files. The script is the copy you inspected, owned by root and not fetched
again on each run. What it downloads is checked before it is used: the plugin against the MD5 in
`manifest.json` and the web bundle against the SHA-256 in `compat.json`, both read from this
repository's `main` branch. So the checksums are only as trustworthy as that branch and your
network path to GitHub; if you do not want to depend on that, leave auto-update off and run the
installer by hand after reading what changed.

**Docker.** Not supported, deliberately. Recreating a container (an image pull, `docker compose up`)
discards a web bundle that was copied into it, so a cron job beside it would only ever re-copy
into a container that is about to be replaced again. Bind-mount the bundle over the web root
instead (see the web client section below), and run `install-browse-modes.sh --docker <name>` by
hand when you update; `--enable-auto-update` refuses `--docker`.

### 1. The plugin

Dashboard → Plugins → Repositories → **+**, and add:

```
https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json
```

Then Catalog → **Browse Modes** → Install, and restart Jellyfin.

Open its settings and paste a [TMDb API key](https://www.themoviedb.org/settings/api) (free).
Only **Trending** and **Top Rated** need it — every other tile works without one.

### 2. The web client

Download the `jellyfin-web-browse-modes-<version>.zip` that matches your Jellyfin version from
[the web fork releases](https://github.com/AvonWilliams/jellyfin-web/releases) (the first two
numbers match: `12.3.x` for Jellyfin 12.3; see [`compat.json`](compat.json)).

The zip extracts its files at the top level (no `dist/` wrapper), so unzip into a fresh directory
before copying — otherwise the cp will fail with "no such file or directory".

**On Docker:**

```bash
mkdir -p /tmp/jbm && unzip -d /tmp/jbm jellyfin-web-browse-modes-<version>.zip
docker cp /tmp/jbm/. <container>:/jellyfin/jellyfin-web/
sudo rm -rf /tmp/jbm
```

**On apt / Debian / Ubuntu / LXC:**

The web directory is `/usr/share/jellyfin/web/`. Back it up first, then replace:

```bash
sudo cp -a /usr/share/jellyfin/web /usr/share/jellyfin/web.bak
mkdir -p /tmp/jbm && unzip -d /tmp/jbm jellyfin-web-browse-modes-<version>.zip
sudo cp -a /tmp/jbm/. /usr/share/jellyfin/web/
sudo rm -rf /tmp/jbm
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
8953482
or
http://aftv.news/8953482
or
avonwilliams.github.io/jellyfin-browse-modes/tv
```

This redirects straight to the latest APK. No typing long URLs.

**Or sideload manually:**

```bash
adb connect <TV-IP>:5555
adb install -r jellyfin-androidtv/app/build/outputs/apk/debug/jellyfin-androidtv-v*-debug.apk
```

Full step-by-step instructions, including enabling ADB on the TV, are in the
[user guide](docs/USER-GUIDE.md).

It installs **alongside** the official app rather than replacing it, so your existing setup keeps
working. Source: [AvonWilliams/jellyfin-androidtv](https://github.com/AvonWilliams/jellyfin-androidtv)
(`main` branch).

## Documentation

| Document | For |
|---|---|
| [User guide](docs/USER-GUIDE.md) | What each tile does, install walkthrough, troubleshooting |
| [Technical reference](docs/TECHNICAL.md) | Every deviation from stock Jellyfin, and how to re-apply it to a new Jellyfin release |
| [Releasing](RELEASING.md) | The runbook for supporting a new Jellyfin release |
| [10.11 back-port scoping](docs/BACKPORT-10.11.md) | What the 10.11 backport involved. Done August 2026 |

## Building from source

You do not need to build anything — releases carry prebuilt artifacts. If you want to anyway, see
[the technical reference](docs/TECHNICAL.md#8-building).

## What's not included

**Most Watched** (play counts summed across every user) was dropped. It needed a new sort key in
the server's `ItemSortBy` enum, which is compiled and closed — a plugin cannot extend it, and
keeping it would have meant forking the server. Dropping one tile was the better trade.

**Decades** and **Age Rating** are on both web and TV. **Vault** (multi-level discovery hub) is deferred.

## Compatibility

Releases are numbered `<Jellyfin major>.<Jellyfin minor>.<patch>`. **`12.3.x` was tested on
Jellyfin 12.3**; the third number is ours and goes up for fixes within the same Jellyfin version.
[`compat.json`](compat.json) is the list of tested releases the installer and auto-update follow,
and a Jellyfin version that is not in it is unsupported until a release is tested and added
(see [RELEASING.md](RELEASING.md)).

- **Jellyfin 12.2:** plugin and web 2.4.2 (listed in `compat.json`).
- **Jellyfin 12.3:** plugin and web 12.3.0. Listed in `compat.json` once released; until then the
  installer reports no tested release for 12.3.
- **Plugin only, frozen at 2.4.2:** Jellyfin 12.0 and 12.1, via the plugin catalog.
- Android TV: **0.19.9**.

> **The plugin also works on 10.11.x.** The TV client targets 10.11 and 12.x. The 10.11 web
> backport lives on the `browse-modes-10.11` branch. See the
> [back-port scoping](docs/BACKPORT-10.11.md) for details. A Jellyfin update will overwrite the web
> build, and the TV app will not auto-update — see the technical reference for how to re-apply to a
> newer release.

## Licence

The plugin is MIT. The web and TV clients are forks of Jellyfin's own projects and remain under
GPL-3.0 / GPL-2.0 respectively.
