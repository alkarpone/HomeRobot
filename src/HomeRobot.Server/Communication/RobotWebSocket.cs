using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Communication;

public sealed class RobotWebSocket(RobotRegistry registry, TimeProvider clock,
    IOptions<RobotProtocolOptions> configuration, ILogger<RobotWebSocket> logger)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var session = registry.Connect();
        logger.LogInformation("Robot WebSocket opened ({ConnectionId})", context.Connection.Id);

        var receiver = ReceiveAsync(socket, session, lifetime.Token);
        var sender = SendAsync(socket, session, lifetime.Token);
        var monitor = MonitorAsync(session, lifetime.Token);
        try
        {
            var finished = await Task.WhenAny(receiver, sender, monitor);
            var closeStatus = finished == receiver ? await receiver : null;
            registry.Remove(session);

            if (closeStatus is { } status)
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await sender.WaitAsync(closeDeadline.Token);
                await socket.CloseOutputAsync(status, null, closeDeadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "Robot WebSocket transport ended");
        }
        finally
        {
            registry.Remove(session);
            await lifetime.CancelAsync();
            if (socket.State != WebSocketState.Closed)
                socket.Abort();

            try
            {
                await Task.WhenAll(receiver, sender, monitor);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException exception)
            {
                logger.LogDebug(exception, "Robot WebSocket worker ended");
            }

            logger.LogInformation("Robot WebSocket closed ({ConnectionId})", context.Connection.Id);
        }
    }

    private async Task<WebSocketCloseStatus?> ReceiveAsync(WebSocket socket, RobotSession session,
        CancellationToken token)
    {
        var maxBytes = configuration.Value.MaxMessageBytes;
        var buffer = new byte[maxBytes + 1];
        var recentIds = new HashSet<string>(StringComparer.Ordinal);
        var idOrder = new Queue<string>();
        while (socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(buffer.AsMemory(length), token);
                if (frame.MessageType == WebSocketMessageType.Close)
                    return WebSocketCloseStatus.NormalClosure;

                length += frame.Count;
                if (length > maxBytes)
                    return WebSocketCloseStatus.MessageTooBig;
            }
            while (!frame.EndOfMessage);

            if (!registry.CheckConnection(session))
                break;

            var response = frame.MessageType == WebSocketMessageType.Text
                ? ProcessMessage(session, buffer.AsMemory(0, length), recentIds, idOrder)
                : LegacyError("unsupported_message_type");
            if (response is not null && !registry.TryQueue(session, response))
                break;
        }

        return null;
    }

    private async Task SendAsync(WebSocket socket, RobotSession session, CancellationToken token)
    {
        while (await session.Ready.Reader.WaitToReadAsync(token))
        {
            session.Ready.Reader.TryRead(out _);
            while (registry.TakeMessage(session) is { } message)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RobotProtocol.Json);
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
            }
        }
    }

    private async Task MonitorAsync(RobotSession session, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), clock);
        while (await timer.WaitForNextTickAsync(token) && registry.CheckConnection(session))
        {
        }
    }

    private object? ProcessMessage(RobotSession session, ReadOnlyMemory<byte> bytes,
        HashSet<string> recentIds, Queue<string> idOrder)
    {
        string? replyTo = null;
        var parsed = false;
        try
        {
            Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                MaxDepth = RobotProtocol.Json.MaxDepth
            });
            parsed = true;
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return LegacyError("invalid_message");

            var messageType = type.Deserialize<string>(RobotProtocol.Json);
            if (messageType == "ping")
                return new { type = "pong" };
            if (root.EnumerateObject().Count() == 1 &&
                messageType is not ("register" or "heartbeat" or "telemetry" or "command_ack" or "command_result"))
                return LegacyError("unsupported_message_type");

            var envelope = root.Deserialize<RobotMessage<JsonElement>>(RobotProtocol.Json)!;
            if (!RobotProtocol.IsIdentifier(envelope.Id) || envelope.Timestamp == default ||
                !IsUtcTimestamp(root.GetProperty("timestamp").GetString()) ||
                envelope.Payload.ValueKind != JsonValueKind.Object)
                return Error("invalid_message");

            replyTo = envelope.Id;
            if (!recentIds.Add(envelope.Id))
                return Error("duplicate_message_id");

            idOrder.Enqueue(envelope.Id);
            if (idOrder.Count > 256)
                recentIds.Remove(idOrder.Dequeue());

            var error = envelope.Type switch
            {
                "register" => Register(session, envelope.Payload.Deserialize<RobotRegistration>(RobotProtocol.Json)!),
                "heartbeat" => registry.UpdateDevice(session,
                    envelope.Payload.Deserialize<RobotHeartbeat>(RobotProtocol.Json)!.RobotId),
                "telemetry" => Telemetry(session, envelope.Payload.Deserialize<RobotTelemetry>(RobotProtocol.Json)!),
                "command_ack" => Acknowledge(session, envelope.Payload.Deserialize<RobotCommandAck>(RobotProtocol.Json)!),
                "command_result" => Result(session, envelope.Payload.Deserialize<RobotCommandResult>(RobotProtocol.Json)!),
                _ => "unsupported_message_type"
            };
            return error is null ? null : Error(error);
        }
        catch (DecoderFallbackException)
        {
            return LegacyError("invalid_json");
        }
        catch (JsonException)
        {
            return !parsed ? LegacyError("invalid_json") : Error("invalid_message");
        }

        object Error(string code) => registry.Message("error", new RobotError(code, replyTo));
    }

    private string? Register(RobotSession session, RobotRegistration registration)
    {
        if (registration.ProtocolVersion != RobotProtocol.Version)
            return "unsupported_protocol_version";
        return RobotProtocol.IsValid(registration) ? registry.Register(session, registration) : "invalid_registration";
    }

    private string? Telemetry(RobotSession session, RobotTelemetry telemetry) =>
        RobotProtocol.IsValid(telemetry) ? registry.UpdateDevice(session, telemetry.RobotId, telemetry) : "invalid_telemetry";

    private string? Acknowledge(RobotSession session, RobotCommandAck ack) =>
        RobotProtocol.IsIdentifier(ack.CommandId)
            ? registry.Transition(session, ack.CommandId, ack.Status, result: false, message: null)
            : "invalid_message";

    private string? Result(RobotSession session, RobotCommandResult result) =>
        RobotProtocol.IsIdentifier(result.CommandId) && result.Message is not { Length: > 256 }
            ? registry.Transition(session, result.CommandId, result.Status, result: true, result.Message)
            : "invalid_message";

    private static bool IsUtcTimestamp(string? value) =>
        value is not null && (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal));

    private static object LegacyError(string code) => new { type = "error", code };
}
