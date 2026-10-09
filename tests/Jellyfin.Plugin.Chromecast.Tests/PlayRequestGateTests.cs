using System;
using Jellyfin.Plugin.Chromecast.Session;
using Xunit;

namespace Jellyfin.Plugin.Chromecast.Tests;

public class PlayRequestGateTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PlayRequestGate<string> NewGate() => new(TimeSpan.FromSeconds(15));

    [Fact]
    public void TheFirstRequestGoesStraightThrough()
    {
        Assert.True(NewGate().TryBegin(A, "a", T0));
    }

    [Fact]
    public void ASecondRequestWaitsUntilTheReceiverReportsTheFirst()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);

        Assert.False(gate.TryBegin(B, "b", T0.AddSeconds(1)));
        Assert.Equal("b", gate.Report("playbackprogress", A, T0.AddSeconds(2)));
    }

    [Fact]
    public void OnlyTheNewestWaitingRequestIsKept()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);
        gate.TryBegin(B, "b", T0.AddSeconds(1));
        gate.TryBegin(C, "c", T0.AddSeconds(2));

        Assert.Equal("c", gate.Report("playbackstart", A, T0.AddSeconds(3)));
    }

    [Fact]
    public void BroadcastsAboutTheOldItemDoNotOpenTheGate()
    {
        var gate = NewGate();
        gate.TryBegin(B, "b", T0);
        gate.TryBegin(C, "c", T0.AddSeconds(1));

        // The previous video keeps reporting progress while the new one loads.
        Assert.Null(gate.Report("playbackprogress", A, T0.AddSeconds(2)));
        Assert.Null(gate.Report("playbackstop", A, T0.AddSeconds(2)));
        Assert.Equal("c", gate.Report("playstatechange", B, T0.AddSeconds(3)));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("connectionerror")]
    public void AnErrorOpensTheGate(string type)
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);
        gate.TryBegin(B, "b", T0.AddSeconds(1));

        Assert.Equal("b", gate.Report(type, Guid.Empty, T0.AddSeconds(2)));
    }

    [Fact]
    public void ARequestTheReceiverNeverReportsStopsBlockingAfterTheTimeout()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);
        gate.TryBegin(B, "b", T0.AddSeconds(1));

        Assert.Null(gate.Report(string.Empty, Guid.Empty, T0.AddSeconds(14)));
        Assert.Equal("b", gate.Report(string.Empty, Guid.Empty, T0.AddSeconds(15)));
    }

    [Fact]
    public void AfterTheTimeoutANewRequestGoesStraightThrough()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);

        Assert.True(gate.TryBegin(B, "b", T0.AddSeconds(16)));
    }

    [Fact]
    public void ARequestThatCouldNotBeSentDoesNotBlock()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);
        gate.Abort(A);

        Assert.True(gate.TryBegin(B, "b", T0.AddSeconds(1)));
    }

    [Fact]
    public void AbortingADifferentItemChangesNothing()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);
        gate.Abort(B);

        Assert.False(gate.TryBegin(C, "c", T0.AddSeconds(1)));
    }

    [Fact]
    public void ClearDropsTheWaitingRequest()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);
        gate.TryBegin(B, "b", T0.AddSeconds(1));
        gate.Clear();

        Assert.Null(gate.Report("playbackprogress", A, T0.AddSeconds(2)));
        Assert.True(gate.TryBegin(C, "c", T0.AddSeconds(3)));
    }

    [Fact]
    public void WithNothingWaitingTheGateJustOpens()
    {
        var gate = NewGate();
        gate.TryBegin(A, "a", T0);

        Assert.Null(gate.Report("playbackstart", A, T0.AddSeconds(1)));
        Assert.True(gate.TryBegin(B, "b", T0.AddSeconds(2)));
    }

    [Fact]
    public void CastingTheSameItemAgainIsReleasedByItsOwnBroadcasts()
    {
        var gate = NewGate();
        gate.TryBegin(A, "first", T0);
        gate.TryBegin(A, "again", T0.AddSeconds(1));

        Assert.Equal("again", gate.Report("playbackprogress", A, T0.AddSeconds(2)));
    }
}
