using Microsoft.AspNetCore.Mvc;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "HomeRobot.Server" });
}
