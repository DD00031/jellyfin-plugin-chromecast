using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Chromecast.Cast;
using Jellyfin.Plugin.Chromecast.Configuration;
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

    /// <summary>Matches Jellyfin API JSON casing (PascalCase) for the inner status payload.</summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new();

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

    private string? _mintedAccessToken;

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
    public bool IsSessionActive => !_disposed && !_stale;

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
    public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return name switch
        {
            SessionMessageType.Play => SendPlayCommand(data as PlayRequest, cancellationToken),
            SessionMessageType.Playstate => SendPlaystateCommand(data as PlaystateRequest, cancellationToken),
            SessionMessageType.GeneralCommand => SendGeneralCommand(data as GeneralCommand, cancellationToken),
            _ => Task.CompletedTask
        };
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

        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (client is null || _connectSdkChannel is null)
        {
            return;
        }

        // The receiver keeps its own internal queue from the full item list (like jellyfin-web's
        // Chrome sender does), so NextTrack/PreviousTrack below are just forwarded commands -
        // this controller does not need to track queue position itself.
        var (accessToken, controllingUser) = await MintAccessTokenAsync(user, cancellationToken).ConfigureAwait(false);
        if (accessToken is null || controllingUser is null)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var appId = config.UseUnstableReceiver ? UnstableReceiverAppId : StableReceiverAppId;

        _logger.LogInformation("Casting {Count} item(s) starting with {ItemName} to {DeviceName}", items.Count, items[0].Name, _receiver.Name);

        await client.LaunchApplicationAsync(appId).ConfigureAwait(false);

        var options = new PlayNowOptions
        {
            Items = items,
            StartPositionTicks = command.StartPositionTicks,
            MediaSourceId = command.MediaSourceId,
            AudioStreamIndex = command.AudioStreamIndex,
            SubtitleStreamIndex = command.SubtitleStreamIndex
        };

        await _connectSdkChannel.SendCommandAsync("PlayNow", controllingUser.Id, accessToken, _serverAddress, _receiver.Name, options).ConfigureAwait(false);
    }

    private async Task SendPlaystateCommand(PlaystateRequest? command, CancellationToken cancellationToken)
    {
        if (command is null || _connectSdkChannel is null || _mintedAccessToken is null)
        {
            return;
        }

        switch (command.Command)
        {
            case PlaystateCommand.Stop:
                await SendReceiverCommandAsync("Stop", null).ConfigureAwait(false);
                await RevokeAccessTokenAsync().ConfigureAwait(false);
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

    private Task SendReceiverCommandAsync(string command, object? options)
    {
        if (_connectSdkChannel is null || _mintedAccessToken is null)
        {
            return Task.CompletedTask;
        }

        return _connectSdkChannel.SendCommandAsync(command, _session.UserId, _mintedAccessToken, _serverAddress, _receiver.Name, options);
    }

    private Task SendGeneralCommand(GeneralCommand? command, CancellationToken cancellationToken)
    {
        // Volume/mute are explicitly *not* implemented by the receiver's own command handler
        // ("implemented on the sender" per its own source) - Chrome's sender handles these via
        // the standard CastV2 receiver channel instead of the connectsdk namespace, so this
        // mirrors that rather than sending a receiver command that would be a no-op.
        if (command is null || _client is null)
        {
            return Task.CompletedTask;
        }

        var currentVolume = _client.ChromecastStatus?.Volume?.Level ?? 0.5;
        var isMuted = _client.ChromecastStatus?.Volume?.Muted ?? false;

        switch (command.Name)
        {
            case GeneralCommandType.VolumeUp:
                return _client.ReceiverChannel.SetVolume(Math.Clamp(currentVolume + 0.05, 0, 1));
            case GeneralCommandType.VolumeDown:
                return _client.ReceiverChannel.SetVolume(Math.Clamp(currentVolume - 0.05, 0, 1));
            case GeneralCommandType.Mute:
                return _client.ReceiverChannel.SetMute(true);
            case GeneralCommandType.Unmute:
                return _client.ReceiverChannel.SetMute(false);
            case GeneralCommandType.ToggleMute:
                return _client.ReceiverChannel.SetMute(!isMuted);
            case GeneralCommandType.SetVolume:
                if (command.Arguments.TryGetValue("Volume", out var volStr)
                    && double.TryParse(volStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var vol))
                {
                    return _client.ReceiverChannel.SetVolume(Math.Clamp(vol / 100d, 0, 1));
                }

                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private async Task<(string? AccessToken, User? User)> MintAccessTokenAsync(User? user, CancellationToken cancellationToken)
    {
        user ??= _userManager.Users.FirstOrDefault(u => u.HasPermission(MediaBrowser.Model.Users.PermissionKind.IsAdministrator));
        if (user is null)
        {
            _logger.LogError("Cannot cast: no controlling user and no administrator fallback available");
            return (null, null);
        }

        // AuthenticateDirect mints a real, correctly-scoped session token for this user without a
        // password - the receiver needs its own token because it makes its own API calls
        // (PlaybackInfo negotiation, playback reporting) as a genuine authenticated Jellyfin
        // client, just like the receiver does when launched by a Chrome sender.
        var authResult = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
        {
            UserId = user.Id,
            Username = user.Username,
            App = "Jellyfin Chromecast Plugin",
            AppVersion = _appHost.ApplicationVersionString,
            DeviceId = "chromecast-" + _session.DeviceId,
            DeviceName = _receiver.Name
        }).ConfigureAwait(false);

        _mintedAccessToken = authResult.AccessToken;
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

            var client = new ChromecastClient();

            var connectSdkChannel = new ConnectSdkChannel(_loggerFactory.CreateLogger<ConnectSdkChannel>());
            client.AddChannel(connectSdkChannel);
            connectSdkChannel.StatusReceived += OnConnectSdkStatusReceived;

            await client.ConnectChromecast(_receiver).ConfigureAwait(false);
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
                    await HandlePlaybackStopAsync(status.Data).ConfigureAwait(false);
                    await RevokeAccessTokenAsync().ConfigureAwait(false);
                    break;
                case "error":
                case "connectionerror":
                    _logger.LogWarning("Chromecast receiver reported an error: {Message}", status.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Chromecast receiver status message ({Type})", status.Type);
        }
    }

    private Task HandlePlaybackStartAsync(string? data)
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

    private Task HandlePlaybackProgressAsync(string? data)
    {
        var info = DeserializeProgress(data);
        if (info is null)
        {
            return Task.CompletedTask;
        }

        info.SessionId = _session.Id;
        return _sessionManager.OnPlaybackProgress(info);
    }

    private Task HandlePlaybackStopAsync(string? data)
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

    private PlaybackProgressInfo? DeserializeProgress(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PlaybackProgressInfo>(data, ApiJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse playback status from Chromecast receiver: {Data}", data);
            return null;
        }
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Chromecast {Name} disconnected", _receiver.Name);

        var client = _client;
        _client = null;
        _connectSdkChannel = null;

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
