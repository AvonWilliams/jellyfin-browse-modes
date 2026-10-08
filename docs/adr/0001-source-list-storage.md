# Store source lists in a plugin-owned SQLite file via EF Core

Multi-source top lists need server-side storage. We decided to store them in a plugin-owned SQLite
file accessed through EF Core (the same stack `Jellyfin.Data` uses), rather than the main
`jellyfin.db`, a config file, or a new database system. SQLite is Jellyfin's default engine, so
this adds no new infrastructure; owning the file keeps the schema isolated from Jellyfin's own
migrations and from other plugins.
