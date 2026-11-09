using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using RustProxy.Core.Serialization;

namespace RustProxy.Core.Sync;

/// <summary>Loads a supported fetch configuration format.</summary>
public interface IConfigurationSource
{
    /// <summary>Returns null only when the source does not handle this filename.</summary>
    Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default);
}

/// <summary>Loads strict JSON configurations, preserving actionable parse errors.</summary>
public sealed class JsonConfigurationSource : IConfigurationSource
{
    /// <inheritdoc />
    public async Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        await using var input = File.OpenRead(path);
        try { return await JsonSerializer.DeserializeAsync(input, RustProxyJsonContext.Default.FetchConfiguration, cancellationToken) ?? throw new InvalidDataException("Configuration is empty."); }
        catch (JsonException exception) { throw new InvalidDataException($"Invalid JSON configuration {path}: {exception.Message}", exception); }
    }
}

/// <summary>Loads TOML configurations with complete TOML syntax support.</summary>
public sealed class TomlConfigurationSource : IConfigurationSource
{
    /// <inheritdoc />
    public async Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)) return null;
        var document = Toml.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        if (document.HasErrors) throw new InvalidDataException($"Invalid TOML configuration {path}: {document.Diagnostics}");
        var table = document.ToModel();
        var config = new FetchConfiguration();
        foreach (var item in table)
        {
            switch (item.Key)
            {
                case "name": config.Name = Text(item.Value, item.Key); break;
                case "output": config.Output = Text(item.Value, item.Key); break;
                case "cargo-project": case "cargoProject": config.CargoProject = Text(item.Value, item.Key); break;
                case "cargo-index-url": case "cargoIndexUrl": config.CargoIndexUrl = Text(item.Value, item.Key); break;
                case "cargo-download-url": case "cargoDownloadUrl": config.CargoDownloadUrl = Text(item.Value, item.Key); break;
                case "retry-count": case "retryCount": config.RetryCount = Integer(item.Value, item.Key); break;
                case "retry-delay-seconds": case "retryDelaySeconds": config.RetryDelaySeconds = Integer(item.Value, item.Key); break;
                case "max-concurrent-downloads": case "maxConcurrentDownloads": config.MaxConcurrentDownloads = Integer(item.Value, item.Key); break;
                case "rustup-init-targets": case "rustupInitTargets":
                    if (item.Value is not TomlArray targets) throw new InvalidDataException("rustupInitTargets must be an array.");
                    config.RustupInitTargets = targets.Select(x => Text(x, item.Key)).ToList();
                    break;
                case "toolchains":
                    if (item.Value is TomlTableArray tables)
                    {
                        foreach (var entry in tables)
                        {
                            if (!entry.TryGetValue("version", out var version) || !entry.TryGetValue("target", out var target)) throw new InvalidDataException("Each toolchain needs version and target.");
                            if (entry.Keys.Any(x => x is not ("version" or "target"))) throw new InvalidDataException("Unknown toolchain setting.");
                            config.Toolchains.Add(new ToolchainConfiguration { Version = Text(version, "version"), Target = Text(target, "target") });
                        }
                    }
                    else if (item.Value is TomlArray toolchains)
                    {
                        foreach (var entry in toolchains)
                        {
                            var parts = Text(entry, "toolchains").Split('/');
                            if (parts.Length != 2) throw new InvalidDataException("Use version/target toolchain entries.");
                            config.Toolchains.Add(new ToolchainConfiguration { Version = parts[0], Target = parts[1] });
                        }
                    }
                    else throw new InvalidDataException("toolchains must be an array.");
                    break;
                default: throw new InvalidDataException($"Unknown fetch setting: {item.Key}");
            }
        }
        return config;
    }

    private static string Text(object? value, string key) => value as string ?? throw new InvalidDataException($"{key} must be a string.");
    private static int Integer(object value, string key) => value is long number && number is >= int.MinValue and <= int.MaxValue ? (int)number : throw new InvalidDataException($"{key} must be an integer.");
}

/// <summary>Converts Cargo.toml into a configuration referencing its real project directory.</summary>
public sealed class CargoTomlConfigurationSource : IConfigurationSource
{
    /// <inheritdoc />
    public Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.GetFileName(path).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<FetchConfiguration?>(null);
        if (!File.Exists(path)) throw new FileNotFoundException("Cargo.toml was not found.", path);
        var directory = Path.GetDirectoryName(path)!;
        return Task.FromResult<FetchConfiguration?>(new FetchConfiguration { Name = Path.GetFileName(directory), CargoProject = directory, Output = Path.Combine(baseDirectory, Path.GetFileName(directory) + ".rpk") });
    }
}

/// <summary>Detects configuration formats and normalizes every source consistently.</summary>
public static class ConfigurationConverter
{
    private static readonly IConfigurationSource[] Sources = [new CargoTomlConfigurationSource(), new JsonConfigurationSource(), new TomlConfigurationSource()];

    /// <summary>Loads, validates and resolves paths relative to the supplied configuration file.</summary>
    public static async Task<FetchConfiguration> ConvertAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        foreach (var source in Sources)
        {
            var config = await source.TryLoadAsync(fullPath, directory, cancellationToken);
            if (config is null) continue;
            config.Validate();
            config.Output = Path.GetFullPath(config.Output, directory);
            if (!string.IsNullOrWhiteSpace(config.CargoProject)) config.CargoProject = Path.GetFullPath(config.CargoProject, directory);
            return config;
        }
        throw new InvalidDataException($"Unsupported configuration: {configPath}. Use JSON, TOML or Cargo.toml.");
    }
}
