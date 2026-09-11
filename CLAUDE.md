# CLAUDE.md

Context for future work on this repo. Read this before making architectural changes - a lot of
the design here is the result of protocol-level research, not guesswork, and re-deriving it from
scratch would waste time (or reintroduce bugs already understood and avoided).

## What this is

A Jellyfin **server plugin** that adds Google Cast (Chromecast) devices to Jellyfin's built-in
"Play On" cast interface, on every client - critically including the iOS/iPadOS/macOS apps, which
have no Google Cast SDK of their own (Safari/WKWebView doesn't support Chrome's `chrome.cast` API
at all). The user's household is almost entirely Apple devices via the official apps, so a
solution that only works from Chrome (i.e. relying on jellyfin-web's existing cast button and
Google's Cast Sender SDK) doesn't solve their actual problem.

Secondary requirement: content that a Chromecast can't decode - specifically H.265/HEVC, which the
user confirmed 3rd-gen Chromecast hardware chokes on - must get transcoded automatically.

Target: **Jellyfin 12.0 only** (released September 2026). Not maintaining 10.x/11.x compatibility;
12.0's plugin ABI is a breaking change from 10.11.x anyway (EF Core rewrite, .NET 10, `Jellyfin.Api`
namespace reorg), so there's no realistic way to support both from one build.

## Why not just use Google's Cast Sender SDK?

That's what jellyfin-web already does for Chrome/Android users, and it already works fine for
them - nothing to build there. It's irrelevant for this user's clients because Safari/WKWebView
(which the iOS/iPadOS/macOS apps use for their web-based UI) has no Cast Sender SDK support at
all. A server-side plugin sidesteps this entirely: it speaks the CastV2 protocol directly from the
.NET server process over the LAN, so it doesn't matter what client (or client platform) initiated
the "cast" - the plugin only needs Jellyfin's own client-agnostic session/remote-control
mechanism (the same one DLNA "Play To" uses) to receive the command.

## History: the first attempt (`jellycast`, built for 10.11.x)

There's a previous prototype at <https://github.com/DD00031/jellycast> (also in this session's
memory as the starting point) that got the core idea half right and half wrong:

**Right:** registering each discovered Chromecast as a Jellyfin `SessionInfo` with a custom
`ISessionController`, exactly mirroring how DLNA PlayTo works. This is *why* it shows up in the
cast menu identically on every client - that menu lists `ISessionManager.Sessions`, not anything
Google-Cast-SDK-specific. This repo keeps that part of the design essentially unchanged
(`Discovery/ChromecastDiscoveryManager.cs`, `Discovery/ChromecastHost.cs`).

**Wrong:** it launched Google's generic **Default Media Receiver** (app ID `CC1AD845`) and
hand-built raw stream URLs (direct MP4 or HLS `master.m3u8`) for it to load via the standard
CastV2 `MediaChannel`. The user reported this was unreliable - "worked sometimes, mostly didn't."
The generic Default Media Receiver's HLS support is known in the wider Chromecast dev community to
be inconsistent, especially against older Chromecast firmware; reinventing codec negotiation and
stream URL construction outside of Jellyfin's own PlaybackInfo pipeline is also just a lot of
surface area for subtle bugs (see the receiver protocol section below for what this repo does
instead, and why it avoids that whole class of problem).

## The actual design: hand off to Jellyfin's own receiver

Jellyfin already ships a full cast receiver app - <https://github.com/jellyfin/jellyfin-chromecast>
- bundled with every Jellyfin server, and it's what Chrome/Android users already cast to today,
successfully, every day. Registered Cast Application IDs:

- **Stable**: `F007D354`
- **Unstable**: `6F511C87`

This receiver is a real Jellyfin client: it runs Google's CAF (Cast Application Framework) player
(far more mature HLS support than the generic Default Media Receiver), calls Jellyfin's own
`PlaybackInfo` API to decide transcode vs. direct-play using **its own accurate, per-device-model
codec support detection** (`components/codecSupportHelper.ts` in that repo probes real hardware
decode support at runtime) - which is exactly the mechanism that already correctly excludes HEVC
for hardware that can't decode it - and reports playback progress straight back to the server via
its own authenticated API calls.

