using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeRobot.Server.AI;
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
        var pending = SubmitPlan("Turn right 90 degrees.");
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

    [Fact]
    public async Task AiPlanCanSetAnExpressionAndStop()
    {
        using var socket = await Registered();
        ai.Output = """
            {"actions":[{"command":"set_expression","parameters":{"expression":"happy"}},
                        {"command":"stop","parameters":{}}]}
            """;
        var (url, _) = await AcceptPlan("Show a happy expression and stop.");
        foreach (var expected in new[] { "set_expression", "stop" })
        {
            var frame = await Receive(socket);
            Assert.Equal(expected, Field(frame.GetProperty("payload"), "command"));
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
