# Changelog

Versions cover the project as a whole — plugin, web client and Android TV client are released
together under one number, even when only one of them changed. Each entry says which parts moved.

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
