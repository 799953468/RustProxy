using Microsoft.AspNetCore.Mvc;
using RustProxy.Server.Services;

namespace RustProxy.Server.Controllers;

/// <summary>Serves Cargo source files from imported packages.</summary>
[ApiController]
[Route("cargo")]
public sealed class CargoController(PackageCatalog catalog, IConfiguration configuration) : ControllerBase
{
    /// <summary>Returns a Cargo source file.</summary>
    [HttpGet("{**path}")]
    public async Task<IActionResult> GetAsync(string? path, CancellationToken cancellationToken)
    {
        var file = await catalog.FindSourceFileAsync("cargo", path, cancellationToken);
        if (file is null) return NotFound();
        if (path == "index/config.json") return CargoIndexConfiguration.Create(this, configuration, "cargo");
        return SourceFileResponse.Create(this, file, path?.StartsWith("index/", StringComparison.Ordinal) == true ? "application/json" : "application/octet-stream", immutable: false);
    }
}
