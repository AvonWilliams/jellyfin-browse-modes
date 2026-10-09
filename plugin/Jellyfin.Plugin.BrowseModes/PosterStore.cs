using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Caches poster images on the server so clients load them from Jellyfin rather than the source
/// each time. Posters are fetched lazily — a client request triggers the download — and removed by
/// a scheduled task when they go unserved.
/// </summary>
public sealed class PosterStore
{
    /// <summary>
    /// Proxy spec prefix meaning "download this URL directly".
    /// </summary>
    public const string UrlPrefix = "u:";

    /// <summary>
    /// Proxy spec prefix meaning "look this title up on TMDb first".
    /// </summary>
    public const string SearchPrefix = "s:";

    private const char SearchSeparator = '\u001f';

    private readonly string _posterDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PosterStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PosterStore}"/> interface.</param>
    public PosterStore(
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<PosterStore> logger)
    {
        _posterDirectory = Path.Combine(applicationPaths.DataPath, "browse-modes", "posters");
        Directory.CreateDirectory(_posterDirectory);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Builds the lazy proxy URL for a fetch spec. The spec is either <see cref="UrlPrefix"/> + a
    /// remote URL, or <see cref="SearchPrefix"/> + title + separator + year.
    /// </summary>
    /// <param name="spec">The fetch spec.</param>
    /// <returns>The <c>/Discover/Posters/{key}</c> URL.</returns>
    public static string EncodeProxyUrl(string spec)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(spec);
        var base64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "/Discover/Posters/" + base64;
    }

    /// <summary>
    /// Decodes a proxy key back into its fetch spec.
    /// </summary>
    /// <param name="key">The poster key from the URL.</param>
    /// <param name="spec">The decoded fetch spec.</param>
    /// <returns>Whether the key was a valid spec.</returns>
    public static bool TryDecodeSpec(string key, out string spec)
    {
        spec = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var base64 = key.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };

        try
        {
            spec = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }

        return spec.StartsWith(UrlPrefix, StringComparison.Ordinal)
            || spec.StartsWith(SearchPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns whether a URL is an allowed poster host, guarding the lazy download against SSRF.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <returns>Whether the URL is allowed.</returns>
    public static bool IsAllowedPosterHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("image.tmdb.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("m.media-amazon.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Splits a <see cref="SearchPrefix"/> payload into its title and optional year.
    /// </summary>
    /// <param name="payload">The part after the prefix.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The release year, when present.</param>
    /// <returns>Whether the payload was well formed.</returns>
    public static bool TrySplitSearch(string payload, out string title, out int? year)
    {
        title = string.Empty;
        year = null;

        var separator = payload.IndexOf(SearchSeparator);
        if (separator < 0)
        {
            return false;
        }

        title = payload.Substring(0, separator);
        var yearText = payload.Substring(separator + 1);
        if (int.TryParse(yearText, out var parsed))
        {
            year = parsed;
        }

        return !string.IsNullOrWhiteSpace(title);
    }

    /// <summary>
    /// Downloads a poster on first use and returns its local file path, or null on failure.
    /// </summary>
    /// <param name="remoteUrl">The absolute source URL of the poster.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The absolute local file path, or null.</returns>
    public async Task<string?> GetOrDownloadAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var key = SanitizeKey(Path.GetFileName(new Uri(remoteUrl).AbsolutePath));
        if (key is null)
        {
            return null;
        }

        var filePath = Path.Combine(_posterDirectory, key);
        if (!File.Exists(filePath))
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var bytes = await client.GetByteArrayAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to cache poster {Url}", remoteUrl);
                return null;
            }
        }

        return filePath;
    }

    /// <summary>
    /// Marks a poster file as just served, so the cleanup task keeps it.
    /// </summary>
    /// <param name="filePath">The poster file path.</param>
    public void Touch(string filePath)
    {
        try
        {
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
        }
        catch
        {
            // Best effort; a failure just means the poster may be re-downloaded later.
        }
    }

    /// <summary>
    /// Deletes posters not served within <paramref name="maxAge"/>. Returns the count removed.
    /// </summary>
    /// <param name="maxAge">How long a poster may go unserved before removal.</param>
    /// <returns>The number of files removed.</returns>
    public int Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_posterDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch
            {
                // Best effort; a locked or missing file is simply skipped.
            }
        }

        return removed;
    }

    private static string? SanitizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = Path.GetFileName(key.Trim());
        return string.IsNullOrEmpty(name) || name == "." || name == ".." ? null : name;
    }
}
