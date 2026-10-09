using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chromecast.Session;

/// <summary>
/// Detects a Jellyfin client connecting to and disconnecting from a remote player. Clients never
/// tell the server which cast target they picked, but jellyfin-web's session player (also what
/// the iOS/macOS apps run) subscribes to session updates over its WebSocket while a remote player
/// is active: SessionsStart on connect, SessionsStop on Disconnect.
/// <para>
/// The dashboard's device list uses the very same subscription, so on its own SessionsStart is
/// ambiguous. Observed against the 12.0 web client: the dashboard always starts a
/// ScheduledTasksInfo subscription in the same instant, a cast connect never does - so a session
/// subscription only counts as remote control if no task subscription is active on that
/// connection or shows up right after it.
/// </para>
/// A closed WebSocket (app backgrounded, phone locked) is deliberately not a disconnect.
/// </summary>
public sealed class RemoteControlListener : IWebSocketListener
{
    private static readonly TimeSpan DashboardGracePeriod = TimeSpan.FromMilliseconds(1500);

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<RemoteControlListener> _logger;
    private static readonly ConcurrentDictionary<Guid, (Guid ItemId, DateTime At)> RecentLocalStops = new();

    private readonly RemoteSubscriptions<IWebSocketConnection> _subscriptions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteControlListener"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public RemoteControlListener(ISessionManager sessionManager, ILogger<RemoteControlListener> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> stopped playing <paramref name="itemId"/> in a Jellyfin
    /// app within the last few seconds - which is what a transfer to a cast target looks like:
    /// the app stops its own player and sends the item on with the position it had reached.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>True if that playback ended moments ago.</returns>
    public static bool WasJustPlayedLocally(Guid userId, Guid itemId)
        => RecentLocalStops.TryGetValue(userId, out var stop) && stop.ItemId == itemId && DateTime.UtcNow - stop.At < TimeSpan.FromSeconds(15);

    private static void OnPlaybackStopped(object? sender, MediaBrowser.Controller.Library.PlaybackStopEventArgs e)
    {
        // Only playback in a Jellyfin app's own player counts, not the receiver's.
        if (e.Item is null || e.Session is null || string.Equals(e.Session.Client, "Chromecast", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var user in e.Users)
        {
            RecentLocalStops[user.Id] = (e.Item.Id, DateTime.UtcNow);
        }
    }

    /// <inheritdoc />
    public Task ProcessWebSocketConnectedAsync(IWebSocketConnection connection, HttpContext httpContext)
    {
        connection.Closed += (_, _) => _subscriptions.Closed(connection);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ProcessMessageAsync(WebSocketMessageInfo message)
    {
        var connection = message.Connection;
        var userId = connection?.AuthorizationInfo?.UserId ?? Guid.Empty;
        if (connection is null || userId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        switch (message.MessageType)
        {
            case SessionMessageType.ScheduledTasksInfoStart:
                _subscriptions.TasksStarted(connection);
                break;

            case SessionMessageType.ScheduledTasksInfoStop:
                _subscriptions.TasksStopped(connection);
                break;

            case SessionMessageType.SessionsStart:
                _ = ConfirmRemoteControlAsync(connection, _subscriptions.SessionsStarted(connection), userId);
                break;

            case SessionMessageType.SessionsStop:
                if (_subscriptions.SessionsStopped(connection))
                {
                    OnRemoteControlEnded(connection, userId);
                }

                break;
        }

        return Task.CompletedTask;
    }

    private async Task ConfirmRemoteControlAsync(IWebSocketConnection connection, int ticket, Guid userId)
    {
        try
        {
            await Task.Delay(DashboardGracePeriod).ConfigureAwait(false);
            if (!_subscriptions.ConfirmRemoteControl(connection, ticket))
            {
                return;
            }

            _logger.LogInformation("{Device} connected to a remote player", connection.AuthorizationInfo.Device);

            var config = Plugin.Instance?.Configuration;
            if (config is not { OpenReceiverOnConnect: true })
            {
                return;
            }

            // Which target was picked is unknown: open the one this user cast to last, or the
            // only one there is. A wrong guess closes itself on the user's first real command.
            var controllers = GetControllers().Where(c => c.IsSessionActive).ToList();
            var lastDeviceId = config.LastCastTargets?.FirstOrDefault(t => t.UserId == userId)?.DeviceId;
            var target = controllers.FirstOrDefault(c => c.DeviceId == lastDeviceId)
                ?? (controllers.Count == 1 ? controllers[0] : null);
            if (target is not null)
            {
                await target.OnRemoteControlStartedAsync(userId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling a remote-player connect");
        }
    }

    private void OnRemoteControlEnded(IWebSocketConnection connection, Guid userId)
    {
        // Another of this user's devices may still be connected to the same Chromecast.
        if (_subscriptions.AnyRemoteControlling(c => c.AuthorizationInfo?.UserId == userId))
        {
            return;
        }

        _logger.LogInformation("{Device} disconnected from its remote player", connection.AuthorizationInfo.Device);
        foreach (var controller in GetControllers())
        {
            _ = controller.OnRemoteControlEndedAsync(userId);
        }
    }

    private IEnumerable<ChromecastSessionController> GetControllers()
        => _sessionManager.Sessions.SelectMany(s => s.SessionControllers).OfType<ChromecastSessionController>();
}
