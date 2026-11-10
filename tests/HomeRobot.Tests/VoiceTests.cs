using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeRobot.Server.AI;
using HomeRobot.Server.Speech;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeRobot.Tests;

public sealed class VoiceTests : IDisposable
{
    private readonly VoiceDependencies speech = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public VoiceTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISpeechToTextService>(speech);
                services.AddSingleton<ITextToSpeechService>(speech);
                services.AddSingleton<IAIProvider>(speech);
            }));
        client = factory.CreateClient();
    }

    [Fact]
    public async Task ChatReturnsAudioAndTimingsAndSharesTextChatHistory()
    {
        using var start = await client.PostAsJsonAsync("/api/ai/chat", new { message = "První tah" });
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();

        using var response = await UploadAsync("chat", Wav(), sessionId.ToString());
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sessionId, result.GetProperty("sessionId").GetGuid());
        Assert.Equal("Jak se máš?", result.GetProperty("transcription").GetString());
        Assert.Equal("Mám se dobře.", result.GetProperty("response").GetString());
        Assert.Equal(Wav(), result.GetProperty("audioBase64").GetBytesFromBase64());
        Assert.Equal(new[] { "První tah", "Mám se dobře.", "Jak se máš?" }, speech.Messages.Skip(1));
        Assert.Equal("Mám se dobře.", speech.Synthesized);

        var timings = result.GetProperty("timings");
        var stages = new[] { "sttMs", "llmMs", "ttsMs" }.Select(key => timings.GetProperty(key).GetInt64());
        Assert.All(stages, value => Assert.True(value >= 0));
        Assert.True(timings.GetProperty("totalMs").GetInt64() >= stages.Sum());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duration")]
    [InlineData("format")]
    [InlineData("truncated")]
    public async Task RejectsInvalidWav(string failure)
    {
        var wav = failure switch
        {
            "malformed" => new byte[60],
            "duration" => Wav(samples: 30 * 16000 + 1),
            "format" => Wav(encoding: 3),
            _ => Wav()[..^1]
        };
        using var response = await UploadAsync("transcribe", wav);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, speech.Transcriptions);
    }

    [Fact]
    public async Task RejectsInvalidRequests()
    {
        using var missing = await UploadAsync("chat", null);
        using var invalidId = await UploadAsync("chat", Wav(), "invalid");
        using var emptyText = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = " " });
        using var oversized = await UploadAsync("transcribe", new byte[6 * 1024 * 1024 + 1]);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyText.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(0, speech.Transcriptions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SilenceReturnsEmptyTextAndSkipsChat(bool digitalSilence)
    {
        speech.Transcription = " ";
        var wav = Wav(sample: digitalSilence ? (short)0 : (short)100);
        using var transcribe = await UploadAsync("transcribe", wav);
        Assert.Equal("", (await transcribe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
        using var chat = await UploadAsync("chat", wav);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, chat.StatusCode);
        Assert.Equal(digitalSilence ? 0 : 2, speech.Transcriptions);
        Assert.Empty(speech.Messages);
        Assert.Null(speech.Synthesized);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(504)]
    public async Task SttErrorsSkipAiAndTts(int status)
    {
        speech.SttError = status;
        using var response = await UploadAsync("chat", Wav());
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(speech.Messages);
        Assert.Null(speech.Synthesized);
    }

    [Fact]
    public async Task FailedSynthesisDoesNotCommitChatTurn()
    {
        using var start = await client.PostAsJsonAsync("/api/ai/chat", new { message = "Original" });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();

        speech.TtsError = 502;
        using var failure = await UploadAsync("chat", Wav(), id.ToString());
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        using var synthesis = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Příliš žluťoučký kůň." });
        Assert.Equal(HttpStatusCode.BadGateway, synthesis.StatusCode);

        speech.TtsError = null;
        using var retry = await UploadAsync("chat", Wav(), id.ToString());
        retry.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "Original", "Mám se dobře.", "Jak se máš?" }, speech.Messages.Skip(1));
    }

    [Fact]
    public async Task SynthesizeReturnsWavAndPassesCzechText()
    {
        using var response = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Příliš žluťoučký kůň." });
        response.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Wav(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("Příliš žluťoučký kůň.", speech.Synthesized);
    }

    [Theory]
    [InlineData("Kolik je dva plus dva?", true, "conversation")]
    [InlineData("Otoč se doprava o 90 stupňů rychlostí 30 %.", true, "action")]
    [InlineData("Otoč se doprava o 90 stupňů.", true, "action")]
    [InlineData("Otoč se do prava o 90° na 30 %", true, "action")]
    [InlineData("Otoč se doprava o 90 stupňů na 30 %.", false, "unclear")]
    [InlineData("Otoč se doprava o 90 stupňů rychlostí hodně procent.", true, "clarification_required")]
    [InlineData("Otoč se doprava.", true, "clarification_required")]
    [InlineData("Otoč se doleva o 20 stupňů a jeď dopředu.", true, "clarification_required")]
    [InlineData("Nejezdi dopředu", true, "ambiguous")]
    [InlineData("Neotáčej se doprava o 90 stupňů na 30 %", true, "ambiguous")]
    [InlineData("Nezastavuj", true, "ambiguous")]
    [InlineData("Jeď někam", true, "ambiguous")]
    [InlineData("Vrať se do kuchyně", true, "unsupported")]
    [InlineData("Odbols doprava", true, "clarification_required")]
    [InlineData("Popojet padesát centimetrů", true, "clarification_required")]
    [InlineData("Mám kočku a psa.", true, "conversation")]
    [InlineData("Zastav?", true, "ambiguous")]
    [InlineData("Zastav.", true, "stop")]
    [InlineData("???", true, "invalid")]
    [InlineData("", true, "invalid")]
    public async Task VoiceRoutesTranscriptionsWithoutCallingAi(string text, bool clear, string route)
    {
        speech.Transcription = text;
        speech.ClearSpeech = clear;
        using var response = await UploadAsync("transcribe", Wav());
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(route, result.GetProperty("route").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("voiceTurn").ValueKind);
        Assert.Empty(speech.Messages);
        Assert.Null(speech.Synthesized);
    }

    [Fact]
    public async Task BriefVoiceConversationReusesChatAndKeepsReplyWhenPiperFails()
    {
        using var chat = await client.PostAsJsonAsync("/api/ai/chat", new { message = "Jak se máš?", briefCzech = true });
        chat.EnsureSuccessStatusCode();
        var result = await chat.Content.ReadFromJsonAsync<JsonElement>();
        var reply = result.GetProperty("response").GetString();
        Assert.Equal("Mám se dobře.", reply);
        Assert.Contains("Never claim to have executed an action", speech.Messages[0]);

        speech.TtsError = 503;
        using var synthesis = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = reply });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, synthesis.StatusCode);
        Assert.Equal(reply, speech.Synthesized);
    }

    private async Task<HttpResponseMessage> UploadAsync(string endpoint, byte[]? wav, string? sessionId = null)
    {
        using var form = new MultipartFormDataContent();
        if (wav is not null)
            form.Add(new ByteArrayContent(wav), "audio", "test.wav");
        if (sessionId is not null)
            form.Add(new StringContent(sessionId), "sessionId");
        return await client.PostAsync($"/api/voice/{endpoint}", form);
    }

    internal static byte[] Wav(int samples = 160, short encoding = 1, short sample = 100)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write(encoding);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write(sample);
        return stream.ToArray();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private sealed class VoiceDependencies : ISpeechToTextService, ITextToSpeechService, IAIProvider
    {
        public int Transcriptions { get; private set; }
        public string Transcription { get; set; } = "Jak se máš?";
        public bool ClearSpeech { get; set; }
        public int? SttError { get; set; }
        public int? TtsError { get; set; }
        public string? Synthesized { get; private set; }
        public string[] Messages { get; private set; } = [];

        public Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
        {
            Transcriptions++;
            return SttError is { } status
                ? Task.FromException<string>(new SpeechException(status, "Whisper error."))
                : Task.FromResult(Transcription);
        }

        public async Task<SpeechTranscript> TranscribeWithConfidenceAsync(Stream audio, CancellationToken cancellationToken) =>
            new(await TranscribeAsync(audio, cancellationToken), ClearSpeech);

        public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Synthesized = text;
            return TtsError is { } status
                ? Task.FromException<byte[]>(new SpeechException(status, "Piper error."))
                : Task.FromResult(Wav());
        }

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            Messages = messages.Select(message => message.Content).ToArray();
            return Task.FromResult("Mám se dobře.");
        }
    }
}
