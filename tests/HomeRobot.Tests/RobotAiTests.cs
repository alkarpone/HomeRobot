using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeRobot.Server.AI;
using HomeRobot.Server.Speech;
using Xunit;

namespace HomeRobot.Tests;

public sealed partial class RobotProtocolTests
{
    private const string AiCommandUrl = "/api/robots/robot-a/ai/command";
    private const string TwoActions = """
        {"actions":[{"command":"rotate_right","parameters":{"angleDegrees":90,"speed":30}},
                    {"command":"move_forward","parameters":{"distanceCm":50,"speed":30}}]}
        """;
    private readonly MockCommandProvider ai = new();

    [Theory]
    [InlineData("Otoč se doprava o 90 stupňů a popojeď 50 centimetrů.")]
    [InlineData("Turn right 90 degrees and move forward 50 centimeters.")]
    public async Task AiPlanWaitsForConfirmedCompletionBeforeSubmittingNextAction(string message)
    {
        using var socket = await Registered();
        var (url, accepted) = await AcceptPlan(message);
        Assert.Equal("robot-a", Field(accepted, "robotId"));
        Assert.Equal("pending", Field(accepted, "status"));
        Assert.Equal(message, ai.Messages![1].Content);
        Assert.Contains("Czech or English", ai.Messages[0].Content);
        Assert.Equal("system", ai.Messages[0].Role);

        var first = await Receive(socket);
        Assert.Equal("rotate_right", Field(first.GetProperty("payload"), "command"));
        var firstId = Field(first, "id")!;
        await Ack(socket, firstId, "accepted");
        await Synchronize(socket);
        var running = await WaitForPlan(url, "running");
        AssertActionStates(running, "running", "pending");

        await Result(socket, firstId, "completed");
        var second = await Receive(socket);
        Assert.Equal("move_forward", Field(second.GetProperty("payload"), "command"));
        var secondId = Field(second, "id")!;
        await Ack(socket, secondId, "accepted");
        await Result(socket, secondId, "completed");
        AssertActionStates(await WaitForPlan(url, "completed"), "completed", "completed");

        using var wrongRobot = await client.GetAsync(url.ToString().Replace("robot-a", "robot-b"), Token);
        Assert.Equal(HttpStatusCode.NotFound, wrongRobot.StatusCode);
    }

    [Theory]
    [InlineData("invalid JSON", "Invalid action plan JSON")]
    [InlineData("""{"actions":[],"actions":[]}""", "Invalid action plan JSON")]
    [InlineData("""{"actions":[]}""", "ambiguous, unsafe, or unsupported")]
    [InlineData("""{"actions":[{"command":"raw","parameters":{}}]}""", "Action 1")]
    [InlineData("""{"actions":[{"command":"rotate_right","parameters":{"angleDegrees":90}}]}""", "Action 1")]
    [InlineData("""
        {"actions":[{"command":"move_forward","parameters":{"distanceCm":50,"speed":30}},
                    {"command":"move_backward","parameters":{"distanceCm":101,"speed":30}}]}
        """, "Action 2")]
    [InlineData("""{"actions":[{},{},{},{},{},{}]}""", "at most 5")]
    public async Task AiRejectsEntireInvalidPlan(string output, string explanation)
    {
        using var socket = await Registered();
        ai.Output = output;
        using var response = await SubmitPlan("Test request");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("rejected", Field(plan, "status"));
        Assert.Empty(plan.GetProperty("actions").EnumerateArray());
        Assert.Contains(explanation, plan.GetProperty("validationErrors")[0].GetString());
        await Synchronize(socket);
    }

    [Theory]
    [InlineData("Jeď někam dopředu.")]
    [InlineData("Jeď dopředu 100 kilometrů.")]
    [InlineData("Move forward 100 cm at 20 percent.")]
    public async Task AiRejectsInventedOrClampedMovementValues(string message)
    {
        using var socket = await Registered();
        ai.Output = """{"actions":[{"command":"move_forward","parameters":{"distanceCm":100,"speed":30}}]}""";
        using var response = await SubmitPlan(message);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await Synchronize(socket);
    }

