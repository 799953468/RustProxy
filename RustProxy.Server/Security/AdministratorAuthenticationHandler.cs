using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RustProxy.Server.Security;

/// <summary>Authenticates the local administrator using a deployment-provided secret over TLS.</summary>
public sealed class AdministratorAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration, AdministratorAccess access) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Authentication scheme name.</summary>
    public const string SchemeName = "Administrator";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["Administration:ApiKey"];
        if (string.IsNullOrEmpty(expected) || expected.Length < 32) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Request.IsHttps && !(Context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address)))
            return Task.FromResult(AuthenticateResult.Fail("Administrator authentication requires HTTPS."));
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..]));
            var colon = decoded.IndexOf(':');
            var principal = colon < 0 ? null : access.Authenticate(decoded[..colon], decoded[(colon + 1)..]);
            if (principal is null)
                return Task.FromResult(AuthenticateResult.Fail("Invalid administrator credentials."));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
        catch (FormatException) { return Task.FromResult(AuthenticateResult.Fail("Invalid authorization header.")); }
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"RustProxy administration\", charset=\"UTF-8\"";
        Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}
