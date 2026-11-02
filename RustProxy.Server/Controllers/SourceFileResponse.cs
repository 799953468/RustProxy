using Microsoft.AspNetCore.Mvc;

namespace RustProxy.Server.Controllers;

internal static class SourceFileResponse
{
    public static bool IsNotModified(ControllerBase controller, string file)
    {
        var info = new FileInfo(file);
        var etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        controller.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        controller.Response.Headers.ETag = etag;
        return controller.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal));
    }
}
