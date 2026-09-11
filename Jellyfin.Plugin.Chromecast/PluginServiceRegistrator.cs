using Jellyfin.Plugin.Chromecast.Discovery;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Chromecast;

/// <summary>
/// Registers the background service that discovers Chromecast devices and exposes them as
/// controllable Jellyfin sessions.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<ChromecastHost>();
    }
}
