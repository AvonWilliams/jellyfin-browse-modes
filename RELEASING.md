# Releasing for a new Jellyfin version

The runbook for every Jellyfin release. Follow it in order. The point of the order is that
**`compat.json` is edited last**: users' installers (and anyone with auto-update on) act only on
releases listed there, so a version appears for them only once it is tested, published and
checksummed.

## Rules

1. **Version numbers are `<Jellyfin major>.<Jellyfin minor>.<our patch>`.** `12.3.0` is the first
   release tested on Jellyfin 12.3. A fix within the same Jellyfin minor is `12.3.1`, then `12.3.2`.
   The third number is ours, not Jellyfin's. Compatibility is decided by the first two numbers only.
2. **Tags:** plugin `v12.3.0` in this repo, web fork `v12.3.0-web` in `AvonWilliams/jellyfin-web`.
   A plugin tag builds only the ABI of its own major.minor (`v12.3.x` -> `targetAbi 12.3.0.0`).
   Older ABIs stay frozen at their existing `manifest.json` entries.
3. **Never replace or edit the assets of a published tag.** If a release is wrong, ship the next
   patch number. A changed asset under an old tag breaks every checksum already recorded.
4. **Checksums come from CI or from GitHub, never from a local build.** Local builds embed paths and
   timestamps, so their bytes (and checksums) differ from CI's. The plugin MD5 comes from the CI run
   (cross-checked against the released asset); the web SHA-256 comes from the web asset downloaded
   back from GitHub. A `PENDING-CI` / `PENDING` value must never be pushed in a `compat.json` entry
   (the installer refuses it anyway).
5. **An entry is added to `compat.json` only after the release is tested and published.** No entry
   means unsupported, and the installer does nothing.

Placeholders below: `<mm>` is the Jellyfin major.minor being released (for example `12.4`),
`<mm>.0` the first release for it (`12.4.0`), `<new>` / `<old>` the upstream tags you rebase onto
and from (for example `12.4` and `12.3`).

## 0. Detect

The `Jellyfin release watch` workflow runs daily and opens an issue titled
`Jellyfin <ver> released: test & release browse-modes` when a stable 12.x release has no
`compat.json` entry. You can also run it by hand (Actions -> Jellyfin release watch -> Run
workflow). Work through the issue's checklist, which mirrors this document.

Upstream tags the first release of a minor without a patch number (`v12.3`), later ones as
`v12.3.1`.

## 1. Rebase the web fork

Needs **Node >= 24.11** and npm >= 11 (see `docs/TECHNICAL.md` section 8).

```bash
cd jellyfin-web                       # remotes: origin = jellyfin/jellyfin-web, fork = AvonWilliams/jellyfin-web
git fetch origin --tags
git checkout browse-modes
git branch backup/browse-modes-pre-<new>                 # restore point
git rebase --onto v<new> v<old> browse-modes             # e.g. --onto v12.4 v12.3
```

Resolve conflicts using the risk table in `docs/TECHNICAL.md` section 7.4. Then:

```bash
npm ci
npx tsc --noEmit
npx eslint <changed files>
npm run build:production              # -> dist/
```

Do not push the branch yet.

## 2. Bump the plugin to match the server

In `plugin/Jellyfin.Plugin.BrowseModes/Jellyfin.Plugin.BrowseModes.csproj`:

- `JellyfinVersion` (CI overrides it with `-p:JellyfinVersion=<mm>.0`; keep the default sensible).
  Check the `Jellyfin.*` packages exist on nuget.org at `<mm>.0` first.
- The compile-time `Microsoft.EntityFrameworkCore.Sqlite` version must equal the server's (look it up
  in the server's `Directory.Packages.props` at the matching upstream tag).
- `AssemblyVersion` / `FileVersion` -> `<mm>.0.0` (CI overrides them from the tag; keep them in step).

In `.github/workflows/plugin.yml` add the new major.minor to `SUPPORTED_JELLYFIN` (space separated,
newest last). Without it, CI refuses the tag.

Add a `manifest.json` entry at the top of `versions`, with `"checksum": "PENDING-CI"` and
`"timestamp": "PENDING-CI"` (filled in step 4):

```json
{
    "version": "12.4.0",
    "changelog": "Jellyfin 12.4 support.",
    "targetAbi": "12.4.0.0",
    "sourceUrl": "https://github.com/AvonWilliams/jellyfin-browse-modes/releases/download/v12.4.0/browse-modes_12.4.0_12.4.0.zip",
    "checksum": "PENDING-CI",
    "timestamp": "PENDING-CI"
}
```

Build check (still local, only to catch compile errors):

