using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RustProxy.Core.Package;
using RustProxy.Core.Storage;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Fetches registry dependencies from Cargo.lock into the standard sparse source layout.</summary>
public sealed class CargoDependencyFetcher(HttpClient httpClient, RetryExecutor retryExecutor, IFetchLogger logger)
{
    private static readonly Regex LockPackagePattern = new(@"(?ms)^\[\[package\]\]\s*(?<body>.*?)(?=^\[\[package\]\]|\z)", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new("(?m)^name\\s*=\\s*\"(?<value>[^\"]+)\"$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("(?m)^version\\s*=\\s*\"(?<value>[^\"]+)\"$", RegexOptions.Compiled);
    private static readonly Regex SourcePattern = new("(?m)^source\\s*=\\s*\"(?<value>[^\"]+)\"$", RegexOptions.Compiled);
    private static readonly Regex ChecksumPattern = new("(?m)^checksum\\s*=\\s*\"(?<value>[a-fA-F0-9]{64})\"$", RegexOptions.Compiled);
    private static readonly Regex DlPattern = new("\\\"dl\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled);

    /// <summary>Downloads registry index entries and crate archives into a source-compatible tree.</summary>
    public async Task<IReadOnlyList<CargoDependencyInfo>> FetchAsync(string projectDirectory, string destinationDirectory, string indexUrl, string? downloadUrlTemplate = null, int maxAttempts = 4, TimeSpan? retryDelay = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectDirectory);
        var lockPath = Path.Combine(root, "Cargo.lock");
        if (!File.Exists(lockPath)) throw new FileNotFoundException("Cargo.lock is required to fetch exact Cargo dependencies.", lockPath);
        var indexBase = EnsureTrailingSlash(new Uri(indexUrl));
        logger.Info($"Cargo: reading {lockPath}");
        using var indexResponse = await RetryAsync("Cargo sparse index config.json", () => httpClient.GetAsync(new Uri(indexBase, "config.json"), cancellationToken), maxAttempts, retryDelay, cancellationToken);
        indexResponse.EnsureSuccessStatusCode();
        var configText = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
        var sourceConfig = JsonNode.Parse(configText)?.AsObject() ?? throw new InvalidDataException("Cargo index config.json is not valid JSON.");
        var dlTemplate = downloadUrlTemplate ?? sourceConfig["dl"]?.GetValue<string>() ?? throw new InvalidDataException("Cargo index config.json has an empty dl URL.");
        var localDownloadTemplate = RewriteToLocalTemplate(dlTemplate, indexBase);
        var localConfig = new JsonObject { ["dl"] = $"/cargo/{localDownloadTemplate}", ["api"] = "/cargo" };
        var packages = ParseLockFile(await File.ReadAllTextAsync(lockPath, cancellationToken));
        var cargoRoot = Path.Combine(destinationDirectory, "cargo");
        var indexRoot = Path.Combine(cargoRoot, "index");
        var cratesRoot = Path.Combine(cargoRoot, "crates");
        Directory.CreateDirectory(indexRoot);
        Directory.CreateDirectory(cratesRoot);
        var seenDownloadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in packages)
        {
            var indexPath = GetIndexPath(dependency.Name);
            var indexFile = Path.Combine(indexRoot, indexPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
            await DownloadFileAsync(new Uri(indexBase, indexPath), indexFile, $"Cargo index {dependency.Name}", null, maxAttempts, retryDelay, cancellationToken);
            var crateName = dependency.Name.ToLowerInvariant();
            var templateValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["crate"] = crateName, ["version"] = dependency.Version, ["prefix"] = crateName[..1], ["lowerprefix"] = crateName[..2], ["sha256-checksum"] = dependency.Checksum ?? string.Empty };
        var relativeDownload = Regex.Replace(dlTemplate, "\\{(?<key>crate|version|prefix|lowerprefix|sha256-checksum)\\}", value => templateValues[value.Groups["key"].Value]);
            var downloadUri = Uri.TryCreate(relativeDownload, UriKind.Absolute, out var absoluteUri) ? absoluteUri : new Uri(indexBase, relativeDownload);
            var downloadPath = relativeDownload;
            if (absoluteUri is not null)
            {
                var basePath = indexBase.AbsolutePath.TrimEnd('/');
                downloadPath = absoluteUri.AbsolutePath;
                if (downloadPath.StartsWith(basePath + "/", StringComparison.Ordinal)) downloadPath = downloadPath[(basePath.Length + 1)..];
                else downloadPath = downloadPath.TrimStart('/');
            }
            downloadPath = downloadPath.TrimStart('/');
            var cratePath = Path.GetFullPath(Path.Combine(cargoRoot, downloadPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!cratePath.StartsWith(Path.GetFullPath(cargoRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Cargo dl template resolved outside the package source tree.");
            Directory.CreateDirectory(Path.GetDirectoryName(cratePath)!);
            if (!seenDownloadPaths.Add(downloadPath)) continue;
            await DownloadFileAsync(downloadUri, cratePath, $"crate {dependency.Name} {dependency.Version}", dependency.Checksum, maxAttempts, retryDelay, cancellationToken);
        }
        await File.WriteAllTextAsync(Path.Combine(indexRoot, "config.json"), localConfig.ToJsonString(), cancellationToken);
        return packages;
    }

    private async Task DownloadFileAsync(Uri uri, string path, string label, string? expectedSha256, int maxAttempts, TimeSpan? delay, CancellationToken cancellationToken)
    {
        logger.Info($"Cargo: downloading {label}");
        await retryExecutor.ExecuteAsync(label, async () =>
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            if (expectedSha256 is not null)
            {
                await using var hashStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"SHA-256 mismatch for {label}");
            }
            return true;
        }, maxAttempts, delay ?? TimeSpan.FromSeconds(3), cancellationToken);
    }

    private async Task<HttpResponseMessage> RetryAsync(string label, Func<Task<HttpResponseMessage>> operation, int maxAttempts, TimeSpan? delay, CancellationToken cancellationToken) =>
        await retryExecutor.ExecuteAsync(label, operation, maxAttempts, delay ?? TimeSpan.FromSeconds(3), cancellationToken);

    private static List<CargoDependencyInfo> ParseLockFile(string content)
    {
        var packages = new List<CargoDependencyInfo>();
        foreach (Match match in LockPackagePattern.Matches(content))
        {
            var body = match.Groups["body"].Value;
            var source = SourcePattern.Match(body);
            if (!source.Success || !source.Groups["value"].Value.StartsWith("registry+", StringComparison.Ordinal)) continue;
            var name = NamePattern.Match(body).Groups["value"].Value;
            var version = VersionPattern.Match(body).Groups["value"].Value;
            var checksum = ChecksumPattern.Match(body).Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version)) packages.Add(new CargoDependencyInfo { Name = name, Version = version, Checksum = checksum });
        }
        return packages.DistinctBy(item => $"{item.Name}:{item.Version}").OrderBy(item => item.Name).ThenBy(item => item.Version).ToList();
    }

    private static string RewriteToLocalTemplate(string template, Uri indexBase)
    {
        if (!Uri.TryCreate(template, UriKind.Absolute, out var uri)) return template.TrimStart('/');
        var basePath = indexBase.AbsolutePath.TrimEnd('/');
        var path = uri.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal) ? uri.AbsolutePath[(basePath.Length + 1)..] : uri.AbsolutePath.TrimStart('/');
        return path;
    }

    private static string GetIndexPath(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Length switch { 1 => $"1/{lower}", 2 => $"2/{lower}", 3 => $"3/{lower[0]}/{lower}", _ => $"{lower[..2]}/{lower[2..4]}/{lower}" };
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
