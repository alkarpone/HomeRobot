using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using HomeRobot.Server.Communication;
using HomeRobot.Server.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeRobot.Tests;

public sealed partial class RobotProtocolTests : IDisposable
{
    private readonly TestClock clock = new();
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private CancellationToken Token => deadline.Token;

    public RobotProtocolTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IAIProvider>(ai);
            }));
        client = factory.CreateClient();
    }

    [Fact]
    public async Task RegistrationRejectsUnsupportedVersionAndDuplicateId()
    {
        using var first = await Connect();
        using var second = await Connect();
        await Send(first, "heartbeat", new { robotId = "robot-a" });
        await ExpectError(first, "registration_required");
        await Register(first, "robot-a", version: 2);
        await ExpectError(first, "unsupported_protocol_version");
        await Register(first, "robot-a");
        var registration = await Receive(first);
        Assert.Equal("register_ack", Field(registration, "type"));
        Assert.Equal(5, registration.GetProperty("payload").GetProperty("heartbeatIntervalSeconds").GetInt32());

        await Register(second, "robot-a");
        await ExpectError(second, "duplicate_robot_id");
        await Register(second, "robot-b", deviceType: "esp32");
        Assert.Equal("register_ack", Field(await Receive(second), "type"));

        var robots = await client.GetFromJsonAsync<RobotDetails[]>("/api/robots", Token);
        Assert.Equal(new[] { "robot-a", "robot-b" }, robots!.Select(robot => robot.RobotId).Order().ToArray());

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
        using var replacement = await Registered();
    }

    [Fact]
    public async Task HeartbeatRefreshesMonotonicDeadlineAndInactivityCancelsMovement()
    {
        using var socket = await Registered();
        var baseline = (await Robot()).LastSeen;
        clock.Advance(9);
        await Send(socket, "heartbeat", new { robotId = "robot-a" });
        await Synchronize(socket);
        Assert.True((await Robot()).LastSeen > baseline);
        clock.Advance(6);
        var movement = await SubmitMove(socket);

        clock.Advance(9);
        await WaitForRemoval();
        var status = await Command(movement);
        Assert.Equal("cancelled", status.Status);
        Assert.Equal("heartbeat_timeout", status.Message);
    }

    [Theory]
    [InlineData("move_forward", """{"distanceCm":101,"speed":30}""")]
    [InlineData("move_backward", """{"distanceCm":50,"speed":0}""")]
    [InlineData("move_forward", """{"distanceCm":"50","speed":30}""")]
    [InlineData("rotate_left", """{"angleDegrees":181,"speed":30}""")]
    [InlineData("rotate_right", """{"angleDegrees":90,"speed":51}""")]
    [InlineData("set_expression", """{"expression":""}""")]
    [InlineData("stop", """{"speed":30}""")]
    [InlineData("raw", """{}""")]
    public async Task InvalidCommandsReturnBadRequest(string command, string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        using var response = await Post(command, document.RootElement);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LifecycleRequiresCorrelatedAcknowledgementAndDeviceCompletion()
    {
        using var socket = await Registered();
        var id = await SubmitMove(socket);
        using var overlap = await Post("rotate_left", new { angleDegrees = 90, speed = 30 });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);

        await Send(socket, "command_ack", new { commandId = "wrong-id", status = "accepted" });
        await ExpectError(socket, "unknown_command");
        await Result(socket, id, "completed");
        await ExpectError(socket, "invalid_command_transition");
        Assert.Equal("pending", (await Command(id)).Status);

        await Ack(socket, id, "accepted");
        await Ack(socket, id, "executing");
        await Synchronize(socket);
        Assert.Equal("executing", (await Command(id)).Status);
        await Ack(socket, id, "accepted");
        await ExpectError(socket, "invalid_command_transition");
        await Result(socket, id, "completed");
        await Synchronize(socket);
        Assert.Equal("completed", (await Command(id)).Status);

        var next = await SubmitMove(socket);
        await Result(socket, next, "failed", "obstacle");
        await Synchronize(socket);
        Assert.Equal("failed", (await Command(next)).Status);
        Assert.Equal("obstacle", (await Command(next)).Message);
    }

    [Fact]
    public async Task EmergencyStopPreemptsMovementAndBlocksNewMovementUntilConfirmed()
    {
        using var socket = await Registered();
        var move = await SubmitMove(socket);
        await Ack(socket, move, "accepted");
        await Ack(socket, move, "executing");
        await Synchronize(socket);

        using var stopResponse = await client.PostAsync("/api/robots/robot-a/stop", null, Token);
        var stop = await AcceptedCommand(stopResponse);
        var message = await Receive(socket);
        Assert.Equal(stop.CommandId, Field(message, "id"));
        Assert.Equal("stop", Field(message.GetProperty("payload"), "command"));
        Assert.Equal("cancelled", (await Command(move)).Status);

        using var repeatedStop = await client.PostAsync("/api/robots/robot-a/stop", null, Token);
        Assert.Equal(stop.CommandId, (await AcceptedCommand(repeatedStop)).CommandId);
        using var overlap = await Post("move_backward", new { distanceCm = 50, speed = 30 });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        await Result(socket, move, "completed");
        await ExpectError(socket, "invalid_command_transition");

        await Ack(socket, stop.CommandId, "accepted");
        await Result(socket, stop.CommandId, "completed");
        await Synchronize(socket);
        Assert.Equal("completed", (await Command(stop.CommandId)).Status);
        await SubmitMove(socket);
    }

    [Fact]
    public async Task CommandTimeoutFailsMovementAndDisconnectsDevice()
    {
        using var socket = await Registered();
        var id = await SubmitMove(socket);
        clock.Advance(10);
        await WaitForRemoval();
        var status = await Command(id);
        Assert.Equal("failed", status.Status);
        Assert.Equal("command_timeout", status.Message);
    }

    [Fact]
    public async Task DisconnectCancelsMovementAndRejectsNewCommands()
    {
        using var socket = await Registered();
        var id = await SubmitMove(socket);
        socket.Abort();
        await WaitForRemoval();
        Assert.Equal("cancelled", (await Command(id)).Status);
        using var response = await Post("move_forward", new { distanceCm = 50, speed = 30 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ValidTelemetryIsAvailableThroughRestAndInvalidUpdatesAreIgnored()
    {
        using var socket = await Registered();
        var telemetry = new RobotTelemetry("robot-a", "idle", new(0, 50, 90), new(0, 0, 0), 100);
        await Send(socket, "telemetry", telemetry);
        await Synchronize(socket);
        Assert.Equal(telemetry, (await Robot()).Telemetry);

        await Send(socket, "telemetry", telemetry with { BatteryPercent = 101 });
        await ExpectError(socket, "invalid_telemetry");
        await Send(socket, "telemetry", telemetry with { RobotId = "other-robot" });
        await ExpectError(socket, "robot_id_mismatch");
        Assert.Equal(telemetry, (await Robot()).Telemetry);
    }

    [Fact]
    public async Task InvalidEnvelopeAndDuplicateIdsLeaveConnectionUsable()
    {
        using var socket = await Registered();
        await SendRaw(socket, """{"type":"heartbeat","id":"local-time","timestamp":"2026-11-04T18:00:00","payload":{"robotId":"robot-a"}}"""u8.ToArray());
        await ExpectError(socket, "invalid_message");

        var heartbeat = """{"type":"heartbeat","id":"replay","timestamp":"2026-11-04T18:00:00Z","payload":{"robotId":"robot-a"}}"""u8.ToArray();
        await SendRaw(socket, heartbeat);
        await SendRaw(socket, heartbeat);
        await ExpectError(socket, "duplicate_message_id");
        await Synchronize(socket);
    }

    [Fact]
    public async Task OversizedMessageClosesWithMessageTooBig()
    {
        using var socket = await Connect();
        await SendRaw(socket, new byte[4097]);
        var frame = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), Token);
        Assert.Equal(WebSocketMessageType.Close, frame.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, frame.CloseStatus);
    }

    private Task<WebSocket> Connect() =>
        factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/robot"), Token);

    private async Task<WebSocket> Registered()
    {
        var socket = await Connect();
        await Register(socket, "robot-a");
        Assert.Equal("register_ack", Field(await Receive(socket), "type"));
        return socket;
    }

    private Task Register(WebSocket socket, string robotId, int version = 1, string deviceType = "simulator") =>
        Send(socket, "register", new { robotId, name = "Test robot", deviceType, protocolVersion = version,
            capabilities = new[] { "movement" } });

    private Task Ack(WebSocket socket, string commandId, string status) =>
        Send(socket, "command_ack", new { commandId, status });

    private Task Result(WebSocket socket, string commandId, string status, string? message = null) =>
        Send(socket, "command_result", new { commandId, status, message });

    private Task Send(WebSocket socket, string type, object payload) =>
        SendRaw(socket, JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = Guid.NewGuid().ToString("N"), type, timestamp = clock.GetUtcNow(), payload
        }, JsonSerializerOptions.Web));

    private Task SendRaw(WebSocket socket, byte[] data) =>
        socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, Token).AsTask();

    private async Task<JsonElement> Receive(WebSocket socket)
    {
        var buffer = new byte[4096];
        var frame = await socket.ReceiveAsync(buffer.AsMemory(), Token);
        Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        Assert.True(frame.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
        return document.RootElement.Clone();
    }

    private async Task Synchronize(WebSocket socket)
    {
        await SendRaw(socket, """{"type":"ping"}"""u8.ToArray());
        Assert.Equal("pong", Field(await Receive(socket), "type"));
    }

    private async Task ExpectError(WebSocket socket, string code)
    {
        var response = await Receive(socket);
        Assert.Equal("error", Field(response, "type"));
        Assert.Equal(code, Field(response.GetProperty("payload"), "code"));
    }

    private async Task<string> SubmitMove(WebSocket socket)
    {
        using var response = await Post("move_forward", new { distanceCm = 50, speed = 30 });
        var status = await AcceptedCommand(response);
        var command = await Receive(socket);
        Assert.Equal("command", Field(command, "type"));
        Assert.Equal(status.CommandId, Field(command, "id"));
        Assert.Equal("move_forward", Field(command.GetProperty("payload"), "command"));
        return status.CommandId;
    }

    private async Task<RobotCommandStatus> AcceptedCommand(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<RobotCommandStatus>(Token))!;
        Assert.Equal("pending", status.Status);
        Assert.EndsWith(status.CommandId, response.Headers.Location?.ToString());
        return status;
    }

    private Task<HttpResponseMessage> Post(string command, object parameters) =>
        client.PostAsJsonAsync("/api/robots/robot-a/commands", new { command, parameters }, Token);

    private async Task<RobotDetails> Robot() =>
        (await client.GetFromJsonAsync<RobotDetails>("/api/robots/robot-a", Token))!;

    private async Task<RobotCommandStatus> Command(string id) =>
        (await client.GetFromJsonAsync<RobotCommandStatus>($"/api/robots/robot-a/commands/{id}", Token))!;

    private async Task WaitForRemoval()
    {
        while (true)
        {
            using var response = await client.GetAsync("/api/robots/robot-a", Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return;
            response.EnsureSuccessStatusCode();
            await Task.Delay(20, Token);
        }
    }

    private static string? Field(JsonElement body, string name) => body.GetProperty(name).GetString();

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        deadline.Dispose();
    }

    private sealed class TestClock : TimeProvider
    {
        private long seconds;
        public void Advance(long amount) => Interlocked.Add(ref seconds, amount);
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref seconds) * TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddSeconds(Interlocked.Read(ref seconds));
    }
}
