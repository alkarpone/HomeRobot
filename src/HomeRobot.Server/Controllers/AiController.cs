using HomeRobot.Server.AI;
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

        try
        {
            var result = await conversations.ReplyAsync(
                request.SessionId, new ChatMessage("user", request.Message), provider, cancellationToken);
            if (result is not { } reply)
                return Problem(statusCode: 503, title: "Conversation capacity reached.");

            return Ok(new { sessionId = reply.SessionId, response = reply.Response, model = options.Value.Model });
        }
        catch (AIProviderException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    [HttpGet("status")]
    public Task<OllamaStatus> Status([FromServices] OllamaProvider ollama, CancellationToken cancellationToken) =>
        ollama.GetStatusAsync(cancellationToken);

    public sealed record ChatRequest(string? Message, Guid? SessionId);
}
