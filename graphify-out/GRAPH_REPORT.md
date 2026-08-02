# Graph Report - .  (2026-08-02)

## Corpus Check
- Large corpus: 13750 files · ~9,434,616 words. Semantic extraction will be expensive (many Claude tokens). Consider running on a subfolder.

## Summary
- 296 nodes · 507 edges · 24 communities (22 shown, 2 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 5 edges (avg confidence: 0.93)
- Token cost: 62,633 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Reference Server Code|Reference Server Code]]
- [[_COMMUNITY_Discover REST Controller|Discover REST Controller]]
- [[_COMMUNITY_Project Documentation|Project Documentation]]
- [[_COMMUNITY_Original Server Fork Controller|Original Server Fork Controller]]
- [[_COMMUNITY_Scheduled Task Runner|Scheduled Task Runner]]
- [[_COMMUNITY_Future Work & Tile Design|Future Work & Tile Design]]
- [[_COMMUNITY_TMDb API Client|TMDb API Client]]
- [[_COMMUNITY_Build System & Dependencies|Build System & Dependencies]]
- [[_COMMUNITY_Item Sorting Engine|Item Sorting Engine]]
- [[_COMMUNITY_Architecture Decisions|Architecture Decisions]]
- [[_COMMUNITY_TMDb Image Conversion|TMDb Image Conversion]]
- [[_COMMUNITY_Plugin Configuration UI|Plugin Configuration UI]]
- [[_COMMUNITY_Plugin Entry Point|Plugin Entry Point]]
- [[_COMMUNITY_10.11 Build Variant|10.11 Build Variant]]
- [[_COMMUNITY_12.x Build Variant|12.x Build Variant]]
- [[_COMMUNITY_Dependency Injection Setup|Dependency Injection Setup]]
- [[_COMMUNITY_Plugin Configuration Model|Plugin Configuration Model]]
- [[_COMMUNITY_Studio Browser Fragment|Studio Browser Fragment]]
- [[_COMMUNITY_Plugin Repository URL|Plugin Repository URL]]

## God Nodes (most connected - your core abstractions)
1. `TmdbClientManager` - 38 edges
2. `Task` - 22 edges
3. `CancellationToken` - 19 edges
4. `IReadOnlyList` - 17 edges
5. `DiscoverController` - 15 edges
6. `DiscoverController` - 12 edges
7. `TmdbDiscoverClient` - 11 edges
8. `Discover API Endpoints` - 8 edges
9. `IEnumerable` - 7 edges
10. `Tile Inventory` - 7 edges

## Surprising Connections (you probably didn't know these)
- `Jellyfin 10.11.x Stable` --semantically_similar_to--> `Jellyfin 12.0-rc3`  [INFERRED] [semantically similar]
  docs/BACKPORT-10.11.md → README.md
- `Browse Modes` --conceptually_related_to--> `Browse Modes Plugin`  [EXTRACTED]
  README.md → docs/TECHNICAL.md
- `Browse Modes Plugin` --conceptually_related_to--> `Jellyfin 12.0-rc3`  [EXTRACTED]
  docs/TECHNICAL.md → README.md
- `BrowseModesConfigPage` --shares_data_with--> `PluginConfiguration`  [INFERRED]
  plugin/Jellyfin.Plugin.BrowseModes/Configuration/config.html → docs/TECHNICAL.md
- `Discover API Endpoints` --conceptually_related_to--> `TMDb (The Movie Database)`  [EXTRACTED]
  docs/TECHNICAL.md → README.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Plugin Implementation Subsystem** — docs_technical_discovercontroller, docs_technical_tmdbdiscoverclient, docs_technical_refreshdiscoverliststask, docs_technical_pluginserviceregistrator, docs_technical_pluginconfiguration, docs_technical_discover_endpoints [INFERRED 0.95]
- **Multi-Client Tile Abstraction** — docs_technical_browsemode, docs_technical_browsemodedefinition, docs_technical_browsepicker, docs_technical_tile_inventory, docs_technical_preset_persistence, docs_technical_icon_colours [INFERRED 0.95]
- **Plugin Packaging Safety Constraints** — docs_technical_copylocallockfileassemblies, docs_technical_excludeassets_runtime, docs_technical_assemblyloadcontext_issue, progress_ci_assembly_guard [INFERRED 0.95]

## Communities (24 total, 2 thin omitted)

### Community 0 - "Reference Server Code"
Cohesion: 0.10
Nodes (26): Collection, FindContainer, FindExternalSource, IMemoryCache, int, Movie, Person, CancellationToken (+18 more)

### Community 1 - "Discover REST Controller"
Cohesion: 0.17
Nodes (20): ControllerBase, DiscoverController, ActionResult, BaseItem, BaseItemDto, BaseItemKind, CancellationToken, Dictionary (+12 more)

### Community 2 - "Project Documentation"
Cohesion: 0.08
Nodes (28): Plugin 10.11 Backport (v1.0.2.0), In-Memory Provider ID Matching, Jellyfin 10.11.x Stable, AssemblyLoadContext Type Identity Problem, Browse Modes Plugin, Scheduled TMDb Cache Warming, CopyLocalLockFileAssemblies, Discover API Endpoints (+20 more)

### Community 3 - "Original Server Fork Controller"
Cohesion: 0.19
Nodes (19): BaseJellyfinApiController, DiscoverController, ActionResult, BaseItem, BaseItemDto, BaseItemKind, CancellationToken, Dictionary (+11 more)

