using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Chromecast.Configuration;
using Jellyfin.Plugin.Chromecast.Session;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;

namespace Jellyfin.Plugin.Chromecast.Discovery;

/// <summary>
/// Discovers Chromecast devices on the local network via mDNS and exposes each one as a
/// controllable Jellyfin <see cref="SessionInfo"/>, mirroring how the built-in DLNA PlayTo
/// feature turns discovered renderers into cast targets. This is what makes Chromecast devices
/// show up in the same "Play On" cast interface on every Jellyfin client - including
/// iOS/iPadOS/macOS, which cannot use Google's Chrome/Android-only Cast Sender SDK at all.
/// </summary>
public sealed class ChromecastDiscoveryManager : IDisposable
{
    private readonly ILogger<ChromecastDiscoveryManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly IServerApplicationHost _appHost;
    private readonly ChromecastLocator _locator;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _addDeviceLock = new(1, 1);
    private readonly Timer _staleSweepTimer;
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _loopCts = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromecastDiscoveryManager"/> class.
    /// </summary>
    public ChromecastDiscoveryManager(
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        IServerApplicationHost appHost)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChromecastDiscoveryManager>();
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _appHost = appHost;

        // The locator logs several Information lines per scan (every 30 s, forever) - only show
        // them when the plugin's "Verbose logging" setting is on.
        _locator = new ChromecastLocator(new VerboseGatedLogger<ChromecastLocator>(loggerFactory.CreateLogger<ChromecastLocator>(), routineInfoIsVerbose: true));

