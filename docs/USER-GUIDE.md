# Browse Modes — User Guide

What this change does, and how to install it.

> **Related documents**
> - [TECHNICAL.md](./TECHNICAL.md) — the full engineering reference:
>   every file changed, why, the known gotchas, and how to re-apply all of it to a newer Jellyfin
>   release.
> - [HANDOFF.md](../README.md) — one-page overview and repo map.

---

## What it does

Normally, clicking a Movies or TV library in Jellyfin drops you straight into one long
alphabetical list of everything you own. Fine if you know what you want. Not much help if you
don't.

This change puts a menu in front of that list. Click a library and you get a grid of tiles, each
one a different way in:

<p align="center"><em>Movies → <strong>Browse by…</strong> → Trending → the trending films you actually own</em></p>

Nothing is hidden. The first tile, **All**, is the ordinary list exactly as it was.

## The tiles

| Tile | Shows you |
|---|---|
| **All** | Everything, as normal |
| **Unwatched** | Only things you haven't seen |
| **Just Added** | Newest arrivals in your library first |
| **Best Unseen** | Highest-rated things you haven't watched yet |
| **Random** | Shuffled, for when you can't decide |
| **Favorites** | Things you've hearted |
| **Genres** | Grouped by genre |
| **Highest Rated** | Best community ratings first |
| **Top Rated** | The all-time greats (from TMDB) that you own |
| **Trending** | What's popular worldwide right now, that you own |
| **New Releases** | Newest by release date, not by when you added it |
| **Studios** / **Networks** | Grouped by studio, or by network for TV |
| **Recently Played** | What you watched most recently |
| **Critics' Picks** | Best critic scores *(films only)* |
| **Longest** | Longest runtime first |
| **Decades** | Pick a decade, then browse it *(web and phone only)* |
| **Age Rating** | Pick a rating, then browse it *(web and phone only)* |

**Trending** and **Top Rated** put a small numbered badge on each poster. That number is the
title's position in TMDB's worldwide ranking — a badge reading `28` means it's the 28th most
popular film right now and you happen to own it. It is not a ranking of your library.

Each tile remembers its own sort order. If you change the sort inside **Just Added**, your normal
library view is left alone.

## Which devices get it

| Device | How it arrives |
|---|---|
| Web browser | Automatically, once the server is updated |
| Jellyfin **phone** app (Android) | Automatically, once the server is updated |
| Jellyfin **TV** app (Android TV / Google TV) | Needs a custom app installed on the TV — see below |

The phone app is really a web browser in disguise: it loads its screens from the server, so it
picks up the change for free. The TV app is a completely separate piece of software that only
asks the server for data and draws everything itself — which is why it needs its own install.

Two tiles (**Decades** and **Age Rating**) aren't on the TV app yet.

---

# Installing

Two parts. **The server part is required.** The TV part is only needed if you want the tiles on
your television.

## Part 1 — The server side

This covers the browser and the phone app, and it also supplies the data that Trending and Top
Rated need on every client — including the TV.

**Your Jellyfin server stays completely standard.** There is no custom server build. You install
a plugin, exactly like any other Jellyfin plugin.

### Step 1: Install the plugin

In Jellyfin: **Dashboard → Plugins → Repositories → +**, and add this URL:

```
https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json
```

Then go to **Catalog**, find **Browse Modes**, click Install, and restart Jellyfin.

### Step 2: Give it a TMDb key

Open the plugin's settings and paste in a TMDb API key. They're free — sign up at
themoviedb.org and copy the key from your account settings.

Only **Trending** and **Top Rated** use it. Every other tile works without one; those two will
just come up empty.

### Step 3: Install the web interface

Download `jellyfin-web-browse-modes.zip` from the Releases page and unpack it over your server's
web folder. For the official Docker image that's `/jellyfin/jellyfin-web`:

```bash
docker cp dist/. <your-container>:/jellyfin/jellyfin-web/
```

If you run Jellyfin without Docker, it's wherever `--webdir` points, typically
`/usr/share/jellyfin/web`.

> Note this is undone if you recreate the container or update Jellyfin — you'd need to repeat it.

### Step 4: Hard-refresh your browser