So instead of reimplementing all of that (stream URLs, HLS, codec negotiation, subtitle handling),
**this plugin launches that same receiver and speaks the same protocol jellyfin-web's Chrome
sender already speaks to it.** The plugin's own code is deliberately thin: discover devices,
appear in the cast menu, launch the receiver, hand off a "play this" command, relay minimal status
back for the "Play On" UI. All the hard, previously-buggy parts are inherited from code that
already works correctly for Chrome/Android users.

### The `connectsdk` protocol

Everything beyond bare media transport goes over a custom CastV2 namespace, exactly:

```
urn:x-cast:com.connectsdk
```

(confirmed from the receiver's own source: `src/helpers.ts` `broadcastToMessageBus`,
`src/components/maincontroller.ts` line ~526).

**Sender → receiver** message shape (`Cast/ConnectSdkChannel.cs` `SendCommandAsync`):

```json
{
  "command": "PlayNow",
  "userId": "<guid>",
  "accessToken": "<jellyfin access token>",
  "serverAddress": "http://192.168.x.x:8096",
  "receiverName": "Living Room TV",
  "options": { }
}
```

`serverAddress` and `accessToken` are required on every message - the receiver calls
`JellyfinApi.setServerInfo(...)` on the *first* message it receives (regardless of which command),
which is also when it reports its device capabilities back to the server. There's no separate
handshake step needed; the first real command (typically `PlayNow`) does double duty.

Commands the receiver's `CommandHandler` supports (from `src/components/commandHandler.ts`):
`PlayNow`, `PlayNext`, `PlayLast`, `Shuffle`, `InstantMix`, `DisplayContent`, `NextTrack`,
`PreviousTrack`, `SetAudioStreamIndex`, `SetSubtitleStreamIndex`, `Identify`, `Seek`, `Stop`,
`PlayPause`, `Pause`, `Unpause`, `SetRepeatMode`. Volume/mute (`VolumeUp`/`VolumeDown`/
`Mute`/`Unmute`/`ToggleMute`/`SetVolume`) are explicitly **not** implemented receiver-side - the
receiver's own source says "handled on the sender" - so this plugin sends those over the
*standard* CastV2 receiver channel (`ReceiverChannel.SetVolume`/`SetMute`) instead, matching what
the Chrome sender does.

