using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using RustProxy.Core.Download;
using RustProxy.Core.Package;
using RustProxy.Core.Storage;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Fetches verified registry dependencies from TOML lockfiles into a sparse registry tree.</summary>
public sealed partial class CargoDependencyFetcher(HttpClient httpClient, RetryExecutor retryExecutor, IFetchLogger logger)
{
    [GeneratedRegex("\\{(?<key>crate|version|prefix|lowerprefix|sha256-checksum)\\}")]
    private static partial Regex TemplatePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex CratePattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    /// <summary>Downloads unique index entries and bounded concurrent crate archives.</summary>
    public async Task<IReadOnlyList<CargoDependencyInfo>> FetchAsync(string projectDirectory, string destinationDirectory, string indexUrl,
        string? downloadUrlTemplate = null, int maxAttempts = 4, TimeSpan? retryDelay = null, CancellationToken cancellationToken = default, int maxConcurrency = 4)
    {
        if (maxConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        var root = Path.GetFullPath(projectDirectory);
        var lockPath = Path.Combine(root, "Cargo.lock");
        var indexBase = HttpDownload.Validate(new Uri(indexUrl.EndsWith('/') ? indexUrl : indexUrl + "/"));
        if (!File.Exists(lockPath)) lockPath = await new CargoCommandResolver(logger).ResolveLockFileAsync(root, cancellationToken);
        var packages = ParseLockFile(await File.ReadAllTextAsync(lockPath, cancellationToken));
        logger.Info($"Cargo: resolved {packages.Count} registry archives; Git/path dependencies are not included in the mirror.");
        var configText = await retryExecutor.ExecuteAsync("Cargo index config.json", () => HttpDownload.ReadTextAsync(httpClient, new Uri(indexBase, "config.json"), cancellationToken, 1024 * 1024),
            maxAttempts, retryDelay ?? TimeSpan.FromSeconds(3), cancellationToken);
        JsonObject config;
        try { config = JsonNode.Parse(configText)?.AsObject() ?? throw new InvalidDataException("Empty Cargo index configuration."); }
        catch (System.Text.Json.JsonException exception) { throw new InvalidDataException("Invalid Cargo index configuration.", exception); }
        var template = downloadUrlTemplate ?? config["dl"]?.GetValue<string>() ?? throw new InvalidDataException("Cargo index dl is missing.");
        if (!template.Contains('{')) template = template.TrimEnd('/') + "/{crate}/{version}/download";
        var cargoRoot = Path.Combine(destinationDirectory, "cargo");
        Directory.CreateDirectory(cargoRoot);
        await Parallel.ForEachAsync(packages.DistinctBy(x => x.Name), new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken }, async (dependency, token) =>
        {
            var indexPath = GetIndexPath(dependency.Name.ToLowerInvariant());
            await DownloadAsync(new Uri(indexBase, indexPath), PathSafety.Resolve(cargoRoot, "index/" + indexPath), null, maxAttempts, retryDelay, token);
        });
        await Parallel.ForEachAsync(packages, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken }, async (dependency, token) =>
        {
            var crate = dependency.Name;
            var values = new Dictionary<string, string>
            {
                ["crate"] = crate, ["version"] = dependency.Version, ["prefix"] = Prefix(crate),
                ["lowerprefix"] = Prefix(crate.ToLowerInvariant()), ["sha256-checksum"] = dependency.Checksum!
            };
            var expanded = TemplatePattern().Replace(template, match => values[match.Groups["key"].Value]);
            if (expanded.Contains('{') || expanded.Contains('}')) throw new InvalidDataException("Unsupported Cargo download URL placeholder.");
            var uri = HttpDownload.Validate(new Uri(indexBase, expanded));
            var target = PathSafety.Resolve(cargoRoot, $"crates/{crate}/{crate}-{dependency.Version}.crate");
            await DownloadAsync(uri, target, dependency.Checksum, maxAttempts, retryDelay, token);
        });
        // The server rewrites this template to its public origin for each index response.
        var local = new JsonObject { ["dl"] = "/cargo/crates/{crate}/{crate}-{version}.crate" };
        Directory.CreateDirectory(Path.Combine(cargoRoot, "index"));
        await File.WriteAllTextAsync(Path.Combine(cargoRoot, "index", "config.json"), local.ToJsonString(), cancellationToken);
        return packages;
    }

    private async Task DownloadAsync(Uri uri, string path, string? checksum, int attempts, TimeSpan? delay, CancellationToken token)
    {
        logger.Info($"Cargo: downloading {Path.GetFileName(path)}");
        await retryExecutor.ExecuteAsync("Cargo download", async () =>
        {
            await HttpDownload.DownloadAsync(httpClient, uri, path, checksum, token);
            return true;
        }, attempts, delay ?? TimeSpan.FromSeconds(3), token);
    }

    private static List<CargoDependencyInfo> ParseLockFile(string content)
    {
        var parsed = Toml.Parse(content);
        if (parsed.HasErrors) throw new InvalidDataException($"Invalid Cargo.lock: {parsed.Diagnostics}");
        var model = parsed.ToModel();
        if (!model.TryGetValue("package", out var value) || value is not TomlTableArray tables) throw new InvalidDataException("Cargo.lock has no package array.");
        var result = new List<CargoDependencyInfo>();
        var registries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            if (!table.TryGetValue("source", out var sourceValue)) continue;
            if (sourceValue is not string source) throw new InvalidDataException("Invalid Cargo.lock source.");
            if (!source.StartsWith("registry+", StringComparison.Ordinal)) continue;
            registries.Add(source);
            if (!table.TryGetValue("name", out var nameValue) || nameValue is not string name || !CratePattern().IsMatch(name) ||
                !table.TryGetValue("version", out var versionValue) || versionValue is not string version || !VersionPattern().IsMatch(version) ||
                !table.TryGetValue("checksum", out var checksumValue) || checksumValue is not string checksum || checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
                throw new InvalidDataException("Registry dependencies need a valid name, version and SHA-256 checksum.");
            result.Add(new CargoDependencyInfo { Name = name, Version = version, Checksum = checksum });
        }
        if (registries.Count > 1) throw new InvalidDataException("Multiple source registries require separate fetch packages; registry identities must not be merged.");
        foreach (var duplicate in result.GroupBy(x => (Name: x.Name.ToLowerInvariant(), x.Version)))
            if (duplicate.Select(x => (x.Name, Checksum: x.Checksum!.ToLowerInvariant())).Distinct().Skip(1).Any())
                throw new InvalidDataException("Cargo.lock contains conflicting dependency names or checksums.");
        return result.DistinctBy(x => (x.Name, x.Version)).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToList();
    }

    private static string Prefix(string name) => name.Length switch { 1 => "1", 2 => "2", 3 => $"3/{name[0]}", _ => $"{name[..2]}/{name[2..4]}" };
    private static string GetIndexPath(string name) => $"{Prefix(name)}/{name}";
}
