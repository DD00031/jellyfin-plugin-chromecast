# Jellyfin Chromecast Plugin

Cast from **any** Jellyfin app to a Chromecast, including the iPhone, iPad and Mac apps.

Jellyfin's own cast button only works in Chrome and on Android. This plugin runs on your Jellyfin
server instead, finds the Chromecasts on your network and adds them to the cast menu every Jellyfin
app already has. Pick one, press Play, and it plays on the TV.

> **Status: early public test release (0.1.3.0).** It works day to day on real Chromecast
> hardware with the iOS, macOS and web apps, but it is young. Please
> [open an issue](https://github.com/DD00031/jellyfin-plugin-chromecast/issues) if something
> doesn't work.

> **Built with AI.** This plugin was written with AI coding tools (Claude Code), directed and
> tested by a human on real hardware. The full development log, including every bug found and how
> it was fixed, is in [CLAUDE.md](CLAUDE.md). Read the code and judge for yourself before trusting
> it with your server.

## What it does

- Chromecasts show up in the cast menu of every Jellyfin app, with no setup on the phone or TV.
- Video the Chromecast can't play (H.265/HEVC on older models, for example) is converted
  automatically.
- Play, pause, seek, skip, volume, stop, the play queue, and switching audio or subtitle tracks all
  work from the app.
- Subtitles work, including `.srt` files that sit next to the video.
- The TV shows Jellyfin's "ready" screen when you connect, mirrors the movie page you're looking
  at, and goes back to its normal screen when you disconnect.

## What you need

- Jellyfin server **12.0**. Each plugin release is built for one exact server version; the current
  one is for 12.0.0.
- A Chromecast or other Google Cast device on the **same local network** as the Jellyfin server.

## Quick start

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add this repository:

   ```
   https://raw.githubusercontent.com/DD00031/jellyfin-plugin-chromecast/main/manifest.json
   ```

2. Open **Catalog**, find **Chromecast** under *General*, and install it.
3. Restart Jellyfin.
4. In any Jellyfin app, tap the cast icon. Your Chromecasts are in the list within about half a
   minute of the server starting.

## Using it

1. **Connect:** tap the cast icon and pick your Chromecast. The TV switches to Jellyfin's ready
   screen. With more than one Chromecast, the plugin opens the one you used last (see
   [Good to know](#good-to-know)).
2. **Browse:** open a movie or episode and the TV shows its details.
3. **Play:** press Play. Pick the audio and subtitle track on the item's page first, or change them
   while it plays.
4. **Stop:** the TV goes back to the ready screen, ready for the next thing.
5. **Disconnect:** tap the cast icon and choose Disconnect. Jellyfin closes on the TV.

## Settings

**Dashboard → Plugins → Chromecast.** The defaults are fine for most people.

| Setting | What it does |
|---|---|
| Discovery interval | How often the server looks for Chromecasts. Default: every 30 seconds. |
| Device timeout | How long a Chromecast may go unseen before it leaves the cast menu. Default: 2 minutes. |
| Device name prefix | Optional text put in front of each Chromecast's name in the cast menu. |
| Use the unstable receiver build | On by default. The stable version of Jellyfin's Chromecast app can't load subtitle files that sit next to the video yet. |
| Show the ready screen when you connect | On by default. Opens Jellyfin on the TV as soon as you connect, before you play anything. |
| Verbose logging | Off by default. Writes every discovery scan and every message to the server log; turn it on when reporting a problem. |

## Good to know

- **Session limits.** Every cast signs the Chromecast in to Jellyfin as the user who started it. If
  a user has a limit under *Dashboard → Users → (user) → Max number of simultaneous user
  sessions*, set it to 0 (unlimited) or leave room for 2 extra sessions per Chromecast, otherwise
  casting fails for that user.
- **More than one Chromecast.** Jellyfin apps don't tell the server which device you picked when
  you connect. The plugin opens the ready screen on the Chromecast you used last; if that was the
  wrong one, it closes again as soon as you open or play something on the right one. Turn off
  *Show the ready screen when you connect* if you'd rather not have that.
- **Subtitles drift after skipping forward or back** (mostly MP4 files). This is how Jellyfin
  streams files it has no keyframe list for, not the plugin. Fix it once on the server: stop
  Jellyfin, open `encoding.xml` in its config folder, add `<string>mp4</string>` inside
  `<AllowOnDemandMetadataBasedKeyframeExtractionForExtensions>`, start Jellyfin, then run
  **Dashboard → Scheduled Tasks → Keyframe Extractor** and give it a daily schedule.
- **"Google Cast unsupported" in the cast menu.** That line comes from the Jellyfin app itself and
  can't be removed by a plugin. Casting works regardless.
- **A Chromecast is missing from the menu.** The server and the Chromecast must be on the same
  network, and the server must not be behind a VPN that hides local devices. A Chromecast powered
  from the TV's USB port is off when the TV is off.
- **After updating Jellyfin**, the plugin needs a build for the new server version. See
  [Releasing](#releasing).

## Reporting a problem

Turn on **Verbose logging** in the plugin settings, reproduce the problem, and attach the relevant
part of the Jellyfin server log to a
[new issue](https://github.com/DD00031/jellyfin-plugin-chromecast/issues), along with your
Jellyfin version, Chromecast model, and which app you cast from.

---

# For developers

Everything below is about how the plugin works and how to build it. [CLAUDE.md](CLAUDE.md) is the
detailed engineering log: design decisions, protocol notes, and every bug found during testing.

## How it works

1. A background service discovers Chromecast devices on the local network (mDNS) and registers
   each one as a Jellyfin session, the same way the built-in DLNA "Play To" feature does. That's
   what makes them show up in the cast menu on every client, not just Chrome.
2. When you cast something, the plugin launches **Jellyfin's own cast receiver app** - the same
   one Chrome and Android users already cast to - and hands off playback to it using the same
   protocol jellyfin-web's Chrome sender uses.
3. From there, the receiver does what it already does for Chrome users: negotiates the right
   transcode/direct-play decision for the specific Chromecast model, handles subtitles, and
   reports playback progress back to the server.

This deliberately reuses Jellyfin's existing receiver instead of reimplementing HLS and codec
handling - see [CLAUDE.md](CLAUDE.md) for why.

## Building

```bash
dotnet build Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj
```

Requires the .NET 10 SDK. A build only loads on the exact Jellyfin version it was built against;
pass `-p:JellyfinServerVersion=<version>` to target another one.

## Releasing

Releases are cut by the **Release** workflow: **Actions → Release → Run workflow**, enter the exact
Jellyfin server version to build for, and optionally a plugin version and changelog. It bumps every
version file, builds, checks the result, publishes the GitHub release and adds it to
`manifest.json`, so servers using this repository see the update.

## License

GPL-3.0, matching the rest of the Jellyfin ecosystem. See [LICENSE](LICENSE).

Vendors a small, patched copy of [SharpCaster](https://github.com/Tapanila/SharpCaster) (MIT) -
see [external/Sharpcaster/PATCH.md](external/Sharpcaster/PATCH.md) for details.