```bash
cd plugin/Jellyfin.Plugin.BrowseModes
dotnet build -c Release -p:JellyfinVersion=<mm>.0
ls bin/Release/net10.0/*.dll          # exactly the three DLLs (docs/TECHNICAL.md section 2.3),
                                      # no Jellyfin.* / MediaBrowser.* assemblies
```

Add a `CHANGELOG.md` entry. Commit locally; do not push yet.

## 3. Test locally against the stock Jellyfin image

Use a throwaway container and a **copy** of a config directory, never the production one.

```bash
df -h                                                    # the image and config copy take space
docker pull jellyfin/jellyfin:<mm>                       # e.g. 12.4
mkdir -p ~/jf-test/config ~/jf-test/cache
# optional: realistic data -> cp -a <a copy of your config>/. ~/jf-test/config/

# Plugin: staging dir named like the installer does, meta.json from the template.
PDIR=~/jf-test/config/plugins/"Browse Modes_<mm>.0"
mkdir -p "$PDIR"
cp plugin/Jellyfin.Plugin.BrowseModes/bin/Release/net10.0/*.dll "$PDIR"/
sed 's/__VERSION__/<mm>.0/; s/__TARGET_ABI__/<mm>.0.0/; s/__TIMESTAMP__/2000-01-01T00:00:00Z/' \
  plugin/meta.template.json > "$PDIR"/meta.json

# Web bundle bind-mounted over the web root (read-only), so recreating the container keeps it.
docker rm -f jellyfin-test 2>/dev/null
docker run -d --name jellyfin-test -p 8098:8096 \
  -v ~/jf-test/config:/config -v ~/jf-test/cache:/cache -v <media>:/media:ro \
  -v <path to jellyfin-web>/dist:/jellyfin/jellyfin-web:ro \
  jellyfin/jellyfin:<mm>
```

Checklist (all must pass):

```bash
# 1. Server is the intended version
curl -s localhost:8098/System/Info/Public               # "Version":"<mm>.x"

# 2. Plugin loaded, no errors from it
docker logs jellyfin-test 2>&1 | grep -E "Loaded plugin: Browse Modes"
docker logs jellyfin-test 2>&1 | grep -iE "browse ?modes" | grep -iE "error|exception|fail" && echo "FOUND ERRORS"

# 3. Authenticate and call the endpoints.
#    NOTE: on 12.3 the X-Emby-Token header returns 401. Use the Authorization header.
AUTH='MediaBrowser Client="smoke", Device="cli", DeviceId="smoke", Version="1"'
TOKEN=$(curl -s -X POST localhost:8098/Users/AuthenticateByName \
  -H "Authorization: $AUTH" -H 'Content-Type: application/json' \
  -d '{"Username":"<user>","Pw":"<password>"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')
H="Authorization: MediaBrowser Token=\"$TOKEN\""
LIB=<a movie library id>
for p in Trending/Movies Trending/Shows TopRated/Movies TopRated/Shows; do
  curl -s -o /dev/null -w "$p %{http_code}\n" -H "$H" "localhost:8098/Discover/$p?parentId=$LIB&limit=10"
done                                                     # each 200
curl -s -o /dev/null -w "Counts %{http_code}\n" -H "$H" "localhost:8098/Discover/Counts?type=genre&parentId=$LIB"
curl -s -o /dev/null -w "TileLayout %{http_code}\n" -H "$H" "localhost:8098/Discover/TileLayout"
```

Trending / Top Rated return real items only with a TMDb API key configured in the plugin settings.
Without one, a 200 with an empty list is acceptable; a 500 is not.

4. In a browser, open `http://localhost:8098/`, hard-refresh (Ctrl+Shift+R), and check: the tile
   grid appears in front of a Movies and a TV library, **All** opens the ordinary list, a tag
   shelf (Mood or Story Themes) loads, Decades and Age Rating pickers show counts, and the plugin
   settings page (Dashboard -> Plugins -> Browse Modes) opens and saves.
5. Dashboard -> Scheduled Tasks shows the Browse Modes tasks and one of them runs to completion.

Fix anything that fails and repeat from step 1 or 2. Then remove the test container:
`docker rm -f jellyfin-test`.

## 4. Release the plugin

```bash
git push origin main                                     # CI runs a build on main (version 0.0.0.N)
git tag v<mm>.0 && git push origin v<mm>.0             # tag build: browse-modes_<mm>.0_<mm>.0.zip
gh run watch -R AvonWilliams/jellyfin-browse-modes       # wait for green
```

Read the MD5 and the timestamp from CI, never from a local build:

```bash
gh run view <run id> -R AvonWilliams/jellyfin-browse-modes --log | grep "md5 browse-modes_"
                                                         # also in the run's step summary
# cross-check against the asset actually published on the release:
mkdir -p /tmp/bm-asset && gh release download v<mm>.0 -R AvonWilliams/jellyfin-browse-modes -D /tmp/bm-asset
md5sum /tmp/bm-asset/*.zip                               # must equal the CI value
unzip -p /tmp/bm-asset/*.zip meta.json | python3 -c 'import json,sys;print(json.load(sys.stdin)["timestamp"])'
```

