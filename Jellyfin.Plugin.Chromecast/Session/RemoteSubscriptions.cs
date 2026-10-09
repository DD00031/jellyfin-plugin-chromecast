using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Chromecast.Session;

/// <summary>
/// Works out, from the subscription messages a Jellyfin client sends over its WebSocket, whether
/// it connected to or disconnected from a remote player.
/// <para>
/// jellyfin-web's session player subscribes to session updates while a remote player is active
/// (SessionsStart on connect, SessionsStop on Disconnect). The dashboard's device list uses the
/// very same subscription, but always starts a ScheduledTasksInfo subscription in the same
/// instant, which a cast connect never does. So a session subscription only counts as remote
/// control once a short grace period has passed without a task subscription on that connection.
/// </para>
/// </summary>
/// <typeparam name="TConnection">What identifies a WebSocket connection.</typeparam>
internal sealed class RemoteSubscriptions<TConnection>
    where TConnection : notnull
{
    private readonly ConcurrentDictionary<TConnection, State> _connections = new();

    /// <summary>The connection subscribed to scheduled-task updates (a dashboard page).</summary>
    public void TasksStarted(TConnection connection) => Get(connection).TasksSubscribed = true;

    /// <summary>The connection unsubscribed from scheduled-task updates.</summary>
    public void TasksStopped(TConnection connection)
    {
        if (_connections.TryGetValue(connection, out var state))
        {
            state.TasksSubscribed = false;
        }
    }

    /// <summary>
    /// The connection subscribed to session updates. Call <see cref="ConfirmRemoteControl"/> with
    /// the returned ticket once the grace period has passed.
    /// </summary>
    /// <returns>A ticket identifying this subscription.</returns>
    public int SessionsStarted(TConnection connection) => Interlocked.Increment(ref Get(connection).Generation);

    /// <summary>
    /// Decides, after the grace period, whether a session subscription was a remote-player connect.
    /// </summary>
    /// <returns>True if the client connected to a remote player.</returns>
    public bool ConfirmRemoteControl(TConnection connection, int ticket)
    {
        if (!_connections.TryGetValue(connection, out var state)
            || Volatile.Read(ref state.Generation) != ticket
            || state.TasksSubscribed)
        {
            return false;
        }

        state.RemoteControlling = true;
        return true;
    }

    /// <summary>The connection unsubscribed from session updates.</summary>
    /// <returns>True if that ends a remote-player connection (the user pressed Disconnect).</returns>
    public bool SessionsStopped(TConnection connection)
    {
        if (!_connections.TryGetValue(connection, out var state))
        {
            return false;
        }

        Interlocked.Increment(ref state.Generation);
        if (!state.RemoteControlling)
        {
            return false;
        }

        state.RemoteControlling = false;
        return true;
    }

    /// <summary>
    /// The WebSocket closed. Deliberately not a disconnect: phones close it whenever the app goes
    /// to the background.
    /// </summary>
    public void Closed(TConnection connection) => _connections.TryRemove(connection, out _);

    /// <summary>Whether any connection matching <paramref name="predicate"/> is remote-controlling.</summary>
    /// <returns>True if one is.</returns>
    public bool AnyRemoteControlling(Func<TConnection, bool> predicate)
        => _connections.Any(c => c.Value.RemoteControlling && predicate(c.Key));

    private State Get(TConnection connection) => _connections.GetOrAdd(connection, static _ => new State());

    private sealed class State
    {
        public int Generation;

        public volatile bool TasksSubscribed;

        public volatile bool RemoteControlling;
    }
}
