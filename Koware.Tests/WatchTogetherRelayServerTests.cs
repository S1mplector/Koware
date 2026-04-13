using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Koware.WatchTogether;
using Xunit;

namespace Koware.Tests;

public sealed class WatchTogetherRelayServerTests
{
    [Fact]
    public async Task StartAsync_ServesHealthTextForHttpRequests()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);

        await relay.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await http.GetStringAsync(listenUri, CancellationToken.None);

        Assert.True(relay.IsRunning);
        Assert.Contains("Koware watch-together relay is running", body);
        Assert.Equal($"ws://127.0.0.1:{listenUri.Port}/", relay.GetRelayUri().ToString());
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_DoesNotRestartRelay()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);

        await relay.StartAsync(CancellationToken.None);
        await relay.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await http.GetStringAsync(listenUri, CancellationToken.None);

        Assert.True(relay.IsRunning);
        Assert.Contains("Koware watch-together relay is running", body);
    }

    [Fact]
    public async Task RunUntilCanceledAsync_ReturnsWhenCancellationIsRequested()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        using var cts = new CancellationTokenSource();

        var runTask = relay.RunUntilCanceledAsync(cts.Token);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await http.GetStringAsync(listenUri, CancellationToken.None);
        cts.Cancel();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(relay.IsRunning);
        Assert.Contains("Koware watch-together relay is running", body);
    }

    [Fact]
    public async Task DisposeAsync_StopsRunningRelay()
    {
        var listenUri = NewLoopbackListenUri();
        var relay = new WatchTogetherRelayServer(listenUri);

        await relay.StartAsync(CancellationToken.None);
        await relay.DisposeAsync();

        Assert.False(relay.IsRunning);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765/", "ws://127.0.0.1:8765/")]
    [InlineData("https://127.0.0.1:8765/", "wss://127.0.0.1:8765/")]
    [InlineData("ws://127.0.0.1:8765/", "ws://127.0.0.1:8765/")]
    [InlineData("wss://127.0.0.1:8765/", "wss://127.0.0.1:8765/")]
    public void GetRelayUri_ReturnsClientFacingWebSocketUri(string listenUri, string expectedRelayUri)
    {
        var relay = new WatchTogetherRelayServer(new Uri(listenUri));

        Assert.Equal(expectedRelayUri, relay.GetRelayUri().ToString());
    }

    [Fact]
    public async Task ConnectAsync_ReceivesWelcomeMessage()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM1", "guest", "Guest", WatchTogetherRoles.Guest));
        await guest.ConnectAsync(CancellationToken.None);

        var welcome = await ReceiveTypeAsync(guest, WatchTogetherMessageTypes.Welcome, CancellationToken.None);

        Assert.Equal("ROOM1", welcome.RoomCode);
        Assert.Equal("relay", welcome.ClientId);
        Assert.Equal(WatchTogetherRoles.System, welcome.Role);
        Assert.Contains("ROOM1", welcome.Text);
    }

    [Fact]
    public async Task ConnectAsync_SendsHelloToOtherRoomClients()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-HELLO", "host", "Host", WatchTogetherRoles.Host));
        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-HELLO", "guest", "Guest", WatchTogetherRoles.Guest));
        await host.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);

        await guest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(guest, CancellationToken.None);

        var hello = await ReceiveTypeAsync(host, WatchTogetherMessageTypes.Hello, CancellationToken.None);

        Assert.Equal("ROOM-HELLO", hello.RoomCode);
        Assert.Equal("guest", hello.ClientId);
        Assert.Equal("Guest", hello.Name);
        Assert.Equal(WatchTogetherRoles.Guest, hello.Role);
    }

    [Fact]
    public async Task SendAsync_EnrichesAndRelayBroadcastsMessageToOtherClientsOnly()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM2", "host-id", "Host User", WatchTogetherRoles.Host));
        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM2", "guest-id", "Guest User", WatchTogetherRoles.Guest));
        await host.ConnectAsync(CancellationToken.None);
        await guest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);
        await DrainWelcomeAsync(guest, CancellationToken.None);

        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.Content,
            Content = new WatchTogetherContent
            {
                Title = "Episode",
                StreamUrl = "https://cdn.example.com/video.m3u8",
                Quality = "720p"
            }
        }, CancellationToken.None);

        var received = await ReceiveTypeAsync(guest, WatchTogetherMessageTypes.Content, CancellationToken.None);

        Assert.Equal("ROOM2", received.RoomCode);
        Assert.Equal("host-id", received.ClientId);
        Assert.Equal("Host User", received.Name);
        Assert.Equal(WatchTogetherRoles.Host, received.Role);
        Assert.Equal("https://cdn.example.com/video.m3u8", received.Content!.StreamUrl);

        using var shortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveTypeAsync(host, WatchTogetherMessageTypes.Content, shortTimeout.Token));
    }

    [Fact]
    public async Task SendAsync_HandlesMessagesLargerThanSingleBuffer()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-LARGE", "host", "Host", WatchTogetherRoles.Host));
        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-LARGE", "guest", "Guest", WatchTogetherRoles.Guest));
        await host.ConnectAsync(CancellationToken.None);
        await guest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);
        await DrainWelcomeAsync(guest, CancellationToken.None);

        var largeTitle = new string('A', 12_000);
        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.Content,
            Content = new WatchTogetherContent
            {
                Title = largeTitle,
                StreamUrl = "https://cdn.example.com/large.m3u8"
            }
        }, CancellationToken.None);

        var received = await ReceiveTypeAsync(guest, WatchTogetherMessageTypes.Content, CancellationToken.None);

        Assert.Equal(largeTitle, received.Content!.Title);
    }

    [Fact]
    public async Task Relay_ReplaysLatestContentAndStateToLateJoiners()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM3", "host", "Host", WatchTogetherRoles.Host));
        await host.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);

        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.Content,
            Content = new WatchTogetherContent
            {
                Query = "frieren",
                EpisodeNumber = 1,
                Title = "Frieren Episode 1",
                StreamUrl = "https://cdn.example.com/episode-1.m3u8"
            }
        }, CancellationToken.None);

        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.State,
            State = new WatchTogetherPlaybackState
            {
                IsPlaying = true,
                PositionMs = 42_000,
                Rate = 1.5,
                SentAtUnixMs = 99
            }
        }, CancellationToken.None);

        await using var lateGuest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM3", "late", "Late Guest", WatchTogetherRoles.Guest));
        await lateGuest.ConnectAsync(CancellationToken.None);

        var content = await ReceiveTypeAsync(lateGuest, WatchTogetherMessageTypes.Content, CancellationToken.None);
        var state = await ReceiveTypeAsync(lateGuest, WatchTogetherMessageTypes.State, CancellationToken.None);

        Assert.Equal("Frieren Episode 1", content.Content!.Title);
        Assert.Equal("https://cdn.example.com/episode-1.m3u8", content.Content.StreamUrl);
        Assert.True(state.State!.IsPlaying);
        Assert.Equal(42_000, state.State.PositionMs);
        Assert.Equal(1.5, state.State.Rate);
    }

    [Fact]
    public async Task Relay_IsolatesRoomsByRoomCode()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-A", "host", "Host", WatchTogetherRoles.Host));
        await using var roomAGuest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-A", "guest-a", "Guest A", WatchTogetherRoles.Guest));
        await using var roomBGuest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-B", "guest-b", "Guest B", WatchTogetherRoles.Guest));

        await host.ConnectAsync(CancellationToken.None);
        await roomAGuest.ConnectAsync(CancellationToken.None);
        await roomBGuest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);
        await DrainWelcomeAsync(roomAGuest, CancellationToken.None);
        await DrainWelcomeAsync(roomBGuest, CancellationToken.None);

        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.State,
            State = new WatchTogetherPlaybackState { IsPlaying = false, PositionMs = 10_000 }
        }, CancellationToken.None);

        var roomAMessage = await ReceiveTypeAsync(roomAGuest, WatchTogetherMessageTypes.State, CancellationToken.None);

        Assert.Equal("ROOM-A", roomAMessage.RoomCode);
        Assert.Equal(10_000, roomAMessage.State!.PositionMs);

        using var shortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveTypeAsync(roomBGuest, WatchTogetherMessageTypes.State, shortTimeout.Token));
    }

    [Fact]
    public async Task Relay_BroadcastsParticipantJoinToExistingClients()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-JOIN", "host", "Host", WatchTogetherRoles.Host));
        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-JOIN", "guest", "Guest", WatchTogetherRoles.Guest));
        await host.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);

        await guest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(guest, CancellationToken.None);

        var participant = await ReceiveTypeAsync(host, WatchTogetherMessageTypes.Participant, CancellationToken.None);

        Assert.Equal("guest", participant.ClientId);
        Assert.Equal("Guest", participant.Name);
        Assert.Equal(WatchTogetherRoles.Guest, participant.Role);
        Assert.Equal("Guest joined", participant.Text);
    }

    [Fact]
    public async Task Relay_DefaultsMissingIdentityQueryValues()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM-DEFAULTS", "host", "Host", WatchTogetherRoles.Host));
        await host.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);

        using var rawGuest = new ClientWebSocket();
        await rawGuest.ConnectAsync(new Uri($"{relay.GetRelayUri()}rooms/ROOM-DEFAULTS"), CancellationToken.None);

        var welcome = WatchTogetherJson.Deserialize<WatchTogetherMessage>(await ReceiveRawTextAsync(rawGuest, CancellationToken.None));
        var participant = await ReceiveTypeAsync(host, WatchTogetherMessageTypes.Participant, CancellationToken.None);

        Assert.Equal(WatchTogetherMessageTypes.Welcome, welcome!.Type);
        Assert.False(string.IsNullOrWhiteSpace(participant.ClientId));
        Assert.Equal("viewer", participant.Name);
        Assert.Equal(WatchTogetherRoles.Guest, participant.Role);
    }

    [Fact]
    public async Task Relay_UsesQueryStringIdentityAndIgnoresSpoofedMessageIdentity()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM4", "real-host", "Real Host", WatchTogetherRoles.Host));
        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM4", "guest", "Guest", WatchTogetherRoles.Guest));
        await host.ConnectAsync(CancellationToken.None);
        await guest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);
        await DrainWelcomeAsync(guest, CancellationToken.None);

        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.State,
            ClientId = "spoofed",
            Name = "Spoofed",
            Role = WatchTogetherRoles.Guest,
            State = new WatchTogetherPlaybackState { IsPlaying = true, PositionMs = 2_000 }
        }, CancellationToken.None);

        var received = await ReceiveTypeAsync(guest, WatchTogetherMessageTypes.State, CancellationToken.None);

        Assert.Equal("real-host", received.ClientId);
        Assert.Equal("Real Host", received.Name);
        Assert.Equal(WatchTogetherRoles.Host, received.Role);
    }

    [Fact]
    public async Task StartReceiveLoopAsync_InvokesCallbackForIncomingMessages()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        await using var host = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM5", "host", "Host", WatchTogetherRoles.Host));
        await using var guest = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM5", "guest", "Guest", WatchTogetherRoles.Guest));
        await host.ConnectAsync(CancellationToken.None);
        await guest.ConnectAsync(CancellationToken.None);
        await DrainWelcomeAsync(host, CancellationToken.None);

        var received = new TaskCompletionSource<WatchTogetherMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = guest.StartReceiveLoopAsync((message, _) =>
        {
            if (message.Type == WatchTogetherMessageTypes.State)
            {
                received.TrySetResult(message);
            }

            return Task.CompletedTask;
        }, loopCts.Token);

        await host.SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.State,
            State = new WatchTogetherPlaybackState { IsPlaying = true, PositionMs = 123 }
        }, CancellationToken.None);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        loopCts.Cancel();
        await loop;

        Assert.Equal(123, message.State!.PositionMs);
    }

    [Fact]
    public async Task DisposeAsync_ClosesConnectedClient()
    {
        var listenUri = NewLoopbackListenUri();
        await using var relay = new WatchTogetherRelayServer(listenUri);
        await relay.StartAsync(CancellationToken.None);

        var client = new WatchTogetherClient(NewSession(relay.GetRelayUri(), "ROOM6", "client", "Client", WatchTogetherRoles.Guest));
        await client.ConnectAsync(CancellationToken.None);

        await client.DisposeAsync();

        Assert.NotEqual(System.Net.WebSockets.WebSocketState.Open, client.State);
    }

    private static WatchTogetherSessionOptions NewSession(Uri relayUri, string roomCode, string clientId, string name, string role)
        => new(relayUri, roomCode, clientId, name, role);

    private static Uri NewLoopbackListenUri()
        => new($"http://127.0.0.1:{GetFreeTcpPort()}/");

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task DrainWelcomeAsync(WatchTogetherClient client, CancellationToken cancellationToken)
    {
        await ReceiveTypeAsync(client, WatchTogetherMessageTypes.Welcome, cancellationToken);
    }

    private static async Task<WatchTogetherMessage> ReceiveTypeAsync(
        WatchTogetherClient client,
        string type,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (true)
        {
            var message = await client.ReceiveAsync(timeout.Token);
            if (message is not null && message.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }
        }
    }

    private static async Task<string> ReceiveRawTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
