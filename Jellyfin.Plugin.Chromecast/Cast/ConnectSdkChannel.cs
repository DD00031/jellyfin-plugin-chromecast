using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharpcaster.Channels;

namespace Jellyfin.Plugin.Chromecast.Cast;

/// <summary>
/// The custom CastV2 channel Jellyfin's own cast receiver (app ID F007D354/6F511C87) uses for
/// everything beyond basic media transport: starting playback by item, seek/pause/stop commands,
/// and status broadcasts sent back to the sender. This is the exact namespace and JSON message
/// shape jellyfin-chromecast's <c>maincontroller.ts</c>/<c>commandHandler.ts</c> implement, and
/// what jellyfin-web's Chrome sender already speaks - implementing the same protocol here lets
/// this plugin hand off to that same battle-tested receiver instead of re-implementing HLS/codec
/// negotiation, which is the likely source of the reliability problems in the previous prototype.
/// See CLAUDE.md for the full message reference.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConnectSdkChannel"/> class. Passes
/// <c>useBaseNamespace: false</c> because the base class otherwise prefixes the namespace with
/// "urn:x-cast:com.google.cast." - the receiver's actual namespace is exactly
/// "urn:x-cast:com.connectsdk", not a sub-namespace of Google's own.
/// </remarks>
/// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
public sealed class ConnectSdkChannel(ILogger<ConnectSdkChannel> logger)
    : ChromecastChannel("urn:x-cast:com.connectsdk", logger, useBaseNamespace: false)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Raised when the receiver broadcasts a status message (playbackstart, playbackprogress,
    /// playbackstop, playstatechange, error, connectionerror, ...).
    /// </summary>
    public event EventHandler<ConnectSdkStatusMessage>? StatusReceived;

    /// <summary>
    /// Sends a command to the receiver in the same shape jellyfin-web's Chrome sender uses:
    /// <c>{ command, userId, accessToken, serverAddress, receiverName, options }</c>.
    /// </summary>
    /// <param name="command">The receiver-side command name, e.g. "PlayNow", "Seek", "Stop".</param>
    /// <param name="userId">The controlling user's id.</param>
    /// <param name="accessToken">A Jellyfin access token the receiver can authenticate with.</param>
    /// <param name="serverAddress">The base URL of the Jellyfin server, reachable from the receiver.</param>
    /// <param name="receiverName">A friendly name for the receiver to report itself as.</param>
    /// <param name="options">The command-specific payload (e.g. <see cref="PlayNowOptions"/>).</param>
    /// <param name="transportId">
    /// The running application's transport id (from the LaunchApplicationAsync/status response's
    /// <c>Application.TransportId</c>). CastV2 messages default to the "receiver-0" platform
    /// destination, which only the receiver *platform* listens on - a message meant for the
    /// running app (which is what the Jellyfin cast receiver is) is silently dropped unless it is
    /// addressed to that app's own transport id instead. SharpCaster's own <c>MediaChannel</c>
    /// does the same for every message it sends.
    /// </param>
    public Task SendCommandAsync(string command, Guid userId, string accessToken, string serverAddress, string receiverName, object? options, string transportId)
    {
        var config = Plugin.Instance?.Configuration;
        var maxBitrate = config is { SkipSpeedTest: true } ? Math.Clamp(config.StreamingBitrateMbps, 1, 500) * 1_000_000L : (long?)null;
        var json = BuildEnvelope(command, userId, accessToken, serverAddress, receiverName, options, maxBitrate);
        Logger?.LogDebug("connectsdk -> {TransportId}: {Json}", transportId, json);
        return SendAsync(json, transportId).WithTimeoutAsync(CastTimeout.Command, $"The \"{command}\" command");
    }

    /// <summary>
    /// Builds the JSON the receiver expects. <paramref name="maxBitrate"/> (bits per second), when
    /// set, makes the receiver use that figure instead of measuring the connection itself - its
    /// measurement downloads up to 10 MB and takes 4 to 8 seconds before the first playback.
    /// </summary>
    internal static string BuildEnvelope(string command, Guid userId, string accessToken, string serverAddress, string receiverName, object? options, long? maxBitrate)
    {
        var envelope = new
        {
            command,
            userId,
            accessToken,
            serverAddress,
            receiverName,
            maxBitrate,
            options = options ?? new { }
        };

        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    /// <inheritdoc />
    public override void OnMessageReceived(string messagePayload, string type)
    {
        Logger?.LogDebug("connectsdk <- (type={Type}): {Message}", type, messagePayload);
        try
        {
            var status = JsonSerializer.Deserialize<ConnectSdkStatusMessage>(messagePayload, SerializerOptions);
            if (status is not null)
            {
                StatusReceived?.Invoke(this, status);
            }
        }
        catch (JsonException ex)
        {
            Logger?.LogDebug(ex, "Ignoring malformed connectsdk message from receiver: {Message}", messagePayload);
        }
    }
}
