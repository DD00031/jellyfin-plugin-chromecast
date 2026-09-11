using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chromecast.Configuration;

/// <summary>
/// Configuration for the Chromecast plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        DiscoveryIntervalSeconds = 30;
        DeviceStaleAfterSeconds = 120;
        DeviceNamePrefix = string.Empty;
        UseUnstableReceiver = false;
        AccessTokenLifetimeMinutes = 180;
        EnableDebugLogging = false;
    }

    /// <summary>
    /// Gets or sets how often (in seconds) the plugin scans the local network for Chromecast devices.
    /// </summary>
    public int DiscoveryIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets how long (in seconds) a Chromecast device may go unseen before its session is
    /// marked inactive and removed from the "Play On" device list.
    /// </summary>
    public int DeviceStaleAfterSeconds { get; set; }

    /// <summary>
    /// Gets or sets an optional prefix added to the device name shown in the Jellyfin cast menu,
    /// e.g. "Chromecast - " so it is easy to tell apart from other session types.
    /// </summary>
    public string DeviceNamePrefix { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to launch the "unstable" build of Jellyfin's cast
    /// receiver (app ID 6F511C87) instead of the default "stable" build (F007D354). Useful for
    /// testing receiver-side fixes; matches the same choice available to Chrome users under
    /// Settings &gt; Playback &gt; Google Cast version.
    /// </summary>
    public bool UseUnstableReceiver { get; set; }

    /// <summary>
    /// Gets or sets how long (in minutes) the access token minted for the receiver on cast start
    /// remains valid before it is force-revoked as a safety net, in case normal revocation on stop
    /// is missed (e.g. server restart mid-cast).
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether verbose Chromecast/CastV2 protocol logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; }
}
