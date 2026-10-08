# Browse Modes — Style Guide

Visual rules for the tile grid. Applies to the web client and the Android TV app unless a rule
says otherwise. This is the canonical home for styling rules; the restructure brief (section 7)
points at the same language.

## Tile legibility

Browse-mode tiles render over a variable backdrop (library fanart / background). Some backdrops
make the icon and label hard to read.

**Rule:** every browse-mode tile carries a translucent background (scrim) behind its icon and
label, so the tile stays legible on any backdrop. Use a semi-opaque dark layer — recommended
~60–80% opacity black, or the server `surface` colour at the same alpha — not a fully opaque
block. Apply the scrim to the tile's own background, not the whole grid.

Applies to web and Android TV alike.

## Tile language

- **16:9 tile**, monochrome icon + a single `iconColor` per mode, label under the icon.
- One `iconColor` per category — reuse the palette in `browseModes.ts` / `pickTiles.ts`. Do not
  invent a new colour system.
- The same tile component serves primary, meta, and secondary grids. Primary vs meta differs only
  by sectioning (and an optional slightly larger minimum tile width), never by a second design.
- Source bar (Trending/Top Rated) = compact chips in the header; one accent colour per source;
  clear active state.

## Iconography

Icons are Material UI (`@mui/icons-material`) glyphs — monochrome, one `iconColor` per tile, defined in `browseModes.ts` and `pickTiles.ts`. The icon is a visual metaphor for what the category *is*; the colour reinforces it. No multicolour or photo icons.

### Primary tiles

| Tile | Icon | Colour | Why |
|------|------|--------|-----|
| All | `Apps` | `#B0BEC5` grey | A grid of squares = the whole library laid out. Neutral. |
| Trending | `TrendingUp` | `#5CD672` green | Upward chart = rising popularity; green = growth. |
| Top Rated | `MilitaryTech` | `#EECE55` gold | A medal = rank/achievement. |
| New Releases | `NewReleases` | `#6FB3E0` blue | Film clapper + sparkle = freshly out. |
| Just Added | `FiberNew` | `#4DD0C4` teal | A "new" starburst badge = newly added. |
| Random | `Shuffle` | `#F08A5D` orange | Crossed shuffle arrows; orange = playful. |

### Meta tiles ("Browse by…")

| Tile | Icon | Colour | Why |
|------|------|--------|-----|
| Genres | `Category` | `#C07CD6` purple | **Placeholder.** Generic category glyph — needs specific genre icons (see below). |
| Mood & Tone | `Mood` | `#EC407A` pink | A face = how a film feels. |
| Story | `AutoStories` | `#FF7043` orange | An open book = narrative. |
| World & Style | `Public` | `#5C6BC0` indigo | A globe = worlds/settings. |
| People | `Group` | `#9CCC65` green | A group = cast & crew. |
| Time | `CalendarMonth` | `#7E9CD8` blue | A calendar = decades/eras. |
| Quality | `Reviews` | `#E0533D` red | A review/star = rating-led picks. |
| Studios / Networks | `Business` | `#8D9EC6` / `#5AC8E0` | A building = production company / network. |

### Underlying tiles (reached under a meta tile)

| Tile | Icon | Why |
|------|------|-----|
| Mood | `Mood` | face |
| Story Themes | `AutoStories` | book |
| Plot Elements | `Timeline` | a timeline = plot structure |
| Worlds | `Public` | globe |
| Styles | `Palette` | paint palette = visual style |
| Decades | `CalendarMonth` | calendar |
| Hidden Gems | `Recommend` | a "recommended" sparkle |
| Age Rating | `FamilyRestroom` | a family = audience suitability |
| Watch Again | `History` | clock with arrow = played before |
| Critics' Picks | `Reviews` | review = critics |
| Actors | `Person` | a person |
| Directors | `Movie` | a film = the director's craft |
| Writers | `Edit` | a pencil = writing |

### Meta tiles borrow one child's icon

`Story` (`AutoStories`), `World & Style` (`Public`) and `Quality` (`Reviews`) each reuse the icon
of a *single* child (Story Themes, Worlds, Critics' Picks) even though they open two or more
things. Known simplification — either accept it, or give each meta tile its own glyph when the
set is revisited.

### Colour ramps

Two deliberate gradients in `pickTiles.ts`, not flat colours:

- **Decades** run warm-and-faded for old decades through cool-and-saturated for recent ones
  (`#B08D57` → `#5CD672`), so the grid reads as a timeline. The icon tracks how films of that era
  were watched: film roll → cinema → radio → vinyl → VHS → TV → slideshow → HD → 4K.
- **Age ratings** grade by severity: green (`ChildCare`, everyone) → teal (`FamilyRestroom`) →
  yellow (`Groups`, teen) → orange (`Warning`, mature) → red (`Explicit`, adult) → grey
  (`HelpOutline`, unrated).

### Genres — needs specific icons

`Genres` currently uses the generic `Category` glyph, which says nothing. Give the common genres
their own icon and fall back to `Category` for anything unlisted. **Narrow the scope** — icon
~10–12 common genres, not every genre, so the work stays tractable and the icons stay distinct:

| Genre | Icon | Why |
|-------|------|-----|
| Action | `Bolt` | lightning = speed/impact |
| Adventure | `Explore` | compass = quest |
| Animation | `Animation` | clapper with motion |
| Comedy | `TheaterComedy` | laughing masks |
| Crime | `Gavel` | gavel = justice |
| Documentary | `Public` | factual, real-world |
| Drama | `Masks` | tragedy/comedy masks |
| Family | `FamilyRestroom` | family |
| Fantasy | `AutoAwesome` | magic sparkles |
| Horror | `DarkMode` | crescent moon = dark |
| Mystery | `Search` | magnifier = investigation |
| Romance | `Favorite` | heart |
| Sci-Fi | `RocketLaunch` | rocket = future |
| Thriller | `Warning` | tension |

Verify each MUI icon name exists before wiring it; pick alternates from the same family where one
is missing.

### Source bar — list provider logos

The source chips on Trending and Top Rated use the **list provider's own brand mark**, not a
generic glyph:

| Provider | Icon |
|----------|------|
| TMDb | TMDb logo (monochrome) |
| IMDb | IMDb logo |
| Netflix | red "N" |
| Rotten Tomatoes | tomato mark |
| Letterboxd | letterbox logo |
| Consensus | TBD |

Same rule for both Trending and Top Rated. Store the marks as small SVGs in the repo. These are
trademarked logos — acceptable in a private, self-hosted client, but don't ship a build that
implies provider endorsement.

## Android TV

- Keep the TV's current visual styling for now; do not force-align it to the web tiles.
- Behaviour parity (primary/meta, source bar, missing tiles) is separate from visual alignment.
