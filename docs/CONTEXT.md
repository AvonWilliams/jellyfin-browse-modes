# Browse Modes

A Jellyfin plugin and client patches that replace the default alphabetical library view
with a discovery-focused tile grid. Each tile is a different way into the library.

## Language

**Browse mode**:
A way of entering a library, presented as a clickable tile.
_Avoid_: Category, entry, option, filter

**Tile**:
The visual card in the grid. Every browse mode renders as a tile; pickers also render
their choices as tiles.
_Avoid_: Card, button, chip

**Collection type**:
The category of a library — `Movies` or `Tvshows`. Determines which browse modes are offered.
A server can have multiple libraries of the same collection type.
_Avoid_: Library type, media type

**Picker**:
A browse mode that shows a secondary tile grid before listing any items. The user narrows
by a value (e.g. a decade or an age rating), and the chosen value travels in the URL as a
`pick` param.
_Avoid_: Sub-menu, drill-down, filter screen

**View**:
An existing library tab (Genres, Studios, Trending, Top Rated) that a browse mode opens
directly instead of seeding the default view's sort/filter settings.
_Avoid_: Tab, page, section

**Discover**:
Plugin-backed endpoints (`/Discover/{Trending,TopRated}/{Movies,Shows}`) that return
TMDb-ranked item lists matched against the user's library. Requires a TMDb API key.
_Avoid_: API mode, remote list, TMDb mode

**Vault**:
A multi-level discovery hub that replaces the concept of "Collections." Contains editorial
sub-categories (Awards, Seasonal, Franchises, Studios, Staff Picks, etc.), each of which
may open further sub-categories or a filtered item list.
_Avoid_: Collections, hub, showcase

**Mood**:
A browse mode backed by Jellyfin tags, offering emotionally grouped entry points
(Feel Good, Mind Bending, Cozy Night, etc.). A randomized weighted subset is shown
each visit.
_Avoid_: Vibe, tone, atmosphere

**Story Themes**:
A browse mode backed by Jellyfin tags, offering narrative/conceptual entry points
(Time Travel, Heists, Space, etc.). Same UI pattern as Mood but with a larger tag pool.
_Avoid_: Themes, topics, subjects

**Hidden Gems**:
Unwatched items sorted by community rating. Renamed from Best Unseen.
_Avoid_: Best Unseen, undiscovered, underrated

**Watch Again**:
Previously played items sorted by play date. Renamed from Recently Played.
_Avoid_: Recently Played, history, continue watching

## Relationships

- A **browse mode** belongs to one or more **collection types** (Movies, TV Shows, or both).
- A **browse mode** has exactly one navigation target: a **view**, sort/filter settings, or a **picker**.
- A **picker** renders a secondary **tile** grid whose values come from `GET /Items/Filters` (Years, OfficialRatings).
- **Discover** modes (Trending, Top Rated) require the plugin and a configured TMDb API key; every other browse mode works without the plugin.
- **Vault** contains nested sub-categories, each of which may contain further sub-categories or link to a filtered item list.
- **Mood** and **Story Themes** are backed by Jellyfin tags — the browse mode filters items by the selected tag.
- The plugin runs on the server and provides the **Discover** endpoints. The tile grid runs in the web and Android TV clients, which are patched forks of upstream Jellyfin.

## Example dialogue

> **Dev:** "When a user clicks Trending, what happens if the plugin isn't installed or the API key is empty?"
> **Domain expert:** "The Discover endpoint returns an empty list, and the library view shows the empty-state message. The tile still appears — we don't hide it — because the presence of tiles shouldn't flicker based on plugin health. The Android TV client handles this the same way: `DiscoverFragment` catches the failed fetch and shows the empty state."
>
> **Dev:** "What's the difference between a browse mode with a picker and one with a view?"
> **Domain expert:** "A picker mode like Decades shows a secondary tile grid first — pick a decade, then see items. A view mode like Genres opens an existing library tab directly — no intermediate step. They're mutually exclusive: a mode can't have both."
>
> **Dev:** "Mood and Story Themes both use tags. How are they different?"
> **Domain expert:** "Only in which tags they display and how they're weighted. Mood tags are emotional/aesthetic (Feel Good, Dark & Gritty). Story Theme tags are narrative/conceptual (Time Travel, Heists). The UI is the same — a picker grid like Genres — but Mood randomizes a subset on each visit, weighted toward commonly-used tags."

## Flagged ambiguities

- "Category" was used in the v2.0 spec to mean both a **browse mode** tile and a **Vault** sub-category. Resolved: use **browse mode** for top-level tiles and **sub-category** for Vault children.
- "Collections" meant Jellyfin's built-in Collections feature in the old UI, but the spec repurposes it to mean an editorial grouping inside **Vault**. Resolved: the new concept is called **Vault**; Jellyfin's built-in Collections are unrelated.
- "More" was spec'd as a secondary tile page for low-frequency modes. Parked — the current tile count (13/12) fits comfortably without a second page.
