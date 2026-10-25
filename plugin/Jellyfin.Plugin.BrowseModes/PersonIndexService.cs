using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BrowseModes.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BrowseModes;

/// <summary>
/// Notices library changes and rebuilds the people index in the background a few minutes after the
/// changes stop, so a scan or a metadata refresh costs one rebuild rather than thousands.
/// </summary>
public sealed class PersonIndexService : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMinutes(3);

    private readonly ILibraryManager _libraryManager;
    private readonly PersonIndexStore _store;
    private readonly PersonIndexBuilder _builder;
    private readonly ILogger<PersonIndexService> _logger;
    private readonly object _timerLock = new();
    private Timer? _timer;
    private long _lastChangeTicks;
    private bool _armed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonIndexService"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="store">Instance of <see cref="PersonIndexStore"/>.</param>
    /// <param name="builder">Instance of <see cref="PersonIndexBuilder"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PersonIndexService}"/> interface.</param>
    public PersonIndexService(
        ILibraryManager libraryManager,
        PersonIndexStore store,
        PersonIndexBuilder builder,
        ILogger<PersonIndexService> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _builder = builder;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Only what shows up in a People page: titles, and the people themselves (a downloaded
        // photo changes the image tag). This runs on every item of a scan, so it stays cheap.
        if (e.Item is not (Movie or Series or Season or Episode or Person))
        {
            return;
        }

        _builder.NotifyChange();
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);

        lock (_timerLock)
        {
            if (_armed)
            {
                return;
            }

            _armed = true;
            _timer ??= new Timer(OnTimer);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }

        // Once per quiet period: flag the stored indexes, which keep being served meanwhile.
        _ = Task.Run(() =>
        {
            try
            {
                _store.MarkAllDirty();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to flag the people index as changed");
            }
        });
    }

    private void OnTimer(object? state)
    {
        lock (_timerLock)
        {
            var sinceChange = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks);
            if (sinceChange < Debounce.Ticks)
            {
                // Still changing: wait out the rest of the quiet period.
                _timer?.Change(TimeSpan.FromTicks(Debounce.Ticks - sinceChange), Timeout.InfiniteTimeSpan);
                return;
            }

            _armed = false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _builder.BuildAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background people index rebuild failed");
            }
        });
    }
}
