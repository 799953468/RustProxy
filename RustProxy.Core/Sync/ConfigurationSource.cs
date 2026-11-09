namespace RustProxy.Core.Sync;

/// <summary>Represents a configuration source that can be converted to FetchConfiguration.</summary>
public interface IConfigurationSource
{
    /// <summary>Attempts to load and convert the configuration.</summary>
    /// <returns>A FetchConfiguration if successful, null otherwise.</returns>
    Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default);
}

/// <summary>Configuration source for standard JSON format.</summary>
public sealed class JsonConfigurationSource : IConfigurationSource
{
    public async Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var config = System.Text.Json.JsonSerializer.Deserialize(
                json,
                Serialization.RustProxyJsonContext.Default.FetchConfiguration);

            if (config != null)
            {
                // Resolve relative paths
                if (!Path.IsPathRooted(config.Output))
                    config.Output = Path.Combine(baseDirectory, config.Output);
                if (!string.IsNullOrWhiteSpace(config.CargoProject) && !Path.IsPathRooted(config.CargoProject))
                    config.CargoProject = Path.Combine(baseDirectory, config.CargoProject);
            }

            return config;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Configuration source for TOML format.</summary>
public sealed class TomlConfigurationSource : IConfigurationSource
{
    public async Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!File.Exists(path))
            return null;

        try
        {
            var tomlContent = await File.ReadAllTextAsync(path, cancellationToken);
            var config = ParseTomlConfiguration(tomlContent, baseDirectory);
            return config;
        }
        catch
        {
            return null;
        }
    }

    private static FetchConfiguration? ParseTomlConfiguration(string tomlContent, string baseDirectory)
    {
        var config = new FetchConfiguration();
        var lines = tomlContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var inToolchainTable = false;
        var currentToolchain = new ToolchainConfiguration();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                continue;

            if (trimmedLine == "[[toolchains]]")
            {
                if (inToolchainTable && !string.IsNullOrWhiteSpace(currentToolchain.Target))
                    config.Toolchains.Add(currentToolchain);
                currentToolchain = new ToolchainConfiguration();
                inToolchainTable = true;
                continue;
            }

            if (trimmedLine.StartsWith('[') && trimmedLine.EndsWith(']'))
            {
                inToolchainTable = false;
                continue;
            }

            if (trimmedLine.StartsWith("toolchains") && trimmedLine.Contains('='))
            {
                var valueStart = trimmedLine.IndexOf('=') + 1;
                var value = trimmedLine[valueStart..].Trim();
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    var toolchainStrings = value[1..^1].Split(',');
                    foreach (var toolchainStr in toolchainStrings)
                    {
                        var parts = toolchainStr.Trim().Trim('"').Split('/');
                        if (parts.Length == 2)
                        {
                            config.Toolchains.Add(new ToolchainConfiguration
                            {
                                Version = parts[0].Trim(),
                                Target = parts[1].Trim()
                            });
                        }
                    }
                }
            }
            else if (inToolchainTable)
            {
                if (trimmedLine.StartsWith("version"))
                    currentToolchain.Version = ExtractTomlValue(trimmedLine);
                else if (trimmedLine.StartsWith("target"))
                    currentToolchain.Target = ExtractTomlValue(trimmedLine);
            }
            else
            {
                if (trimmedLine.StartsWith("name"))
                    config.Name = ExtractTomlValue(trimmedLine);
                else if (trimmedLine.StartsWith("output"))
                    config.Output = ExtractTomlValue(trimmedLine);
                else if (trimmedLine.StartsWith("cargo-project") || trimmedLine.StartsWith("cargoProject"))
                    config.CargoProject = ExtractTomlValue(trimmedLine);
                else if (trimmedLine.StartsWith("cargo-index-url") || trimmedLine.StartsWith("cargoIndexUrl"))
                    config.CargoIndexUrl = ExtractTomlValue(trimmedLine);
                else if (trimmedLine.StartsWith("cargo-download-url") || trimmedLine.StartsWith("cargoDownloadUrl"))
                    config.CargoDownloadUrl = ExtractTomlValue(trimmedLine);
                else if (trimmedLine.StartsWith("retry-count") || trimmedLine.StartsWith("retryCount"))
                {
                    if (int.TryParse(ExtractTomlValue(trimmedLine), out var retryCount))
                        config.RetryCount = retryCount;
                }
                else if (trimmedLine.StartsWith("retry-delay-seconds") || trimmedLine.StartsWith("retryDelaySeconds"))
                {
                    if (int.TryParse(ExtractTomlValue(trimmedLine), out var retryDelay))
                        config.RetryDelaySeconds = retryDelay;
                }
            }
        }

        if (inToolchainTable && !string.IsNullOrWhiteSpace(currentToolchain.Target))
            config.Toolchains.Add(currentToolchain);

        return config;
    }

    private static string ExtractTomlValue(string line)
    {
        var eqIndex = line.IndexOf('=');
        if (eqIndex < 0)
            return string.Empty;

        var value = line[(eqIndex + 1)..].Trim();
        if (value.StartsWith('"') && value.EndsWith('"'))
            return value[1..^1];
        if (value.StartsWith('\'') && value.EndsWith('\''))
            return value[1..^1];
        return value;
    }
}

/// <summary>Configuration source for Cargo.toml files (extracts project path).</summary>
public sealed class CargoTomlConfigurationSource : IConfigurationSource
{
    public async Task<FetchConfiguration?> TryLoadAsync(string path, string baseDirectory, CancellationToken cancellationToken = default)
    {
        if (!Path.GetFileName(path).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!File.Exists(path))
            return null;

        try
        {
            var projectDir = Path.GetDirectoryName(path)!;
            var config = new FetchConfiguration
            {
                Name = Path.GetFileName(projectDir) ?? "offline-rust",
                Output = Path.Combine(baseDirectory, $"{Path.GetFileName(projectDir) ?? "offline-rust"}.rpk"),
                CargoProject = projectDir
            };
            return config;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Converter that detects and loads configuration from various sources.</summary>
public sealed class ConfigurationConverter
{
    private static readonly IConfigurationSource[] Sources =
    [
        new JsonConfigurationSource(),
        new TomlConfigurationSource(),
        new CargoTomlConfigurationSource()
    ];

    /// <summary>Attempts to load and convert configuration from a file, detecting the format automatically.</summary>
    public static async Task<FetchConfiguration> ConvertAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(configPath);
        var baseDirectory = Path.GetDirectoryName(fullPath)!;

        foreach (var source in Sources)
        {
            var config = await source.TryLoadAsync(fullPath, baseDirectory, cancellationToken);
            if (config != null)
                return config;
        }

        throw new InvalidDataException($"Unable to load configuration from {configPath}. Supported formats: JSON (.json), TOML (.toml), or Cargo.toml");
    }
}
