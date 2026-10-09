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
/// each time. Posters that go unserved are removed by a scheduled task.
/// </summary>
public sealed class PosterStore
{
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
    /// Downloads a poster on first use and returns the local relative URL, or null on failure.
    /// </summary>
    /// <param name="remoteUrl">The absolute source URL of the poster.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The local <c>/Discover/Posters/{key}</c> URL, or null.</returns>
    public async Task<string?> CacheAsync(string remoteUrl, CancellationToken cancellationToken)
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

        return "/Discover/Posters/" + key;
    }

    /// <summary>
    /// Resolves a poster key to its file path, or null when absent.
    /// </summary>
    /// <param name="key">The poster key from the URL.</param>
    /// <returns>The absolute file path, or null.</returns>
    public string? GetFilePath(string key)
    {
        var safe = SanitizeKey(key);
        if (safe is null)
        {
            return null;
        }

        var path = Path.Combine(_posterDirectory, safe);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Marks a poster as just served, so the cleanup task keeps it.
    /// </summary>
    /// <param name="key">The poster key from the URL.</param>
    public void Touch(string key)
    {
        var path = GetFilePath(key);
        if (path is not null)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // Best effort; a failure just means the poster may be re-downloaded later.
            }
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
