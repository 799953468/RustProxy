using Microsoft.AspNetCore.Mvc;
using RustProxy.Server.Services;

namespace RustProxy.Server.Controllers;

/// <summary>Serves Cargo source files from imported packages.</summary>
[ApiController]
[Route("cargo")]
public sealed class CargoController(PackageCatalog catalog) : ControllerBase
{
    /// <summary>Returns a Cargo source file.</summary>
    [HttpGet("{**path}")]
    public async Task<IActionResult> GetAsync(string? path, CancellationToken cancellationToken)
    {
        var file = await catalog.FindSourceFileAsync("cargo", path, cancellationToken);
        if (file is null) return NotFound();
        if (path?.Contains("index/", StringComparison.OrdinalIgnoreCase) == true) Response.ContentType = "application/json";
        if (SourceFileResponse.IsNotModified(this, file)) return StatusCode(StatusCodes.Status304NotModified);
        return PhysicalFile(file, Response.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
    }
}
