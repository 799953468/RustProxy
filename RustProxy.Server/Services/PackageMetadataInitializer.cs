using SqlSugar;
using RustProxy.Server.Entities;

namespace RustProxy.Server.Services;

/// <summary>Initializes the package metadata database schema and removes stale records.</summary>
public sealed class PackageMetadataInitializer(ISqlSugarClient database, PackageCatalog catalog, ILogger<PackageMetadataInitializer> logger) : BackgroundService
{
    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<PackageMetadata, PackageCargoDependencyMetadata, PackageToolchainMetadata, PackageFileMetadata>();
        DatabaseStorage.UpgradeOptionalColumns(database.CurrentConnectionConfig.ConnectionString);
        await catalog.MigrateLegacyFilesAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            var failures = await catalog.AuditAsync(stoppingToken);
            logger.LogInformation("Background integrity audit finished with {Failures} unavailable objects", failures);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Background integrity audit failed; catalog history was retained"); }
    }
}