`PlayNow`/`PlayNext`/`PlayLast` options shape (`Cast/ConnectSdkMessages.cs` `PlayNowOptions`,
matching the receiver's `PlayRequest` interface):

```json
{
  "items": [ /* full BaseItemDto objects, not just IDs - the receiver reads metadata directly from these */ ],
  "startPositionTicks": 0,
  "mediaSourceId": "...",
  "audioStreamIndex": 1,
  "subtitleStreamIndex": null
}
```

The receiver keeps its **own** internal play queue from the full `items` list (see
`translateItems` in `maincontroller.ts`), so `NextTrack`/`PreviousTrack` are just forwarded
commands - this plugin does not track queue position itself.

**Receiver → sender** status broadcasts (`Cast/ConnectSdkMessages.cs` `ConnectSdkStatusMessage`,
matching the receiver's `BusMessage` type in `src/types/global.d.ts`):

```json
{ "type": "playbackstart" | "playbackprogress" | "playbackstop" | "playstatechange" | "error" | "connectionerror",
  "message": "...",
  "data": "<JSON-encoded PlaybackProgressInfo, itself a string, not a nested object>" }
```

Important: the receiver already calls Jellyfin's `reportPlaybackStart/Progress/Stopped` REST
endpoints **itself**, using its own access token, as a real authenticated client
(`components/jellyfinActions.ts`). It broadcasts the same data back over `connectsdk` purely so a
sender's own UI can mirror state - **this plugin does not need to, and does not, treat these
broadcasts as the source of truth for playback history/resume points.** It only uses them to keep
its synthetic `SessionInfo`'s displayed state (play/pause, position) in sync for the "Play On"
remote-control UI. This is a meaningful robustness improvement over the previous prototype: even
if this plugin's `ChromecastSessionController` crashes or reconnects mid-cast, the actual playback
record is unaffected because the receiver reports it independently.

### Access tokens

The receiver needs its own valid Jellyfin access token to make those API calls (it's a real
client, not a proxy). This plugin mints one server-side, scoped to the controlling user, via:

```csharp
ISessionManager.AuthenticateDirect(new AuthenticationRequest { UserId = ..., ... })
```

Confirmed by reading the actual server source
(`Emby.Server.Implementations/Session/SessionManager.cs`): `AuthenticateDirect` calls
`AuthenticateNewSessionInternal(request, enforcePassword: false)` - i.e. it looks the user up by
ID and skips password validation entirely. This is the same category of trusted, in-process
mechanism used elsewhere in Jellyfin for "phantom" devices; a server-side plugin runs with full
server trust, so this is the intended pattern, not a workaround. The token is revoked
(`ISessionManager.Logout(token)`) when the cast session stops, disconnects, or errors, with a
configurable fallback expiry as a safety net (`PluginConfiguration.AccessTokenLifetimeMinutes`) in
case revocation is missed (e.g. a server restart mid-cast).

## Vendored SharpCaster

`external/Sharpcaster/` is a trimmed, lightly patched copy of
<https://github.com/Tapanila/SharpCaster> (MIT license) used for the CastV2 protocol, mDNS
discovery (`ChromecastLocator`, via the `Zeroconf` package), and low-level media control.

**Why vendored instead of a plain NuGet package reference:** `ChromeCastClient` only wires up a
fixed, hardcoded set of built-in channels in its constructor (Connection, Heartbeat, Receiver,
Media, MultiZone, Spotify) with no public API to register an additional channel for a custom
namespace - and reaching the `connectsdk` namespace is required for everything described above.
The patch adds exactly one method (`AddChannel`); see `external/Sharpcaster/PATCH.md` for the full
rationale and the option of upstreaming it later to drop the vendoring.

Note for future reference: this is a *different* reason than what the previous `jellycast`
prototype's vendoring comment suggested (it cited a RID-specific runtime-asset conflict). That
issue is real too and is handled the same way here (`CopyLocalRuntimeTargetAssets=false` in the
plugin `.csproj`, plus a post-publish target stripping `Microsoft.Extensions.*` assemblies that
Jellyfin's host already provides - see the comments in `Jellyfin.Plugin.Chromecast.csproj` for
why; a mismatched copy of those breaks the plugin loader's reflection-based `GetTypes()` scan with
a `TypeLoadException` and silently disables the whole plugin). Both problems are handled; they're
independent of each other.

## Project layout

```
Jellyfin.Plugin.Chromecast/
  Plugin.cs, PluginServiceRegistrator.cs      - plugin entry points
  Configuration/                              - PluginConfiguration.cs, configPage.html (dashboard settings UI)
  Discovery/
    ChromecastHost.cs                         - IHostedService owning the discovery manager's lifetime
    ChromecastDiscoveryManager.cs             - mDNS scan loop, registers/retires SessionInfo per device
  Cast/
    ConnectSdkChannel.cs                      - custom CastV2 channel for urn:x-cast:com.connectsdk
    ConnectSdkMessages.cs                     - DTOs for the connectsdk message shapes above
  Session/
    ChromecastSessionController.cs            - ISessionController: Jellyfin commands -> connectsdk commands,
                                                 connectsdk status -> Jellyfin session state
external/Sharpcaster/                         - vendored + patched SharpCaster (see PATCH.md)
```

## Current status (as of this writing)

**The plugin builds successfully** against real Jellyfin.Controller/Model/Data 12.0.0 packages
(`dotnet build Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj`). Several API surfaces
changed from what the pre-EF-Core-rewrite `jellycast` prototype assumed - resolved by decompiling
the actual 12.0 NuGet packages rather than guessing (see git log for specifics): `MediaType` moved
to `Jellyfin.Data.Enums`; `User.HasPermission`/`IsAdministrator` is no longer a direct member but
an extension method (`Jellyfin.Data.UserEntityExtensions.HasPermission`) against
`Jellyfin.Database.Implementations.Enums.PermissionKind`; `ISessionManager.ReportCapabilities`
gained a second (controlling) session id parameter.

**Not yet verified:** the plugin has not been loaded into an actual running Jellyfin server yet
(the test instance is still 10.11.6 - see below), and a full cast has never been attempted.

### Chromecast discovery: works at the OS level, not yet from .NET on this dev machine

Confirmed the target Chromecast is discoverable via macOS's native `dns-sd -B _googlecast._tcp
local.` - found immediately. But a standalone test using both SharpCaster's `ChromecastLocator`
*and* the raw `Zeroconf` NuGet package directly (bypassing our code entirely) found nothing, even
with a generous 10s timeout. This points to something rejecting/dropping multicast for .NET-built
binaries specifically on this machine - possibly macOS's Local Network privacy permission (TCC)
not being granted to the ad-hoc-signed `dotnet build` output (each rebuild can produce a "new"
unrecognized binary identity, unlike a stable, already-approved app), or another local
socket/entitlement restriction. The user confirmed the test Jellyfin server is a **native macOS
install on this same machine** (not Docker), so this isn't a container-networking problem, but it
does mean whatever is blocking .NET's multicast here could plausibly also affect the real
`jellyfin` server process once the plugin is loaded into it - this needs to be re-tested against
the actual server, not just a throwaway console app, before concluding anything. Worth checking:
System Settings → Privacy & Security → Local Network for an entry for `dotnet`/`jellyfin`/Terminal
that needs enabling.

If this turns out to be a genuine, unfixable-from-inside-the-process macOS restriction (unlikely
for an unsandboxed native install, but not yet ruled out), a fallback worth considering is
shelling out to `dns-sd`/`avahi-browse` as an alternative discovery mechanism - but do not reach
for that until discovery is actually confirmed broken *inside the real Jellyfin server process*,
since this dev-machine console-app test may simply not be representative.

### Next steps, in order

1. **The test Jellyfin instance is currently running 10.11.6, not 12.0.** A 12.0-targeted plugin's
   assembly version requirements mean it will not load there at all (see the `JellyfinServerVersion`
   comment in the `.csproj` - Jellyfin plugin assemblies aren't forward/backward compatible across
   server versions). The user needs to upgrade that instance before the plugin can be loaded and
   tested end-to-end; flagged, not yet resolved as of this writing.
2. An API key for the test instance (`http://192.168.2.14:8096`) was provided during development
   for diagnostics. **Do not commit it to this repo** - treat it as a local secret only.
3. Once on 12.0: install the plugin, confirm it loads (check the server log for the
   `PluginServiceRegistrator`/`ChromecastHost` startup messages), and confirm discovery actually
   finds the Chromecast from inside the real server process.
4. Then: verify a cast actually starts the receiver and plays back, verify an H.265 source
   transcodes correctly, verify play/pause/seek/stop/volume all round-trip correctly, verify the
   minted access token is revoked on stop.
5. Still worth confirming once real casting works: exact JSON field casing Jellyfin's API uses for
   `PlaybackProgressInfo` in the connectsdk status broadcasts (assumed PascalCase, matching .NET's
   default `System.Text.Json` behavior - not yet confirmed against a live wire capture), and that
   `F007D354`/`6F511C87` are launchable by an arbitrary CastV2 sender (should be, per Google's
   "Custom application" registration model having no sender allowlist, but blocked on discovery
   working first to actually reach a device to launch on).

## Building

```bash
dotnet build Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj
```

To build against a different server patch version than the default in the `.csproj`:

```bash
dotnet build -p:JellyfinServerVersion=12.0.1 Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj
```

Plugin assemblies are **not** forward/backward compatible across Jellyfin server patch versions -
a build against 12.0.0 will fail to load with a bare assembly-load error against 12.0.1 and vice
versa. Always match the exact running server version.

## Git / GitHub

Public repo, pushed after each meaningful chunk of work per the user's request. Remote should be
created under the `DD00031` GitHub account (same as the previous `jellycast` prototype) via `gh
repo create`.
