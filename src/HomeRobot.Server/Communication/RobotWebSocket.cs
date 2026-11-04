using System.Net.WebSockets;
using System.Text.Json;

namespace HomeRobot.Server.Communication;

public sealed class RobotWebSocket(ILogger<RobotWebSocket> logger)
{
    private const int MaxMessageBytes = 4096;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var cancellationToken = context.RequestAborted;
        logger.LogInformation("Robot connected ({ConnectionId})", context.Connection.Id);

        try
        {
            var buffer = new byte[MaxMessageBytes + 1];
            while (socket.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                        return;
                    }

                    length += frame.Count;
                    if (length > MaxMessageBytes)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, cancellationToken);
                        return;
                    }
                }
                while (!frame.EndOfMessage);

                var response = frame.MessageType == WebSocketMessageType.Text
                    ? GetResponse(buffer.AsMemory(0, length))
                    : new { type = "error", code = "unsupported_message_type" };

                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response).AsMemory(),
                    WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "Robot WebSocket transport ended");
        }
        finally
        {
            logger.LogInformation("Robot disconnected ({ConnectionId})", context.Connection.Id);
        }
    }

    private static object GetResponse(ReadOnlyMemory<byte> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return new { type = "error", code = "invalid_message" };
            }

            return type.GetString() switch
            {
                "ping" => new { type = "pong" },
                _ => new { type = "error", code = "unsupported_message_type" }
            };
        }
        catch (JsonException)
        {
            return new { type = "error", code = "invalid_json" };
        }
    }
}
