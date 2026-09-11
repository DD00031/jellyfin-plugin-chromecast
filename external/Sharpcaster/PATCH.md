# Vendoring note

This is a vendored copy of the `Sharpcaster` library project from
[Tapanila/SharpCaster](https://github.com/Tapanila/SharpCaster) (MIT license, see `LICENSE.txt`),
trimmed to just the library itself (no tests/samples/CI), with **four small patches**, all found
by actually casting to real hardware - none were obvious from reading the code alone. Patch 4 is
the one that actually explains and fixes the "connection doesn't survive between commands" issue
CLAUDE.md describes at length - patches 2 and 3 are real, confirmed bugs found along the way, but
turned out not to be the cause of that particular symptom.

## Why vendored instead of the NuGet package

Jellyfin's own cast receiver app (the one already used by every Chrome-casting Jellyfin user)
expects commands over a custom CastV2 namespace, `urn:x-cast:com.connectsdk`, in addition to the
standard Google Cast media namespace. SharpCaster's `ChromeCastClient` only wires up a fixed,
hardcoded set of built-in channels (`ConnectionChannel`, `HeartbeatChannel`, `ReceiverChannel`,
`MediaChannel`, `MultiZoneChannel`, `SpotifyChannel`) in its constructor - there is no public API
to register an additional channel for a custom namespace. Reaching Jellyfin's own receiver
properly (see `CLAUDE.md` for why that matters for reliability and correct H.265 handling)
requires one.

## The patches

### 1. `ChromeCastClient.AddChannel` (allows registering `ConnectSdkChannel` at all)

```csharp
public void AddChannel(IChromecastChannel channel)
{
    channel.Client = this;
    Channels = Channels.Append(channel);
}
```

Message dispatch (`Channels.FirstOrDefault(c => c.Namespace == castMessage.Namespace)`) already
works generically for any channel in the list, so this alone is sufficient to make a custom
channel reachable by namespace.

### 2. `ChromeCastClient`'s receive loop dispatches to a channel only for *known* message types

Reaching the channel by namespace isn't enough on its own, though. The receive loop parses every
incoming message's `"type"` field and only calls `channel.OnMessageReceived(...)` if that type is
one of the fixed, built-in Cast protocol types registered in `RegisterMessages` (`PING`, `PONG`,
`RECEIVER_STATUS`, `LAUNCH_ERROR`, ...) - anything else was silently dropped before ever reaching
the channel, with no error, no log reaching the channel, nothing. Jellyfin's receiver's own status
broadcasts on `connectsdk` use its own `"type"` values (`"playbackstart"`, `"playbackprogress"`,
`"error"`, ...), none of which are in that fixed list, so **every status broadcast the receiver
ever sent back was silently discarded** - discovered only by noticing `ConnectSdkChannel`'s own
receive-side logging never once fired during real casts, despite the connection clearly being
live (sends succeeded, playback visibly worked).

Changed the `else` branch (the "unrecognized type" case) from logging + `Debugger.Break()` to
simply dispatching anyway:

```csharp
channel?.OnMessageReceived(payload, message?.Type ?? string.Empty);
```

Known types still get the exact same handling as before (including `WaitingTasks`/request-id
completion); this only changes what happens to types nothing recognizes, so it can't affect any
of SharpCaster's own built-in channel behavior - it only turns a silent drop into an actual
dispatch for namespaces (like ours) that were never going to have a "known" type in the first
place.

### 3. `HeartbeatChannel.TimerElapsed` never restarts its own timer after sending a ping

`_timer` is `AutoReset = false` (single-shot). The branch that sends our own outbound PING (when
we haven't heard from the device in a while) sets `_triedToPing = true` but never called
`_timer.Start()` again - so the *next* elapse, which is what's supposed to check whether that
ping actually got a response and raise a timeout if not, could never happen. Added `_timer.Start()`
right after sending the ping. Also added a settable `AdditionalDestinationId` property so
heartbeat ping/pong traffic can be addressed to a launched app's transport id as well as the
default "receiver-0" platform destination, in case a device treats those as separately-tracked
connections for liveness purposes. Neither of these turned out to be the actual cause of the
connection-longevity issue in `CLAUDE.md` (see patch 4, which is), but both are real, confirmed
bugs/gaps independent of that, so they're fixed/added regardless.

### 4. One bad inbound message could silently kill the entire receive loop forever

This is the one that actually explains the connection-longevity issue in `CLAUDE.md`. The
`while (true)` receive loop in `ChromeCastClient.cs` had no per-message error handling - only a
single `try/catch` around the *entire* loop, whose `catch` just logs and lets the loop end.
Confirmed by testing: a real Chromecast sent an unsolicited `MEDIA_STATUS` broadcast (CAF's own
internal media session status push, unrelated to anything this plugin does directly - the
receiver app uses CAF's `PlayerManager` internally) whose `"requestId"` value didn't fit
`System.Text.Json`'s deserialization target for `MessageWithId.RequestId`
(`[JsonPropertyName("requestId")] public int RequestId`), throwing a `JsonException` **outside**
any per-message try/catch. That one exception ended the receive loop for good, silently: no
retry, no reconnect, just a connection that stops receiving *anything* from that point on -
including this same client's own heartbeat PONG replies. The eventual "heartbeat timeout"
disconnect seen ~20-30s later wasn't a real heartbeat failure at all; it was this client
noticing, only much later, a symptom of the receive loop having already died.

Wrapped the entire per-message body (namespace lookup through dispatch) in its own try/catch that
logs and continues to the next message, instead of letting anything escape to the loop-ending
catch. A single malformed/unexpected message can now never take down the whole connection.

## Keeping this in sync with upstream

To pull in upstream fixes, re-copy `Sharpcaster/` from a fresh clone of the upstream repo and
re-apply all four patches above:
- `AddChannel` - search for `GetChannel<TChannel>` in `ChromeCastClient.cs`, add it directly after.
- The receive-loop dispatch change (turning a silent drop into a real dispatch for unrecognized
  types) - search for `LogMessageConversionError` in the same file.
- The heartbeat timer fix and `AdditionalDestinationId` - search for `_triedToPing = true` and
  `AdditionalDestinationId` in `Channels/HeartbeatChannel.cs`.
- The per-message try/catch in the receive loop - search for `LogExceptionProcessingResponse` in
  `ChromeCastClient.cs`; the fix wraps everything between the namespace lookup and the end of the
  `while` loop body in its own try/catch.

These are small enough, self-contained changes that they may be worth proposing upstream as a PR
at some point, which would let us drop the vendoring entirely.