    [Theory]
    [InlineData("failed", "failed", "obstacle")]
    [InlineData("disconnect", "cancelled", "disconnected")]
    [InlineData("timeout", "failed", "command_timeout")]
    public async Task AiAbortsRemainingActionsWhenP4CannotCompleteMovement(
        string outcome, string expectedStatus, string expectedMessage)
    {
        using var socket = await Registered();
        var (url, _) = await AcceptPlan();
        var id = Field(await Receive(socket), "id")!;
        switch (outcome)
        {
            case "failed":
                await Result(socket, id, "failed", "obstacle");
                break;
            case "disconnect":
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, Token);
                break;
            case "timeout":
                clock.Advance(11);
                break;
        }

        var plan = await WaitForPlan(url, expectedStatus);
        AssertActionStates(plan, expectedStatus, "cancelled");
        Assert.Equal(expectedMessage, Field(plan, "message"));
        if (outcome == "failed")
            await Synchronize(socket);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task P4StopCancelsAiPlanAndRejectsConcurrentPlans(bool stopThroughCommandEndpoint)
    {
        using var socket = await Registered();
        var (url, _) = await AcceptPlan();
        var movement = Field(await Receive(socket), "id")!;
        using var concurrent = await SubmitPlan("Move forward 50 cm.");
        Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);

        using var response = stopThroughCommandEndpoint
            ? await Post("stop", new { })
            : await client.PostAsync("/api/robots/robot-a/stop", null, Token);
        var stop = await AcceptedCommand(response);
        var frame = await Receive(socket);
        Assert.Equal(stop.CommandId, Field(frame, "id"));
        Assert.Equal("stop", Field(frame.GetProperty("payload"), "command"));

