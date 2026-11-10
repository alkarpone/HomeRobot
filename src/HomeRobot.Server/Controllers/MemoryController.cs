using HomeRobot.Server.AI;
using HomeRobot.Server.Dashboard;
using HomeRobot.Server.Memory;
using Microsoft.AspNetCore.Mvc;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/memory")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MemoryController(MemoryStore store, ConversationPrompt prompt) : ControllerBase
{
    [HttpGet]
    public IActionResult List([FromQuery] string? category)
    {
        if (!CanManage())
            return StatusCode(403);
        return category is null || MemoryStore.ValidCategory(category) ? Ok(store.List(category)) : BadRequest();
    }

    [HttpPost]
    [RequestSizeLimit(8192)]
    public IActionResult Create(MemoryRequest request)
    {
        if (!CanManage())
            return StatusCode(403);
        if (!Valid(request))
            return BadRequest();
        var entry = store.Create(request.Content!, request.Category, "api");
        return entry is null ? Conflict() : StatusCode(201, entry);
    }

    [HttpPut("{id:guid}")]
    [RequestSizeLimit(8192)]
    public IActionResult Update(Guid id, MemoryRequest request)
    {
        if (!CanManage())
            return StatusCode(403);
        if (!Valid(request))
            return BadRequest();
        var previous = store.Snapshot(null).FirstOrDefault(row => row.Entry.Id == id);
        if (previous is null)
            return NotFound();
        return store.Update(id, request.Content!, request.Category, previous.Revision) ? NoContent() : Conflict();
    }

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id)
    {
        if (!CanManage())
            return StatusCode(403);
        return store.Delete(id) ? NoContent() : NotFound();
    }

    [HttpDelete]
    public IActionResult DeleteAll()
    {
        if (!CanManage())
            return StatusCode(403);
        if (Request.Headers["X-Confirm-Memory-Deletion"] != "delete-all")
            return Problem(statusCode: 400, title: "Set X-Confirm-Memory-Deletion to delete-all to confirm.");
        store.DeleteAll(null);
        return NoContent();
    }

    [HttpGet("/api/personality")]
    public IActionResult GetPersonality() =>
        CanManage() ? Ok(prompt.Profile) : StatusCode(403);

    [HttpPut("/api/personality")]
    [RequestSizeLimit(8192)]
    public IActionResult PutPersonality(PersonalityProfile profile)
    {
        if (!CanManage())
            return StatusCode(403);
        if (!profile.IsValid())
            return BadRequest();
        prompt.Save(profile);
        return Ok(profile);
    }

    [HttpPost("/api/personality/preview")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> Preview(PreviewRequest request, [FromServices] IAIProvider provider,
        CancellationToken cancellationToken)
    {
        if (!CanManage())
            return StatusCode(403);
        if (request.Profile is null || !request.Profile.IsValid())
            return Problem(statusCode: 400, title: "Invalid personality settings.");
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 800)
            return Problem(statusCode: 400, title: "Enter a sample question of at most 800 characters.");

        try
        {
            var messages = prompt.Compose(request.Profile, [], new ChatMessage("user", request.Message), [], false);
            var (response, _) = ConversationPrompt.ReadReply(
                await provider.GenerateAsync(messages, cancellationToken), request.Profile.DefaultExpression);
            return Ok(new { response });
        }
        catch (AIProviderException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    public sealed record PreviewRequest(PersonalityProfile? Profile, string? Message);

    private bool CanManage() => DashboardAccess.AllowsManagement(HttpContext) || MemoryAccess.IsLocal(HttpContext);
    private static bool Valid(MemoryRequest request) =>
        MemoryStore.ValidContent(request.Content) && MemoryStore.ValidCategory(request.Category);

    public sealed record MemoryRequest(string? Content, string Category = "other");
}
