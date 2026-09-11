using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Chromecast.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Chromecast;

/// <summary>
/// Adds Google Cast (Chromecast) support to Jellyfin's own "Play On" / remote-control cast
/// interface, so devices discovered on the local network can be selected from the existing cast
/// button in every Jellyfin client - including the iOS/iPadOS/macOS apps, which cannot use
/// Google's Chrome/Android-only Cast Sender SDK directly - and controlled the same way any other
/// Jellyfin client session is.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Chromecast";

    /// <inheritdoc />
    public override string Description => "Cast to Google Cast devices from Jellyfin's built-in cast interface, on every client including iOS/iPadOS/macOS.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("06afff54-6a74-4e08-9d50-9fa63803c73d");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
