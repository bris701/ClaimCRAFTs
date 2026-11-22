using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClaimCRAFTs.Models;
using MessagePack;

namespace ClaimCRAFTs.Services;

/// <summary>
/// A single public WebSocket for the selected claim. REST remains authoritative:
/// change events invalidate snapshots, progress events can update the current row.
/// </summary>
public sealed class LiveFeed : IDisposable
{
    private const string SocketUrl = "wss://craft-api.resubaka.dev/websocket?encoding=MessagePack";
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private Task? _loop;

    public event Action<bool>? ConnectionChanged;
    public event Action<LiveMessage>? MessageReceived;

    public void Start(string claimId)
    {
        if (_cts is not null) throw new InvalidOperationException("Feed already started.");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(JsonData.ValidateId(claimId), token), token);
    }

    private async Task RunAsync(string claimId, CancellationToken cancellationToken)
    {
        var delaySeconds = 2;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            _socket = socket;
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
            try
            {
                await socket.ConnectAsync(new Uri(SocketUrl), cancellationToken);
                var topics = new[] { "claim_crafting", $"claim_crafting.{claimId}", $"claim_craft_progress.{claimId}" };
                await SendAsync(socket, JsonSerializer.Serialize(new { t = "Subscribe", c = new { topics } }), cancellationToken);
                ConnectionChanged?.Invoke(true);
                delaySeconds = 2;
                await ReceiveAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Network or protocol failure; the UI retains the latest valid REST snapshot.
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
                _socket = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken); }
            catch (OperationCanceledException) { break; }
            delaySeconds = Math.Min(30, delaySeconds * 2);
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        // Reuse the receive stream across events instead of allocating a new one
        // (and its backing buffer) for every small crafting progress notification.
        using var packet = new MemoryStream(2048);
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            packet.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                packet.Write(buffer, 0, result.Count);
                if (packet.Length > 8 * 1024 * 1024)
                    throw new FormatException("WebSocket message is unexpectedly large.");
            } while (!result.EndOfMessage);

            string json;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // MessagePack retains signed 64-bit integers; ConvertToJson writes their
                // exact decimal representation for System.Text.Json (no JavaScript Number).
                json = MessagePackSerializer.ConvertToJson(packet.ToArray());
            }
            else if (result.MessageType == WebSocketMessageType.Text)
                json = Encoding.UTF8.GetString(packet.ToArray());
            else continue;

            using var document = JsonDocument.Parse(json);
            var body = document.RootElement;
            var type = JsonData.String(body, "t");
            if (type is null) continue;
            var content = JsonData.Property(body, "c");
            MessageReceived?.Invoke(new LiveMessage(
                type,
                JsonData.Id(content, "claim_id"),
                JsonData.Id(content, "craft_id"),
                JsonData.Int64(content, "progress"),
                JsonData.DateTime(content, "lock_expiration")));
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _socket?.Abort(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        // The asynchronous reconnect loop owns and disposes its sockets.
    }
}
