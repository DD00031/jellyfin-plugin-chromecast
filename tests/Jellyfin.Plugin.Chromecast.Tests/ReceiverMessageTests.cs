using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Chromecast.Cast;
using Jellyfin.Plugin.Chromecast.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Chromecast.Tests;

/// <summary>
/// The receiver is JavaScript and reads these messages by exact, case-sensitive field name. Every
/// name asserted here was a real bug at some point or comes straight from the receiver's source.
/// </summary>
public class ReceiverMessageTests
{
    private static readonly Guid UserId = Guid.Parse("aa4eecbe-eb66-45f2-9e7d-662d3884cc76");
    private static readonly Guid MovieId = Guid.Parse("b565cf71-76b9-43ce-b165-fece9b26181f");
    private static readonly Guid OtherId = Guid.Parse("1c37930c-189f-3079-71ab-965b63373f41");

    private static BaseItemDto Item(Guid id, string name, string? mediaSourceId = null) => new()
    {
        Id = id,
        Name = name,
        MediaSources = mediaSourceId is null ? null : [new MediaSourceInfo { Id = mediaSourceId }]
    };

    private static JsonElement Envelope(string command, object? options, long? maxBitrate = null)
        => JsonDocument.Parse(ConnectSdkChannel.BuildEnvelope(command, UserId, "token", "http://server:8096", "Living Room", options, maxBitrate)).RootElement;

    [Fact]
    public void TheEnvelopeUsesTheFieldNamesTheReceiverReads()
    {
        var json = Envelope("Pause", null);

        Assert.Equal("Pause", json.GetProperty("command").GetString());
        Assert.Equal(UserId, json.GetProperty("userId").GetGuid());
        Assert.Equal("token", json.GetProperty("accessToken").GetString());
        Assert.Equal("http://server:8096", json.GetProperty("serverAddress").GetString());
        Assert.Equal("Living Room", json.GetProperty("receiverName").GetString());
        Assert.Equal(JsonValueKind.Object, json.GetProperty("options").ValueKind);
    }

    [Fact]
    public void WithoutABitrateTheReceiverMeasuresItself()
    {
        // The receiver tests `if (data.maxBitrate)`: null leaves its own measurement in charge.
        Assert.Equal(JsonValueKind.Null, Envelope("PlayNow", null).GetProperty("maxBitrate").ValueKind);
    }

    [Fact]
    public void AConfiguredBitrateIsSentInBitsPerSecond()
    {
        Assert.Equal(20_000_000, Envelope("PlayNow", null, 20_000_000).GetProperty("maxBitrate").GetInt64());
    }

    [Fact]
    public void PlayOptionsAreCamelCaseButItemsKeepJellyfinsOwnCasing()
    {
        var options = ChromecastSessionController.BuildPlayNowOptions(
            [Item(MovieId, "Caminandes")],
            new PlayRequest { ItemIds = [MovieId], StartPositionTicks = 600, AudioStreamIndex = 2, SubtitleStreamIndex = 0, MediaSourceId = "src" });

        var json = Envelope("PlayNow", options).GetProperty("options");

        Assert.Equal(600, json.GetProperty("startPositionTicks").GetInt64());
        Assert.Equal(2, json.GetProperty("audioStreamIndex").GetInt32());
        Assert.Equal(0, json.GetProperty("subtitleStreamIndex").GetInt32());
        Assert.Equal("src", json.GetProperty("mediaSourceId").GetString());

        // The receiver reads each item as a genuine BaseItemDto: PascalCase, like the REST API.
        var item = json.GetProperty("items")[0];
        Assert.Equal("Caminandes", item.GetProperty("Name").GetString());
        Assert.True(item.TryGetProperty("Id", out _));
        Assert.False(item.TryGetProperty("name", out _));
    }

    [Fact]
    public void StartIndexIsOnlySentWhenNotTheFirstItem()
    {
        var items = new List<BaseItemDto> { Item(MovieId, "One"), Item(OtherId, "Two") };

        var first = Envelope("PlayNow", ChromecastSessionController.BuildPlayNowOptions(items, new PlayRequest { ItemIds = [MovieId, OtherId] }));
        var second = Envelope("PlayNow", ChromecastSessionController.BuildPlayNowOptions(items, new PlayRequest { ItemIds = [MovieId, OtherId], StartIndex = 1 }));

        Assert.False(first.GetProperty("options").TryGetProperty("startIndex", out _));
        Assert.Equal(1, second.GetProperty("options").GetProperty("startIndex").GetInt32());
    }

    [Fact]
    public void TrackSwitchingUsesALowercaseIndex()
    {
        var json = Envelope("SetSubtitleStreamIndex", new SetIndexOptions { Index = -1 });

        Assert.Equal(-1, json.GetProperty("options").GetProperty("index").GetInt32());
    }

    [Fact]
    public void DisplayContentUsesAPascalCaseItemId()
    {
        var json = Envelope("DisplayContent", new DisplayContentOptions { ItemId = "abc" });

        Assert.Equal("abc", json.GetProperty("options").GetProperty("ItemId").GetString());
    }

    [Fact]
    public void SeekSendsThePositionInSeconds()
    {
        var json = Envelope("Seek", new SeekOptions { Position = 180.5 });

        Assert.Equal(180.5, json.GetProperty("options").GetProperty("position").GetDouble());
    }
}
