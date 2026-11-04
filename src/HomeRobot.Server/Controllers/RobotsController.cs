using System.Text.Json;
using HomeRobot.Server.Communication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/robots")]
public sealed class RobotsController(RobotRegistry registry, IOptions<RobotProtocolOptions> options) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<RobotDetails> List() => registry.List();

    [HttpGet("{robotId}")]
    public ActionResult<RobotDetails> Get(string robotId) =>
        registry.Find(robotId) is { } robot ? robot : NotFound();

    [HttpPost("{robotId}/commands")]
    [RequestSizeLimit(4096)]
    public IActionResult Submit(string robotId, [FromBody] JsonElement body) =>
        RobotCommand.Parse(body, options.Value) is { } command
            ? SubmitCommand(robotId, command)
            : BadRequest(new { code = "invalid_command" });

    [HttpPost("{robotId}/stop")]
    public IActionResult Stop(string robotId) => SubmitCommand(robotId, new RobotCommand("stop", new EmptyParameters()));

    [HttpGet("{robotId}/commands/{commandId}")]
    public ActionResult<RobotCommandStatus> GetCommand(string robotId, string commandId) =>
        registry.FindCommand(robotId, commandId) is { } command ? command : NotFound();

    private IActionResult SubmitCommand(string robotId, RobotCommand command)
    {
        var result = registry.Submit(robotId, command);
        return result.Command is { } status
            ? AcceptedAtAction(nameof(GetCommand), new { robotId, commandId = status.CommandId }, status)
            : StatusCode(result.HttpStatus, new { code = result.Error });
    }
}
