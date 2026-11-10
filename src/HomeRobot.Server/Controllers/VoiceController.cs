using System.Diagnostics;
using HomeRobot.Server.AI;
using HomeRobot.Server.Dashboard;
using HomeRobot.Server.Memory;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Speech;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/voice")]
public sealed class VoiceController(
    ISpeechToTextService stt, ITextToSpeechService tts, ConversationStore conversations, IAIProvider ai,
    RobotRegistry robots, IOptions<RobotProtocolOptions> configuration)
    : ControllerBase
{
    private const int MaxUploadRequest = WavAudio.MaxBytes + 64 * 1024;

    [HttpPost("transcribe")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequest)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequest)]
    public async Task<IActionResult> Transcribe([FromForm] IFormFile? audio, [FromForm] string? robotId,
        [FromForm] Guid? sessionId, [FromForm] bool conversation, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var turn = robotId is null ? null : robots.GetVoiceTurn(robotId);
        try
        {
            var transcript = await TranscribeAsync(audio, cancellationToken);
            var sttMs = timer.ElapsedMilliseconds;
            if (conversation && transcript.Text.Length is > 0 and <= 2000 && !transcript.Text.Any(char.IsControl) &&
                MemoryCommands.IsExplicit(transcript.Text))
            {
                var memory = await conversations.ReplyAsync(sessionId, new("user", transcript.Text), ai, cancellationToken,
                    briefCzech: true, localMemory: MemoryAccess.IsLocal(HttpContext), clearSpeech: transcript.ClearSpeech);
                if (memory is not { } reply)
                    return Problem(statusCode: 503, title: "Conversation capacity reached.");
                return Ok(new
                {
                    text = transcript.Text, route = "memory", intent = "memory", reply.SessionId,
                    reply.Response, reply.Expression, sttMs, analysisMs = timer.ElapsedMilliseconds - sttMs
                });
            }
            var intent = VoiceIntent.Analyze(transcript, configuration.Value.DefaultSpeed);
            cancellationToken.ThrowIfCancellationRequested();
            var voiceTurn = intent.Route == "action" && turn is { } generation
                ? robots.AuthorizeVoiceInstruction(robotId!, generation, transcript.Text) : null;
            var stopSubmitted = intent.Route == "stop" && turn is { } stopTurn &&
                robots.TryVoiceStop(robotId!, stopTurn);
            return Ok(new
            {
                text = transcript.Text, correctedText = intent.Text, route = intent.Route, intent = intent.Intent,
                reason = intent.Reason, voiceTurn, stopSubmitted, sttMs,
                correctionStatus = intent.Text == transcript.Text ? "unchanged" : "normalized",
                analysisMs = timer.ElapsedMilliseconds - sttMs
            });
        }
        catch (SpeechException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    [HttpPost("synthesize")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Synthesize(SynthesisRequest request,
        [FromServices] DashboardDiagnostics diagnostics, [FromServices] ILogger<VoiceController> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 8000)
            return Problem(statusCode: 400, title: "Text must contain 1 to 8000 characters.");

        try
        {
            var wave = await tts.SynthesizeAsync(request.Text, cancellationToken);
            diagnostics.RecordSpeech(false, "ready");
            logger.LogInformation("Piper sample synthesis completed.");
            return File(wave, "audio/wav");
        }
        catch (SpeechException ex)
        {
            diagnostics.RecordSpeech(false, "failed", ex.Message);
            logger.LogWarning("Piper sample synthesis failed.");
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    [HttpPost("chat")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequest)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequest)]
    public async Task<IActionResult> Chat(
        [FromForm] IFormFile? audio, [FromForm] Guid? sessionId, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        try
        {
            var transcript = await TranscribeAsync(audio, cancellationToken);
            var transcription = transcript.Text;
            var sttMs = total.ElapsedMilliseconds;
            if (string.IsNullOrWhiteSpace(transcription))
                return Problem(statusCode: 422, title: "No speech detected.");
            if (transcription.Length > 8000)
                return Problem(statusCode: 422, title: "Transcription exceeds 8000 characters.");

            var llm = Stopwatch.StartNew();
            var synthesis = Stopwatch.StartNew();
            byte[] wav = [];
            var result = await conversations.ReplyAsync(sessionId, new ChatMessage("user", RobotVocabulary.Normalize(transcription, commandWords: false)),
                ai, cancellationToken, async (response, token) =>
                {
                    llm.Stop();
                    synthesis.Restart();
                    wav = await tts.SynthesizeAsync(response, token);
                    synthesis.Stop();
                }, briefCzech: true, localMemory: MemoryAccess.IsLocal(HttpContext), clearSpeech: transcript.ClearSpeech);
            if (result is not { } reply)
                return Problem(statusCode: 503, title: "Conversation capacity reached.");

            var audioBase64 = Convert.ToBase64String(wav);
            return Ok(new
            {
                sessionId = reply.SessionId,
                transcription,
                response = reply.Response,
                expression = reply.Expression,
                audioBase64,
                timings = new
                {
                    sttMs, llmMs = llm.ElapsedMilliseconds, ttsMs = synthesis.ElapsedMilliseconds,
                    totalMs = total.ElapsedMilliseconds
                }
            });
        }
        catch (SpeechException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
        catch (AIProviderException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    private async Task<SpeechTranscript> TranscribeAsync(IFormFile? audio, CancellationToken cancellationToken)
    {
        if (audio is null || audio.Length == 0)
            throw new SpeechException(400, "The audio form field must contain a WAV file.");
        if (audio.Length > WavAudio.MaxBytes)
            throw new SpeechException(413, "WAV upload exceeds 6 MiB.");

        using var input = audio.OpenReadStream();
        var wav = new byte[(int)audio.Length];
        await input.ReadExactlyAsync(wav, cancellationToken);
        if (!WavAudio.TryValidate(wav, out var silent))
            throw new SpeechException(400, "Expected 16-bit PCM WAV (8–48 kHz, mono or stereo, up to 30 seconds).");
        if (silent)
            return new("", false);

        using var validated = new MemoryStream(wav, writable: false);
        var result = await stt.TranscribeWithConfidenceAsync(validated, cancellationToken);
        return result with { Text = result.Text.Trim() };
    }

    public sealed record SynthesisRequest(string? Text);
}