### Community 4 - "Scheduled Task Runner"
Cohesion: 0.08
Nodes (18): IScheduledTask, RefreshDiscoverListsTask, CancellationToken, IEnumerable, ILogger, IProgress, Task, TaskTriggerInfo (+10 more)

### Community 5 - "Future Work & Tile Design"
Cohesion: 0.14
Nodes (18): Most Watched (Dropped), Pick Tile Icons (Decades/Age Rating), US TV Rating Prefix Stripping Bug, Light Theme Contrast Issue, Additional Tile Candidates, Tile Reordering Task, BrowseMode, BrowseModeDefinition (+10 more)

### Community 6 - "TMDb API Client"
Cohesion: 0.25
Nodes (10): IDisposable, TmdbDiscoverClient, MemoryCache, CancellationToken, Func, IEnumerable, IReadOnlyList, Task (+2 more)

### Community 7 - "Build System & Dependencies"
Cohesion: 0.12
Nodes (11): IDisposableAnalyzers, Microsoft.AspNetCore.Authorization, Microsoft.CodeAnalysis.BannedApiAnalyzers, Microsoft.Extensions.Http, SerilogAnalyzer, SmartAnalyzers.MultithreadingAnalyzer, StyleCop.Analyzers, Swashbuckle.AspNetCore (+3 more)

### Community 8 - "Item Sorting Engine"
Cohesion: 0.25
Nodes (7): BaseItemEntity, Expression, InternalItemsQuery, OrderMapper, ItemSortBy, JellyfinDbContext, Func

### Community 9 - "Architecture Decisions"
Cohesion: 0.27
Nodes (10): Android TV App Fork (jellyfin-androidtv), Landing Page Bypass (Escape Hatch), Client Patch Files, shouldShowBrowseModes Guard, Smart Screen Displacement (TV), TV Launcher Interception (ItemLauncher.java), UI Cannot Be Plugin, Web Client Fork (jellyfin-web) (+2 more)

### Community 10 - "TMDb Image Conversion"
Cohesion: 0.53
Nodes (4): ImageData, ImageType, IEnumerable, RemoteImageInfo

### Community 11 - "Plugin Configuration UI"
Cohesion: 0.31
Nodes (9): CacheDurationHours Configuration Field, DiscoverPagesToScan Configuration Field, ApiClient.getPluginConfiguration, BrowseModesConfigPage, TmdbApiKey Configuration Field, ApiClient.updatePluginConfiguration, PluginConfiguration, TMDb Bundled Key (Unreachable) (+1 more)

### Community 12 - "Plugin Entry Point"
Cohesion: 0.25
Nodes (6): BasePlugin, IHasWebPages, Plugin, IEnumerable, PluginConfiguration, PluginPageInfo

### Community 13 - "10.11 Build Variant"
Cohesion: 0.25
Nodes (7): net9.0, Jellyfin.Controller (10.11.11), Jellyfin.Data (10.11.11), Jellyfin.Extensions (10.11.11), Jellyfin.Model (10.11.11), TMDbLib (3.0.0), Microsoft.NET.Sdk

### Community 14 - "12.x Build Variant"
Cohesion: 0.25
Nodes (7): net10.0, Jellyfin.Controller (12.0.0-rc3), Jellyfin.Data (12.0.0-rc3), Jellyfin.Extensions (12.0.0-rc3), Jellyfin.Model (12.0.0-rc3), TMDbLib (3.0.0), Microsoft.NET.Sdk

### Community 15 - "Dependency Injection Setup"
Cohesion: 0.33
Nodes (4): IPluginServiceRegistrator, IServerApplicationHost, IServiceCollection, PluginServiceRegistrator

### Community 17 - "Studio Browser Fragment"
Cohesion: 0.67
Nodes (3): ByStudioFragment (TV), StudioItemsFragment (TV), Two-Step Studios Flow (TV)

## Knowledge Gaps
- **103 isolated node(s):** `net9.0`, `Jellyfin.Controller (10.11.11)`, `Jellyfin.Model (10.11.11)`, `Jellyfin.Data (10.11.11)`, `Jellyfin.Extensions (10.11.11)` (+98 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **2 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `TmdbClientManager` connect `Reference Server Code` to `TMDb Image Conversion`, `TMDb API Client`?**
  _High betweenness centrality (0.050) - this node is a cross-community bridge._
- **Why does `Discover API Endpoints` connect `Project Documentation` to `Future Work & Tile Design`?**
  _High betweenness centrality (0.021) - this node is a cross-community bridge._
- **What connects `net9.0`, `Jellyfin.Controller (10.11.11)`, `Jellyfin.Model (10.11.11)` to the rest of the system?**
  _107 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Reference Server Code` be split into smaller, more focused modules?**
  _Cohesion score 0.0957372466806429 - nodes in this community are weakly interconnected._
- **Should `Project Documentation` be split into smaller, more focused modules?**
  _Cohesion score 0.08465608465608465 - nodes in this community are weakly interconnected._
- **Should `Scheduled Task Runner` be split into smaller, more focused modules?**
  _Cohesion score 0.08333333333333333 - nodes in this community are weakly interconnected._
- **Should `Future Work & Tile Design` be split into smaller, more focused modules?**
  _Cohesion score 0.13725490196078433 - nodes in this community are weakly interconnected._