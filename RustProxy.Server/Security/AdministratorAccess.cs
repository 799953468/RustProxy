using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace RustProxy.Server.Security;

/// <summary>Issues process-local credential stamps and checks them against the current deployment secret.</summary>
public sealed class AdministratorAccess(IConfiguration configuration)
{
    private const string StampClaim = "rustproxy:administrator-credential";
    private readonly byte[] _stampKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Authenticates a configured administrator and stamps the resulting identity.</summary>
    public ClaimsPrincipal? Authenticate(string userName, string password)
    {
        var expected = configuration["Administration:ApiKey"];
        if (userName != "admin" || expected is not { Length: >= 32 } || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(password)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)))) return null;
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator"), new Claim(StampClaim, Stamp(expected))
        ], AdministratorAuthenticationHandler.SchemeName));
    }

    /// <summary>Checks the identity and credential stamp, including secret rotation and administrator disablement.</summary>
    public bool IsAuthorized(ClaimsPrincipal actor)
    {
        var expected = configuration["Administration:ApiKey"];
        var stamp = actor.FindFirst(StampClaim)?.Value;
        return actor.Identity?.IsAuthenticated == true && actor.Identity.AuthenticationType == AdministratorAuthenticationHandler.SchemeName &&
            actor.IsInRole("Administrator") && expected is { Length: >= 32 } && stamp is { Length: 64 } &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stamp), Encoding.ASCII.GetBytes(Stamp(expected)));
    }

    /// <summary>Rejects imports from unauthenticated or revoked administrator sessions.</summary>
    public void RequireAdministrator(ClaimsPrincipal actor)
    {
        if (!IsAuthorized(actor)) throw new UnauthorizedAccessException("Administrator credentials are missing, revoked, or invalid.");
    }

    private string Stamp(string secret) => Convert.ToHexString(HMACSHA256.HashData(_stampKey, Encoding.UTF8.GetBytes(secret)));
}
