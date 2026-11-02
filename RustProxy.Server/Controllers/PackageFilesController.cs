using Microsoft.AspNetCore.Mvc;
using RustProxy.Server.Services;

namespace RustProxy.Server.Controllers;

/// <summary>Serves files belonging to a named imported package.</summary>
[ApiController]
[Route("packages/{name}")]
public sealed class PackageFilesController(PackageCatalog catalog) : ControllerBase
{
    /// <summary>Returns a package file.</summary>
    [HttpGet("{**path}")]
    public async Task<IActionResult> GetAsync(string name, string? path, CancellationToken cancellationToken)
    {
        var file = await catalog.FindPackageFileAsync(name, path, cancellationToken);
        if (file is null) return NotFound();
        if (path?.Contains("cargo/index/", StringComparison.OrdinalIgnoreCase) == true) Response.ContentType = "application/json";
        if (SourceFileResponse.IsNotModified(this, file)) return StatusCode(StatusCodes.Status304NotModified);
        return PhysicalFile(file, Response.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
    }
}
