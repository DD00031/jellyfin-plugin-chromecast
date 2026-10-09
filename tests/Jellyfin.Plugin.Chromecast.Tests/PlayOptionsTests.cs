using System;
using Jellyfin.Plugin.Chromecast.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Chromecast.Tests;

public class PlayOptionsTests
{
    private static readonly Guid ItemId = Guid.Parse("b565cf71-76b9-43ce-b165-fece9b26181f");

    private static BaseItemDto Item(Guid id, params string[] mediaSourceIds) => new()
    {
        Id = id,
        MediaSources = mediaSourceIds.Length == 0 ? null : Array.ConvertAll(mediaSourceIds, s => new MediaSourceInfo { Id = s })
    };

    // Jellyfin's PlaybackInfo silently ignores a requested audio/subtitle track unless the media
    // source is named too - the cause of "subtitles worked once and never again".
    [Theory]
    [InlineData(null, 0)]
    [InlineData(2, null)]
    [InlineData(2, 0)]
    public void AMediaSourceIsFilledInWheneverATrackIsRequested(int? audio, int? subtitle)
    {
        Assert.Equal("first", ChromecastSessionController.ResolveMediaSourceId(Item(ItemId, "first", "second"), null, audio, subtitle));
    }

    [Fact]
    public void WithoutATrackRequestTheServerKeepsItsOwnChoiceOfVersion()
    {
        Assert.Null(ChromecastSessionController.ResolveMediaSourceId(Item(ItemId, "first", "second"), null, null, null));
    }

    [Fact]
    public void AnExplicitMediaSourceIsNeverReplaced()
    {
        Assert.Equal("chosen", ChromecastSessionController.ResolveMediaSourceId(Item(ItemId, "first"), "chosen", 1, 2));
    }

    [Fact]
    public void AnItemWithoutMediaSourcesFallsBackToItsOwnId()
    {
        Assert.Equal("b565cf7176b943ceb165fece9b26181f", ChromecastSessionController.ResolveMediaSourceId(Item(ItemId), null, null, 0));
    }

    [Theory]
    [InlineData(null, 3, 0)]
    [InlineData(0, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(3, 3, 0)]
    [InlineData(-1, 3, 0)]
    public void TheStartIndexIsKeptInsideTheList(int? requested, int count, int expected)
    {
        Assert.Equal(expected, ChromecastSessionController.GetStartIndex(new PlayRequest { StartIndex = requested }, count));
    }

    [Fact]
    public void PlayFromHereUsesTheMediaSourceOfTheItemItStartsWith()
    {
        var other = Guid.NewGuid();
        var options = ChromecastSessionController.BuildPlayNowOptions(
            [Item(ItemId, "first-source"), Item(other, "second-source")],
            new PlayRequest { ItemIds = [ItemId, other], StartIndex = 1, SubtitleStreamIndex = 0 });

        Assert.Equal("second-source", options.MediaSourceId);
        Assert.Equal(1, options.StartIndex);
    }

    // The receiver names its own Jellyfin session btoa(receiverName); signing it in under the
    // same id keeps a cast at one session for the user instead of two.
    [Theory]
    [InlineData("Eettafel TV", "RWV0dGFmZWwgVFY=")]
    [InlineData("Café", "Q2Fm6Q==")]
    public void TheReceiverDeviceIdMatchesJavaScriptsBtoa(string name, string expected)
    {
        Assert.Equal(expected, ChromecastSessionController.GetReceiverDeviceId(name));
    }

    [Fact]
    public void NamesBtoaCannotEncodeHaveNoReceiverDeviceId()
    {
        Assert.Null(ChromecastSessionController.GetReceiverDeviceId("客厅电视"));
    }
}