Replace `PENDING-CI` in the `manifest.json` entry with the MD5 and that timestamp, then commit and
push. (This publishes the plugin in the Jellyfin catalog; the installer still ignores it until
step 6.)

## 5. Release the web bundle

The web fork has no CI. Build from the rebased branch, tag it, upload, then read the checksum back
from GitHub.

```bash
cd jellyfin-web
npm ci && npm run build:production
(cd dist && zip -r ../jellyfin-web-browse-modes-<mm>.0.zip .)     # files at the zip's top level, no dist/ wrapper
git push --force-with-lease fork browse-modes                       # the rebase rewrote history
git tag v<mm>.0-web && git push fork v<mm>.0-web
gh release create v<mm>.0-web -R AvonWilliams/jellyfin-web \
  --title "Browse Modes web <mm>.0 (<mm>)" --notes "Jellyfin <mm>" jellyfin-web-browse-modes-<mm>.0.zip

# Download the asset back and hash what GitHub serves:
mkdir -p /tmp/bm-web && gh release download v<mm>.0-web -R AvonWilliams/jellyfin-web \
  -p 'jellyfin-web-browse-modes-*.zip' -D /tmp/bm-web
sha256sum /tmp/bm-web/*.zip
gh api repos/AvonWilliams/jellyfin-web/releases/tags/v<mm>.0-web --jq '.assets[0].digest'   # must agree
```

## 6. Publish: add the `compat.json` entry

Only now. Add the entry (keep older entries; the installer picks by the running Jellyfin version):

```json
"12.4": {
    "plugin": "12.4.0",
    "web": "12.4.0",
    "webSha256": "<sha256 from step 5>"
}
```

`plugin` and `web` are independent version numbers (for example plugin `12.4.1`, web `12.4.0`);
each names the tag `v<plugin>` / `v<web>-web`. The installer builds the web URL as
`https://github.com/AvonWilliams/jellyfin-web/releases/download/v<web>-web/jellyfin-web-browse-modes-<web>.zip`
and finds the plugin in `manifest.json` by version and `targetAbi <key>.0.0`.

Verify before pushing. The dry run downloads the real GitHub assets, checks them against the
new entry, and changes nothing (it needs a running server of version `<mm>`, such as the test
container from step 3, still up):

```bash
python3 -m json.tool compat.json >/dev/null && python3 -m json.tool manifest.json >/dev/null
tests/test-installer.sh
BM_RAW_BASE=file://$PWD BM_ALLOW_LOCAL_URLS=1 ./install-browse-modes.sh --dry-run --host localhost --port 8098   # the <mm> test container
```

Commit and `git push origin main`. Within about five minutes (raw.githubusercontent.com cache)
installers and auto-update start offering the release to Jellyfin <mm> servers.

## 7. Deploy to your own server, then announce

```bash
sudo ./install-browse-modes.sh --status
sudo ./install-browse-modes.sh            # or let auto-update do it
```

Hard-refresh the browser, check the tiles, then announce (README compatibility line and release
notes should already be current).

Close the tracking issue.

## Fix releases

Found a bug in a published release? **Do not touch the old tag.** Bump the patch number:
`12.3.1`, tag `v12.3.1` (and `v12.3.1-web` if the web bundle changed), and repeat steps 3 to 7
(only the parts that changed). Update the `compat.json` entry to the new numbers at step 6.

The installer never downgrades, so servers that already took the bad release keep it until the fix
is published. That is why fixing forward is the only reliable route.

## Rollback and kill switch

- **Stop new installs of a release:** remove its `compat.json` entry (or, for a fix release, point
  it at the fixed numbers) and push to `main`. Installers and auto-update then do nothing for that
  Jellyfin version. Servers that already updated are not changed.
- **A server that took a bad release:** the updater keeps its last three backups under
  `/var/lib/browse-modes-updater/backups/<timestamp>/` (`web/` and `plugin/`). To restore by hand:
  stop Jellyfin, replace `/usr/share/jellyfin/web` with the backup's `web/` and the
  `Browse Modes_*` directory in `/var/lib/jellyfin/plugins` with the backup's `plugin/` contents,
  start Jellyfin, and turn auto-update off until the fix is out
  (`sudo bash /usr/local/lib/browse-modes/updater.sh --disable-auto-update`).
  If the server fails to come back right after an update, the updater rolls back by itself.
- **Plugin catalog (Dashboard -> Catalog):** remove the `manifest.json` entry for the version and
  push.
