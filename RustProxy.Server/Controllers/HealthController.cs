using Microsoft.AspNetCore.Mvc;

namespace RustProxy.Server.Controllers;

/// <summary>Reports server availability.</summary>
[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Returns the server health status.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "rustproxy-server" });
}
