using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Caches poster images on the server. Each poster is fetched once — a client request triggers the
/// download — and saved to a file named after its title spec, so later requests (across client
/// refreshes and server restarts) serve the saved file without touching the source again.
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
    private readonly TmdbDiscoverClient _discoverClient;
    private readonly ILogger<PosterStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="discoverClient">Instance of <see cref="TmdbDiscoverClient"/>, used for poster lookups.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PosterStore}"/> interface.</param>
    public PosterStore(
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        TmdbDiscoverClient discoverClient,
        ILogger<PosterStore> logger)
    {
        _posterDirectory = Path.Combine(applicationPaths.DataPath, "browse-modes", "posters");
        Directory.CreateDirectory(_posterDirectory);
        _httpClientFactory = httpClientFactory;
        _discoverClient = discoverClient;
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
        var bytes = Encoding.UTF8.GetBytes(spec);
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
            spec = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
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
    /// Returns the local poster file for a fetch spec, downloading it on first use. The file is
    /// named from the spec itself, so once saved it is served directly on every later request.
    /// </summary>
    /// <param name="spec">The fetch spec.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The absolute local file path, or null on failure.</returns>
    public async Task<string?> GetOrDownloadAsync(string spec, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_posterDirectory, ComputeKey(spec) + ".jpg");
        if (File.Exists(filePath))
        {
            return filePath;
        }

        var remoteUrl = await ResolveRemoteUrlAsync(spec, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(remoteUrl))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var bytes = await client.GetByteArrayAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to cache poster {Spec}", spec);
            return null;
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

    private async Task<string?> ResolveRemoteUrlAsync(string spec, CancellationToken cancellationToken)
    {
        if (spec.StartsWith(UrlPrefix, StringComparison.Ordinal))
        {
            var url = spec.Substring(UrlPrefix.Length);
            return IsAllowedPosterHost(url) ? url : null;
        }

        if (spec.StartsWith(SearchPrefix, StringComparison.Ordinal))
        {
            if (!TrySplitSearch(spec.Substring(SearchPrefix.Length), out var title, out var year))
            {
                return null;
            }

            return await _discoverClient.FindPosterUrlAsync(title, year, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static string ComputeKey(string spec)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(spec));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
