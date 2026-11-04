using System.Diagnostics;
using HomeRobot.Server.AI;
using HomeRobot.Server.Speech;
using Microsoft.AspNetCore.Mvc;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/voice")]
public sealed class VoiceController(
    ISpeechToTextService stt, ITextToSpeechService tts, ConversationStore conversations, IAIProvider ai)
    : ControllerBase
{
    private const int MaxUploadRequest = WavAudio.MaxBytes + 64 * 1024;

    [HttpPost("transcribe")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequest)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequest)]
    public async Task<IActionResult> Transcribe([FromForm] IFormFile? audio, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { text = await TranscribeAsync(audio, cancellationToken) });
        }
        catch (SpeechException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    [HttpPost("synthesize")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Synthesize(SynthesisRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 8000)
            return Problem(statusCode: 400, title: "Text must contain 1 to 8000 characters.");

        try
        {
            return File(await tts.SynthesizeAsync(request.Text, cancellationToken), "audio/wav");
        }
        catch (SpeechException ex)
        {
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
            var transcription = await TranscribeAsync(audio, cancellationToken);
            var sttMs = total.ElapsedMilliseconds;
            if (string.IsNullOrWhiteSpace(transcription))
                return Problem(statusCode: 422, title: "No speech detected.");
            if (transcription.Length > 8000)
                return Problem(statusCode: 422, title: "Transcription exceeds 8000 characters.");

            var llm = Stopwatch.StartNew();
            var synthesis = Stopwatch.StartNew();
            byte[] wav = [];
            var result = await conversations.ReplyAsync(sessionId, new ChatMessage("user", transcription),
                ai, cancellationToken, async (response, token) =>
                {
                    llm.Stop();
                    synthesis.Restart();
                    wav = await tts.SynthesizeAsync(response, token);
                    synthesis.Stop();
                });
            if (result is not { } reply)
                return Problem(statusCode: 503, title: "Conversation capacity reached.");

            var audioBase64 = Convert.ToBase64String(wav);
            return Ok(new
            {
                sessionId = reply.SessionId,
                transcription,
                response = reply.Response,
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

    private async Task<string> TranscribeAsync(IFormFile? audio, CancellationToken cancellationToken)
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
            return "";

        using var validated = new MemoryStream(wav, writable: false);
        return (await stt.TranscribeAsync(validated, cancellationToken)).Trim();
    }

    public sealed record SynthesisRequest(string? Text);
}
