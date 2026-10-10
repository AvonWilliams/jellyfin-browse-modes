# Next session — 2026-08-09

## Studio icon layout — fixed

The Tile component's `<img>` tag was using `position: absolute; inset: 0` which buried the
label text behind the icon. Changed to normal-flow `<img>` with explicit `width`/`height`
matching MUI Icon sizing (`2.5rem` regular, `5rem` large).

**Both 12.x and 10.11 codebases are fixed and built.** 10.11 dist is bind-mounted at
`/config` on `jf-test-10`, so the container just needs a restart after plugin install.

## Installing the plugin on 10.11

Container `jf-test-10` is running on **port 8196**:
- The repository URL was added via API: `https://avonwilliams.github.io/jellyfin-browse-modes/manifest.json`
- The plugin shows up in packages (guid `0d5f6a1e3b7c4c629a4d2e8f1b6c9a37`)
- Has versions for both 10.11 and 12.x target ABIs
- **Needs installation** — easiest way: open `http://localhost:8196` in browser, login,
  go to Dashboard → Plugins → Catalog, find Browse Modes, click Install, restart

## Icon quality — remaining work

The 226 studio SVGs have three quality issues (unrelated to rendering):

1. **14 simple circles** (Sony, Paramount, Netflix, 20th Century, HBO, NBC, CBS, FOX,
   Showtime, STARZ, Crunchyroll, Film4, MUBI, Bento Box) — bare `<circle>` with no
   silhouette. All look like slightly different colored discs.

2. **6 mismatched fill/stroke paths** (dharma productions, hammer film, lux vide,
   dreamworks animation, mk2 films, united artists) — fill and stroke have different
   `d=` values causing ghosting/double-image.

3. **Lionsgate** reads as a house silhouette at 24×24 — the gate/portcullis shape.

See agent reports in the session transcript for full technical detail on each icon.

## Key commands

```bash
# Web 12.x build + deploy
cd jellyfin-web && npm run build:production
docker cp dist/. jellyfin:/jellyfin/jellyfin-web/

# Web 10.11 build (already bind-mounted to dist/)
cd jellyfin-web-10.11
export PATH=$PWD/../.toolchain/node-v24.9.0-linux-x64/bin:$PATH
npm run build:production

# Plugin build (12.x only — needs .NET 9 SDK for 10.11 target)
cd jellyfin-browse-modes/plugin/Jellyfin.Plugin.BrowseModes
dotnet build -c Release
```

## Test containers

| | 10.11 | 12.0 |
|---|---|---|
| Container | `jf-test-10` | `jellyfin` |
| Port | 8196 | host networking (8096) |
| Web dist | bind-mounted | `docker cp` |
| Plugin | Needs install from catalog | Installed (v2.0.1 dev build) |
