using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using HomeRobot.Server.Speech;
using HomeRobot.Server.AI;
using Microsoft.AspNetCore.TestHost;
using HomeRobot.Server.Communication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeRobot.Tests;

public sealed partial class RobotProtocolTests
{
    private readonly AudioTts audioTts = new();
    private sealed class AudioTts : ITextToSpeechService
    {
        public int Calls;
        public byte[] Wave = VoiceTests.Wav();
        public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Wave);
        }
    }

    private async Task<WebSocket> AudioRobot(string id = "speaker")
    {
        var socket = await Connect();
        await Send(socket, "register", new { robotId = id, name = "ESP32", deviceType = "esp32", protocolVersion = 1,
            capabilities = new[] { "audio_output", "tts_playback", "audio_stop" } });
        Assert.Equal("register_ack", Field(await Receive(socket), "type"));
        return socket;
    }

    private async Task<(string Id, JsonElement Parameters)> SynthesizeAudio(WebSocket socket, string robot = "speaker")
    {
        using var response = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Ahoj.", esp32RobotId = robot }, Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal(audioTts.Wave, await response.Content.ReadAsByteArrayAsync(Token));
        var command = await Receive(socket);
        Assert.Equal("audio_play", Field(command.GetProperty("payload"), "command"));
        return (Field(command, "id")!, command.GetProperty("payload").GetProperty("parameters"));
    }

    private async Task<HttpResponseMessage> Download(JsonElement parameters, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Field(parameters, "resource"));
        request.Headers.Add("X-Audio-Token", token ?? Field(parameters, "token"));
        return await client.SendAsync(request, Token);
    }

    [Fact]
    public async Task DualAudioSynthesizesOnceAndTracksActualPlaybackThroughCompletion()
    {
        using var socket = await AudioRobot();
        var (command, parameters) = await SynthesizeAudio(socket);
        Assert.Equal(1, audioTts.Calls);
        Assert.Equal(16000, parameters.GetProperty("sampleRate").GetInt32());
        Assert.Equal("pcm_s16le", Field(parameters, "encoding"));
        var robot = await client.GetFromJsonAsync<RobotDetails>("/api/robots/speaker", Token);
        Assert.Contains("audio_output", robot!.Capabilities);
        Assert.Equal("buffering", robot.Audio!.Status);
        using var downloaded = await Download(parameters);
        downloaded.EnsureSuccessStatusCode();
        Assert.Equal(audioTts.Wave[44..], await downloaded.Content.ReadAsByteArrayAsync(Token));
        await Ack(socket, command, "accepted");
        await Ack(socket, command, "executing");
        await Send(socket, "heartbeat", new { robotId = "speaker" });
        // A protocol error round trip orders preceding acknowledgements before the REST check.
        await Ack(socket, command, "accepted");
        await ExpectError(socket, "invalid_command_transition");
        robot = await client.GetFromJsonAsync<RobotDetails>("/api/robots/speaker", Token);
        Assert.Equal("playing", robot!.Audio!.Status);
        await Result(socket, command, "completed");
        await Result(socket, command, "completed");
        await ExpectError(socket, "invalid_command_transition");
        robot = await client.GetFromJsonAsync<RobotDetails>("/api/robots/speaker", Token);
        Assert.Equal("completed", robot!.Audio!.Status);
        using var released = await Download(parameters);
        Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);
    }

    private sealed class CountingConversationAi : IAIProvider
    {
        public int Calls;
        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult("Mám se dobře.");
        }
    }

    [Fact]
    public async Task TextConversationThenDualOutputUsesOneInferenceAndOneSynthesis()
    {
        var provider = new CountingConversationAi();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IAIProvider>(provider)));
        using var http = host.CreateClient();
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/robot"), Token);
        await Send(socket, "register", new { robotId = "speaker", name = "ESP32", deviceType = "esp32", protocolVersion = 1,
            capabilities = new[] { "audio_output", "tts_playback", "audio_stop" } });
        await Receive(socket);
        using var chat = await http.PostAsJsonAsync("/api/ai/chat", new { message = "Ahoj, jak se dnes máš?" }, Token);
        chat.EnsureSuccessStatusCode();
        var reply = await chat.Content.ReadFromJsonAsync<JsonElement>(Token);
        using var speech = await http.PostAsJsonAsync("/api/voice/synthesize",
            new { text = Field(reply, "response"), esp32RobotId = "speaker" }, Token);
        speech.EnsureSuccessStatusCode();
        Assert.Equal(audioTts.Wave, await speech.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal("audio_play", Field((await Receive(socket)).GetProperty("payload"), "command"));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, audioTts.Calls);
    }

    [Fact]
    public async Task AudioResourcesRequireTokenAndExpireAndCannotBeReadAfterDisconnect()
    {
        using var socket = await AudioRobot();
        var (_, parameters) = await SynthesizeAudio(socket);
        using var noToken = await client.GetAsync(Field(parameters, "resource"), Token);
        using var wrongToken = await Download(parameters, new string('A', 64));
        Assert.Equal(HttpStatusCode.NotFound, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
        clock.Advance(46);
        using var expired = await Download(parameters);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task AudioStopTargetsOnlyItsPlaybackAndResourcesAreReleased()
    {
        using var socket = await AudioRobot();
        var (play, parameters) = await SynthesizeAudio(socket);
        using var stale = await client.PostAsJsonAsync("/api/audio/speaker/stop", new { playbackId = "stale" }, Token);
        Assert.Equal(HttpStatusCode.Accepted, stale.StatusCode);
        using var stop = await client.PostAsJsonAsync("/api/audio/speaker/stop", new { playbackId = Field(parameters, "playbackId") }, Token);
        Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        using var repeated = await client.PostAsJsonAsync("/api/audio/speaker/stop", new { playbackId = Field(parameters, "playbackId") }, Token);
        Assert.Equal((await stop.Content.ReadFromJsonAsync<RobotCommandStatus>(Token))!.CommandId,
            (await repeated.Content.ReadFromJsonAsync<RobotCommandStatus>(Token))!.CommandId);
        var command = await Receive(socket);
        Assert.Equal("audio_stop", Field(command.GetProperty("payload"), "command"));
        await Ack(socket, play, "accepted");
        await Ack(socket, Field(command, "id")!, "accepted");
        await Result(socket, play, "cancelled");
        await Result(socket, Field(command, "id")!, "completed");
        await Result(socket, play, "completed");
        await ExpectError(socket, "invalid_command_transition");
        using var resource = await Download(parameters);
        Assert.Equal(HttpStatusCode.NotFound, resource.StatusCode);
    }

    [Fact]
    public void StopBeforeWebSocketDispatchRemovesQueuedAudio()
    {
        var registry = factory.Services.GetRequiredService<RobotRegistry>();
        var playback = factory.Services.GetRequiredService<AudioPlaybackService>();
        var session = registry.Connect();
        Assert.Null(registry.Register(session, new("queued-speaker", "ESP32", "esp32", 1,
            ["audio_output", "tts_playback", "audio_stop"])));
        var started = playback.Play("queued-speaker", VoiceTests.Wav());
        Assert.Null(started.Error);
        var stopped = playback.Stop("queued-speaker", started.PlaybackId!);
        Assert.Equal(202, stopped.Status);
        Assert.Equal("cancelled", stopped.Command!.Status);
        Assert.NotNull(registry.TakeMessage(session)); // Registration acknowledgement.
        Assert.Null(registry.TakeMessage(session)); // Neither stale play nor premature stop is sent.
        Assert.Equal("idle", registry.Find("queued-speaker")!.Audio!.Status);
        registry.Remove(session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("disconnected")]
    [InlineData("robot-a")]
    public async Task MacAudioSurvivesMissingOrIncompatibleDestination(string? destination)
    {
        using var simulator = await Registered();
        using var response = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Ahoj.", esp32RobotId = destination }, Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal(audioTts.Wave, await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(1, audioTts.Calls);
        if (destination is not null) Assert.True(response.Headers.Contains("X-Audio-Error"));
    }

    [Fact]
    public async Task CancellationBeforeSynthesisFinishesPreventsLateDispatch()
    {
        using var socket = await AudioRobot();
        const string playbackId = "cancel-during-piper";
        using var stop = await client.PostAsJsonAsync("/api/audio/speaker/stop", new { playbackId }, Token);
        Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        using var speech = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Ahoj.", esp32RobotId = "speaker", playbackId }, Token);
        speech.EnsureSuccessStatusCode();
        Assert.Equal("playback_cancelled", Assert.Single(speech.Headers.GetValues("X-Audio-Error")));
        Assert.Equal(audioTts.Wave, await speech.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task InvalidFormatAndBusySpeakerDoNotBreakMacAudio()
    {
        using var socket = await AudioRobot();
        await SynthesizeAudio(socket);
        using var busy = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Ahoj.", esp32RobotId = "speaker" }, Token);
        busy.EnsureSuccessStatusCode();
        Assert.Equal("audio_busy", Assert.Single(busy.Headers.GetValues("X-Audio-Error")));
        audioTts.Wave = [1, 2, 3];
        using var unsupported = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Ahoj.", esp32RobotId = "speaker" }, Token);
        unsupported.EnsureSuccessStatusCode();
        Assert.Equal("unsupported_audio_format", Assert.Single(unsupported.Headers.GetValues("X-Audio-Error")));
    }

    [Fact]
    public async Task PlaybackFailureIsVisibleAndRobotCanReconnectWithoutStaleAudio()
    {
        using var first = await AudioRobot();
        var (play, parameters) = await SynthesizeAudio(first);
        await Result(first, play, "failed", "Network interruption");
        await Result(first, play, "completed");
        await ExpectError(first, "invalid_command_transition");
        var robot = await client.GetFromJsonAsync<RobotDetails>("/api/robots/speaker", Token);
        Assert.Equal("error", robot!.Audio!.Status);
        Assert.Equal("Network interruption", robot.Audio.Error);
        using var released = await Download(parameters);
        Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
        using var second = await AudioRobot();
        var (next, nextParameters) = await SynthesizeAudio(second);
        Assert.NotEqual(play, next);
        Assert.NotEqual(Field(parameters, "playbackId"), Field(nextParameters, "playbackId"));
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
        using var lost = await Download(nextParameters);
        Assert.Equal(HttpStatusCode.NotFound, lost.StatusCode);
    }

    [Fact]
    public async Task IndependentRobotsHaveSeparateResourcesAndPlaybackDeadlinesExceedMotionDeadline()
    {
        using var first = await AudioRobot();
        using var second = await AudioRobot("second");
        var (play, parameters) = await SynthesizeAudio(first);
        var (_, other) = await SynthesizeAudio(second, "second");
        Assert.NotEqual(Field(parameters, "token"), Field(other, "token"));
        using var crossResource = await Download(other, Field(parameters, "token"));
        Assert.Equal(HttpStatusCode.NotFound, crossResource.StatusCode);
        clock.Advance(11);
        await Send(first, "heartbeat", new { robotId = "speaker" });
        await Ack(first, play, "accepted");
        await Ack(first, play, "accepted");
        await ExpectError(first, "invalid_command_transition");
        using var download = await Download(parameters);
        download.EnsureSuccessStatusCode();
    }
}
