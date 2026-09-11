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

        _locator = new ChromecastLocator(loggerFactory.CreateLogger<ChromecastLocator>());

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
        _ = Task.Run(() => RunDiscoveryLoopAsync(interval, _loopCts.Token));
    }

    private async Task RunDiscoveryLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var receivers = await _locator.FindReceiversAsync().ConfigureAwait(false);
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
        var config = GetConfig();
        var deviceName = string.IsNullOrEmpty(config.DeviceNamePrefix) ? receiver.Name : config.DeviceNamePrefix + receiver.Name;

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
            SupportedCommands = new[]
            {
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

    private void SweepStaleDevices(object? state)
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

                if (_lastSeen.TryGetValue(session.DeviceId, out var lastSeen) && now - lastSeen > maxAge)
                {
                    _logger.LogInformation("Chromecast {Name} has not responded to discovery in {Age}, marking session inactive", session.DeviceName, now - lastSeen);
                    controller.MarkStale();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sweeping for stale Chromecast sessions");
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
        _loopCts.Cancel();
        _loopCts.Dispose();
        _locator.Dispose();
        _staleSweepTimer.Dispose();
        _addDeviceLock.Dispose();
    }
}
