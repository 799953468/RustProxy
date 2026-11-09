using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Server.Security;

namespace RustProxy.Tests;

[TestClass]
public sealed class AuthenticationTests
{
    [TestMethod]
    public void CredentialRotationAndDisablementRevokeExistingPrincipals()
    {
        var configuration = new ConfigurationManager { ["Administration:ApiKey"] = TestWorkspace.AdministratorKey };
        var access = new AdministratorAccess(configuration);
        var principal = access.Authenticate("admin", TestWorkspace.AdministratorKey)!;
        access.IsAuthorized(principal).Should().BeTrue();
        configuration["Administration:ApiKey"] = new string('r', 40);
        access.IsAuthorized(principal).Should().BeFalse();
        access.Authenticate("admin", TestWorkspace.AdministratorKey).Should().BeNull();
        principal = access.Authenticate("admin", new string('r', 40))!;
        access.IsAuthorized(principal).Should().BeTrue();
        configuration["Administration:ApiKey"] = null;
        access.IsAuthorized(principal).Should().BeFalse();
    }

    [TestMethod]
    public void AdministratorRoleAloneAndOtherProcessStampsAreInsufficient()
    {
        var configuration = new ConfigurationManager { ["Administration:ApiKey"] = TestWorkspace.AdministratorKey };
        var access = new AdministratorAccess(configuration);
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], AdministratorAuthenticationHandler.SchemeName));
        access.IsAuthorized(forged).Should().BeFalse();
        access.IsAuthorized(TestWorkspace.Administrator).Should().BeFalse();
        access.Authenticate("another-user", TestWorkspace.AdministratorKey).Should().BeNull();
        configuration["Administration:ApiKey"] = "short";
        access.Authenticate("admin", "short").Should().BeNull();
    }
}
