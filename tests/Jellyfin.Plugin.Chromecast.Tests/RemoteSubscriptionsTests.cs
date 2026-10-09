using Jellyfin.Plugin.Chromecast.Session;
using Xunit;

namespace Jellyfin.Plugin.Chromecast.Tests;

/// <summary>
/// The message sequences here were captured from the Jellyfin 12.0 web client (see CLAUDE.md).
/// </summary>
public class RemoteSubscriptionsTests
{
    private const string Phone = "phone";

    [Fact]
    public void ConnectingToARemotePlayerIsRecognized()
    {
        var subs = new RemoteSubscriptions<string>();

        var ticket = subs.SessionsStarted(Phone);

        Assert.True(subs.ConfirmRemoteControl(Phone, ticket));
        Assert.True(subs.AnyRemoteControlling(c => c == Phone));
    }

    [Fact]
    public void DisconnectIsRecognizedOnce()
    {
        var subs = new RemoteSubscriptions<string>();
        subs.ConfirmRemoteControl(Phone, subs.SessionsStarted(Phone));

        Assert.True(subs.SessionsStopped(Phone));
        Assert.False(subs.SessionsStopped(Phone));
        Assert.False(subs.AnyRemoteControlling(_ => true));
    }

    [Fact]
    public void OpeningTheDashboardIsNotAConnect()
    {
        var subs = new RemoteSubscriptions<string>();

        // The dashboard sends both in the same instant, in this order...
        var ticket = subs.SessionsStarted(Phone);
        subs.TasksStarted(Phone);

        Assert.False(subs.ConfirmRemoteControl(Phone, ticket));

        // ...and leaving it must not look like a disconnect either.
        subs.TasksStopped(Phone);
        Assert.False(subs.SessionsStopped(Phone));
    }

    [Fact]
    public void DashboardWithTasksSubscribedFirstIsNotAConnect()
    {
        var subs = new RemoteSubscriptions<string>();
        subs.TasksStarted(Phone);

        Assert.False(subs.ConfirmRemoteControl(Phone, subs.SessionsStarted(Phone)));
    }

    [Fact]
    public void DisconnectingBeforeTheGracePeriodEndsIsNotAConnect()
    {
        var subs = new RemoteSubscriptions<string>();
        var ticket = subs.SessionsStarted(Phone);

        Assert.False(subs.SessionsStopped(Phone));
        Assert.False(subs.ConfirmRemoteControl(Phone, ticket));
    }

    [Fact]
    public void OnlyTheLatestSubscriptionCanBeConfirmed()
    {
        var subs = new RemoteSubscriptions<string>();
        var first = subs.SessionsStarted(Phone);
        var second = subs.SessionsStarted(Phone);

        Assert.False(subs.ConfirmRemoteControl(Phone, first));
        Assert.True(subs.ConfirmRemoteControl(Phone, second));
    }

    [Fact]
    public void VisitingTheDashboardWhileConnectedKeepsTheConnection()
    {
        var subs = new RemoteSubscriptions<string>();
        subs.ConfirmRemoteControl(Phone, subs.SessionsStarted(Phone));

        // Already subscribed to sessions, so the dashboard only adds and removes the task subscription.
        subs.TasksStarted(Phone);
        subs.TasksStopped(Phone);

        Assert.True(subs.AnyRemoteControlling(c => c == Phone));
        Assert.True(subs.SessionsStopped(Phone));
    }

    [Fact]
    public void AClosedSocketIsNotADisconnect()
    {
        var subs = new RemoteSubscriptions<string>();
        subs.ConfirmRemoteControl(Phone, subs.SessionsStarted(Phone));

        subs.Closed(Phone);

        Assert.False(subs.SessionsStopped(Phone));
        Assert.False(subs.AnyRemoteControlling(_ => true));
    }

    [Fact]
    public void ConnectionsAreTrackedSeparately()
    {
        var subs = new RemoteSubscriptions<string>();
        subs.ConfirmRemoteControl(Phone, subs.SessionsStarted(Phone));
        subs.ConfirmRemoteControl("mac", subs.SessionsStarted("mac"));

        Assert.True(subs.SessionsStopped(Phone));
        Assert.True(subs.AnyRemoteControlling(c => c == "mac"));
        Assert.False(subs.AnyRemoteControlling(c => c == Phone));
    }
}
