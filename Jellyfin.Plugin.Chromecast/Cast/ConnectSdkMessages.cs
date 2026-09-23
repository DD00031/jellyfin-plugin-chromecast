using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Chromecast.Cast;

/// <summary>
/// A status message broadcast by Jellyfin's cast receiver over the connectsdk namespace. Mirrors
/// the receiver's own <c>BusMessage</c> type (see jellyfin-chromecast's <c>types/global.d.ts</c>).
/// <see cref="Data"/> is a nested JSON object (confirmed against a live wire capture - not a
/// JSON-encoded string as its own field name might suggest), shaped like the receiver's
/// <c>getSenderReportingData()</c> output: <c>{ ItemId, PlayState: {...}, QueueableMediaTypes,
/// NowPlayingItem }</c>, where <c>PlayState</c> is what maps onto
/// <see cref="MediaBrowser.Model.Session.PlaybackProgressInfo"/>'s fields - not <c>Data</c> itself.
/// </summary>
public sealed class ConnectSdkStatusMessage
{
    /// <summary>
    /// Gets or sets the event name, e.g. "playbackstart", "playbackprogress", "playbackstop",
    /// "playstatechange", "error", "connectionerror".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a free-text message, used for error-type events.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the payload for the event - a <c>{ ItemId, PlayState, ... }</c> object for
    /// playback events. See the type-level remarks for why this isn't deserialized directly into
    /// <see cref="MediaBrowser.Model.Session.PlaybackProgressInfo"/>.
    /// </summary>
    public JsonElement? Data { get; set; }
}

/// <summary>
/// The options payload for the "PlayNow"/"PlayNext"/"PlayLast" receiver commands. Field names and
/// casing must match jellyfin-chromecast's <c>PlayRequest</c> interface exactly, since the
/// receiver deserializes this with the same case-sensitive-by-default JS/TS JSON handling used
/// throughout the rest of the connectsdk protocol.
/// </summary>
public sealed class PlayNowOptions
{
    /// <summary>
    /// Gets or sets the items to play, as full DTOs - the receiver reads item metadata directly
    /// from these rather than fetching them itself. Pre-serialized to <see cref="JsonElement"/>
    /// (with Jellyfin's normal PascalCase API casing, not this envelope's camelCase) by the
    /// caller, since each <c>BaseItemDto</c> must keep the exact field names/casing a real
    /// <c>GET /Items</c> response would use - the receiver's TS code reads these as genuine
    /// Jellyfin API DTOs, not as this envelope's own camelCase wrapper fields. Serializing a
    /// nested BaseItemDto through the same camelCase options as the rest of this envelope would
    /// silently rename every one of its properties and the receiver would fail to read them.
    /// </summary>
    public required IReadOnlyList<JsonElement> Items { get; set; }

    /// <summary>
    /// Gets or sets the position (in ticks) to start playback from.
    /// </summary>
    [JsonPropertyName("startPositionTicks")]
    public long? StartPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the media source id to play, if the item has more than one.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the audio stream index to play.
    /// </summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the subtitle stream index to display.
    /// </summary>
    public int? SubtitleStreamIndex { get; set; }
}

/// <summary>
/// The options payload for the "Seek" receiver command.
/// </summary>
public sealed class SeekOptions
{
    /// <summary>
    /// Gets or sets the target position, in seconds.
    /// </summary>
    public double Position { get; set; }
}

/// <summary>
/// The options payload for the "SetAudioStreamIndex"/"SetSubtitleStreamIndex" receiver commands.
/// Matches the receiver's <c>SetIndexRequest</c> interface (<c>types/global.d.ts</c>) - unlike
/// most other options payloads in this file, that interface uses a single lowercase <c>index</c>
/// field, not one that merely happens to camelCase the same way PascalCase would.
/// </summary>
public sealed class SetIndexOptions
{
    /// <summary>
    /// Gets or sets the stream index to select, or -1 to disable (subtitles only).
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }
}
