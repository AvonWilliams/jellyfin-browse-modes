# Jellyfin Browse Modes

Open a Movies or TV library in Jellyfin and you land in one long alphabetical list. Fine if you
know what you want; not much help if you don't.

Browse Modes puts a menu in front of that list — a grid of tiles, each a different way in:
**Trending**, **Top Rated**, **Mood**, **Genres**, **Story Themes**, **Worlds**, **Hidden Gems**,
**Just Added**, **Random**, and more. Tag-based discovery categories show stacked poster shelves
with sort, shuffle, and grid/shelf toggle controls.

Nothing is hidden. The first tile, **All**, is the ordinary list exactly as it was.

**Contents:** [What you need](#what-you-need) · [Install](#install) ·
[Automatic updates](#automatic-updates-optional) · [Troubleshooting](#troubleshooting) ·
[Documentation](#documentation) · [Compatibility](#compatibility)

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

- [Versions and compatibility](#versions-and-compatibility)
- [Quick install with the script](#quick-install-with-the-script)
- [What the script does to your server](#what-the-script-does-to-your-server)
- [Automatic updates (optional)](#automatic-updates-optional)
- [Manual install](#manual-install)
- [Troubleshooting](#troubleshooting)

### Versions and compatibility

Releases are numbered `<Jellyfin major>.<Jellyfin minor>.<patch>`. In **12.3.0**, the `12.3` says
which Jellyfin release it was tested on, and the last number is ours: it goes up for fixes within
the same Jellyfin version (12.3.1, 12.3.2, and so on).

[`compat.json`](compat.json) lists, for each Jellyfin major.minor, the plugin and web releases that
were tested on it. The installer and the automatic updates only ever follow that list. It is also
the kill switch: if there is **no entry for your Jellyfin version, the installer changes nothing**
and you keep stock Jellyfin. An entry is only added once a release has been tested on that Jellyfin
version, so right after a new Jellyfin release you may have to wait a little.

### Quick install with the script

[`install-browse-modes.sh`](install-browse-modes.sh) installs the plugin and the web bundle. It
works on:

- **native installs** (apt/Debian/Ubuntu packages, run by systemd), and
- **Docker** containers, both the official `jellyfin/jellyfin` image and the linuxserver image
  (`lscr.io/linuxserver/jellyfin`). Podman is not searched.

You need `curl`, `unzip`, `python3`, `md5sum` and `sha256sum` (all standard on Debian/Ubuntu). A
native install needs **root** (run it with `sudo`). For Docker, run it as root or as a user who can
run `docker` (a member of the `docker` group); no `sudo` is needed in that case.

> **Precaution:** the one-liners pipe a remote script straight into `bash`, as root. Never run a
> script you have not read. The safe way is to download and read it first, then run your copy.

Run it directly:

```bash
curl -fsSL https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh | sudo bash
# or
wget -qO- https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh | sudo bash
```

Or download, read, then run:

```bash
curl -fsSL -o install-browse-modes.sh https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh
less install-browse-modes.sh
chmod +x install-browse-modes.sh
sudo ./install-browse-modes.sh
```

When you pipe the script from `curl`, the script itself is on standard input, so it reads your
answers from the terminal instead (`/dev/tty`). If there is no terminal at all (a cron job, a CI
run), it cannot ask anything: it then only handles the native install and tells you so. Use
`--target`, `--all` or `--yes` (below) to choose servers without a terminal.

#### Interactive mode: it finds your servers

Run it with no target and it looks for Jellyfin on the machine: the native install (systemd unit,
`jellyfin` package or web directory) and every Docker container, running or stopped, whose image is
Jellyfin or that contains a Jellyfin web directory. (The official image keeps the web files in
`/jellyfin/jellyfin-web`, the linuxserver image under `/usr/share/jellyfin/web`; the script checks
each container rather than assuming.) For every server it shows:

- a number, the kind (native or Docker), the container name and image,
- the Jellyfin version, whether it is running, and its address,
- the compatibility status for that version: the tested plugin and web versions, or "no tested
  release",
- what Browse Modes files are installed now (plugin version, web bundle version, or none).

You then choose which to update:

| You type | Meaning |
|---|---|
| `1` | only server 1 |
| `1,3` | servers 1 and 3 |
| `1-3` | servers 1, 2 and 3 |
| `a` or `all` | every running server that has a tested release |
| `q` | quit without changing anything |

Two kinds of entry are shown but never offered by `a`, `--all` or `--yes`:

- **Same server twice.** A native entry that answers at the same address with the same server Id as
  a running container (typically a host-network container next to leftover native files) is marked
  "same server as docker:NAME". Update it through the container; it is never touched twice.
- **Stale files.** A native web directory with no Jellyfin service, no package and nothing
  listening is shown as "not running (stale files)".

Bad input (a letter, a number out of range, an empty line) is reported and you are asked again. If
only one server is found, it is shown and you just confirm with `Y/n`. Stopped containers are
listed so you can see them, but they cannot be updated until they are running; they are skipped.
The script then prints a plan (what will be installed on each server and where the backup goes) and
asks for confirmation before it changes anything. With several servers, each one is handled on its
own: its own backup, restart, health check and rollback. A failure on one server does not stop the
others.

An example session:

```text
$ sudo ./install-browse-modes.sh
Jellyfin servers found on this machine:
  1) native
       Jellyfin 12.2.0, running, http://127.0.0.1:8096
       Compatibility: tested release for 12.2: plugin 2.4.2, web 2.4.2
       Installed now: plugin none, web bundle none
  2) docker: jellyfin (image jellyfin/jellyfin:12.2)
       Jellyfin 12.2.0, running, http://127.0.0.1:8096
       Compatibility: tested release for 12.2: plugin 2.4.2, web 2.4.2
       Installed now: plugin 2.4.2, web bundle none
  3) docker: jellyfin-old (image jellyfin/jellyfin:12.1)
       Jellyfin unknown, stopped
       Compatibility: unknown (version not available)
       Installed now: plugin none, web bundle none
       Note: stopped: start the container to update it

Which servers should be updated? Enter numbers (1, 1,3, 1-3), a for all with a tested release, or q to quit.
> 1-2
Plan:
  - native: install plugin 2.4.2 and web bundle 2.4.2 for Jellyfin 12.2;
      the current files are backed up under /var/lib/browse-modes-updater/backups/ first, then the server is restarted
  - docker:jellyfin: install plugin 2.4.2 and web bundle 2.4.2 for Jellyfin 12.2;
      the current files are backed up under /var/lib/browse-modes-updater/backups/docker-jellyfin/ first, then the server is restarted

Go ahead with these 2 server(s)? [Y/n] y
... (progress for each server) ...

Automatic updates (optional): a daily job re-checks each server and installs the tested
release when one exists, for example after a Jellyfin upgrade. Nothing is enabled unless you say yes.
Enable daily automatic updates for native? [y/N] n

Summary
=======
  native
      Jellyfin:     12.2.0
      Result:       installed
      Plugin now:   2.4.2    Web bundle now: 2.4.2
      Backup:       /var/lib/browse-modes-updater/backups/20261025190411
      Auto-update:  not enabled
  ...
```

#### Choosing servers without questions

| Option | What it does |
|---|---|
| `--target native` | work on the native install (repeat `--target` for several servers) |
| `--target docker:NAME` | work on the Docker container `NAME` |
| `--native`, `--docker NAME` | short forms of the two above |
| `--all` | every discovered server that is running and has a tested release |
| `-y`, `--yes` | accept the defaults and ask nothing: the same set as `--all` (the only server, if there is just one). No automatic-update question is asked |
| `--host HOST`, `--port PORT` | where Jellyfin listens, if not `127.0.0.1:8096`. Without `--docker` this means the native install. With a container, the script then asks that address from the host instead of asking the container |
| `--plugin-only`, `--web-only` | apply only one half |
| `--dry-run` | show the plan, then download and verify everything, but change nothing |
| `--status` | show version, compatibility, what is installed, auto-update state and the last run for one target |
| `--auto` | for cron: never asks, quiet, logs to a file and syslog, and does nothing (exit 0) when there is nothing to do |
| `--enable-auto-update`, `--disable-auto-update` | see [Automatic updates](#automatic-updates-optional) (`--disable-auto-update --all` removes every job) |
| `-h`, `--help` | the full option list |

Examples:

```bash
sudo ./install-browse-modes.sh --target native --target docker:jellyfin   # two servers, no discovery
./install-browse-modes.sh --docker jellyfin --dry-run                     # what would happen to one container
sudo ./install-browse-modes.sh --all                                      # every supported server found
```

Giving a target yourself skips discovery and the selection question.

#### The summary and exit codes

Every run ends with a summary (except `--auto`, which only writes to the log). Per server it shows:
the target and Jellyfin version; the result (installed, updated, already up to date, skipped with
the reason such as "no tested release for 12.4", failed with the reason, or rolled back); the
plugin and web bundle versions now installed; where the backup is; whether automatic updates are
enabled; then totals, the log file and next steps (hard refresh the page, how to roll back, how to
switch automatic updates on or off).

The exit code is `0` when nothing failed (installed, already up to date, or a dry run), `1` if any
server failed or was rolled back, and `3` when every server you chose was skipped because there is
no tested release for its Jellyfin version (a mix of skipped and installed servers is `0`).
`--auto` exits `0` in that case so cron stays quiet.

### What the script does to your server

For each server it works on, in this order:

1. Reads the running Jellyfin version and looks up its major.minor in `compat.json`. No entry: it
   stops there, changes nothing and says so.
2. Compares with what is installed. It skips what is already at the tested version, **never
   downgrades**, and **never installs a bundle for a different Jellyfin major.minor**. For the web
   bundle the version marker alone is not trusted: the install also records the SHA-256 of
   `index.html`, `runtime.bundle.js` and `main.jellyfin.bundle.js` (in `.browse-modes-files` next to
   `.browse-modes-version`), and the bundle counts as installed only while those files still match.
   So when `apt` reinstalls or upgrades `jellyfin-web` (even `12.3.0` to `12.3.1`) and puts stock
   files back, the next run notices and installs the bundle again. A bundle installed by an
   earlier version of this script has no fingerprint; it is installed once more to record one
   (one extra restart on the first run after you update the script).
3. Downloads the release and **verifies it before touching anything**: the plugin against the MD5
   in `manifest.json`, the web bundle against the SHA-256 in `compat.json`. A missing or
   placeholder checksum is refused.
4. **Backs up** the files it is about to replace: the web directory and the Browse Modes plugin
   directory. Natively and for Docker the backups are under
   `/var/lib/browse-modes-updater/backups/` (`docker-NAME/` for a container; the last three are
   kept). Without root, Docker runs keep them under `~/.local/state/browse-modes-updater/`.
5. Installs the new files and restarts Jellyfin (the service, or `docker restart` for a
   container). Old files are **renamed, not deleted**: the previous Browse Modes plugin folder (and
   the previous web directory) is moved into a `.bm-trash` folder next to the plugins or web
   directory, so Jellyfin never loads it, and any other `Browse Modes_*` folder (older, newer, or an
   empty leftover) goes there too, leaving exactly one. Deleting a plugin folder whose DLLs the
   running server has open fails on some filesystems (ntfs-3g/FUSE); renaming does not.
6. **Health-checks**: it waits up to about a minute for the server to answer. If it does not, it
   renames the new files aside, renames the old ones back and restarts again. If the restart
   command itself fails, it rolls back at once instead of waiting.
7. Deletes the `.bm-trash` folder once everything is settled. If that fails, the files stay there
   (harmless), the summary says where, and the next run tries again.

A lock stops two runs from overlapping.

For **Docker**, nothing about how you created the container changes. The web bundle is copied into
the running container's Jellyfin web directory, and the plugin goes into its config volume (the
mounted `/config` folder, written from the host when it is a bind mount, otherwise copied in with
`docker cp`). If the web directory is a read-only mount you set up yourself, the script leaves the
web bundle alone and still updates the plugin.

### Automatic updates (optional)

Off by default. If you want a tested release applied without remembering to run the script, it can
install a daily job that runs its own saved copy. It never fetches and runs a script from the
internet on each run. Use a saved copy, not `curl | bash`, because the copy you inspected is the
one that is stored:

```bash
curl -fsSL -o install-browse-modes.sh https://raw.githubusercontent.com/AvonWilliams/jellyfin-browse-modes/refs/heads/main/install-browse-modes.sh
less install-browse-modes.sh                                   # read it first
sudo bash install-browse-modes.sh --enable-auto-update         # native install
bash install-browse-modes.sh --enable-auto-update --docker jellyfin   # a Docker container
```

(After an interactive install the script also offers this, per server, with the answer defaulting
to no.)

It prints exactly what it added. The script copy is stored in `/usr/local/lib/browse-modes/` (root)
or `~/.local/lib/browse-modes/` (a non-root Docker user), and a cron line is added to the crontab
of the user who ran it. There is **one line per server**, and they can coexist. A native line looks
like this:

```cron
27 3 * * * /usr/local/lib/browse-modes/updater.sh --auto # browse-modes-auto-update
```

and a Docker line (one per container) like this:

```cron
41 2 * * * PATH=/usr/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin /usr/local/lib/browse-modes/updater.sh --auto --docker jellyfin # browse-modes-auto-update docker:jellyfin
```

The minute and hour are picked at random between 02:00 and 05:59 and kept when you re-run. The
`PATH=` part is there because cron starts with a short `PATH` that often lacks the folder `docker`
lives in; the script puts the folder it found at enable time first.

Check and remove:

```bash
sudo bash /usr/local/lib/browse-modes/updater.sh --status                              # native
bash /usr/local/lib/browse-modes/updater.sh --status --docker jellyfin                  # a container
sudo bash /usr/local/lib/browse-modes/updater.sh --disable-auto-update                  # removes the native line only
bash /usr/local/lib/browse-modes/updater.sh --disable-auto-update --docker jellyfin     # removes that container's line only
bash /usr/local/lib/browse-modes/updater.sh --disable-auto-update --all                 # removes every browse-modes line
```

The stored copy is removed together with the last job. If the stored copy is missing or broken,
remove the job by hand; each job is a single line ending in `# browse-modes-auto-update`
(plus ` docker:NAME` for a container). To remove all of them, and the stored copy:

```bash
sudo crontab -l | grep -v '# browse-modes-auto-update' | sudo crontab -     # root's crontab
crontab -l | grep -v '# browse-modes-auto-update' | crontab -               # your own crontab
sudo rm -rf /usr/local/lib/browse-modes
```

Results go to `/var/lib/browse-modes-updater/updater.log` and to syslog.

**What it does and does not do**

- It does not upgrade Jellyfin itself. Update Jellyfin the way you normally do.
- It does nothing when there is no tested release for your Jellyfin version (exit 0, a line in the
  log). It never downgrades and never installs a release meant for another Jellyfin version.
- It restarts Jellyfin only when it installs something; a day with nothing to do changes nothing.
- It does not update itself. Newer installer logic arrives only if you run `--enable-auto-update`
  again from a fresh download.

**Native installs**

A package upgrade of Jellyfin replaces the web directory and removes the web bundle. The plugin
(in `/var/lib/jellyfin/plugins`) stays. The next scheduled run notices the missing bundle and puts
it back, if `compat.json` has an entry for the new version.

**Docker**

The web bundle is copied into the running container, so it lives in the container's own storage.
When the container is **recreated** (an image update, `docker compose up` after a new image, or a
tool such as Watchtower), the new container starts from the stock image: the web bundle is gone.
The plugin is in your config volume and stays. The next scheduled run sees that the bundle is
missing and, if `compat.json` has an entry for the new Jellyfin version, copies it back in. So
there can be a **gap of up to a day** (until the next run) with stock web and no tiles in the
browser. You do not need to change your `docker run` or compose file, and no bind mount is needed.
If you cannot wait, run the script by hand.

The job needs permission to run `docker`: run `--enable-auto-update` as root or as a member of the
`docker` group, and the job runs as that same user. If the container is missing or stopped when the
job runs, it logs that and does nothing. The script reads the Jellyfin version from inside the
container, which works with bridge, host and published-port setups alike; if that does not suit
you, `--host` and `--port` make it ask that address from the host instead.

**Right after a Jellyfin upgrade**

Until a tested release for the new Jellyfin version exists, nothing is installed. A native or
recreated Docker setup then has the stock web files, so the tiles are gone in the browser. The
plugin from the older release stays where it is; whether it still loads depends on the Jellyfin
version. Once a release is tested and listed in `compat.json`, the next run installs it.

**Trust.** This is a cron job that runs a script stored on your machine, as the user who enabled
it (root for a native install), and that script downloads and installs files. The stored copy is
the one you inspected and is not fetched again on each run. What it downloads is checked before it
is used: the plugin against the MD5 in `manifest.json` and the web bundle against the SHA-256 in
`compat.json`, both read from this repository's `main` branch. So the checksums are only as
trustworthy as that branch and your network path to GitHub. If you do not want to depend on that,
leave automatic updates off and run the installer by hand after reading what changed.

Maintainers: the release process is in [RELEASING.md](RELEASING.md).

### Manual install

If you prefer not to use the script, install the pieces by hand with steps 1 to 3 below.

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
> Docker image pull replaces the web directory and the tiles vanish. The easiest fix is the
> installer with [automatic updates](#automatic-updates-optional), which puts the bundle back once
> a tested release exists for your new Jellyfin version. By hand:
>
> **Docker:** bind-mount the bundle over the web root, so recreating the container keeps it:
> ```
> -v /path/to/dist:/jellyfin/jellyfin-web:ro
> ```
> (The installer leaves a read-only web mount like this alone and only updates the plugin.)
>
> **apt:** hold the `jellyfin-web` package so upgrades don't touch it:
> ```bash
> sudo apt-mark hold jellyfin-web
> ```
> When you do want to upgrade, re-apply the bundle from a matching release afterwards.
>
> **Other setups:** find the web root with `find / -name index.html -path "*web*" 2>/dev/null`. Common paths are `/jellyfin/jellyfin-web` (official Docker image), `/usr/share/jellyfin/web` (apt/LXC and the linuxserver image).

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

### Troubleshooting

**The installer says "No tested Browse Modes release for Jellyfin X.Y".** There is no entry for
your Jellyfin version in [`compat.json`](compat.json) yet, so nothing was changed and you are on
stock Jellyfin. Wait for a release for that version, or stay on a Jellyfin version listed in the
[compatibility](#compatibility) section.

**The tiles disappeared after a Jellyfin upgrade.** An apt upgrade or reinstall replaces
`/usr/share/jellyfin/web`, and a new Docker image has stock web files. The installer compares the
web files with the fingerprint it recorded, so re-running it (or the next scheduled run) installs the
bundle again even when the version marker is still there. Re-run the installer, or let
[automatic updates](#automatic-updates-optional) do it on the next scheduled run (up to a day).
Then hard-refresh the browser (Ctrl+Shift+R).

**Checking the server through its API returns 401.** On Jellyfin 12.3 the `X-Emby-Token` header is
rejected. Send the token in the `Authorization` header instead:

```bash
curl -s -H 'Authorization: MediaBrowser Token="YOUR_API_KEY"' http://localhost:8096/Plugins
```

**The update failed with "Directory not empty" or `.fuse_hidden` files (Docker with `/config` on an
NTFS or other FUSE drive).** Older versions of the script deleted the old plugin folder while the
server still had its DLLs open; on ntfs-3g that fails, the restore failed the same way, and the
plugin folder was left without `meta.json`. The current script renames the old folder into
`.bm-trash` instead and puts it back by renaming if the update has to be rolled back. If an update
already left you with a broken plugin folder, stop Jellyfin (`docker stop NAME`), delete the
`Browse Modes_*` folder in the config volume's `plugins` directory (this works once the server is
stopped) and run the installer again. If the script cannot restore the files it prints their exact
state and the commands to fix it by hand; the backup folder it names is always a complete copy.

**A `.bm-trash` folder is left next to `plugins`.** The script could not delete the old files (they
were still locked). It is outside the plugins directory, so Jellyfin ignores it; delete it by hand
(`sudo rm -rf` on the path the summary names) or let the next run do it.

**Jellyfin shows no Browse Modes plugin after a restart, and there are two `Browse Modes_*`
folders.** Jellyfin keeps only one folder per plugin and an empty or older one can win. The
installer now moves every other `Browse Modes_*` folder aside; with an older script, remove the
extra folders by hand (keep the one with a `meta.json` of the version you want) and restart.

**Restoring from a backup.** The installer prints the backup folder in its summary. Each backup
holds `web/` (the previous web directory, when it was replaced) and `plugin/` (the previous Browse
Modes plugin folder). Native example, with the newest backup:

```bash
B=$(ls -1d /var/lib/browse-modes-updater/backups/2* | tail -1)
sudo systemctl stop jellyfin
sudo rm -rf /usr/share/jellyfin/web && sudo cp -a "$B/web" /usr/share/jellyfin/web
sudo rm -rf /var/lib/jellyfin/plugins/Browse\ Modes_* && sudo cp -a "$B/plugin/." /var/lib/jellyfin/plugins/
sudo systemctl start jellyfin
```

For a container, the backup is under `backups/docker-NAME/<time>/`; copy it back with
`docker cp "$B/web/." NAME:/jellyfin/jellyfin-web/` (and the plugin folder into the config volume's
`plugins` directory), then `docker restart NAME`. To go back to plain Jellyfin without the tiles,
remove the `Browse Modes_*` plugin folder and restore the stock web files (re-pull the image or
reinstall `jellyfin-web`).

**Automatic updates did nothing.** Run `--status` (see above) and read
`/var/lib/browse-modes-updater/updater.log`. Common reasons: no tested release for your version,
the container was stopped at the time, or the cron user cannot run `docker`.

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

Releases are numbered `<Jellyfin major>.<Jellyfin minor>.<patch>` (see
[Versions and compatibility](#versions-and-compatibility)). [`compat.json`](compat.json) is the
list of tested releases the installer and the automatic updates follow. A Jellyfin version that is
not in it is unsupported until a release is tested and added (see [RELEASING.md](RELEASING.md)).

| Jellyfin | Plugin | Web bundle | Installer |
|---|---|---|---|
| 12.3 | 12.3.0 (plugin ABI 12.3) | 12.3.0 | listed in `compat.json` |
| 12.2 | 2.4.2 (ABI 12.2) | 2.4.2 | listed in `compat.json` |
| 12.1 | 2.4.2 (ABI 12.1), frozen | no web build | plugin catalog only |
| 12.0 | 2.4.2 (ABI 12.0), frozen | no web build | plugin catalog only |

- Android TV: **0.19.9**.

> **The plugin also works on 10.11.x.** The TV client targets 10.11 and 12.x. The 10.11 web
> backport lives on the `browse-modes-10.11` branch. See the
> [back-port scoping](docs/BACKPORT-10.11.md) for details. A Jellyfin update will overwrite the web
> build, and the TV app will not auto-update — see the technical reference for how to re-apply to a
> newer release.

## Licence

The plugin is MIT. The web and TV clients are forks of Jellyfin's own projects and remain under
GPL-3.0 / GPL-2.0 respectively.
