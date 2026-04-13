using Koware.WatchTogether;
using Xunit;

namespace Koware.Tests;

public sealed class WatchTogetherClientTests
{
    [Theory]
    [InlineData("http://relay.example.com", "ws://relay.example.com/")]
    [InlineData("https://relay.example.com", "wss://relay.example.com/")]
    [InlineData("ws://relay.example.com/base", "ws://relay.example.com/base")]
    [InlineData("wss://relay.example.com/base/", "wss://relay.example.com/base/")]
    public void NormalizeRelayUri_ConvertsHttpSchemesToWebSocketSchemes(string input, string expected)
    {
        var actual = WatchTogetherClient.NormalizeRelayUri(input);

        Assert.Equal(expected, actual.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a uri")]
    [InlineData("ftp://relay.example.com")]
    public void NormalizeRelayUri_RejectsInvalidOrUnsupportedRelayUris(string input)
    {
        Assert.Throws<ArgumentException>(() => WatchTogetherClient.NormalizeRelayUri(input));
    }

    [Fact]
    public void BuildRoomUri_AppendsRoomPathAndEncodesQueryValues()
    {
        var relayUri = WatchTogetherClient.NormalizeRelayUri("https://relay.example.com/api");

        var roomUri = WatchTogetherClient.BuildRoomUri(
            relayUri,
            "ROOM 42",
            "client/1",
            "Alice & Bob",
            "host");

        Assert.Equal("wss://relay.example.com/api/rooms/ROOM%2042?clientId=client%2F1&name=Alice%20%26%20Bob&role=host", roomUri.AbsoluteUri);
    }

    [Fact]
    public void BuildRoomUri_ConvertsHttpRelayUriToWebSocketRoomUri()
    {
        var roomUri = WatchTogetherClient.BuildRoomUri(
            new Uri("http://127.0.0.1:8765/"),
            "ABCD12",
            "client",
            "viewer",
            "guest");

        Assert.Equal("ws://127.0.0.1:8765/rooms/ABCD12?clientId=client&name=viewer&role=guest", roomUri.ToString());
    }

    [Fact]
    public void BuildRoomUri_PreservesRelayBasePath()
    {
        var roomUri = WatchTogetherClient.BuildRoomUri(
            new Uri("wss://relay.example.com/koware/watch/"),
            "ROOM",
            "client",
            "viewer",
            "guest");

        Assert.Equal("/koware/watch/rooms/ROOM", roomUri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildRoomUri_RejectsEmptyRoomCodes(string roomCode)
    {
        Assert.Throws<ArgumentException>(() => WatchTogetherClient.BuildRoomUri(
            new Uri("ws://relay.example.com/"),
            roomCode,
            "client",
            "viewer",
            "guest"));
    }

    [Fact]
    public void SessionOptions_IsHost_IsCaseInsensitive()
    {
        var session = new WatchTogetherSessionOptions(
            new Uri("wss://relay.example.com/"),
            "ROOM",
            "client",
            "Host",
            "HoSt");

        Assert.True(session.IsHost);
    }

    [Fact]
    public async Task SendAsync_WhenNotConnected_DoesNotThrow()
    {
        await using var client = new WatchTogetherClient(new WatchTogetherSessionOptions(
            new Uri("ws://127.0.0.1:1/"),
            "ROOM",
            "client",
            "viewer",
            WatchTogetherRoles.Guest));

        await client.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.State,
            State = new WatchTogetherPlaybackState { PositionMs = 10 }
        }, CancellationToken.None);
    }
}
