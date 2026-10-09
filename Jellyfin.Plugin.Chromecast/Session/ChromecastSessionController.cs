using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Chromecast.Cast;
using Jellyfin.Plugin.Chromecast.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;

namespace Jellyfin.Plugin.Chromecast.Session;

/// <summary>
/// Bridges a single Chromecast device into Jellyfin's session/remote-control system. Rather than
/// re-implementing stream URL building, codec negotiation and HLS playback (the likely source of
/// reliability problems in the previous jellycast prototype - see CLAUDE.md), this launches
/// Jellyfin's own cast receiver app - the same one Chrome users already cast to today - and hands
/// off via the exact "connectsdk" protocol jellyfin-web's Chrome sender already speaks. The
/// receiver then authenticates itself with a token minted for the casting user, negotiates its
/// own transcode/direct-play decision (correctly excluding HEVC on hardware that can't decode it),
/// and reports playback progress straight back to the Jellyfin server on its own. This controller
/// only needs to relay commands in, and mirror status back out for the "Play On" UI.
/// </summary>
public sealed class ChromecastSessionController : ISessionController, IAsyncDisposable
{
    /// <summary>The "stable" build of Jellyfin's own cast receiver.</summary>
    private const string StableReceiverAppId = "F007D354";

    /// <summary>The "unstable" build of Jellyfin's own cast receiver.</summary>
    private const string UnstableReceiverAppId = "6F511C87";

