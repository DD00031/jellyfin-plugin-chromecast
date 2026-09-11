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

**Casting is confirmed working end-to-end against real hardware**, including the core goal of this
whole project: an H.265/HEVC source correctly transcodes to H.264 server-side and plays back
successfully on a real 2015-era Chromecast. Verified by casting to two real devices ("Woonkamer
TV", "Eettafel TV") from the local (now 12.0) test Jellyfin server on the user's Mac. Both a plain
H.264 item and an HEVC item were cast, watched playing on the actual TV, and confirmed via the
`/Sessions` API (`PlayMethod: Transcode`, advancing `PositionTicks`, correct ffmpeg
`-codec:v:0 libx264` invocation for the HEVC source - visible in the server log).

### Bugs found only by testing against a real device (not catchable by compiling alone)

1. **CastV2 messages must target the launched app's transport id, not "receiver-0".** The default
   CastV2 destination (`Sharpcaster.DefaultIdentifiers.DESTINATION_ID = "receiver-0"`) is the
   platform-level channel; a message meant for the *running app* (our launched receiver) is
   silently dropped unless addressed to that app's own `TransportId` from the launch/status
   response. SharpCaster's own `MediaChannel` always does this; the connectsdk channel didn't.
   Fixed by threading `transportId` through `ConnectSdkChannel.SendCommandAsync` and
   `ChromecastSessionController` (captured from `LaunchApplicationAsync`'s response, stored in
   `_appTransportId`, cleared on disconnect/invalidate).
2. **Mixed JSON casing requirement.** The outer connectsdk envelope and `PlayRequest`-shaped
   options (`command`, `userId`, `startPositionTicks`, `mediaSourceId`, ...) are camelCase (plain
   JS/TS convention), but the nested `items` array must stay in Jellyfin's native PascalCase - the
   receiver reads each one as a genuine `BaseItemDto`, identical to a real `GET /Items` response.
   Serializing everything through one camelCase `JsonSerializerOptions` silently renamed every
   field on every item, and the receiver just never got usable metadata (no error, no crash - it
   would have failed some `item.Name`/`item.Id`-style property access client-side with nothing
   reported back over connectsdk). Fixed by pre-serializing each item to a `JsonElement` with
   default (PascalCase) options before it goes into the outer camelCase envelope - a `JsonElement`
   already embedded in the object tree is written out verbatim, bypassing the outer naming policy.
3. **Uncaught exceptions inside `SendPlayCommand`/`SendPlaystateCommand` vanish silently.**
   Jellyfin's `SessionManager` does not appear to await/observe the `Task` an `ISessionController`
   returns (commands are presumably broadcast to every session controller without waiting on each
   one individually) - a fault thrown from either method disappears with **no log output
   whatsoever**, which is exactly what turned a real, later-confirmed exception (a write to a dead
   `SslStream`) into what looked for a long time like a total, unexplainable silent hang. Both
   methods now wrap their core logic in try/catch and log any exception explicitly. **Any
   `ISessionController` method that does real I/O should assume its return value is never
   observed by the caller and must handle/log its own failures.**
4. **No timeout on SharpCaster's request/response matching.** `WaitingTasks` (keyed by CastV2
   `requestId`) has no built-in timeout - if a response never arrives, the awaiting call hangs
   forever with no exception. `LaunchApplicationAsync` is now wrapped with an explicit 10s
   `Task.WhenAny`-based timeout.
5. **A failed send should invalidate the cached connection, not just log and move on** - otherwise
   every subsequent command keeps failing against the same known-dead `ChromecastClient`. Added
   `InvalidateConnection()` (nulls `_client`/`_connectSdkChannel`/`_appTransportId`), called from
   every catch block that touches the network. A fresh `SendPlayCommand` afterwards correctly
   reconnects and relaunches from scratch (confirmed).
