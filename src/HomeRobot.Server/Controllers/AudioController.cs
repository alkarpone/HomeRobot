using HomeRobot.Server.Speech;
using Microsoft.AspNetCore.Mvc;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/audio")]
public sealed class AudioController(AudioPlaybackService playback, ITextToSpeechService tts) : ControllerBase
{
    [HttpGet("resources/{id}")]
    public IActionResult Download(string id, [FromHeader(Name = "X-Audio-Token")] string? token) =>
        token is not null && playback.Read(id, token) is { } pcm
            ? File(pcm, "application/octet-stream") : NotFound();

    [HttpPost("{robotId}/stop")]
    public IActionResult Stop(string robotId, StopRequest request)
    {
        if (!Communication.RobotProtocol.IsIdentifier(request.PlaybackId)) return BadRequest();
        var result = playback.Stop(robotId, request.PlaybackId);
        return StatusCode(result.Status, (object?)result.Command ?? new { code = result.Error });
    }

    [HttpPost("{robotId}/test")]
    public async Task<IActionResult> Test(string robotId, CancellationToken cancellationToken)
    {
        try
        {
            var wav = await tts.SynthesizeAsync("Ahoj, tady je domácí robot. Zkouška reproduktoru.", cancellationToken);
            var result = playback.Play(robotId, wav);
            return result.Error is null ? Accepted(new { result.PlaybackId }) : Conflict(new { code = result.Error });
        }
        catch (SpeechException ex) { return Problem(statusCode: ex.StatusCode, title: ex.Message); }
        catch (ArgumentException) { return Problem(statusCode: 422, title: "Unsupported speech audio format."); }
    }

    public sealed record StopRequest(string PlaybackId);
}