    /// <summary>
    /// Matches Jellyfin API JSON casing (PascalCase) for the inner status payload. Two converters
    /// were needed to stop every single incoming status broadcast from failing to deserialize into
    /// <see cref="PlaybackProgressInfo"/> (confirmed by testing: "Could not parse playback status"
    /// logged for every progress update, silently preventing this session's own
    /// OnPlaybackStart/Progress from ever firing - which is also why the "Play On" remote control
    /// UI never showed the now-playing title/artwork for this session, even while the receiver's
    /// own separate, direct API reporting kept working fine):
    /// <list type="bullet">
    /// <item><see cref="JsonStringEnumConverter"/> - the receiver writes enums as strings (e.g.
    /// <c>"PlayMethod":"Transcode"</c>), which the default numeric enum handling rejects.</item>
    /// <item><see cref="FlexibleGuidConverter"/> - the receiver's own internal reporting writes
    /// item ids in Jellyfin's dashless "N" format (e.g. <c>"b565cf7176b943ceb165fece9b26181f"</c>),
    /// which <c>System.Text.Json</c>'s built-in <c>Guid</c> converter rejects outright - it only
    /// accepts the canonical dashed format, unlike the more permissive <c>Guid.Parse(string)</c>.
    /// </item>
    /// </list>
    /// </summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(), new FlexibleGuidConverter() }
    };

    private readonly SessionInfo _session;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly IServerApplicationHost _appHost;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChromecastSessionController> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private ChromecastReceiver _receiver;
    private readonly string _serverAddress;
    private ChromecastClient? _client;
    private ConnectSdkChannel? _connectSdkChannel;

    /// <summary>
    /// The running receiver app's CastV2 transport id. Messages for the app (as opposed to the
    /// receiver platform itself) must be addressed to this, not the default "receiver-0"
    /// destination - see <see cref="ConnectSdkChannel.SendCommandAsync"/>.
    /// </summary>
    private string? _appTransportId;

    /// <summary>
    /// Periodically re-sends CONNECT to <see cref="_appTransportId"/> while a cast is active.
    /// CastV2 heartbeat (ping/pong) keeps the "receiver-0" platform connection alive, but the
    /// separate virtual connection to the *app's own* transport id apparently has its own,
    /// shorter idle timeout - observed emptying the receiver's app-transport connection well
    /// under a minute after the initial cast, with the device then sending CLOSE (which
    /// SharpCaster's ConnectionChannel obediently reacts to by tearing down the whole client).
    /// Re-issuing CONNECT is a lightweight, idempotent way to signal continued interest in that
    /// connection - actual PlaybackInfo/progress traffic to the app happens independently on the
    /// receiver's own HTTP connection to Jellyfin, unaffected either way.
    /// </summary>
    private Timer? _appKeepAliveTimer;

    private string? _mintedAccessToken;
    private Guid _mintedTokenUserId;

    /// <summary>Bumped by every cast and Stop, so a delayed receiver close can tell it's been superseded.</summary>
    private int _castGeneration;

    /// <summary>
    /// Fallback for when a disconnect goes unnoticed (app killed, or a client that doesn't send
    /// SessionsStop) - the receiver disables CAF's own idle timeout.
    /// </summary>
    private static readonly object LastTargetLock = new();

    private static readonly TimeSpan IdleCloseAfter = TimeSpan.FromMinutes(30);

    private Guid _lastControllingUserId;

    private int _missedKeepAlives;

    /// <summary>How long mirrored item details stay on the TV before it returns to the ready screen.</summary>
    private static readonly TimeSpan DetailsScreenTime = TimeSpan.FromSeconds(30);

    private volatile bool _showingDetails;

    private DateTime _detailsShownAt;

    /// <summary>
    /// Set when playback was transferred here from a player in a Jellyfin app. jellyfin-web keeps
    /// its player page open as the remote control after a transfer and sends Stop when that page
    /// is closed (video/index.js onViewHideStopPlayback) - indistinguishable from a deliberate
    /// Stop, so the first Stop after a transfer is taken to be that and ignored.
    /// </summary>
    private volatile bool _ignoreNextStop;

    /// <summary>Lets one PlayNow reach the receiver at a time; see <see cref="PlayRequestGate{TRequest}"/>.</summary>
    private readonly PlayRequestGate<PlayRequest> _playGate = new(TimeSpan.FromSeconds(15));

    private DateTime _lastNotification;

    /// <summary>The user this receiver was opened for on a connect guess, until a real command confirms it.</summary>
    private Guid _openedOnConnectFor;

    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Whether the receiver has an item loaded (playing or paused). Only cleared on positive
    /// evidence from the receiver's own broadcasts, so an unexpected broadcast shape can never
    /// get a playing movie closed as "idle".
    /// </summary>
    private volatile bool _receiverHasItem = true;

    private bool _disposed;
    private volatile bool _stale;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromecastSessionController"/> class.
    /// </summary>
    public ChromecastSessionController(
        SessionInfo session,
        ChromecastReceiver receiver,
        string serverAddress,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        IServerApplicationHost appHost,
        ILoggerFactory loggerFactory)
    {
        _session = session;
        _receiver = receiver;
        _serverAddress = serverAddress;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _appHost = appHost;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChromecastSessionController>();
    }

    /// <inheritdoc />
    public bool IsSessionActive => !_disposed && !_stale && !IsHidden;

    /// <inheritdoc />
    public bool SupportsMediaControl => IsSessionActive;

    /// <summary>
    /// Refreshes the known network location for this device after it responds to a fresh
    /// discovery probe (its IP address may have changed via DHCP).
    /// </summary>
    public void UpdateReceiver(ChromecastReceiver receiver)
    {
        _receiver = receiver;
        _stale = false;
    }

    /// <summary>
    /// Marks this session inactive because the device has not answered discovery recently.
    /// It will reactivate automatically if it is seen again.
    /// </summary>
    public void MarkStale() => _stale = true;

    /// <inheritdoc />
    public async Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        MarkActivity();

        var controllingUserId = data switch
        {
            PlayRequest play => play.ControllingUserId,
            GeneralCommand general => general.ControllingUserId,
            PlaystateRequest playstate when Guid.TryParse(playstate.ControllingUserId, out var id) => id,
            _ => Guid.Empty
        };
        if (controllingUserId != Guid.Empty)
        {
            _lastControllingUserId = controllingUserId;
            _openedOnConnectFor = Guid.Empty;
            RememberAsLastTarget(controllingUserId);

            // This is the Chromecast the user actually picked - close any other one that was
            // opened on the connect guess (see OnRemoteControlStartedAsync).
            foreach (var other in _sessionManager.Sessions.SelectMany(s => s.SessionControllers).OfType<ChromecastSessionController>())
            {
                if (!ReferenceEquals(other, this))
                {
                    _ = other.CloseIfOpenedOnConnectForAsync(controllingUserId);
                }
            }
        }

        try
        {
            var task = name switch
            {
                SessionMessageType.Play => SendPlayCommand(data as PlayRequest, cancellationToken),
                SessionMessageType.Playstate => SendPlaystateCommand(data as PlaystateRequest, cancellationToken),
                SessionMessageType.GeneralCommand => SendGeneralCommand(data as GeneralCommand, cancellationToken),
                _ => Task.CompletedTask
            };
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders on top of the per-command try/catches below: Jellyfin's
            // SessionManager does not appear to await/observe the task this method returns, so
            // anything thrown before reaching one of those inner try blocks (e.g. while resolving
            // items from the library, before SendPlayCommandCoreAsync is ever called) would
            // otherwise vanish with no log at all.
            _logger.LogError(ex, "Error handling {MessageType} command for Chromecast {Name}", name, _receiver.Name);
        }
    }

    private async Task SendPlayCommand(PlayRequest? command, CancellationToken cancellationToken)
    {
        if (command?.ItemIds is null || command.ItemIds.Length == 0)
        {
            return;
        }

        var user = command.ControllingUserId == Guid.Empty ? null : _userManager.GetUserById(command.ControllingUserId);
        var dtoOptions = new DtoOptions(true);

        var items = command.ItemIds
            .Select(id => _libraryManager.GetItemById(id))
            .Where(item => item is not null)
            .Select(item => _dtoService.GetBaseItemDto(item!, dtoOptions, user))
            .ToList();

        if (items.Count == 0)
        {
            _logger.LogWarning("Chromecast play requested but none of the requested items could be resolved");
            return;
        }

        try
        {
            await SendPlayCommandCoreAsync(command, items, user, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Jellyfin's SessionManager does not appear to await/observe the task this method
            // returns (commands are broadcast to every session controller without necessarily
            // waiting on each one) - an exception left to propagate out of here would vanish
            // silently instead of ever reaching a log, which is exactly what made an earlier bug
            // in this method look like a silent hang instead of the fault it actually was.
            _logger.LogError(ex, "Error casting to Chromecast {Name}", _receiver.Name);
            NotifyUser(command.ControllingUserId, $"Casting to {DisplayName} failed. Try again.");

            // A failure here (e.g. a write to a dead SslStream because the device closed the
            // connection between casts without us noticing) means the cached client can no
            // longer be trusted - without this, every subsequent cast attempt would keep
            // failing the same way against the same broken connection. The next attempt will
            // reconnect from scratch via EnsureConnectedAsync.
            InvalidateConnection();
        }
    }

    /// <summary>
    /// Drops the cached CastV2 connection so the next cast attempt reconnects from scratch,
    /// without trying to gracefully close a connection that may already be dead.
    /// </summary>
    private void InvalidateConnection()
    {
        _client = null;
        _connectSdkChannel = null;
        _appTransportId = null;
        StopAppKeepAlive();
        ForgetPlayRequests();
        _showingDetails = false;
        _ignoreNextStop = false;
        Interlocked.Exchange(ref _missedKeepAlives, 0);
    }

    /// <summary>
    /// Shows a short message in the Jellyfin app of the user who is casting, so a failed cast
    /// doesn't just look like nothing happened. The command doesn't say which of the user's
    /// devices sent it, so this goes to the one that was active most recently.
    /// </summary>
    private void NotifyUser(Guid userId, string text)
    {
        var now = DateTime.UtcNow;
        if (userId == Guid.Empty || now - _lastNotification < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastNotification = now;

        try
        {
            var target = _sessionManager.Sessions
                .Where(s => s.UserId.Equals(userId) && s.IsActive && !s.SessionControllers.OfType<ChromecastSessionController>().Any())
                .OrderByDescending(s => s.LastActivityDate)
                .FirstOrDefault();
            if (target is null)
            {
                return;
            }

            _ = _sessionManager
                .SendMessageCommand(_session.Id, target.Id, new MessageCommand { Header = "Chromecast", Text = text, TimeoutMs = 8000 }, CancellationToken.None)
                .ContinueWith(t => _logger.LogDebug(t.Exception, "Could not show a message in the casting app"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not show a message in the casting app");
        }
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private void StopAppKeepAlive()
    {
        _appKeepAliveTimer?.Dispose();
        _appKeepAliveTimer = null;
    }

    private void StartAppKeepAlive(ChromecastClient client, string transportId)
    {
        StopAppKeepAlive();
        var interval = TimeSpan.FromSeconds(5);
        _appKeepAliveTimer = new Timer(_ => AppKeepAliveTick(client, transportId), null, interval, interval);
    }

    private static async Task PingAsync(ChromecastClient client, string transportId)
    {
        await client.ConnectionChannel.ConnectAsync(transportId).ConfigureAwait(false);
        await client.ReceiverChannel.GetChromecastStatusAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets a connection that turned out to be dead (device unplugged or rebooted), so the
    /// next command reconnects and relaunches instead of talking to a socket nobody reads.
    /// </summary>
    private void HandleConnectionLost(ChromecastClient client, string reason)
    {
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        _logger.LogInformation("Lost the connection to Chromecast {Name}: {Reason}", _receiver.Name, reason);
        client.Disconnected -= OnClientDisconnected;
        InvalidateConnection();
        _ = RevokeAccessTokenAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing the dead connection to {Name}", _receiver.Name);
            }
        });
    }

    /// <summary>
    /// Checks that a cached connection still answers before it is relied on. A send into a dead
    /// TCP connection does not fail - it just vanishes - so without this the first command after
    /// the Chromecast was restarted was silently lost.
    /// </summary>
    private async Task EnsureAliveAsync()
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        try
        {
            var status = client.ReceiverChannel.GetChromecastStatusAsync();
            if (await Task.WhenAny(status, Task.Delay(TimeSpan.FromSeconds(2.5))).ConfigureAwait(false) == status)
            {
                // The connection is fine, but the receiver may have been closed behind our back
                // (another app was cast to the device, or it restarted and we reconnected).
                var current = await status.ConfigureAwait(false);
                if (_appTransportId is { } transportId && current?.Applications?.Any(a => a.TransportId == transportId) != true)
                {
                    _logger.LogInformation("The Jellyfin receiver on {Name} is no longer running", _receiver.Name);
                    _appTransportId = null;
                    StopAppKeepAlive();
                    ForgetPlayRequests();
                    _showingDetails = false;
                    _ignoreNextStop = false;
                }

                return;
            }

            HandleConnectionLost(client, "no answer to a status request");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status request to {Name} failed", _receiver.Name);
            HandleConnectionLost(client, "a status request failed");
        }
    }

    /// <summary>
    /// Tries a direct connection to the Chromecast's cast port.
    /// </summary>
    /// <returns>Whether the device answered within two seconds.</returns>
    public async Task<bool> ProbeAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks this Chromecast as gone (it answers neither discovery nor a direct connection).
    /// </summary>
    public void OnUnreachable()
    {
        MarkStale();
        if (_client is { } client)
        {
            HandleConnectionLost(client, "the device is unreachable");
        }
    }

    private async void AppKeepAliveTick(ChromecastClient client, string transportId)
    {
        try
        {
            // Re-issuing CONNECT to the app's own transport alone was NOT enough to prevent the
            // device disconnecting ~28-30s after a cast (confirmed by testing) - that's a
            // non-standard use of the connection namespace the receiver *platform* likely doesn't
            // treat as "sender is still here" for its own liveness accounting. A standard
            // RECEIVER_STATUS request on "receiver-0", which is what a normal Chrome/Android
            // sender periodically does anyway, is the traffic pattern the platform actually
            // watches for.
            // SharpCaster requests have no timeout: against a device that was unplugged or
            // rebooted they simply never complete. Two silent ticks in a row means it is gone.
            var ping = PingAsync(client, transportId);
            if (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false) != ping)
            {
                if (Interlocked.Increment(ref _missedKeepAlives) >= 2)
                {
                    HandleConnectionLost(client, "it stopped answering");
                }

                return;
            }

            await ping.ConfigureAwait(false);
            Interlocked.Exchange(ref _missedKeepAlives, 0);

            // Lets a waiting play request through if the receiver never reported the previous one.
            OnReceiverReported(string.Empty, Guid.Empty);

            // Clients don't say when an item page is left, so the mirrored details would stay
            // on the TV indefinitely; go back to the ready screen after a while instead.
            if (_showingDetails && !_receiverHasItem && DateTime.UtcNow - _detailsShownAt > DetailsScreenTime)
            {
                _showingDetails = false;
                await SendReceiverCommandAsync("Identify", null).ConfigureAwait(false);
            }

            // Clients send nothing when they disconnect from a cast target, and this keep-alive
            // would otherwise hold an idle Jellyfin screen on the TV forever.
            var idleFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            if (!_receiverHasItem && idleFor > IdleCloseAfter)
            {
                _logger.LogInformation("Jellyfin receiver on {Name} has been idle for {IdleFor}, closing it", _receiver.Name, idleFor);
                MarkActivity();
                await CloseReceiverAppAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The connection is probably already dead at this point; the next real command will
            // discover that (via InvalidateConnection) and reconnect. Nothing further to do here.
            _logger.LogDebug(ex, "Chromecast {Name} app keep-alive failed", _receiver.Name);
        }
    }

    private async Task SendPlayCommandCoreAsync(PlayRequest command, List<BaseItemDto> items, User? user, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _castGeneration);

        // PlayNext/PlayLast mean "add to the queue of whatever is already casting" - they must
        // never go through the launch-a-fresh-app path below. Sending them there anyway (the
        // original bug: this method always launched fresh and sent "PlayNow" regardless of what
        // PlayCommand actually asked for) restarts the receiver app mid-playback, which is what
        // "adding to the queue crashed the whole stream" turned out to mean - confirmed by testing.
        if (command.PlayCommand is PlayCommand.PlayNext or PlayCommand.PlayLast)
        {
            await SendQueueCommandAsync(command, items).ConfigureAwait(false);
            return;
        }

        var firstItemId = items[GetStartIndex(command, items.Count)].Id;
        if (!_playGate.TryBegin(firstItemId, command, DateTime.UtcNow))
        {
            _logger.LogDebug("{Name} is still loading the previous request; {ItemName} will follow", _receiver.Name, items[0].Name);
            return;
        }

        var sent = false;
        try
        {
            sent = await SendPlayNowAsync(command, items, user, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!sent)
            {
                _playGate.Abort(firstItemId);
            }
        }
    }

    /// <summary>
    /// Called for every receiver status broadcast: once the receiver reports the item it was
    /// asked to load (or an error), the next waiting play request may go out.
    /// </summary>
    private void OnReceiverReported(string type, Guid itemId)
    {
        if (_playGate.Report(type, itemId, DateTime.UtcNow) is { } next)
        {
            _ = SendPlayCommand(next, CancellationToken.None);
        }
    }

    /// <summary>
    /// The item in a play request's list to start with: "play from here" sends the whole list
    /// plus an index.
    /// </summary>
    internal static int GetStartIndex(PlayRequest command, int itemCount)
        => command.StartIndex is { } index && index > 0 && index < itemCount ? index : 0;

    /// <summary>
    /// Whether this cast continues something the same user was just watching in a Jellyfin app's
    /// own player (the "cast" button inside the player), as opposed to starting it from a page.
    /// </summary>
    private bool IsTransferFromLocalPlayer(PlayRequest command, List<BaseItemDto> items)
    {
        if (command.ControllingUserId == Guid.Empty || (command.StartPositionTicks ?? 0) <= 0)
        {
            return false;
        }

        var itemId = items[GetStartIndex(command, items.Count)].Id;
        var playingLocally = _sessionManager.Sessions.Any(s =>
            s.UserId.Equals(command.ControllingUserId)
            && s.NowPlayingItem?.Id == itemId
            && !string.Equals(s.Client, "Chromecast", StringComparison.Ordinal));
        return playingLocally || RemoteControlListener.WasJustPlayedLocally(command.ControllingUserId, itemId);
    }

    private void ForgetPlayRequests() => _playGate.Clear();

    private async Task<bool> SendPlayNowAsync(PlayRequest command, List<BaseItemDto> items, User? user, CancellationToken cancellationToken)
    {
        await EnsureAliveAsync().ConfigureAwait(false);
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (client is null || _connectSdkChannel is null)
        {
            NotifyUser(command.ControllingUserId, $"Couldn't reach {DisplayName}. Check that it is switched on and on the same network as the server.");
            return false;
        }

        // The receiver keeps its own internal queue from the full item list (like jellyfin-web's
        // Chrome sender does), so NextTrack/PreviousTrack below are just forwarded commands -
        // this controller does not need to track queue position itself.
        var (accessToken, controllingUser) = await GetAccessTokenAsync(user, cancellationToken).ConfigureAwait(false);
        if (accessToken is null || controllingUser is null)
        {
            return false;
        }

        _logger.LogInformation("Casting {Count} item(s) starting with {ItemName} to {DeviceName}", items.Count, items[0].Name, _receiver.Name);

        var (transportId, launched) = await EnsureReceiverAppAsync(client, cancellationToken).ConfigureAwait(false);
        if (transportId is null)
        {
            NotifyUser(command.ControllingUserId, $"{DisplayName} didn't open the Jellyfin app. Try again in a moment.");
            return false;
        }

        // "Identify" shows the receiver's own branded "ready" screen
        // (DocumentManager.setAppStatus(Waiting)); only meaningful on a fresh launch.
        if (launched)
        {
            await _connectSdkChannel.SendCommandAsync("Identify", controllingUser.Id, accessToken, _serverAddress, _receiver.Name, null, transportId).ConfigureAwait(false);
        }

        var options = BuildPlayNowOptions(items, command);
        await _connectSdkChannel.SendCommandAsync("PlayNow", controllingUser.Id, accessToken, _serverAddress, _receiver.Name, options, transportId).ConfigureAwait(false);
        _showingDetails = false;
        _ignoreNextStop = IsTransferFromLocalPlayer(command, items);
        if (_ignoreNextStop)
        {
            _logger.LogInformation("Playback of {ItemName} was transferred to {DeviceName} from a player in the app", items[0].Name, _receiver.Name);
        }

        return true;
    }

    /// <summary>
    /// Joins the Jellyfin receiver if it is already running on the device, otherwise launches it.
    /// Relaunching on every cast (the previous behavior) discarded the receiver's own state -
    /// notably its bitrate measurement, which it otherwise caches for 10 minutes and which costs
    /// ~8 seconds of /Playback/BitrateTest downloads before every fresh playback.
    /// </summary>
    /// <returns>The app's transport id (null on failure), and whether it was freshly launched.</returns>
    private async Task<(string? TransportId, bool Launched)> EnsureReceiverAppAsync(ChromecastClient client, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var appId = config.UseUnstableReceiver ? UnstableReceiverAppId : StableReceiverAppId;

        var running = client.ChromecastStatus?.Applications?.FirstOrDefault(a => a.AppId == appId);
        if (running is not null && running.TransportId == _appTransportId)
        {
            return (_appTransportId, false);
        }

        // SharpCaster's request/response matching has no built-in timeout - a response that never
        // arrives would otherwise hang this call forever with no log output.
        var launchTask = client.LaunchApplicationAsync(appId, joinExistingApplicationSession: true);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        var completed = await Task.WhenAny(launchTask, timeoutTask).ConfigureAwait(false);
        if (completed == timeoutTask)
        {
            _logger.LogWarning("Timed out launching the Jellyfin receiver on {Name}", _receiver.Name);
            InvalidateConnection();
            return (null, false);
        }

        var status = await launchTask.ConfigureAwait(false);
        var transportId = status?.Applications?.FirstOrDefault(a => a.AppId == appId)?.TransportId;
        if (transportId is null)
        {
            _logger.LogWarning("Chromecast {Name} did not report the Jellyfin receiver running after launch", _receiver.Name);
            return (null, false);
        }

        if (running is null)
        {
            _logger.LogInformation("Launched the Jellyfin receiver on {Name}", _receiver.Name);
        }
        else
        {
            _logger.LogInformation("Joined the already-running Jellyfin receiver on {Name}", _receiver.Name);
        }

        _appTransportId = transportId;
        _receiverHasItem = true;
        client.HeartbeatChannel.AdditionalDestinationId = transportId;
        StartAppKeepAlive(client, transportId);
        return (transportId, running is null);
    }

    /// <summary>
    /// Adds items to the queue of whatever is already casting ("PlayNext"/"PlayLast" from
    /// <see cref="PlayCommand"/>) - unlike a fresh <see cref="PlayCommand.PlayNow"/>, this must
    /// reuse the existing connection/app instance rather than relaunching, since the receiver
    /// keeps its own internal queue that a relaunch would destroy along with whatever was
    /// currently playing.
    /// </summary>
    private Task SendQueueCommandAsync(PlayRequest command, List<BaseItemDto> items)
    {
        if (_connectSdkChannel is null || _appTransportId is null || _mintedAccessToken is null)
        {
            _logger.LogWarning(
                "Chromecast {Name} received a {Command} queue command with nothing currently casting - ignoring",
                _receiver.Name,
                command.PlayCommand);
            return Task.CompletedTask;
        }

        var receiverCommand = command.PlayCommand == PlayCommand.PlayNext ? "PlayNext" : "PlayLast";
        _logger.LogInformation("Adding {Count} item(s) to the Chromecast queue ({Command}) on {DeviceName}", items.Count, receiverCommand, _receiver.Name);

        var options = BuildPlayNowOptions(items, command);
        return _connectSdkChannel.SendCommandAsync(receiverCommand, _session.UserId, _mintedAccessToken, _serverAddress, _receiver.Name, options, _appTransportId);
    }

    internal static PlayNowOptions BuildPlayNowOptions(List<BaseItemDto> items, PlayRequest command)
    {
        var startIndex = GetStartIndex(command, items.Count);

        return new PlayNowOptions
        {
            // PascalCase, matching Jellyfin's normal API casing - see the XML doc on
            // PlayNowOptions.Items for why this must not go through the envelope's camelCase
            // serializer options.
            Items = items.Select(item => JsonSerializer.SerializeToElement(item, ApiJsonOptions)).ToList(),
            StartIndex = startIndex == 0 ? null : startIndex,
            StartPositionTicks = command.StartPositionTicks,
            MediaSourceId = ResolveMediaSourceId(items[startIndex], command.MediaSourceId, command.AudioStreamIndex, command.SubtitleStreamIndex),
            AudioStreamIndex = command.AudioStreamIndex,
            SubtitleStreamIndex = command.SubtitleStreamIndex
        };
    }

    /// <summary>
    /// Jellyfin's PlaybackInfo only honors a requested audio/subtitle stream index when the same
    /// request also names the media source (MediaInfoHelper.SetDeviceSpecificData); otherwise it
    /// silently falls back to the item's default or remembered selection. Clients casting from the
    /// item page often send the indices without a media source id, so fill in the item's first
    /// source whenever an index was actually requested.
    /// </summary>
    internal static string? ResolveMediaSourceId(BaseItemDto item, string? mediaSourceId, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        if (!string.IsNullOrEmpty(mediaSourceId) || (audioStreamIndex is null && subtitleStreamIndex is null))
        {
            return mediaSourceId;
        }

        return item.MediaSources?.FirstOrDefault()?.Id ?? item.Id.ToString("N", CultureInfo.InvariantCulture);
    }

    private async Task SendPlaystateCommand(PlaystateRequest? command, CancellationToken cancellationToken)
    {
        if (command is null || _connectSdkChannel is null || _mintedAccessToken is null)
        {
            return;
        }

        try
        {
            switch (command.Command)
            {
                case PlaystateCommand.Stop:
                    // The receiver returns to its "ready" screen on its own. It stays open (and the
                    // token stays valid - its backdrop rotation still uses it) until the client
                    // disconnects; see OnRemoteControlEndedAsync.
                    if (_ignoreNextStop)
                    {
                        _ignoreNextStop = false;
                        _logger.LogInformation("Ignoring the Stop the app sends when its player page closes after transferring playback to {Name}", _receiver.Name);
                        break;
                    }

                    ForgetPlayRequests();
                    await SendReceiverCommandAsync("Stop", null).ConfigureAwait(false);
                    break;
                case PlaystateCommand.Pause:
                    await SendReceiverCommandAsync("Pause", null).ConfigureAwait(false);
                    break;
                case PlaystateCommand.Unpause:
                    await SendReceiverCommandAsync("Unpause", null).ConfigureAwait(false);
                    break;
                case PlaystateCommand.PlayPause:
                    await SendReceiverCommandAsync("PlayPause", null).ConfigureAwait(false);
                    break;
                case PlaystateCommand.Seek:
                    await SendReceiverCommandAsync("Seek", new SeekOptions { Position = (command.SeekPositionTicks ?? 0) / 10_000_000d }).ConfigureAwait(false);
                    break;
                case PlaystateCommand.NextTrack:
                    await SendReceiverCommandAsync("NextTrack", null).ConfigureAwait(false);
                    break;
                case PlaystateCommand.PreviousTrack:
                    await SendReceiverCommandAsync("PreviousTrack", null).ConfigureAwait(false);
                    break;
                default:
                    _logger.LogDebug("Playstate command {Command} is not supported for Chromecast sessions", command.Command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending playstate command {Command} to Chromecast {Name}", command.Command, _receiver.Name);
            InvalidateConnection();
        }
    }

    /// <summary>
    /// Gets the device id of this Chromecast's Jellyfin session.
    /// </summary>
    public string DeviceId => _session.DeviceId;

    /// <summary>
    /// Gets the name this Chromecast is shown under in Jellyfin (custom name, or prefix + own name).
    /// </summary>
    public string DisplayName => _session.DeviceName;

    /// <summary>
    /// Gets the name the Chromecast itself reports.
    /// </summary>
    public string ReceiverName => _receiver.Name;

    /// <summary>
    /// Gets the Chromecast's model name.
    /// </summary>
    public string Model => _receiver.Model;

    /// <summary>
    /// Gets the Chromecast's network address.
    /// </summary>
    public string Host => _receiver.DeviceUri.Host;

    /// <summary>
    /// Gets the Chromecast's cast port.
    /// </summary>
    public int Port => _receiver.Port;

    /// <summary>
    /// Gets a value indicating whether the Chromecast answered a recent discovery scan.
    /// </summary>
    public bool IsOnline => !_disposed && !_stale;

    /// <summary>
    /// Gets a value indicating whether the Jellyfin receiver is currently open on the Chromecast.
    /// </summary>
    public bool IsReceiverOpen => _appTransportId is not null;

    /// <summary>
    /// Gets the name of what is playing, if anything.
    /// </summary>
    public string? NowPlaying => _session.NowPlayingItem?.Name;

    /// <summary>
    /// Gets a value indicating whether an admin hid this Chromecast in the plugin settings.
    /// </summary>
    public bool IsHidden => (Plugin.Instance?.Configuration.DeviceSettings ?? []).Any(d => d.Hidden && string.Equals(d.DeviceId, DeviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Called when a Jellyfin client of <paramref name="userId"/> connects to a remote player and
    /// this Chromecast is the best guess for which one (see <see cref="RemoteControlListener"/>).
    /// Opens the receiver's "ready" screen so the TV reacts to the connect itself.
    /// </summary>
    /// <param name="userId">The user whose client connected.</param>
    /// <returns>A task that completes once the receiver is open, or the attempt failed.</returns>
    public async Task OnRemoteControlStartedAsync(Guid userId)
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _castGeneration);
        MarkActivity();

        try
        {
            await EnsureAliveAsync().ConfigureAwait(false);
            if (_appTransportId is not null)
            {
                // Already open and answering - leave whatever it is showing alone.
                return;
            }

            var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
            if (client is null || _connectSdkChannel is null)
            {
                return;
            }

            var (accessToken, user) = await GetAccessTokenAsync(_userManager.GetUserById(userId), CancellationToken.None).ConfigureAwait(false);
            if (accessToken is null || user is null)
            {
                return;
            }

            var (transportId, launched) = await EnsureReceiverAppAsync(client, CancellationToken.None).ConfigureAwait(false);
            if (transportId is null)
            {
                return;
            }

            if (launched)
            {
                _openedOnConnectFor = userId;
                _logger.LogInformation("Opened the ready screen on {Name} because {User} connected to a remote player", _receiver.Name, user.Username);
            }

            await _connectSdkChannel.SendCommandAsync("Identify", user.Id, accessToken, _serverAddress, _receiver.Name, null, transportId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening the ready screen on Chromecast {Name}", _receiver.Name);
            InvalidateConnection();
        }
    }

    /// <summary>
    /// Shows the receiver's "ready" screen so an admin can check from the settings page that this
    /// Chromecast is reachable. If the receiver had to be opened for it, it closes again shortly
    /// after unless someone starts using it.
    /// </summary>
    /// <returns>Whether the Chromecast responded.</returns>
    public async Task<bool> TestAsync()
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            await EnsureAliveAsync().ConfigureAwait(false);
            if (IsReceiverOpen)
            {
                await SendReceiverCommandAsync("Identify", null).ConfigureAwait(false);
                return true;
            }

            var generation = Interlocked.Increment(ref _castGeneration);
            MarkActivity();

            var client = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
            if (client is null || _connectSdkChannel is null)
            {
                return false;
            }

            var (accessToken, user) = await GetAccessTokenAsync(null, CancellationToken.None).ConfigureAwait(false);
            if (accessToken is null || user is null)
            {
                return false;
            }

            var (transportId, _) = await EnsureReceiverAppAsync(client, CancellationToken.None).ConfigureAwait(false);
            if (transportId is null)
            {
                return false;
            }

            await _connectSdkChannel.SendCommandAsync("Identify", user.Id, accessToken, _serverAddress, _receiver.Name, null, transportId).ConfigureAwait(false);
            _ = CloseAfterTestAsync(generation);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing Chromecast {Name}", _receiver.Name);
            InvalidateConnection();
            return false;
        }
    }

    private async Task CloseAfterTestAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (generation == Volatile.Read(ref _castGeneration))
        {
            await CloseReceiverAppAsync().ConfigureAwait(false);
        }
    }

    private Task CloseIfOpenedOnConnectForAsync(Guid userId)
    {
        if (_openedOnConnectFor != userId || userId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        _openedOnConnectFor = Guid.Empty;
        _logger.LogInformation("{Name} was opened on a connect guess but another Chromecast was chosen, closing it", _receiver.Name);
        return CloseReceiverAppAsync();
    }

    private void RememberAsLastTarget(Guid userId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (LastTargetLock)
        {
            var targets = plugin.Configuration.LastCastTargets ?? [];
            if (targets.Any(t => t.UserId == userId && t.DeviceId == DeviceId))
            {
                return;
            }

            plugin.Configuration.LastCastTargets = targets
                .Where(t => t.UserId != userId)
                .Append(new LastCastTarget { UserId = userId, DeviceId = DeviceId })
                .ToArray();
            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// Called when a Jellyfin client of <paramref name="userId"/> disconnects from its remote
    /// player (see <see cref="RemoteControlListener"/>). Closes the receiver if that user was the
    /// one controlling this Chromecast.
    /// </summary>
    /// <param name="userId">The user whose client disconnected.</param>
    /// <returns>A task that completes once the receiver is closed or left alone.</returns>
    public async Task OnRemoteControlEndedAsync(Guid userId)
    {
        if (_disposed || _appTransportId is null || (userId != _lastControllingUserId && userId != _openedOnConnectFor))
        {
            return;
        }

        _openedOnConnectFor = Guid.Empty;

        _logger.LogInformation("Controlling client disconnected from {Name}, closing the Jellyfin receiver", _receiver.Name);
        try
        {
            await SendReceiverCommandAsync("Stop", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping playback on {Name} before closing", _receiver.Name);
        }

        await CloseReceiverAppAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the Jellyfin receiver so the TV returns to its normal screen.
    /// The receiver disables CAF's idle timeout and only exits once every sender disconnects, and
    /// this plugin's own heartbeat/keep-alive would otherwise keep it on screen indefinitely.
    /// </summary>
    private async Task CloseReceiverAppAsync()
    {
        try
        {
            await CloseReceiverAppCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing the Jellyfin receiver on {Name}", _receiver.Name);
        }
    }

    private async Task CloseReceiverAppCoreAsync()
    {
        var generation = Interlocked.Increment(ref _castGeneration);
        var client = _client;
        if (client is null)
        {
            await RevokeAccessTokenAsync().ConfigureAwait(false);
            return;
        }

        // Let the receiver report the stop to Jellyfin (with the token we're about to revoke).
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        // A new cast arrived in the meantime - it owns the receiver now.
        if (generation != Volatile.Read(ref _castGeneration) || !ReferenceEquals(_client, client))
        {
            return;
        }

        var appId = (Plugin.Instance?.Configuration ?? new PluginConfiguration()).UseUnstableReceiver ? UnstableReceiverAppId : StableReceiverAppId;
        if (client.ChromecastStatus?.Application?.AppId == appId)
        {
            try
            {
                // No built-in timeout on SharpCaster requests; the device may also just drop the
                // connection once the app is gone instead of answering.
                await client.ReceiverChannel.StopApplication().WithTimeoutAsync(CastTimeout.Command, "Closing the receiver").ConfigureAwait(false);
                _logger.LogInformation("Closed the Jellyfin receiver on {Name}", _receiver.Name);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing the Jellyfin receiver on {Name}", _receiver.Name);
            }
        }

        if (ReferenceEquals(_client, client))
        {
            client.Disconnected -= OnClientDisconnected;
            InvalidateConnection();
            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disconnecting from Chromecast {Name}", _receiver.Name);
            }
        }

        await RevokeAccessTokenAsync().ConfigureAwait(false);
    }

    private Task SendReceiverCommandAsync(string command, object? options)
    {
        if (_connectSdkChannel is null || _mintedAccessToken is null || _appTransportId is null)
        {
            return Task.CompletedTask;
        }

        return _connectSdkChannel.SendCommandAsync(command, _session.UserId, _mintedAccessToken, _serverAddress, _receiver.Name, options, _appTransportId);
    }

    private Task SendGeneralCommand(GeneralCommand? command, CancellationToken cancellationToken)
    {
        if (command is null)
        {
            return Task.CompletedTask;
        }

        if (command.Name == GeneralCommandType.DisplayContent)
        {
            return DisplayContentAsync(command, cancellationToken);
        }

        // Every Jellyfin sender (jellyfin-web's remote control, the mobile/TV apps' subtitle and
        // audio track pickers) sends these as a GeneralCommand with Arguments["Index"], mirroring
        // sessionPlayer.js's setSubtitleStreamIndex/setAudioStreamIndex. The receiver's own
        // CommandHandler supports both ("SetAudioStreamIndex"/"SetSubtitleStreamIndex" in
        // commandHandler.ts), expecting an options payload shaped like { index: <int> } (see
        // SetIndexRequest in the receiver's types/global.d.ts - lowercase "index", NOT the same
        // PascalCase-then-camelCased shape PlayNowOptions uses) - without this, switching
        // subtitles/audio tracks after a cast has already started was a silent no-op.
        if (command.Name is GeneralCommandType.SetSubtitleStreamIndex or GeneralCommandType.SetAudioStreamIndex)
        {
            if (command.Arguments.TryGetValue("Index", out var indexStr) && int.TryParse(indexStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
            {
                var receiverCommand = command.Name == GeneralCommandType.SetSubtitleStreamIndex ? "SetSubtitleStreamIndex" : "SetAudioStreamIndex";
                return SendReceiverCommandAsync(receiverCommand, new SetIndexOptions { Index = index });
            }

            return Task.CompletedTask;
        }

        // Volume/mute are explicitly *not* implemented by the receiver's own command handler
        // ("implemented on the sender" per its own source) - Chrome's sender handles these via
        // the standard CastV2 receiver channel instead of the connectsdk namespace, so this
        // mirrors that rather than sending a receiver command that would be a no-op.
        if (_client is null)
        {
            return Task.CompletedTask;
        }

        var client = _client;
        var currentVolume = client.ChromecastStatus?.Volume?.Level ?? 0.5;
        var isMuted = client.ChromecastStatus?.Volume?.Muted ?? false;

        switch (command.Name)
        {
            case GeneralCommandType.VolumeUp:
                return SetVolumeAsync(client, () => client.ReceiverChannel.SetVolume(Math.Clamp(currentVolume + 0.05, 0, 1)));
            case GeneralCommandType.VolumeDown:
                return SetVolumeAsync(client, () => client.ReceiverChannel.SetVolume(Math.Clamp(currentVolume - 0.05, 0, 1)));
            case GeneralCommandType.Mute:
                return SetVolumeAsync(client, () => client.ReceiverChannel.SetMute(true));
            case GeneralCommandType.Unmute:
                return SetVolumeAsync(client, () => client.ReceiverChannel.SetMute(false));
            case GeneralCommandType.ToggleMute:
                return SetVolumeAsync(client, () => client.ReceiverChannel.SetMute(!isMuted));
            case GeneralCommandType.SetVolume:
                if (command.Arguments.TryGetValue("Volume", out var volStr)
                    && double.TryParse(volStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var vol))
                {
                    return SetVolumeAsync(client, () => client.ReceiverChannel.SetVolume(Math.Clamp(vol / 100d, 0, 1)));
                }

                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private async Task SetVolumeAsync(ChromecastClient client, Func<Task> change)
    {
        try
        {
            await change().WithTimeoutAsync(CastTimeout.Command, "A volume change").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the volume on Chromecast {Name}", _receiver.Name);
            HandleConnectionLost(client, "a volume command failed");
        }
    }

    /// <summary>
    /// Jellyfin clients send DisplayContent whenever an item page is opened while connected to a
    /// remote player ("display mirroring", on by default). Connecting to a cast target itself sends
    /// nothing to the server, so this is the earliest signal available: it starts (or joins) the
    /// receiver so it shows the item and does its bitrate measurement while the user is still
    /// browsing, instead of all of that happening after Play is pressed.
    /// </summary>
    private async Task DisplayContentAsync(GeneralCommand command, CancellationToken cancellationToken)
    {
        if (!command.Arguments.TryGetValue("ItemId", out var itemId) || string.IsNullOrEmpty(itemId))
        {
            return;
        }

        Interlocked.Increment(ref _castGeneration);

        try
        {
            // The user navigated, so the app's player page (if it was open) is closed: any Stop
            // from here on is deliberate.
            _ignoreNextStop = false;

            await EnsureAliveAsync().ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (client is null || _connectSdkChannel is null)
            {
                return;
            }

            var user = command.ControllingUserId == Guid.Empty ? null : _userManager.GetUserById(command.ControllingUserId);
            var (accessToken, controllingUser) = await GetAccessTokenAsync(user, cancellationToken).ConfigureAwait(false);
            if (accessToken is null || controllingUser is null)
            {
                return;
            }

            var (transportId, _) = await EnsureReceiverAppAsync(client, cancellationToken).ConfigureAwait(false);
            if (transportId is null)
            {
                return;
            }

            // The receiver ignores this while it's playing something, so it's safe to forward always.
            await _connectSdkChannel.SendCommandAsync("DisplayContent", controllingUser.Id, accessToken, _serverAddress, _receiver.Name, new DisplayContentOptions { ItemId = itemId }, transportId).ConfigureAwait(false);
            _detailsShownAt = DateTime.UtcNow;
            _showingDetails = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending DisplayContent to Chromecast {Name}", _receiver.Name);
            InvalidateConnection();
        }
    }

    /// <summary>
    /// The device id Jellyfin's receiver gives itself: JavaScript's <c>btoa(receiverName)</c>, which
    /// encodes the name as Latin-1. Names btoa can't encode fall back to an id of our own.
    /// </summary>
    private string GetReceiverDeviceId() => GetReceiverDeviceId(_receiver.Name) ?? "chromecast-receiver-" + _session.DeviceId;

    /// <summary>JavaScript's <c>btoa(name)</c>, or null where btoa would throw (characters beyond Latin-1).</summary>
    internal static string? GetReceiverDeviceId(string name)
        => name.All(c => c <= 0xFF) ? Convert.ToBase64String(System.Text.Encoding.Latin1.GetBytes(name)) : null;

    private async Task<(string? AccessToken, User? User)> GetAccessTokenAsync(User? user, CancellationToken cancellationToken)
    {
        user ??= _userManager.GetUsers().FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator));
        if (user is null)
        {
            _logger.LogError("Cannot cast: no controlling user and no administrator fallback available");
            return (null, null);
        }

        // Reuse the live token: minting another for the same device id makes Jellyfin log out the
        // previous one, which kills whatever the receiver is still streaming with it.
        if (_mintedAccessToken is not null && _mintedTokenUserId == user.Id)
        {
            return (_mintedAccessToken, user);
        }

        // AuthenticateDirect mints a real, correctly-scoped session token for this user without a
        // password - the receiver needs its own token because it makes its own API calls
        // (PlaybackInfo negotiation, playback reporting) as a genuine authenticated Jellyfin
        // client, just like the receiver does when launched by a Chrome sender.
        MediaBrowser.Controller.Authentication.AuthenticationResult authResult;
        try
        {
            authResult = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = user.Id,
                Username = user.Username,
                // Sign in under the identity the receiver itself will report (client "Chromecast",
                // device id btoa(receiverName)), so Jellyfin keeps one session for the cast instead
                // of one for this token plus one for the receiver using it.
                App = "Chromecast",
                AppVersion = _appHost.ApplicationVersionString,
                DeviceId = GetReceiverDeviceId(),
                DeviceName = _receiver.Name
            }).ConfigureAwait(false);
        }
        catch (MediaBrowser.Controller.Net.SecurityException ex)
        {
            // Most likely "User is at their maximum number of sessions": every cast signs the
            // Chromecast in as the casting user, which counts against that user's session limit.
            _logger.LogError(
                "Cannot cast to {Name} as {User}: {Reason} If this user has a simultaneous session limit (Dashboard > Users), raise it or set it to 0 - each cast needs up to 2 extra sessions.",
                _receiver.Name,
                user.Username,
                ex.Message);
            NotifyUser(user.Id, "Casting isn't possible right now: your account has reached its limit of simultaneous sessions. Ask the server admin to raise it.");
            return (null, null);
        }

        _mintedAccessToken = authResult.AccessToken;
        _mintedTokenUserId = user.Id;
        return (authResult.AccessToken, user);
    }

    private async Task RevokeAccessTokenAsync()
    {
        var token = _mintedAccessToken;
        _mintedAccessToken = null;

        if (token is null)
        {
            return;
        }

        try
        {
            await _sessionManager.Logout(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error revoking Chromecast receiver access token (it may already be gone)");
        }
    }

    private async Task<ChromecastClient?> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            // The parameterless ChromecastClient() constructor passes a null logger through to
            // every built-in channel (ConnectionChannel, HeartbeatChannel, ReceiverChannel, ...),
            // silencing all of SharpCaster's own diagnostics - including exactly the heartbeat
            // ping/pong/timeout logging needed to debug the connection-longevity issue in
            // CLAUDE.md. Pass a real logger.
            var client = new ChromecastClient(new VerboseGatedLogger<ChromecastClient>(_loggerFactory.CreateLogger<ChromecastClient>(), routineInfoIsVerbose: true));

            var connectSdkChannel = new ConnectSdkChannel(new VerboseGatedLogger<ConnectSdkChannel>(_loggerFactory.CreateLogger<ConnectSdkChannel>(), routineInfoIsVerbose: false));
            client.AddChannel(connectSdkChannel);
            connectSdkChannel.StatusReceived += OnConnectSdkStatusReceived;

            try
            {
                await client.ConnectChromecast(_receiver).WithTimeoutAsync(CastTimeout.Connect, "Connecting").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Don't leave a half-open connection behind.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await client.DisconnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error abandoning a connection attempt to {Name}", _receiver.Name);
                    }
                });
                throw;
            }

            client.Disconnected += OnClientDisconnected;

            _client = client;
            _connectSdkChannel = connectSdkChannel;
            return _client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Chromecast {Name}", _receiver.Name);
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async void OnConnectSdkStatusReceived(object? sender, ConnectSdkStatusMessage status)
    {
        if (_disposed)
        {
            return;
        }

        if (status.Data is { ValueKind: JsonValueKind.Object } data
            && data.TryGetProperty("ItemId", out var itemId)
            && itemId.ValueKind == JsonValueKind.String)
        {
            _receiverHasItem = !string.IsNullOrEmpty(itemId.GetString());
        }

        if (status.Type == "playbackstop")
        {
            _receiverHasItem = false;
            _ignoreNextStop = false;
        }

        var reportedItemId = Guid.Empty;
        if (status.Data is { ValueKind: JsonValueKind.Object } statusData
            && statusData.TryGetProperty("ItemId", out var reportedItem)
            && reportedItem.ValueKind == JsonValueKind.String)
        {
            Guid.TryParse(reportedItem.GetString(), out reportedItemId);
        }

        OnReceiverReported(status.Type, reportedItemId);

        try
        {
            switch (status.Type)
            {
                case "playbackstart":
                    await HandlePlaybackStartAsync(status.Data).ConfigureAwait(false);
                    break;
                case "playbackprogress":
                case "playstatechange":
                    await HandlePlaybackProgressAsync(status.Data).ConfigureAwait(false);
                    break;
                case "playbackstop":
                    // Deliberately does NOT revoke the access token here. A "playbackstop"
                    // broadcast fires for the *current item* stopping, which also happens for a
                    // perfectly normal mid-queue transition (item N finishing right before item
                    // N+1 starts, e.g. after NextTrack) - not only when the whole cast session
                    // ends. Revoking here broke exactly that case (confirmed by testing): the
                    // token died between items, the receiver's own follow-up API calls for the
                    // next item failed with "Invalid token", and it gave up with an unhelpful
                    // empty "connectionerror" broadcast, stopping playback entirely instead of
                    // advancing. Token revocation now only happens on an explicit user Stop
                    // command, a CastV2 disconnect, or this controller being disposed - see
                    // RevokeAccessTokenAsync's other call sites.
                    await HandlePlaybackStopAsync(status.Data).ConfigureAwait(false);
                    break;
                case "error":
                case "connectionerror":
                    _logger.LogWarning("Chromecast receiver reported an error: {Message}", status.Message);
                    NotifyUser(_lastControllingUserId, $"{DisplayName} couldn't play this.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Chromecast receiver status message ({Type})", status.Type);
        }
    }

    private Task HandlePlaybackStartAsync(JsonElement? data)
    {
        var info = DeserializeProgress(data);
        if (info is null)
        {
            return Task.CompletedTask;
        }

        return _sessionManager.OnPlaybackStart(new PlaybackStartInfo
        {
            ItemId = info.ItemId,
            SessionId = _session.Id,
            MediaSourceId = info.MediaSourceId,
            PlaySessionId = info.PlaySessionId,
            PositionTicks = info.PositionTicks,
            CanSeek = true,
            PlayMethod = info.PlayMethod
        });
    }

    private Task HandlePlaybackProgressAsync(JsonElement? data)
    {
        var info = DeserializeProgress(data);
        if (info is null)
        {
            return Task.CompletedTask;
        }

        info.SessionId = _session.Id;
        return _sessionManager.OnPlaybackProgress(info);
    }

    private Task HandlePlaybackStopAsync(JsonElement? data)
    {
        var info = DeserializeProgress(data);
        if (info is null)
        {
            return Task.CompletedTask;
        }

        return _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
        {
            ItemId = info.ItemId,
            SessionId = _session.Id,
            MediaSourceId = info.MediaSourceId,
            PlaySessionId = info.PlaySessionId,
            PositionTicks = info.PositionTicks
        });
    }

    private PlaybackProgressInfo? DeserializeProgress(JsonElement? data)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object || !data.Value.TryGetProperty("PlayState", out var playState))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PlaybackProgressInfo>(playState.GetRawText(), ApiJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse playback status from Chromecast receiver: {Data}", playState.GetRawText());
            return null;
        }
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Chromecast {Name} disconnected", _receiver.Name);

        var client = _client;
        InvalidateConnection();

        if (client is not null)
        {
            client.Disconnected -= OnClientDisconnected;
        }

        _ = RevokeAccessTokenAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAppKeepAlive();

        await RevokeAccessTokenAsync().ConfigureAwait(false);

        var client = _client;
        _client = null;

        if (client is not null)
        {
            client.Disconnected -= OnClientDisconnected;

            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disconnecting Chromecast {Name} during dispose", _receiver.Name);
            }
        }

        _connectLock.Dispose();
    }
}

/// <summary>
/// <c>System.Text.Json</c>'s built-in <see cref="Guid"/> converter only accepts the canonical
/// dashed format - Jellyfin's own dashless "N" format (as used internally, including by the cast
/// receiver's own status reporting) fails to parse with it. <c>Guid.Parse(string)</c>
/// itself already handles both (and more), so this just delegates to that instead.
/// </summary>
internal sealed class FlexibleGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return string.IsNullOrEmpty(value) ? Guid.Empty : Guid.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
