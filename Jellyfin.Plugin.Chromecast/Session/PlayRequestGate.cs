using System;

namespace Jellyfin.Plugin.Chromecast.Session;

/// <summary>
/// Lets one play request reach the receiver at a time.
/// <para>
/// The receiver handles each PlayNow independently and awaits its own PlaybackInfo call before
/// loading, so when two arrive close together the one whose stream is ready last wins - not the
/// one requested last (seen in testing: ten quick casts ended on the ninth, a slow-starting
/// transcode). So a newer request waits until the receiver reports the item it was asked to load,
/// reports an error, or a timeout passes - and only the newest waiting request is kept.
/// </para>
/// </summary>
/// <typeparam name="TRequest">The play request type.</typeparam>
internal sealed class PlayRequestGate<TRequest>
    where TRequest : class
{
    private readonly object _lock = new();
    private readonly TimeSpan _timeout;
    private Guid _loadingItemId;
    private DateTime _loadingSince;
    private TRequest? _pending;

    public PlayRequestGate(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>
    /// Asks to send <paramref name="request"/>, whose first item is <paramref name="itemId"/>.
    /// </summary>
    /// <returns>True to send it now; false if it was parked behind a request still loading.</returns>
    public bool TryBegin(Guid itemId, TRequest request, DateTime now)
    {
        lock (_lock)
        {
            if (_loadingItemId != Guid.Empty && now - _loadingSince < _timeout)
            {
                _pending = request;
                return false;
            }

            _loadingItemId = itemId;
            _loadingSince = now;
            return true;
        }
    }

    /// <summary>The request for <paramref name="itemId"/> could not be sent after all.</summary>
    public void Abort(Guid itemId)
    {
        lock (_lock)
        {
            if (_loadingItemId == itemId)
            {
                _loadingItemId = Guid.Empty;
            }
        }
    }

    /// <summary>
    /// Feeds in a receiver status broadcast (or, with an empty type, just the passage of time).
    /// </summary>
    /// <param name="type">The broadcast's type.</param>
    /// <param name="itemId">The item the broadcast is about.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The parked request to send now, if the gate opened and one was waiting.</returns>
    public TRequest? Report(string type, Guid itemId, DateTime now)
    {
        lock (_lock)
        {
            if (_loadingItemId == Guid.Empty)
            {
                return null;
            }

            var stalled = now - _loadingSince >= _timeout;
            var failed = type is "error" or "connectionerror";
            var loaded = itemId == _loadingItemId && type is "playbackstart" or "playbackprogress" or "playstatechange";
            if (!stalled && !failed && !loaded)
            {
                return null;
            }

            _loadingItemId = Guid.Empty;
            var next = _pending;
            _pending = null;
            return next;
        }
    }

    /// <summary>Forgets the request in flight and any parked one (Stop, or the connection is gone).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _loadingItemId = Guid.Empty;
            _pending = null;
        }
    }
}
