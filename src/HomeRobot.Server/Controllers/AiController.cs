using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Speech;
using HomeRobot.Server.Memory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/ai")]
public sealed class AiController(
    ConversationStore conversations, IAIProvider provider, IOptions<OllamaOptions> options) : ControllerBase
{
    [HttpPost("chat")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Chat(ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 8000)
            return Problem(statusCode: 400, title: "Message must contain text and at most 8000 characters.");

        if (request.RobotId is { } robotId)
        {
            if (!RobotProtocol.IsIdentifier(robotId)) return BadRequest();
            var intent = VoiceIntent.Analyze(new SpeechTranscript(request.Message, true), 30);
            if (intent.Actions is { Count: 1 } actions && actions[0].Parameters is ExpressionParameters face)
            {
                if (!HttpContext.RequestServices.GetRequiredService<RobotRegistry>().SetFace(robotId, face.Expression))
                    return StatusCode(503);
                return Ok(new { sessionId = request.SessionId ?? Guid.NewGuid(), response = "Hotovo.", expression = (string?)null, model = "deterministic", faceCommand = true });
            }
        }

        try
        {
            var result = await conversations.ReplyAsync(
                request.SessionId, new ChatMessage("user", request.Message), provider, cancellationToken, briefCzech: request.BriefCzech,
                localMemory: MemoryAccess.IsLocal(HttpContext));
            if (result is not { } reply)
                return Problem(statusCode: 503, title: "Conversation capacity reached.");

            return Ok(new { sessionId = reply.SessionId, response = reply.Response, expression = reply.Expression, model = options.Value.Model });
        }
        catch (AIProviderException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    [HttpGet("status")]
    public Task<OllamaStatus> Status([FromServices] OllamaProvider ollama, CancellationToken cancellationToken) =>
        ollama.GetStatusAsync(cancellationToken);

    public sealed record ChatRequest(string? Message, Guid? SessionId, bool BriefCzech = false, string? RobotId = null);
}