        var plan = await WaitForPlan(url, "cancelled");
        AssertActionStates(plan, "cancelled", "cancelled");
        Assert.Equal("emergency_stop", Field(plan, "message"));
        Assert.Equal("cancelled", (await Command(movement)).Status);
        await Ack(socket, stop.CommandId, "accepted");
        await Result(socket, stop.CommandId, "completed");
        await Synchronize(socket);
    }

    [Fact]
    public async Task P4StopAlsoCancelsPlanDuringInterpretation()
    {
        using var socket = await Registered();
        ai.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = SubmitPlan("Turn right 90 degrees and move forward 50 cm.");
        await ai.Entered.Task.WaitAsync(Token);
        using var concurrent = await SubmitPlan("Move forward 50 cm.");
        Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);

        using var response = await client.PostAsync("/api/robots/robot-a/stop", null, Token);
        var stop = await AcceptedCommand(response);
        Assert.Equal(stop.CommandId, Field(await Receive(socket), "id"));
        await Ack(socket, stop.CommandId, "accepted");
        await Result(socket, stop.CommandId, "completed");
        await Synchronize(socket);

        ai.Release.SetResult();
        using var accepted = await pending;
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        AssertActionStates(await WaitForPlan(accepted.Headers.Location!, "cancelled"), "cancelled", "cancelled");
        await Synchronize(socket);
    }

    [Theory]
    [InlineData("happy")]
    [InlineData("smile")]
    public async Task AiPlanCanSetAnExpressionAndStop(string expression)
    {
        using var socket = await Registered();
        ai.Output = """
            {"actions":[{"command":"set_expression","parameters":{"expression":"EXPRESSION"}},
                        {"command":"stop","parameters":{}}]}
            """.Replace("EXPRESSION", expression);
        var (url, _) = await AcceptPlan("Usměj se a zastav.");
        foreach (var expected in new[] { "set_expression", "stop" })
        {
            var frame = await Receive(socket);
            var payload = frame.GetProperty("payload");
            Assert.Equal(expected, Field(payload, "command"));
            if (expected == "set_expression")
                Assert.Equal("happy", Field(payload.GetProperty("parameters"), "expression"));
            var id = Field(frame, "id")!;
            await Ack(socket, id, "accepted");
            await Result(socket, id, "completed");
        }

        AssertActionStates(await WaitForPlan(url, "completed"), "completed", "completed");
    }

    private Task<HttpResponseMessage> SubmitPlan(string message) =>
        client.PostAsJsonAsync(AiCommandUrl, new { message }, Token);

    private async Task<(Uri Url, JsonElement Plan)> AcceptPlan(string message = "Turn right 90 degrees, then move forward 50 cm.")
    {
        using var response = await SubmitPlan(message);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.EndsWith(Field(plan, "planId"), response.Headers.Location?.ToString());
        return (response.Headers.Location!, plan);
    }

    private async Task<JsonElement> WaitForPlan(Uri url, string state)
    {
        while (true)
        {
            var plan = await client.GetFromJsonAsync<JsonElement>(url, Token);
            if (Field(plan, "status") == state)
                return plan;
            await Task.Delay(20, Token);
        }
    }

    private static void AssertActionStates(JsonElement plan, params string[] statuses) =>
        Assert.Equal(statuses, plan.GetProperty("actions").EnumerateArray().Select(action => Field(action, "status")));

    private const string SpokenTurn = "Robote, otoč se do prava o devadesát stupňů rychlostí třicet procent.";
    private const string CorrectedTurn = "Robote, otoč se doprava o devadesát stupňů rychlostí třicet procent.";

    [Theory]
    [InlineData("Jeď 100 centimetrů dopředu.", 30)]
    [InlineData("Jeď 100 centimetrů dopředu, 25 procentní rychlostí.", 25)]
    public async Task VoiceMovementDispatchesWithDefaultOrExplicitSpeed(string text, int speed)
    {
        using var socket = await Registered();
        robotVoice.Text = text;
        var voiceTurn = await TranscribeRobotTurn();
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message = robotVoice.Text, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await Receive(socket);
        var payload = command.GetProperty("payload");
        Assert.Equal("move_forward", Field(payload, "command"));
        Assert.Equal(100, payload.GetProperty("parameters").GetProperty("distanceCm").GetDouble());
        Assert.Equal(speed, payload.GetProperty("parameters").GetProperty("speed").GetDouble());
        var id = Field(command, "id")!;
        await Ack(socket, id, "accepted");
        await Result(socket, id, "completed");
        await WaitForPlan(response.Headers.Location!, "completed");
        Assert.Null(ai.Messages);
    }

    [Fact]
    public async Task VoiceSequenceWithoutConjunctionDispatchesInOrder()
    {
        using var socket = await Registered();
        robotVoice.Text = "Jeď rovně 10 centimetrů odboč do prava 45 stupňů.";
        var voiceTurn = await TranscribeRobotTurn();
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message = robotVoice.Text, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var planUrl = response.Headers.Location!;

        var first = await Receive(socket);
        Assert.Equal("move_forward", Field(first.GetProperty("payload"), "command"));
        var firstId = Field(first, "id")!;
        await Ack(socket, firstId, "accepted");
        AssertActionStates(await WaitForPlan(planUrl, "running"), "running", "pending");
        await Result(socket, firstId, "completed");

        var second = await Receive(socket);
        Assert.Equal("rotate_right", Field(second.GetProperty("payload"), "command"));
        var secondId = Field(second, "id")!;
        await Ack(socket, secondId, "accepted");
        await Result(socket, secondId, "completed");
        AssertActionStates(await WaitForPlan(planUrl, "completed"), "completed", "completed");
        Assert.Null(ai.Messages);
    }

    [Fact]
    public async Task VoiceRejectsWholeSequenceWhenLaterActionExceedsLimits()
    {
        using var socket = await Registered();
        robotVoice.Text = "Jeď vpřed 20 cm na 25 % a pak couvni dva metry při rychlosti 30 procent.";
        var voiceTurn = await TranscribeRobotTurn();
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message = robotVoice.Text, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var rejection = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Contains("configured command limits", rejection.GetProperty("validationErrors")[0].GetString());
        Assert.Null(ai.Messages);
        await Synchronize(socket);
    }

    [Fact]
    public async Task FailedVoiceActionPreservesTheDeviceResultAndCannotBeReplayed()
    {
        using var socket = await Registered();
        var voiceTurn = await TranscribeRobotTurn();
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message = SpokenTurn, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await Receive(socket);
        await Result(socket, Field(command, "id")!, "failed", "obstacle");
        var plan = await WaitForPlan(response.Headers.Location!, "failed");
        Assert.Equal("obstacle", Field(plan.GetProperty("actions")[0], "message"));

        Assert.Equal(CorrectedTurn, Field(plan, "correctedText"));
        Assert.Equal("corrected", Field(plan, "correctionStatus"));
        Assert.Null(ai.Messages);

        using var replay = await client.PostAsJsonAsync(AiCommandUrl, new { message = SpokenTurn, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        await Synchronize(socket);
    }

    [Fact]
    public async Task DirectStopInvalidatesVoiceTurnEvenAfterStopCompletes()
    {
        using var socket = await Registered();
        var voiceTurn = await TranscribeRobotTurn();
        using var stop = await client.PostAsync("/api/robots/robot-a/stop", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        var command = await Receive(socket);
        Assert.Equal("stop", Field(command.GetProperty("payload"), "command"));
        var id = Field(command, "id")!;
        await Ack(socket, id, "accepted");
        await Result(socket, id, "completed");
        await Synchronize(socket);

        using var late = await client.PostAsJsonAsync(AiCommandUrl, new { message = SpokenTurn, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Null(ai.Messages);
        await Synchronize(socket);
    }

    [Fact]
    public async Task VoiceRejectsChangedTranscript()
    {
        using var socket = await Registered();
        var voiceTurn = await TranscribeRobotTurn();
        var message = SpokenTurn.Replace("devadesát", "osmdesát");
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(ai.Messages);
        await Synchronize(socket);
    }

    [Fact]
    public async Task VoiceRejectsWholeSequenceWhenRobotLacksACapability()
    {
        using var socket = await Registered();
        robotVoice.Text = "Jeď dopředu 20 centimetrů na 30 % a pak se usměj.";
        var voiceTurn = await TranscribeRobotTurn();
        using var response = await client.PostAsJsonAsync(AiCommandUrl, new { message = robotVoice.Text, voiceTurn }, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(ai.Messages);
        await Synchronize(socket);
    }

    [Theory]
    [InlineData("Zastav", "stop", true)]
    [InlineData("Nezastavuj", "ambiguous", false)]
    [InlineData("Nejezdi dopředu", "ambiguous", false)]
    [InlineData("Nejezdi 20 centimetrů dopředu, 30 procentní rychlostí.", "ambiguous", false)]
    [InlineData("Jeď někam", "ambiguous", false)]
    [InlineData("Vrať se do kuchyně", "unsupported", false)]
    public async Task VoiceTranscriptionDispatchesOnlyClearEmergencyStop(string text, string route, bool stop)
    {
        using var socket = await Registered();
        robotVoice.Text = text;
        var result = await TranscribeForRobot();
        Assert.Equal(route, Field(result, "route"));
        Assert.Equal(stop, result.GetProperty("stopSubmitted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("voiceTurn").ValueKind);
        if (stop)
        {
            var command = await Receive(socket);
            Assert.Equal("stop", Field(command.GetProperty("payload"), "command"));
        }
        Assert.Null(ai.Messages);
        await Synchronize(socket);
    }

    private async Task<Guid> TranscribeRobotTurn() =>
        (await TranscribeForRobot()).GetProperty("voiceTurn").GetGuid();

    private async Task<JsonElement> TranscribeForRobot()
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(VoiceTests.Wav()), "audio", "test.wav" },
            { new StringContent("robot-a"), "robotId" }
        };
        using var response = await client.PostAsync("/api/voice/transcribe", form, Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private readonly RobotVoiceTranscript robotVoice = new();

    private sealed class RobotVoiceTranscript : ISpeechToTextService
    {
        public string Text { get; set; } = SpokenTurn;

        public Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken) =>
            Task.FromResult(Text);

        public Task<SpeechTranscript> TranscribeWithConfidenceAsync(Stream audio, CancellationToken cancellationToken) =>
            Task.FromResult(new SpeechTranscript(Text, true));
    }

    private sealed class MockCommandProvider : IAIProvider
    {
        public string Output { get; set; } = TwoActions;
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; set; }

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Robot commands must request JSON output.");

        public async Task<string> GenerateJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            Messages = messages;
            Entered.TrySetResult();
            if (Release is not null)
                await Release.Task.WaitAsync(cancellationToken);
            return Output;
        }
    }
}
