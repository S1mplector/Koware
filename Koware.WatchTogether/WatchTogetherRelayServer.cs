// Author: Ilgaz Mehmetoğlu
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Koware.WatchTogether;

public sealed class WatchTogetherRelayServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, RelayRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    public WatchTogetherRelayServer(Uri listenUri)
    {
        ListenUri = listenUri;
    }

    public Uri ListenUri { get; }

    public bool IsRunning => _listener.IsListening;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener.IsListening)
        {
            return Task.CompletedTask;
        }

        _listener.Prefixes.Add(ToHttpListenerPrefix(ListenUri));
        _listener.Start();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task RunUntilCanceledAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public Uri GetRelayUri()
    {
        var builder = new UriBuilder(ListenUri)
        {
            Scheme = ListenUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                     ListenUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            await WriteHttpResponseAsync(context.Response, "Koware watch-together relay is running.\n", 200);
            return;
        }

        var roomCode = TryGetRoomCode(context.Request.Url?.AbsolutePath);
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            await WriteHttpResponseAsync(context.Response, "Expected /rooms/{roomCode}.\n", 404);
            return;
        }

        WebSocketContext webSocketContext;
        try
        {
            webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
        }
        catch
        {
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var clientId = context.Request.QueryString["clientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = Guid.NewGuid().ToString("N");
        }

        var name = context.Request.QueryString["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "viewer";
        }

        var role = context.Request.QueryString["role"];
        if (string.IsNullOrWhiteSpace(role))
        {
            role = WatchTogetherRoles.Guest;
        }

        var room = _rooms.GetOrAdd(roomCode, static code => new RelayRoom(code));
        var client = new RelayClient(clientId, name, role, webSocketContext.WebSocket);
        await room.AddAsync(client, cancellationToken);

        try
        {
            await ReceiveClientLoopAsync(room, client, cancellationToken);
        }
        finally
        {
            await room.RemoveAsync(client.ClientId, cancellationToken);
        }
    }

    private static async Task ReceiveClientLoopAsync(RelayRoom room, RelayClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await client.Socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(stream.ToArray());
            var message = WatchTogetherJson.Deserialize<WatchTogetherMessage>(json);
            if (message is null)
            {
                continue;
            }

            var enriched = message with
            {
                RoomCode = room.RoomCode,
                ClientId = client.ClientId,
                Name = client.Name,
                Role = client.Role
            };

            await room.BroadcastAsync(enriched, excludeClientId: client.ClientId, cancellationToken);
        }
    }

    private static string? TryGetRoomCode(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !parts[^2].Equals("rooms", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.UnescapeDataString(parts[^1]).Trim();
    }

    private static async Task WriteHttpResponseAsync(HttpListenerResponse response, string text, int statusCode)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static string ToHttpListenerPrefix(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme switch
            {
                "ws" => "http",
                "wss" => "https",
                _ => uri.Scheme
            },
            Host = uri.Host is "0.0.0.0" or "*" ? "+" : uri.Host,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        var prefix = builder.Uri.ToString();
        return prefix.EndsWith('/') ? prefix : $"{prefix}/";
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Ignore shutdown failures.
            }
        }

        foreach (var room in _rooms.Values)
        {
            await room.DisposeAsync();
        }

        _shutdown.Dispose();
    }

    private sealed class RelayRoom : IAsyncDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<string, RelayClient> _clients = new(StringComparer.OrdinalIgnoreCase);
        private WatchTogetherMessage? _lastContent;
        private WatchTogetherMessage? _lastState;

        public RelayRoom(string roomCode)
        {
            RoomCode = roomCode;
        }

        public string RoomCode { get; }

        public async Task AddAsync(RelayClient client, CancellationToken cancellationToken)
        {
            WatchTogetherMessage? lastContent;
            WatchTogetherMessage? lastState;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                _clients[client.ClientId] = client;
                lastContent = _lastContent;
                lastState = _lastState;
            }
            finally
            {
                _lock.Release();
            }

            await client.SendAsync(new WatchTogetherMessage
            {
                Type = WatchTogetherMessageTypes.Welcome,
                RoomCode = RoomCode,
                ClientId = "relay",
                Name = "relay",
                Role = WatchTogetherRoles.System,
                Text = $"Joined room {RoomCode}"
            }, cancellationToken);

            if (lastContent is not null)
            {
                await client.SendAsync(lastContent, cancellationToken);
            }

            if (lastState is not null)
            {
                await client.SendAsync(lastState, cancellationToken);
            }

            await BroadcastAsync(new WatchTogetherMessage
            {
                Type = WatchTogetherMessageTypes.Participant,
                RoomCode = RoomCode,
                ClientId = client.ClientId,
                Name = client.Name,
                Role = client.Role,
                Text = $"{client.Name} joined"
            }, excludeClientId: client.ClientId, cancellationToken);
        }

        public async Task RemoveAsync(string clientId, CancellationToken cancellationToken)
        {
            RelayClient? removed = null;

            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                if (_clients.Remove(clientId, out var client))
                {
                    removed = client;
                }
            }
            finally
            {
                _lock.Release();
            }

            if (removed is not null)
            {
                await removed.DisposeAsync();
            }
        }

        public async Task BroadcastAsync(WatchTogetherMessage message, string? excludeClientId, CancellationToken cancellationToken)
        {
            List<RelayClient> clients;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (message.Type.Equals(WatchTogetherMessageTypes.Content, StringComparison.OrdinalIgnoreCase))
                {
                    _lastContent = message;
                }
                else if (message.Type.Equals(WatchTogetherMessageTypes.State, StringComparison.OrdinalIgnoreCase))
                {
                    _lastState = message;
                }

                clients = _clients.Values
                    .Where(client => !client.ClientId.Equals(excludeClientId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            finally
            {
                _lock.Release();
            }

            foreach (var client in clients)
            {
                await client.SendAsync(message, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<RelayClient> clients;

            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                clients = _clients.Values.ToList();
                _clients.Clear();
            }
            finally
            {
                _lock.Release();
                _lock.Dispose();
            }

            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    private sealed class RelayClient : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public RelayClient(string clientId, string name, string role, WebSocket socket)
        {
            ClientId = clientId;
            Name = name;
            Role = role;
            Socket = socket;
        }

        public string ClientId { get; }

        public string Name { get; }

        public string Role { get; }

        public WebSocket Socket { get; }

        public async Task SendAsync(WatchTogetherMessage message, CancellationToken cancellationToken)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return;
            }

            var json = WatchTogetherJson.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch
            {
                // The receive loop will remove dead clients.
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "relay closing", cts.Token);
                }
            }
            catch
            {
                // Ignore shutdown failures.
            }

            Socket.Dispose();
            _sendLock.Dispose();
        }
    }
}
