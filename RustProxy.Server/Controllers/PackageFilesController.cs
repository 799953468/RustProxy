using Microsoft.AspNetCore.Mvc;
using RustProxy.Server.Services;
using RustProxy.Core.Serialization;

namespace RustProxy.Server.Controllers;

/// <summary>Serves files belonging to a named imported package.</summary>
[ApiController]
[Route("packages/{id:long}")]
public sealed class PackageFilesController(PackageCatalog catalog, IConfiguration configuration) : ControllerBase
{
    /// <summary>Returns a package file.</summary>
    [HttpGet("{**path}")]
    public async Task<IActionResult> GetAsync(long id, string? path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, "manifest.json", StringComparison.Ordinal))
        {
            var manifest = await catalog.FindAsync(id, cancellationToken);
            if (manifest is null) return NotFound();
            Response.Headers.CacheControl = "no-cache";
            return new JsonResult(manifest, RustProxyJsonContext.Default.Options);
        }
        var file = await catalog.FindPackageFileAsync(id, path, cancellationToken);
        if (file is null) return NotFound();
        if (path == "cargo/index/config.json") return CargoIndexConfiguration.Create(this, configuration, $"packages/{id}/cargo");
        return SourceFileResponse.Create(this, file, path?.StartsWith("cargo/index/", StringComparison.Ordinal) == true ? "application/json" : "application/octet-stream", immutable: true);
    }
}
