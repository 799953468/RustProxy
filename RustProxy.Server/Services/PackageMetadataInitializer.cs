using SqlSugar;
using RustProxy.Server.Entities;

namespace RustProxy.Server.Services;

/// <summary>Initializes the catalog and periodically checks stored object integrity.</summary>
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
        await Task.Yield();
        do
        {
            try
            {
                var failures = await catalog.AuditAsync(stoppingToken);
                logger.LogInformation("Background integrity audit finished with {Failures} unavailable objects", failures);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Background integrity audit failed; it will be retried and catalog history was retained"); }
            try { await catalog.CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Storage maintenance failed; it will be retried"); }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
        while (!stoppingToken.IsCancellationRequested);
    }
}