6. **Class library projects don't copy-local their full transitive dependency closure by
   default** - only apps do. A plain `dotnet build` of this plugin was silently missing
   `Zeroconf.dll`/`Google.Protobuf.dll`/`System.Reactive.dll` (Sharpcaster's own dependencies,
   pulled in via the vendored `ProjectReference`) from the output folder, discovered only by
   actually deploying and checking the plugin folder contents against the server log's "Loaded
   assembly" lines. Fixed with `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
   in the plugin `.csproj`.

### RESOLVED: the CastV2 control connection didn't reliably survive between commands

Confirmed fixed by testing: cast → wait 38s (well past the previous ~20-30s failure window) →
`Pause` → **actually paused on the TV** → `Unpause` → `Stop` → **actually stopped on the TV**,
with the minted access token correctly revoked (`Logging out access token` in the server log) on
`Stop`. Also confirmed: the "Play On" session's live `PlayState` (position, pause state, ...) now
populates correctly during playback, which it never reliably did before either - same root cause.

The actual root cause turned out to be completely different from what the symptom ("dead SslStream
write error" a while after casting) suggested, and had nothing to do with heartbeat timing as
such. Real Chromecast hardware sends unsolicited `MEDIA_STATUS` broadcasts (CAF's own internal
media session status - the receiver uses `cast.framework.PlayerManager` internally, so this
happens regardless of anything this plugin's own connectsdk protocol does). One such broadcast had
a `"requestId"` value that didn't fit `System.Text.Json`'s deserialization target
(`MessageWithId.RequestId` is a plain `System.Int32`), throwing a `JsonException` **outside** the
one per-message try/catch that existed in SharpCaster's receive loop. That exception propagated
all the way out to the try/catch wrapping the *entire* `while(true)` receive loop, whose `catch`
just logs and lets the loop end - silently, permanently. Every subsequent inbound message was lost
from that point on, including this same client's own heartbeat PONG replies. The "heartbeat
timeout" disconnect actually observed ~20-30s later wasn't a real heartbeat failure - it was this
client finally noticing, on its own 10s+10s timer, a symptom of the receive loop having already
died seconds after the cast started.

This was hard to see for a long time because `new ChromecastClient()` (the parameterless
constructor, which is what this plugin was using) passes a **null logger** through to every
built-in SharpCaster channel, silencing all of its own diagnostics - heartbeat ping/pong, and
critically, the receive-loop error itself. Debugging only became tractable after (a) passing a
real `ILogger<ChromecastClient>` in `EnsureConnectedAsync`, and (b) temporarily raising the
`Sharpcaster`/`Jellyfin.Plugin.Chromecast` log categories to `Debug` in
`config/logging.default.json` (reverted afterward - don't leave verbose logging on by default).

Fixes (see `external/Sharpcaster/PATCH.md` patch 4 for the main one, patch 3 for a secondary real
bug found along the way that didn't turn out to be the cause):
- **The actual fix**: wrap each message's processing in the receive loop in its own try/catch
  instead of one try/catch around the whole loop, so a single malformed/unexpected message can
  never take the entire connection down.
- `HeartbeatChannel.TimerElapsed` now restarts its own timer after sending an outbound ping
  (`AutoReset = false` meant the "did we get a response" check could otherwise never run - a real,
  separate, confirmed bug, just not the one causing this particular symptom).
- `HeartbeatChannel` gained a settable `AdditionalDestinationId` so heartbeat traffic can also
  target a launched app's own transport id, not just `"receiver-0"` - speculative hardening, not
  confirmed necessary once the real fix was in, but cheap and plausible for other devices/firmware.
- The plugin now passes a real logger into `new ChromecastClient(...)`, and keeps a periodic
  lightweight keep-alive (`ConnectionChannel.ConnectAsync` + `ReceiverChannel.GetChromecastStatusAsync`
  every 5s) to the app's transport while a cast is active, on top of the heartbeat fixes - belt and
  suspenders, not the load-bearing fix, but reasonable to keep.
- `ConnectSdkStatusMessage.Data` was modeled as a JSON-encoded *string*; a live wire capture showed
  it's actually a nested JSON *object* shaped like the receiver's `getSenderReportingData()` output
  (`{ ItemId, PlayState: {...}, QueueableMediaTypes, NowPlayingItem }`), where `PlayState` - not
  `Data` itself - is what maps onto `PlaybackProgressInfo`. Every status broadcast was silently
  failing to parse ("malformed connectsdk message") until this was fixed, independent of the
  receive-loop issue - this is why session `PlayState` visibility didn't work even in the brief
  windows where the connection happened to still be alive.

### macOS-specific testing gotchas (irrelevant to real deployment, but costly if hit again)

- **`/System/Restart` (the REST API and Dashboard "Restart" button) is an in-process soft restart
  on this platform/build, not a real process restart.** The `jellyfin` OS process keeps the same
  PID across it. This matters enormously for plugin development: swapping the plugin `.dll` in the
  filesystem and hitting "Restart" does *not* replace already-instantiated long-lived objects like
  an existing `ChromecastSessionController` for an already-known device - `ISessionManager`'s
  session list is not cleared by a soft restart, so an old session just gets `UpdateReceiver`
  called on it forever, keeping the code and captured state from whenever it was first created.
  This produced a very confusing multi-hour debugging detour that looked like a mysterious silent
  hang with no explanation, when the actual code was fine and just wasn't running. **To actually
  test a new build, kill the real OS process and relaunch the app - don't rely on `/System/Restart`:**
  ```bash
  CURPID=$(ps aux | grep "jellyfin --webdir" | grep -v grep | awk '{print $2}')
  kill "$CURPID"   # if this leaves an orphaned "Jellyfin Server" wrapper process, `pkill -f Jellyfin` instead
  open -a "/Applications/Jellyfin.app"
  ```
- Chromecast **discovery** specifically (mDNS/Zeroconf, and even a bare `TcpClient` connect) does
  *not* work from an ad-hoc-signed standalone `dotnet run`/`dotnet build` console binary on this
  Mac, even though native tools (`dns-sd`) and - critically - **the real, properly-installed
  `Jellyfin.app` process itself** can reach the same devices without issue. If you need a
  standalone repro outside the actual plugin, this will waste your time; it did here (a long
  detour through macOS Local Network/TCC permissions and Info.plist `NSBonjourServices` theories
  that were ultimately irrelevant - the real Jellyfin server was never actually affected). Test
  inside the real plugin/server instead of a scratch console app.
- Binary string-searching a compiled `.dll` to verify what code shipped (`strings -a`, or a raw
  UTF-16LE decode) is **not reliable** for confirming build content one way or the other on this
  toolchain/version - several genuinely-present, plainly-literal strings didn't turn up under
  either method for reasons never fully explained (not an encoding issue - a proper UTF-16LE
  decode was tried too). Use a real behavioral signal instead - a raw `File.AppendAllText` probe
  actually invoked at runtime (as a last resort; remove it once done) is far more trustworthy than
  inspecting the assembly's bytes.
- Also worth knowing: `dotnet build-server shutdown` exists and stops the persistent Roslyn/MSBuild
  compiler server process, in case of *actual* stale-incremental-build suspicions (this was tried
  during the same debugging detour above; it turned out not to be the cause that time, but is a
  reasonable thing to reach for if a rebuild seems to not be taking effect).

### An API key was used for local testing - do not commit it

An API key for the local test instance (`http://192.168.2.14:8096`) was used during this session
for diagnostics and for driving test casts via the REST API directly. It is not stored in this
repo and must never be committed - treat any such key as a local secret only.

### RESOLVED: status broadcasts silently failed to parse (dashless Guid format)

Found via real-world testing from the actual iOS app (not just direct REST API calls): the user
reported (a) the "Play On" remote-control screen showed playback controls that worked (play,
pause, volume) but no title/artwork for what was casting, and (b) seeking always reset to 0:00
instead of landing on the target position, and the skip buttons did nothing.

Root cause for (a): **every single incoming connectsdk status broadcast was silently failing to
deserialize**, logged only at Debug level ("Could not parse playback status") so invisible by
default. `System.Text.Json`'s built-in `Guid` converter only accepts the canonical dashed format -
the receiver's own reporting writes item ids in Jellyfin's dashless "N" format (e.g.
`"b565cf7176b943ceb165fece9b26181f"`), which it rejects outright, unlike the more permissive
`Guid.Parse(string)`. Added `FlexibleGuidConverter` (delegates to `Guid.Parse`) and
`JsonStringEnumConverter` (needed for `"PlayMethod":"Transcode"`-style string enums, the next
thing that would have failed) to `ApiJsonOptions`. This session's own `OnPlaybackStart`/`Progress`
had never successfully fired even once before this fix - `NowPlayingItem`/`PlayState` stayed
empty on *this* session the entire time, even though the receiver's own separate,
directly-authenticated session (see "two sessions per cast" below) kept reporting real state
correctly under its own identity throughout. Confirmed fixed: casting now populates
`NowPlayingItem`/`PlayState` on this plugin's own session correctly.

(b) turned out to be the *same* bug, not a separate one: `SendPlaystateCommand`'s `Seek` case
was always correctly implemented, but the whole session's state was invisible/untrusted before
this fix, and earlier ad-hoc curl testing had also been unknowingly hitting a second, dead/stale
session for the same device (see below) rather than a real seek failure. Confirmed fixed against
real hardware post-fix: seeking a ~592s video to 3:00 landed correctly at 3:00, not 0:00. The skip
forward/back buttons route through the same `Seek` command, so should be fixed too, though not yet
explicitly re-tested with real button presses (only via direct `SeekPositionTicks` API calls).

### Two Jellyfin sessions exist per cast (not a bug - documented for context)

Every cast currently creates **two** separate Jellyfin sessions for the same physical device:
1. This plugin's own synthetic `SessionInfo` (`DeviceId: chromecast-<hash>`,
   `SupportsMediaControl: true`) - what shows up in the "Play On" cast menu and receives
   Play/Playstate commands.
2. The receiver's **own** session, created automatically the moment it authenticates with the
   token this plugin mints for it (`DeviceId: base64(receiverName)` - confirmed:
   `JellyfinApi.setServerInfo` in jellyfin-chromecast's own source sets
   `this.deviceId = btoa(receiverName)` when a `receiverName` is provided, which this plugin
   always does). This session is not attached to any `ISessionController` and doesn't support
   media control (`SupportsMediaControl: false`) - it exists purely because the receiver is a
   real, independently-authenticated Jellyfin client making its own API calls (which is
   deliberate - see "The actual design" above for why).

Architecturally inherent, not a bug to fix outright - Chrome's own official casting flow has
exactly the same duality (the receiver has always created its own session there too), it's just
invisible normally because Chrome's cast icon never relied on Jellyfin's own session list at all.
Both sessions now correctly show live, matching `NowPlayingItem`/`PlayState` after the parsing fix
above (confirmed side by side in the `/Sessions` API during testing).

### RESOLVED: "pick up an in-progress cast from a second client" didn't work

This was the same root cause as the parsing bug above, confirmed by a real multi-device test: cast
something from the phone, confirm via `/Sessions` that this plugin's own session (`#1` above) has
correct live state, then open the Mac's Jellyfin app and select the same Chromecast - **it now
correctly shows the remote-control screen with the in-progress item already loaded**, instead of
prompting to start something new. Before the parsing fix, session #1 (the one any client actually
queries/joins) had no live state to show at all, which looked indistinguishable from "nothing is
playing here" to a client trying to join. No code changes were needed beyond the parsing fix
already committed.

### RESOLVED: adding to the queue crashed the whole stream

`SendPlayCommandCoreAsync` always launched a fresh receiver app instance and sent "PlayNow"
regardless of what `PlayRequest.PlayCommand` actually asked for - so `PlayNext`/`PlayLast` (how
Jellyfin represents "add to queue") relaunched and restarted the whole session exactly like a
brand new cast, destroying whatever was already playing. Fixed: `PlayNext`/`PlayLast` now go
through a new `SendQueueCommandAsync` that reuses the existing connection/app instance (no
relaunch) and sends the matching `"PlayNext"`/`"PlayLast"` receiver command, only when something
is already actively casting (otherwise logs a warning and no-ops). Confirmed by testing: adding an
item to the queue while something else plays no longer interrupts playback.

### RESOLVED: the receiver's "ready to cast" screen stopped appearing

The receiver only shows its own branded waiting/idle screen
(`DocumentManager.setAppStatus(Waiting)` in its source) in response to an explicit `"Identify"`
command - this plugin never sent one, so a fresh launch went straight from blank to loading media
with no visible "connected" moment. This wasn't a real regression from earlier working behavior;
what the user recalled seeing was residual receiver state left over from a *previous* test's
natural idle transition during the heavy back-to-back testing earlier in this session - once the
device was rebooted to a clean state, the gap became visible. Fixed: `"Identify"` is now sent
right after launch, before `"PlayNow"`. Confirmed by testing: the ready screen now flashes briefly
before playback starts.

### Investigated, not fixable from this plugin: "(Google Cast Unsupported)" text in the cast menu

Confirmed via jellyfin-web's own source (`src/components/playback/playerSelectionMenu.js`): this
text is shown whenever `pluginManager.plugins` (a purely client-side JS registry) has no entry
with `id === 'chromecast'` - which is the *built-in*, Chrome-only `chromecastPlayer` JS plugin
(`src/plugins/chromecastPlayer/plugin.js`, `this.id = 'chromecast'`), which only ever registers
itself when `window.chrome.cast` (Google's own Cast Sender SDK) is present. This check is
completely independent of whether any actual cast targets are available - it will show this text
in Safari/WKWebView (and therefore the iOS/macOS apps) regardless of what this plugin does,
because there is no bridge for a server-side C# plugin to register anything in that client-side
registry. A real fix would mean patching jellyfin-web itself (e.g. changing that condition to also
check for available cast-type targets) and rebuilding/distributing a patched jellyfin-web - a
legitimately separate project, out of scope here. Purely cosmetic otherwise: casting itself is
unaffected.

### Still open

- Test multi-item queue playback more thoroughly (`NextTrack`/`PreviousTrack` navigation through a
  queue built via repeated `PlayLast` calls - the *adding* half is now confirmed working, the
  *playing through it* half isn't yet explicitly re-tested since the queue-add fix).
- Decide whether the `HeartbeatChannel.AdditionalDestinationId` patch and the plugin's own 5s
  keep-alive timer are still worth keeping now that the real fix (patch 4, the receive-loop
  try/catch) is in - they were reasonable hardening added while still hypothesizing about the
  cause, not proven necessary on their own. Low priority to revisit; they're harmless as-is.
- Clean up the `NU1510` NuGet warnings in the vendored `Sharpcaster.csproj` (harmless, low
  priority).
- Consider proposing the four Sharpcaster patches upstream (see `external/Sharpcaster/PATCH.md`)
  to eventually drop the vendoring.
- Write real user-facing installation instructions (README currently just points here) once ready
  to cut an actual release/manifest entry.
- The Jellyfin dashboard's own "Stop" button was reported not working during testing - the user
  confirmed this is a pre-existing Jellyfin UI issue unrelated to this plugin (Stop via the
  `/Sessions/{id}/Playing/Stop` API works correctly, confirmed repeatedly).

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
