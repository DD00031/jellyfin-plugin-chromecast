# Jellyfin Chromecast Plugin

Adds Google Cast (Chromecast) devices to Jellyfin's built-in "Play On" / cast interface - on
**every** Jellyfin client, including the iOS/iPadOS/macOS apps, which have no Google Cast SDK of
their own and can't use the Chrome-only cast button that jellyfin-web ships with.

> **Status: early development, not yet released.** Core casting - including transcoding H.265/HEVC
> sources to H.264 - is confirmed working end-to-end against real Chromecast hardware. Playback
> controls (pause/seek/stop) have a known reliability issue after the initial cast; see
> [CLAUDE.md](CLAUDE.md) for full technical background, current progress, and open questions.

## How it works

1. A background service discovers Chromecast devices on the local network (mDNS) and registers
   each one as a Jellyfin session, the same way the built-in DLNA "Play To" feature does. That's
   what makes them show up in the cast menu on every client, not just Chrome.
2. When you cast something, the plugin launches **Jellyfin's own cast receiver app** - the exact
   same one Chrome and Android users already cast to today - and hands off playback to it using
   the same protocol jellyfin-web's Chrome sender uses.
3. From there, the receiver does what it already does well for Chrome users: negotiates the right
   transcode/direct-play decision for the specific Chromecast model (so H.265/HEVC sources get
   transcoded to H.264 on hardware that can't decode HEVC, e.g. 3rd-gen Chromecast), handles
   subtitles, and reports playback progress back to your server.

This deliberately reuses Jellyfin's existing, battle-tested receiver instead of reimplementing
HLS/codec handling from scratch - see [CLAUDE.md](CLAUDE.md) for why.

## Requirements

- Jellyfin server **12.0** or later.
- A Chromecast (or other Google Cast device) on the same local network as your Jellyfin server.

## Installation

Not yet published. Build instructions are in [CLAUDE.md](CLAUDE.md#building).

## Configuration

Dashboard → Plugins → Chromecast:

| Setting | Description |
|---|---|
| Discovery interval | How often to scan the network for Chromecast devices. |
| Device timeout | How long a device may go unseen before it's removed from the cast menu. |
| Device name prefix | Optional text prepended to each device's name in the cast menu. |
| Use the unstable receiver build | Matches the "Google Cast version" option Chrome/web users have. |
| Access token safety-net lifetime | Fallback expiry for the token minted per cast session. |
| Verbose logging | Logs CastV2 protocol detail to the server log for troubleshooting. |

## License

GPL-3.0, matching the rest of the Jellyfin ecosystem. See [LICENSE](LICENSE).

Vendors a small, patched copy of [SharpCaster](https://github.com/Tapanila/SharpCaster) (MIT) -
see [external/Sharpcaster/PATCH.md](external/Sharpcaster/PATCH.md) for details.
