using System.Net;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;
using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Speech;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeRobot.Tests;

public sealed partial class RobotProtocolTests
{
    private async Task<System.Net.WebSockets.WebSocket> FaceDisplay(string id, string logical = "robot-a")
    {
        var socket = await Connect();
        await Send(socket, "register", new { robotId = id, name = "Display", deviceType = "simulator",
            protocolVersion = 1, capabilities = new[] { "display", "face_sync" }, faceRobotId = logical });
        Assert.Equal("register_ack", Field(await Receive(socket), "type"));
        return socket;
    }

    [Theory]
    [InlineData("Zamrač se.", "angry")]
    [InlineData("Usměj se.", "happy")]
    [InlineData("Buď smutný.", "sad")]
    [InlineData("Vypadáš překvapeně.", "surprised")]
    [InlineData("Překvap se.", "surprised")]
    [InlineData("Zavři oči.", "eyes_closed")]
    [InlineData("Vrať se do normálu.", "idle")]
    [InlineData("Mrkni.", "blink")]
    public void CzechFaceCommandsUseExistingIntentEngine(string text, string expression)
    {
        var intent = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Equal("action", intent.Route);
        Assert.Equal(new ExpressionParameters(expression), Assert.Single(intent.Actions!).Parameters);
    }

    [Fact]
    public async Task TextFaceCommandNotifiesBothDisplaysAndRestoresOnReconnectWithoutAI()
    {
        using var first = await FaceDisplay("robot-a");
        using var second = await FaceDisplay("display-b");
        Assert.Equal("idle", Field((await Receive(first)).GetProperty("payload"), "expression"));
        await Receive(second);
        using var response = await client.PostAsJsonAsync("/api/ai/chat", new { message = "Zamrač se.", robotId = "robot-a", sessionId = (Guid?)null }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var socket in new[] { first, second })
        {
            var frame = await Receive(socket);
            Assert.Equal("face_state", Field(frame, "type"));
            Assert.Equal("angry", Field(frame.GetProperty("payload"), "expression"));
        }
        Assert.Null(ai.Messages);
        using var replacement = await FaceDisplay("display-c");
        Assert.Equal("angry", Field((await Receive(replacement)).GetProperty("payload"), "expression"));
        Assert.Equal(3, factory.Services.GetRequiredService<RobotRegistry>().GetFace("robot-a").DisplayClients.Length);
    }

