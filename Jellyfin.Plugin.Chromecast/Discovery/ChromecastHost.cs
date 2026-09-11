using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chromecast.Discovery;

/// <summary>
/// An <see cref="IHostedService"/> that owns the lifetime of the Chromecast discovery manager.
/// </summary>
public sealed class ChromecastHost : IHostedService, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChromecastHost> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly IServerApplicationHost _appHost;

    private ChromecastDiscoveryManager? _manager;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromecastHost"/> class.
    /// </summary>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    public ChromecastHost(
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        IServerApplicationHost appHost)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChromecastHost>();
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _appHost = appHost;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // An exception thrown out of IHostedService.StartAsync aborts startup of the entire
        // generic host - i.e. it would take down the whole Jellyfin server, not just this
        // plugin. Discovery setup touches the network (mDNS socket binding), so failures here
        // must be contained: casting simply won't be available rather than the server refusing
        // to start.
        try
        {
            _logger.LogInformation("Starting Chromecast discovery");

            _manager = new ChromecastDiscoveryManager(
                _loggerFactory,
                _sessionManager,
                _libraryManager,
                _userManager,
                _dtoService,
                _appHost);

            _manager.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Chromecast discovery - Chromecast casting will be unavailable");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _manager?.Dispose();
        _manager = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _manager?.Dispose();
        _disposed = true;
    }
}
