# Vendoring note

This is a vendored copy of the `Sharpcaster` library project from
[Tapanila/SharpCaster](https://github.com/Tapanila/SharpCaster) (MIT license, see `LICENSE.txt`),
trimmed to just the library itself (no tests/samples/CI), with **one small patch**.

## Why vendored instead of the NuGet package

Jellyfin's own cast receiver app (the one already used by every Chrome-casting Jellyfin user)
expects commands over a custom CastV2 namespace, `urn:x-cast:com.connectsdk`, in addition to the
standard Google Cast media namespace. SharpCaster's `ChromeCastClient` only wires up a fixed,
hardcoded set of built-in channels (`ConnectionChannel`, `HeartbeatChannel`, `ReceiverChannel`,
`MediaChannel`, `MultiZoneChannel`, `SpotifyChannel`) in its constructor - there is no public API
to register an additional channel for a custom namespace. Reaching Jellyfin's own receiver
properly (see `CLAUDE.md` for why that matters for reliability and correct H.265 handling)
requires one.

## The patch

Added one method to `ChromeCastClient.cs`:

```csharp
public void AddChannel(IChromecastChannel channel)
{
    channel.Client = this;
    Channels = Channels.Append(channel);
}
```

That's it. Message dispatch (`Channels.FirstOrDefault(c => c.Namespace == castMessage.Namespace)`)
already works generically for any channel in the list, so this alone is sufficient - no other
changes were needed.

## Keeping this in sync with upstream

To pull in upstream fixes, re-copy `Sharpcaster/` from a fresh clone of the upstream repo and
re-apply the `AddChannel` addition above (search for `GetChannel<TChannel>` in `ChromeCastClient.cs`
and add it directly after). This is a small enough, self-contained addition that it may also be
worth proposing upstream as a PR at some point, which would let us drop the vendoring entirely.
