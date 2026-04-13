// Author: Ilgaz Mehmetoğlu
using System.Net.WebSockets;
using System.Text;

namespace Koware.WatchTogether;

public sealed class WatchTogetherClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WatchTogetherClient(WatchTogetherSessionOptions session)
    {
        Session = session;
    }

    public WatchTogetherSessionOptions Session { get; }

    public WebSocketState State => _socket.State;

    public static Uri NormalizeRelayUri(string relay)
    {
        if (!Uri.TryCreate(relay, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid relay URI '{relay}'.", nameof(relay));
        }

        var builder = new UriBuilder(uri);
        builder.Scheme = builder.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => throw new ArgumentException("Relay URI must use ws, wss, http, or https.", nameof(relay))
        };

        return builder.Uri;
    }

    public static Uri BuildRoomUri(Uri relayUri, string roomCode, string clientId, string displayName, string role)
    {
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            throw new ArgumentException("Room code is required.", nameof(roomCode));
        }

        var builder = new UriBuilder(relayUri);
        builder.Scheme = builder.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            _ => builder.Scheme
        };

        var basePath = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(basePath)
            ? $"/rooms/{Uri.EscapeDataString(roomCode)}"
            : $"{basePath}/rooms/{Uri.EscapeDataString(roomCode)}";

        var queryParts = new[]
        {
            $"clientId={Uri.EscapeDataString(clientId)}",
            $"name={Uri.EscapeDataString(displayName)}",
            $"role={Uri.EscapeDataString(role)}"
        };
        builder.Query = string.Join("&", queryParts);
        return builder.Uri;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var roomUri = BuildRoomUri(
            Session.RelayUri,
            Session.RoomCode,
            Session.ClientId,
            Session.DisplayName,
            Session.Role);

        await _socket.ConnectAsync(roomUri, cancellationToken);

        await SendAsync(new WatchTogetherMessage
        {
            Type = WatchTogetherMessageTypes.Hello,
            RoomCode = Session.RoomCode,
            ClientId = Session.ClientId,
            Name = Session.DisplayName,
            Role = Session.Role
        }, cancellationToken);
    }

    public async Task SendAsync(WatchTogetherMessage message, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var enriched = message with
        {
            RoomCode = string.IsNullOrWhiteSpace(message.RoomCode) ? Session.RoomCode : message.RoomCode,
            ClientId = string.IsNullOrWhiteSpace(message.ClientId) ? Session.ClientId : message.ClientId,
            Name = string.IsNullOrWhiteSpace(message.Name) ? Session.DisplayName : message.Name,
            Role = string.IsNullOrWhiteSpace(message.Role) ? Session.Role : message.Role
        };

        var json = WatchTogetherJson.Serialize(enriched);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<WatchTogetherMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return WatchTogetherJson.Deserialize<WatchTogetherMessage>(json);
    }

    public Task StartReceiveLoopAsync(
        Func<WatchTogetherMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveAsync(cancellationToken);
                    if (message is null)
                    {
                        break;
                    }

                    await onMessage(message, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown path for player windows and tests.
            }
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "leaving", cts.Token);
            }
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}
