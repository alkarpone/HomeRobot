using HomeRobot.Server.Speech;
using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/robots/{robotId}/ai")]
public sealed class RobotAiController(RobotRegistry registry, IOptions<RobotProtocolOptions> configuration) : ControllerBase
{
    [HttpPost("command")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Submit(string robotId, CommandRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2000)
            return Problem(statusCode: 400, title: "Message must contain text and at most 2000 characters.");

        var voice = request.VoiceTurn.HasValue
            ? VoiceIntent.Analyze(new SpeechTranscript(request.Message, true), configuration.Value.DefaultSpeed)
            : null;
        if (voice is not null && voice.Route != "action")
            return Rejected(robotId, "Unsupported or ambiguous voice instruction.");

        var (lease, error) = registry.ReservePlan(robotId, request.VoiceTurn, request.Message);
        if (lease is null)
            return StatusCode(error == "robot_not_connected" ? 404 : 409, new { code = error });

        var started = false;
        try
        {
            IReadOnlyList<RobotCommand> commands;
            SpeechCorrection? correction = null;
            if (voice?.Actions is { } actions)
            {
                correction = new(voice.Text, voice.Text == request.Message ? "unchanged" : "corrected");
                var capabilities = registry.Find(robotId)?.Capabilities;
                if (actions.Any(action => capabilities?.Contains(action.IsMovement ? "movement" : "display") != true))
                    return Rejected(robotId, "The robot lacks a required capability.", correction);
                if (actions.Any(action => !action.HasValidParameters(configuration.Value)))
                    return Rejected(robotId, "The voice plan exceeds the configured command limits.", correction);
                commands = actions;
            }
            else
            {
                var interpreter = HttpContext.RequestServices.GetRequiredService<IRobotCommandInterpreter>();
                var interpretation = await interpreter.InterpretAsync(request.Message, cancellationToken);
                if (interpretation.Error is { } validationError)
                    return Rejected(robotId, validationError);
                commands = interpretation.Actions;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var executor = HttpContext.RequestServices.GetRequiredService<RobotPlanExecutor>();
            var plan = executor.TryStart(robotId, lease, commands, correction);
            if (plan is null)
                return StatusCode(503, new { code = "plan_capacity_reached" });

            started = true;
            return AcceptedAtAction(nameof(GetPlan), new { robotId, planId = plan.PlanId }, plan);
        }
        catch (AIProviderException ex)
        {
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
        finally
        {
            if (!started)
                registry.ReleasePlan(robotId, lease);
        }
    }

    [HttpGet("plans/{planId}")]
    public IActionResult GetPlan(string robotId, string planId)
    {
        var executor = HttpContext.RequestServices.GetRequiredService<RobotPlanExecutor>();
        return executor.Find(robotId, planId) is { } plan ? Ok(plan) : NotFound();
    }

    private IActionResult Rejected(string robotId, string error, SpeechCorrection? correction = null) =>
        UnprocessableEntity(new RobotPlanStatus(RobotProtocol.NewId(), robotId, "rejected", [], [error],
            CorrectedText: correction?.Text, CorrectionStatus: correction?.Status));

    public sealed record CommandRequest(string? Message, Guid? VoiceTurn = null);
}