**Ctrl+Shift+R.** Jellyfin caches its own interface aggressively, and without this the tiles
simply won't appear and you'll think the install failed. This trips people up constantly.

On the phone app, force-close it and reopen.

### Prefer to build it yourself?

The web bundle can be built from source instead of downloading it:

```bash
cd jellyfin-web
npm ci
npm run build:production      # -> dist/
```

Needs Node 24 or newer.

---

## Part 2 — The TV app

You'll install a custom version of the Jellyfin TV app. It installs **alongside** your existing
one — your normal Jellyfin app is untouched and keeps working. The new one appears as a separate
icon.

### Step 1: Turn on debugging on the TV

On the television:

1. **Settings → Device Preferences → About**
2. Scroll to **Build** and click it **seven times**. You'll see "You are now a developer".
3. Go back to **Settings → Device Preferences → Developer options**
4. Turn on **Network debugging** (sometimes called USB debugging / ADB debugging)

Note the TV's IP address from **Settings → Network & Internet**.

### Step 2: Build the app

```bash
cd Media-Apps/jellyfin-androidtv
export JAVA_HOME=$PWD/../.toolchain/jdk-21.0.12+8
export ANDROID_HOME=$PWD/../.toolchain/android-sdk
./gradlew assembleDebug --no-daemon
```

First run takes around 8 minutes. After that, well under a minute.

### Step 3: Connect and install

```bash
export PATH=$PWD/../.toolchain/android-sdk/platform-tools:$PATH

adb connect <TV-IP>:5555
adb install -r app/build/outputs/apk/debug/*.apk
```

**The TV will show a prompt** the first time — "Allow debugging from this computer?" Accept it,
and tick *Always allow* so you don't have to repeat this. The install fails until you do.

Expect `Success` when it finishes.

### Step 4: Open it

There'll be a second Jellyfin icon on your TV. Open it, enter your server address (for example
`http://<SERVER-IP>:8097`), and sign in. It keeps its own login, separate from the stock app.

Click a Movies or TV library and you should see the tiles.

### If the TV can't be found

- **Is your VPN on?** A VPN on the computer hides the TV completely. This is the single most
  common cause. Turn it off and retry.
- **Is the TV actually awake?** A sleeping TV won't answer.
- Ordinary `ping` won't find it — it only responds on its debugging port. To scan for it:
  ```bash
  nmap -p 5555 --open <YOUR-SUBNET>.0/24
  ```
- Still stuck? Re-check that Network debugging is still on. Some TVs reset it after an update.

---

## Turning it off

If you'd rather go straight into the plain list again:

**On TV** — inside a library, open the display preferences and untick **Browse modes**. Per
library, so you can have tiles for Movies and not for TV.

**On web/phone** — set a specific landing view for the library in its settings, and the tile page
is skipped.

Neither uninstalls anything, and neither affects the other.

---

## Common questions

**Do I need a TMDb account or API key?**
Yes, for Trending and Top Rated only. Keys are free from themoviedb.org and go in the plugin's
settings. Jellyfin does bundle a key for its own metadata lookups, but that lives in a part of
the server a plugin isn't allowed to reach, so this needs its own.

**Will Trending show films I don't own?**
No, deliberately. It only ever shows things already in your library, so you never click a poster
and hit a dead end. If you want request-what-you-don't-have, that's what Jellyseerr is for.

**Trending is empty or very short.**
Normal for a small library — it's the overlap between your collection and TMDb's worldwide
ranking. On a 56-film library it found 5 matches. Check you've entered a TMDb key, and note the
lists build on server startup and then refresh every 4 hours, so give it a minute after a
restart.

**Why is there no Most Watched tile?**
It was dropped. Ranking by total plays across all users needed a change inside the Jellyfin
server itself, which would have meant shipping a modified server rather than a plugin. Keeping
your server standard was worth more than one tile.

**Will this survive a Jellyfin update?**
Partly. The **plugin** survives — it's a normal plugin and keeps working. The **web interface**
does not: a Jellyfin update replaces the web folder, so you'd re-copy it afterwards. The **TV
app** is unaffected by server updates but won't auto-update itself.
