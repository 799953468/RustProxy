using SqlSugar;
using RustProxy.Server.Entities;

namespace RustProxy.Server.Services;

/// <summary>Initializes the package metadata database schema.</summary>
public sealed class PackageMetadataInitializer(ISqlSugarClient database) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<PackageMetadata, PackageCargoDependencyMetadata, PackageToolchainMetadata, PackageFileMetadata>();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
