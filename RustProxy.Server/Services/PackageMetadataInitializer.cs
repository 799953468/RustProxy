using SqlSugar;
using RustProxy.Server.Entities;

namespace RustProxy.Server.Services;

/// <summary>Initializes the package metadata database schema and removes stale records.</summary>
public sealed class PackageMetadataInitializer(ISqlSugarClient database, PackageCatalog catalog) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<PackageMetadata, PackageCargoDependencyMetadata, PackageToolchainMetadata, PackageFileMetadata>();
        await catalog.RemoveInvalidPackagesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
