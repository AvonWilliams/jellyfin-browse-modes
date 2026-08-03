# Next session — 2026-08-03

## Where to pick up

v2.0.0 is shipped. The main feature work (tag-based discovery) is done. Next priorities:

### 1. Vault (Chunk 3) — multi-level discovery hub
Replace the "Collections" concept with an editorial discovery hub:
- Awards, Seasonal, Franchises, Studios, Staff Picks, etc.
- Multi-level picker: click a category → another grid of sub-categories → items
- Data model: define groupings (could use tags, genre combos, or hardcoded lists)

### 2. Polish (Chunk 5)
- View toggle button could be more prominent (user noted it's not obvious enough)
- Android TV: port the 5 new browse modes
- Icon/color consistency pass

### 3. Toggles (Chunks 1d/1e) — if still wanted
- Per-library toggles: use `userSettings.get('browseModes-' + libraryId)` pattern
- Per-tile toggles: filter enabled modes list
- Plugin config page updates

See [`HANDOFF.md`](../HANDOFF.md) for the full project map, build commands, and test environments.