    [Fact]
    public async Task SharedFaceDeduplicatesRejectsInvalidAndBlinkPreservesEmotion()
    {
        using var display = await FaceDisplay("robot-a");
        await Receive(display);
        var registry = factory.Services.GetRequiredService<RobotRegistry>();
        Assert.True(registry.SetFace("robot-a", "happy"));
        await Receive(display);
        var revision = registry.GetFace("robot-a").Revision;
        Assert.True(registry.SetFace("robot-a", "happy"));
        Assert.Equal(revision, registry.GetFace("robot-a").Revision);
        Assert.False(registry.SetFace("robot-a", "unknown"));
        Assert.True(registry.SetFace("robot-a", "blink"));
        var blink = (await Receive(display)).GetProperty("payload");
        Assert.Equal("happy", Field(blink, "expression"));
        Assert.NotEqual(JsonValueKind.Null, blink.GetProperty("blinkAt").ValueKind);
        Assert.Equal("happy", registry.GetFace("robot-a").Expression);
        using var invalid = await Post("set_expression", new { expression = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await Synchronize(display); // No duplicate face messages queued.
    }

    [Fact]
    public async Task FaceSyncCommandCompletesWithoutDependingOnHardware()
    {
        using var display = await FaceDisplay("robot-a");
        await Receive(display);
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message = "Zamrač se." }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("angry", Field((await Receive(display)).GetProperty("payload"), "expression"));
        await WaitForPlan(response.Headers.Location!, "completed");
        Assert.Null(ai.Messages);
    }
}

public sealed class Esp32FaceTests
{
    [Fact]
    public void FaceBackpressureCoalescesWithoutDisconnectingOrDisplacingCommands()
    {
        var registry = new RobotRegistry(TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new RobotProtocolOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RobotRegistry>.Instance);
        var session = registry.Connect();
        Assert.Null(registry.Register(session, new("display", "Display", "simulator", 1, ["display", "face_sync"])));
        Assert.NotNull(registry.TakeMessage(session)); // Registration acknowledgement remains first.
        for (var i = 0; i < 64; i++) Assert.True(registry.TryQueue(session, i));
        registry.SetFace("display", "happy");
        registry.SetFace("display", "angry");
        Assert.True(registry.CheckConnection(session));
        for (var i = 0; i < 64; i++) Assert.Equal(i, registry.TakeMessage(session));
        var face = Assert.IsType<RobotMessage<RobotFaceState>>(registry.TakeMessage(session));
        Assert.Equal("angry", face.Payload.Expression);
        Assert.Null(registry.TakeMessage(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryUsesLogicalHTTPCommandAndReportsOffline(bool offline)
    {
        var handler = new FaceHandler(offline);
        using var http = new HttpClient(handler);
        if (offline)
            await Assert.ThrowsAsync<HttpRequestException>(() => Esp32FaceDelivery.DeliverAsync(http, new Uri("http://esp32"), "angry", default));
        else
            await Esp32FaceDelivery.DeliverAsync(http, new Uri("http://esp32"), "angry", default);
        Assert.Equal("/face", handler.Path);
        Assert.Equal("angry", handler.Expression);
    }

    private sealed class FaceHandler(bool offline) : HttpMessageHandler
    {
        public string? Path;
        public string? Expression;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content!.Headers.ContentLength);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Path = request.RequestUri!.AbsolutePath;
            Expression = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("expression").GetString();
            if (offline) throw new HttpRequestException("offline");
            return new(HttpStatusCode.Accepted);
        }
    }
}

public sealed class Esp32FaceRecoveryTests
{
    [Fact]
    public async Task OfflineDisplayDoesNotBlockStateAndRestoresLatestExpression()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Face:Esp32:Enabled"] = "true", ["Face:Esp32:RobotId"] = "robot-a", ["Face:Esp32:BaseUrl"] = "http://esp32" }).Build();
        var registry = new RobotRegistry(TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new RobotProtocolOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RobotRegistry>.Instance);
        var handler = new RecoveringDisplay();
        using var delivery = new Esp32FaceDelivery(new Factory(handler), configuration, registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Esp32FaceDelivery>.Instance);
        registry.SetFace("robot-a", "angry");
        await delivery.StartAsync(timeout.Token);
        async Task Wait(Func<bool> ready)
        {
            while (!ready()) await Task.Delay(20, timeout.Token);
        }
        await Wait(() => handler.RemoteExpression == "angry");
        registry.SetFace("robot-a", "blink");
        await Wait(() => handler.Blinks == 1);
        Assert.Equal("angry", registry.GetFace("robot-a").Expression);
        handler.Offline = true;
        registry.SetFace("robot-a", "sad");
        await Wait(() => delivery.Status == "error");
        Assert.Equal("sad", registry.GetFace("robot-a").Expression);
        handler.RemoteExpression = "idle"; // A device reboot loses its local state.
        handler.Offline = false;
        await Wait(() => handler.RemoteExpression == "sad" && delivery.Status == "delivered");
        Assert.Equal(1, handler.Blinks); // Never replay a temporary event after reconnect.
        Assert.Equal("idle", registry.GetFace("other-robot").Expression);
        await delivery.StopAsync(timeout.Token);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecoveringDisplay : HttpMessageHandler
    {
        public volatile bool Offline;
        public volatile string RemoteExpression = "idle";
        public volatile int Blinks;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("offline");
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { expression = RemoteExpression }) };
            var expression = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("expression").GetString()!;
            if (expression == "blink") Blinks++;
            else RemoteExpression = expression;
            return new(HttpStatusCode.Accepted);
        }
    }
}
