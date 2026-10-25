using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// One page of people returned by <c>GET /Discover/People</c>.
/// </summary>
public sealed class PersonIndexPageDto
{
    /// <summary>
    /// Gets or sets how many people match, across all pages.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the index of the first person in <see cref="Items"/>.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the people on this page.
    /// </summary>
    public List<PersonIndexItemDto> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets how many people start with each letter (A to Z, then "#"), when requested.
    /// </summary>
    public Dictionary<string, int>? Letters { get; set; }

    /// <summary>
    /// Gets or sets where the page came from: "index" (the stored people index) or "live" (computed
    /// from the library for this request).
    /// </summary>
    public string Source { get; set; } = "index";
}

/// <summary>
/// A person on a People page.
/// </summary>
public sealed class PersonIndexItemDto
{
    /// <summary>
    /// Gets or sets the person's item id, the same id the rest of the API uses.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many movies or series the person appears in.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the cache tag of the person's primary image; null when the person has none.
    /// </summary>
    public string? ImageTag { get; set; }
}
