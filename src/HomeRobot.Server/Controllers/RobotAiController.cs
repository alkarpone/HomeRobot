using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using Microsoft.AspNetCore.Mvc;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/robots/{robotId}/ai")]
public sealed class RobotAiController(RobotRegistry registry) : ControllerBase
{
    [HttpPost("command")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Submit(string robotId, CommandRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2000)
            return Problem(statusCode: 400, title: "Message must contain text and at most 2000 characters.");

        var (lease, error) = registry.ReservePlan(robotId);
        if (lease is null)
            return StatusCode(error == "robot_not_connected" ? 404 : 409, new { code = error });

        var started = false;
        try
        {
            var interpreter = HttpContext.RequestServices.GetRequiredService<IRobotCommandInterpreter>();
            var interpretation = await interpreter.InterpretAsync(request.Message, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (interpretation.Error is { } validationError)
                return UnprocessableEntity(new RobotPlanStatus(RobotProtocol.NewId(), robotId, "rejected", [], [validationError]));

            var executor = HttpContext.RequestServices.GetRequiredService<RobotPlanExecutor>();
            var plan = executor.TryStart(robotId, lease, interpretation.Actions);
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

    public sealed record CommandRequest(string? Message);
}
