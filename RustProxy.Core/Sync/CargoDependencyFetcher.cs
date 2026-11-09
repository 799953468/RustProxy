using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RustProxy.Core.Package;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Fetches registry dependencies from Cargo.lock into the standard sparse source layout.</summary>
public sealed partial class CargoDependencyFetcher(
    HttpClient httpClient,
    RetryExecutor retryExecutor,
    IFetchLogger logger)
{
    [GeneratedRegex(@"(?ms)^\[\[package\]\]\s*(?<body>.*?)(?=^\[\[package\]\]|\z)")]
    private static partial Regex LockPackagePattern();

    [GeneratedRegex("(?m)^name\\s*=\\s*\"(?<value>[^\"]+)\"$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("(?m)^version\\s*=\\s*\"(?<value>[^\"]+)\"$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("(?m)^source\\s*=\\s*\"(?<value>[^\"]+)\"$")]
    private static partial Regex SourcePattern();

    [GeneratedRegex("(?m)^checksum\\s*=\\s*\"(?<value>[a-fA-F0-9]{64})\"$")]
    private static partial Regex ChecksumPattern();

    [GeneratedRegex("\\{(?<key>crate|version|prefix|lowerprefix|sha256-checksum)\\}")]
    private static partial Regex TemplatePattern();

    /// <summary>Downloads registry dependencies from Cargo.lock, generating a lockfile from Cargo.toml when absent.</summary>
    public async Task<IReadOnlyList<CargoDependencyInfo>> FetchAsync(string projectDirectory,
        string destinationDirectory, string indexUrl, string? downloadUrlTemplate = null, int maxAttempts = 4,
        TimeSpan? retryDelay = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectDirectory);
        var lockPath = Path.Combine(root, "Cargo.lock");
        var indexBase = EnsureTrailingSlash(new Uri(indexUrl));
        if (!File.Exists(lockPath))
        {
            logger.Info("Cargo.lock is missing; resolving Cargo.toml with Cargo.");
            await new CargoCommandResolver(logger).GenerateLockAsync(root, cancellationToken);
        }

        logger.Info($"Cargo: reading {lockPath}");
        using var indexResponse = await RetryAsync("Cargo sparse index config.json", () => httpClient.GetAsync(new Uri(indexBase, "config.json"), cancellationToken), maxAttempts, retryDelay,
            cancellationToken);
        indexResponse.EnsureSuccessStatusCode();
        var configText = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
        var sourceConfig = JsonNode.Parse(configText)?.AsObject() ?? throw new InvalidDataException("Cargo index config.json is not valid JSON.");
        var upstreamDownloadTemplate = downloadUrlTemplate ?? sourceConfig["dl"]?.GetValue<string>() ?? throw new InvalidDataException("Cargo index config.json has an empty dl URL.");
        var dlTemplate = NormalizeDownloadTemplate(upstreamDownloadTemplate);
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
            var templateValues = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["crate"] = crateName, ["version"] = dependency.Version, ["prefix"] = crateName[..1],
                ["lowerprefix"] = crateName[..2], ["sha256-checksum"] = dependency.Checksum ?? string.Empty
            };
            var expandedTemplate = TemplatePattern().Replace(dlTemplate, value => templateValues[value.Groups["key"].Value]);
            var downloadUri = ResolveDownloadUri(expandedTemplate, indexBase);
            var downloadPath = downloadUri.AbsolutePath.TrimStart('/');
            if (downloadUri.Host.Equals(indexBase.Host, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(indexBase.AbsolutePath.Trim('/')))
            {
                var basePath = indexBase.AbsolutePath.Trim('/');
                if (downloadPath.StartsWith(basePath + "/", StringComparison.Ordinal))
                    downloadPath = downloadPath[(basePath.Length + 1)..];
            }

            var cratePath = Path.GetFullPath(Path.Combine(cargoRoot, Uri.UnescapeDataString(downloadPath).Replace('/', Path.DirectorySeparatorChar)));
            if (!cratePath.StartsWith(Path.GetFullPath(cargoRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Cargo dl template resolved outside the package source tree.");
            Directory.CreateDirectory(Path.GetDirectoryName(cratePath)!);
            if (!seenDownloadPaths.Add(downloadPath)) continue;
            logger.Info($"Cargo: resolved download URL for {dependency.Name} {dependency.Version}: {downloadUri.GetLeftPart(UriPartial.Path)}");
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
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status == 403)
                {
                    var detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                    if (detail.Length > 300) detail = detail[..300];
                    throw new HttpRequestException($"HTTP 403 Forbidden while downloading {label} from {uri}. {detail}", null, response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (expectedSha256 is not null)
            {
                await using var hashStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"SHA-256 mismatch for {label}");
            }

            return true;
        }, maxAttempts, delay ?? TimeSpan.FromSeconds(3), cancellationToken);
    }

    private async Task<HttpResponseMessage> RetryAsync(string label, Func<Task<HttpResponseMessage>> operation,
        int maxAttempts, TimeSpan? delay, CancellationToken cancellationToken) =>
        await retryExecutor.ExecuteAsync(label, operation, maxAttempts, delay ?? TimeSpan.FromSeconds(3), cancellationToken);

    private static List<CargoDependencyInfo> ParseLockFile(string content)
    {
        var packages = new List<CargoDependencyInfo>();
        foreach (Match match in LockPackagePattern().Matches(content))
        {
            var body = match.Groups["body"].Value;
            var source = SourcePattern().Match(body);
            if (!source.Success || !source.Groups["value"].Value.StartsWith("registry+", StringComparison.Ordinal)) continue;
            var name = NamePattern().Match(body).Groups["value"].Value;
            var version = VersionPattern().Match(body).Groups["value"].Value;
            var checksum = ChecksumPattern().Match(body).Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                packages.Add(new CargoDependencyInfo { Name = name, Version = version, Checksum = checksum });
        }

        return [.. packages.DistinctBy(item => $"{item.Name}:{item.Version}").OrderBy(item => item.Name).ThenBy(item => item.Version)];
    }

    private static Uri ResolveDownloadUri(string value, Uri indexBase)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute;
        return new Uri(indexBase, value.TrimStart('/'));
    }

    private static string NormalizeDownloadTemplate(string template)
    {
        if (template.Contains("{crate}", StringComparison.Ordinal) && template.Contains("{version}", StringComparison.Ordinal)) return template;
        var root = template.TrimEnd('/');
        return $"{root}/{{crate}}/{{crate}}-{{version}}.crate";
    }

    private static string RewriteToLocalTemplate(string template, Uri indexBase)
    {
        if (!Uri.TryCreate(template, UriKind.Absolute, out var uri)) return template.TrimStart('/');
        var basePath = indexBase.AbsolutePath.TrimEnd('/');
        var path = uri.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal)
            ? uri.AbsolutePath[(basePath.Length + 1)..]
            : uri.AbsolutePath.TrimStart('/');
        return Uri.UnescapeDataString(path);
    }

    private static string GetIndexPath(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Length switch
        {
            1 => $"1/{lower}", 2 => $"2/{lower}", 3 => $"3/{lower[0]}/{lower}",
            _ => $"{lower[..2]}/{lower[2..4]}/{lower}"
        };
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
