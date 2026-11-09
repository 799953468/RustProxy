using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using RustProxy.Server.Services;

namespace RustProxy.Server.Controllers;

internal static class SourceFileResponse
{
    public static PhysicalFileResult Create(ControllerBase controller, StoredSourceFile file, string contentType, bool immutable)
    {
        controller.Response.Headers.CacheControl = immutable ? "public,max-age=31536000,immutable" : "no-cache";
        controller.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return controller.PhysicalFile(file.Path, contentType, lastModified: null,
            entityTag: new EntityTagHeaderValue($"\"{file.Sha256}\""), enableRangeProcessing: true);
    }
}
