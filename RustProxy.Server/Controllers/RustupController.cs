using Microsoft.AspNetCore.Mvc;
using RustProxy.Server.Services;

namespace RustProxy.Server.Controllers;

/// <summary>Serves rustup distribution files from imported packages.</summary>
[ApiController]
[Route("rustup")]
[Route("dist")]
public sealed class RustupController(PackageCatalog catalog) : ControllerBase
{
    /// <summary>Returns a rustup distribution file.</summary>
    [HttpGet("{**path}")]
    public async Task<IActionResult> GetAsync(string? path, CancellationToken cancellationToken)
    {
        var sourcePath = Request.Path.StartsWithSegments("/dist", StringComparison.OrdinalIgnoreCase)
            ? $"dist/{path}"
            : path;
        var file = await catalog.FindSourceFileAsync("rustup", sourcePath, cancellationToken);
        if (file is null) return NotFound();
        if (SourceFileResponse.IsNotModified(this, file)) return StatusCode(StatusCodes.Status304NotModified);
        return PhysicalFile(file, "application/octet-stream", enableRangeProcessing: true);
    }
}
