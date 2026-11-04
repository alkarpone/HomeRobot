using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HomeRobot.Tests;

public sealed class ServerTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HealthReturnsServiceStatus()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("HomeRobot.Server", body.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task FragmentedPingReturnsPongAndConnectionClosesGracefully()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var socket = await ConnectAsync(token);

        await socket.SendAsync("{\"type\":"u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, token);
        await socket.SendAsync("\"ping\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
        using var response = await ReceiveAsync(socket, token);
        Assert.Equal("pong", response.RootElement.GetProperty("type").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Theory]
    [InlineData("{", "invalid_json")]
    [InlineData("[]", "invalid_message")]
    [InlineData("{}", "invalid_message")]
    [InlineData("{\"type\":42}", "invalid_message")]
    [InlineData("{\"type\":\"unknown\"}", "unsupported_message_type")]
    public async Task InvalidMessageReturnsErrorAndConnectionRemainsUsable(string message, string code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var socket = await ConnectAsync(token);

        await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, token);
        using var error = await ReceiveAsync(socket, token);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal(code, error.RootElement.GetProperty("code").GetString());

        await socket.SendAsync("{\"type\":\"ping\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
        using var pong = await ReceiveAsync(socket, token);
        Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
    }

    private Task<WebSocket> ConnectAsync(CancellationToken token) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/robot"), token);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[256];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            message.Write(buffer, 0, frame.Count);
        }
        while (!frame.EndOfMessage);

        return JsonDocument.Parse(message.ToArray());
    }
}
