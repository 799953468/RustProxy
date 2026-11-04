using RustProxy.Core.Package;
using RustProxy.Core.Storage;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Executes a configuration-driven Cargo and rustup fetch.</summary>
public sealed class FetchCoordinator(
    CargoDependencyFetcher cargoFetcher,
    RustupToolchainFetcher rustupFetcher,
    IFetchLogger logger)
{
    /// <summary>Fetches all configured inputs and creates one combined RPK package.</summary>
    public async Task FetchAsync(FetchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.CargoProject) && configuration.Toolchains.Count == 0)
            throw new ArgumentException("Configure CargoProject or at least one toolchain.", nameof(configuration));
        var root = Path.Combine(Path.GetTempPath(), $"rustproxy-fetch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new PackageManifest { Name = configuration.Name, Source = "rustproxy.fetch.json" };
            if (!string.IsNullOrWhiteSpace(configuration.CargoProject))
            {
                logger.Info($"Cargo: fetching locked dependencies from {configuration.CargoProject}");
                var cargoDependencies = await cargoFetcher.FetchAsync(configuration.CargoProject, root, configuration.CargoIndexUrl, configuration.CargoDownloadUrl, configuration.RetryCount,
                    TimeSpan.FromSeconds(configuration.RetryDelaySeconds), cancellationToken);
                metadata.CargoDependencies.AddRange(cargoDependencies);
                logger.Info($"Cargo: {metadata.CargoDependencies.Count} dependencies fetched");
            }

            foreach (var toolchain in configuration.Toolchains)
            {
                logger.Info($"rustup: fetching {toolchain.Version} for {toolchain.Target}");
                await rustupFetcher.FetchToDirectoryAsync(toolchain.Version, toolchain.Target, root,
                    maxAttempts: configuration.RetryCount,
                    retryDelay: TimeSpan.FromSeconds(configuration.RetryDelaySeconds),
                    cancellationToken: cancellationToken,
                    rustupInitTargets: configuration.RustupInitTargets);
                metadata.Toolchains.Add(new RustToolchainInfo { Version = toolchain.Version, Target = toolchain.Target });
                logger.Info($"rustup: {toolchain.Version}/{toolchain.Target} fetched");
            }

            foreach (var manifest in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
                File.Delete(manifest);
            foreach (var package in Directory.EnumerateFiles(root, "*.rpk", SearchOption.TopDirectoryOnly))
                File.Delete(package);
            logger.Info($"Creating package {configuration.Output}");
            await PackageStore.CreateFromDirectoryAsync(root, configuration.Output, configuration.Name, "rustproxy.fetch.json", metadata, cancellationToken);
            logger.Info("Fetch completed");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                //ignore
            }
        }
    }
}
