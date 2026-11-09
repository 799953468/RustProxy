using Microsoft.AspNetCore.Mvc;
using RustProxy.Server.Services;
using RustProxy.Server.Entities;
using SqlSugar;

namespace RustProxy.Server.Controllers;

/// <summary>Reports server availability.</summary>
[ApiController]
[Route("health")]
public sealed class HealthController(PackageCatalog catalog, ISqlSugarClient database, ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>Returns the server health status.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "rustproxy-server" });

    /// <summary>Checks database availability, disk capacity and the background integrity audit.</summary>
    [HttpGet("ready")]
    public async Task<IActionResult> ReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.Queryable<PackageMetadata>().Take(1).ToListAsync(cancellationToken);
            var drive = new DriveInfo(Path.GetPathRoot(catalog.RootDirectory)!);
            if (drive.AvailableFreeSpace < 64L * 1024 * 1024 || catalog.AuditFailures != 0)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable", auditFailures = catalog.AuditFailures });
            return Ok(new { status = "ready" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Readiness check failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
    }
}
