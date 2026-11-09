using Microsoft.AspNetCore.Mvc;

namespace RustProxy.Server.Controllers;

internal static class CargoIndexConfiguration
{
    public static IActionResult Create(ControllerBase controller, IConfiguration configuration, string resourcePrefix)
    {
        var configured = configuration["PublicBaseUrl"];
        var baseUrl = string.IsNullOrWhiteSpace(configured)
            ? $"{controller.Request.Scheme}://{controller.Request.Host}{controller.Request.PathBase}/"
            : configured.TrimEnd('/') + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo))
            throw new InvalidOperationException("PublicBaseUrl must be an absolute HTTP(S) URL without credentials.");
        controller.Response.Headers.CacheControl = "no-cache";
        return controller.Ok(new { dl = baseUrl + resourcePrefix + "/crates/{crate}/{crate}-{version}.crate" });
    }
}