        var staleCheckInterval = TimeSpan.FromSeconds(Math.Max(15, GetConfig().DeviceStaleAfterSeconds / 2));
        _staleSweepTimer = new Timer(SweepStaleDevices, null, staleCheckInterval, staleCheckInterval);
    }

    /// <summary>
    /// Starts polling the local network for Chromecast devices.
    /// </summary>
    public void Start()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, GetConfig().DiscoveryIntervalSeconds));
        _logger.LogInformation("Starting Chromecast mDNS discovery (interval: {Interval})", interval);

        // ChromecastLocator's FindReceiversAsync() is called on every cycle (rather than relying
        // on a "device found" event that only fires once per device for the locator's lifetime)
        // so that liveness is refreshed for devices we already know about too, not just newly
        // seen ones.
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        _ = Task.Run(() => RunDiscoveryLoopAsync(interval, _loopCts.Token));
    }

    private async Task RunDiscoveryLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // By default the locator listens for 400 ms and stops as soon as *any* device has
                // answered, so with several Chromecasts a slower one is regularly missed - often
                // enough in a row to be dropped from the cast menu. One full-length listen instead.
                var receivers = await _locator.FindReceiversAsync(ScanTime, ScanTime, ScanTime).ConfigureAwait(false);
                foreach (var receiver in receivers)
                {
                    await ProcessReceiverAsync(receiver).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during Chromecast discovery scan, will retry next cycle");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// The name a Chromecast is shown under in Jellyfin: the admin's custom name if one is set in
    /// the plugin settings, otherwise the optional prefix plus the Chromecast's own name.
    /// </summary>
    private static string GetDisplayName(string receiverName, string deviceId)
    {
        var config = GetConfig();
        var custom = (config.DeviceSettings ?? [])
            .FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        return string.IsNullOrEmpty(config.DeviceNamePrefix) ? receiverName : config.DeviceNamePrefix + receiverName;
    }

    /// <summary>
    /// Applies renamed Chromecasts right after the settings are saved, instead of on the next scan.
    /// </summary>
    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        foreach (var session in _sessionManager.Sessions)
        {
            var controller = session.SessionControllers.OfType<ChromecastSessionController>().FirstOrDefault();
            if (controller is not null)
            {
                session.DeviceName = GetDisplayName(controller.ReceiverName, session.DeviceId);
            }
        }
    }

    private static PluginConfiguration GetConfig() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private async Task ProcessReceiverAsync(ChromecastReceiver receiver)
    {
        if (_disposed)
        {
            return;
        }

        var deviceId = GetStableDeviceId(receiver);
        _lastSeen[deviceId] = DateTime.UtcNow;

        await _addDeviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var existing = _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            var controller = existing?.SessionControllers.OfType<ChromecastSessionController>().FirstOrDefault();
            if (controller is not null)
            {
                // Already tracked - just refresh the receiver info in case the IP changed, and
                // clear any stale flag now that it has responded again.
                controller.UpdateReceiver(receiver);
                existing!.DeviceName = GetDisplayName(receiver.Name, deviceId);
                return;
            }

            await AddDeviceAsync(receiver, deviceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering Chromecast device {Name}", receiver.Name);
        }
        finally
        {
            _addDeviceLock.Release();
        }
    }

    private async Task AddDeviceAsync(ChromecastReceiver receiver, string deviceId)
    {
        var deviceName = GetDisplayName(receiver.Name, deviceId);

        var sessionInfo = await _sessionManager
            .LogSessionActivity("Chromecast", _appHost.ApplicationVersionString, deviceId, deviceName, receiver.DeviceUri.Host, null)
            .ConfigureAwait(false);

        var serverAddress = GetServerAddress(receiver);

        var controller = new ChromecastSessionController(
            sessionInfo,
            receiver,
            serverAddress,
            _sessionManager,
            _libraryManager,
            _userManager,
            _dtoService,
            _appHost,
            _loggerFactory);

        sessionInfo.AddController(controller);

        _sessionManager.ReportCapabilities(sessionInfo.Id, sessionInfo.Id, new ClientCapabilities
        {
            PlayableMediaTypes = new[] { MediaType.Video, MediaType.Audio },
            // Clients only show track pickers for commands listed here: jellyfin-web's item
            // details page hides its whole version/audio/subtitle block unless the active
            // player advertises PlayMediaSource, and its remote-control UI checks
            // SetAudioStreamIndex/SetSubtitleStreamIndex before offering track switching.
            // DisplayContent enables the clients' "display mirroring" (on by default), which is the
            // only signal the server gets before Play that someone is casting here. EndSession is
            // deliberately absent: jellyfin-web's session player has no endSession(), so
            // advertising it would break the Disconnect button.
            SupportedCommands = new[]
            {
                GeneralCommandType.DisplayContent,
                GeneralCommandType.PlayMediaSource,
                GeneralCommandType.SetAudioStreamIndex,
                GeneralCommandType.SetSubtitleStreamIndex,
                GeneralCommandType.VolumeUp,
                GeneralCommandType.VolumeDown,
                GeneralCommandType.Mute,
                GeneralCommandType.Unmute,
                GeneralCommandType.ToggleMute,
                GeneralCommandType.SetVolume
            },
            SupportsMediaControl = true
        });

        _logger.LogInformation("Chromecast session created for {Name} ({Model}) at {Uri}", receiver.Name, receiver.Model, receiver.DeviceUri);
    }

    private string GetServerAddress(ChromecastReceiver receiver)
    {
        try
        {
            var ip = IPAddress.Parse(receiver.DeviceUri.Host);
            return _appHost.GetSmartApiUrl(ip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve a smart API URL for {Host}, falling back to local access URL", receiver.DeviceUri.Host);
            return _appHost.GetApiUrlForLocalAccess();
        }
    }

    private static string GetStableDeviceId(ChromecastReceiver receiver)
    {
        if (receiver.ExtraInformation is not null && receiver.ExtraInformation.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id))
        {
            return "chromecast-" + id;
        }

        // Fall back to a hash of the name + host, which is stable as long as the device isn't renamed.
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(receiver.Name + "|" + receiver.DeviceUri.Host));
        return "chromecast-" + Convert.ToHexString(bytes);
    }

    private async void SweepStaleDevices(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var maxAge = TimeSpan.FromSeconds(Math.Max(30, GetConfig().DeviceStaleAfterSeconds));
            var now = DateTime.UtcNow;

            foreach (var session in _sessionManager.Sessions)
            {
                var controller = session.SessionControllers.OfType<ChromecastSessionController>().FirstOrDefault();
                if (controller is null)
                {
                    continue;
                }

                if (!controller.IsOnline || !_lastSeen.TryGetValue(session.DeviceId, out var lastSeen) || now - lastSeen <= maxAge)
                {
                    continue;
                }

                // mDNS answers get lost; only drop a device that really is gone. An open cast
                // connection or an answering cast port means it is still there.
                if (controller.IsReceiverOpen || await IsReachableAsync(controller.Host, controller.Port).ConfigureAwait(false))
                {
                    _lastSeen[session.DeviceId] = DateTime.UtcNow;
                    continue;
                }

                _logger.LogInformation("Chromecast {Name} has not responded in {Age}, removing it from the cast menu until it is back", session.DeviceName, now - lastSeen);
                controller.MarkStale();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sweeping for stale Chromecast sessions");
        }
    }

    private static async Task<bool> IsReachableAsync(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        _loopCts.Cancel();
        _loopCts.Dispose();
        _locator.Dispose();
        _staleSweepTimer.Dispose();
        _addDeviceLock.Dispose();
    }
}
