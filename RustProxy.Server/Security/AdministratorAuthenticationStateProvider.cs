using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace RustProxy.Server.Security;

/// <summary>Revokes interactive administrator sessions when their configured credential changes.</summary>
public sealed class AdministratorAuthenticationStateProvider(ILoggerFactory loggerFactory, AdministratorAccess access)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <inheritdoc />
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(access.IsAuthorized(authenticationState.User));
}
