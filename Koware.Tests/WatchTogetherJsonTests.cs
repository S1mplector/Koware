using System.Text.Json;
using Koware.WatchTogether;
using Xunit;

namespace Koware.Tests;

public sealed class WatchTogetherJsonTests
{
    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var message = new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.State,
            RoomCode = "ROOM",
            ClientId = "client",
            State = new WatchTogetherPlaybackState
            {
                IsPlaying = true,
                PositionMs = 12_345,
                Rate = 1.25,
                SentAtUnixMs = 999
            }
        };

        var json = WatchTogetherJson.Serialize(message);

        Assert.Contains("\"roomCode\":\"ROOM\"", json);
        Assert.Contains("\"clientId\":\"client\"", json);
        Assert.Contains("\"isPlaying\":true", json);
        Assert.Contains("\"positionMs\":12345", json);
        Assert.DoesNotContain("\"RoomCode\"", json);
    }

    [Fact]
    public void Deserialize_RoundTripsContentAndPlaybackState()
    {
        var original = new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.Content,
            RoomCode = "ROOM",
            ClientId = "host",
            Name = "Host",
            Role = WatchTogetherRoles.Host,
            Content = new WatchTogetherContent
            {
                Query = "frieren",
                EpisodeNumber = 4,
                Quality = "1080p",
                Title = "Frieren - Episode 4",
                StreamUrl = "https://cdn.example.com/master.m3u8",
                Referrer = "https://source.example/",
                UserAgent = "KowareTest/1.0",
                Subtitles =
                [
                    new WatchTogetherSubtitle("English", "https://cdn.example.com/en.vtt", "en"),
                    new WatchTogetherSubtitle("Deutsch", "https://cdn.example.com/de.vtt", "de")
                ]
            },
            State = new WatchTogetherPlaybackState
            {
                IsPlaying = true,
                PositionMs = 8_000,
                Rate = 1.0,
                SentAtUnixMs = 1_234
            },
            SentAtUnixMs = 5_678
        };

        var json = WatchTogetherJson.Serialize(original);
        var roundTripped = WatchTogetherJson.Deserialize<WatchTogetherMessage>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Type, roundTripped.Type);
        Assert.Equal(original.RoomCode, roundTripped.RoomCode);
        Assert.Equal(original.ClientId, roundTripped.ClientId);
        Assert.Equal(original.Content!.StreamUrl, roundTripped.Content!.StreamUrl);
        Assert.Equal(2, roundTripped.Content.Subtitles.Count);
        Assert.Equal("de", roundTripped.Content.Subtitles[1].Language);
        Assert.True(roundTripped.State!.IsPlaying);
        Assert.Equal(8_000, roundTripped.State.PositionMs);
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => WatchTogetherJson.Deserialize<WatchTogetherMessage>("{"));
    }
}
