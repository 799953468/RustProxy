using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RustProxy.Core.Package;
using RustProxy.Core.Storage;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Fetches a selected rustup channel manifest and target archives.</summary>
public sealed class RustupToolchainFetcher(HttpClient httpClient, RetryExecutor retryExecutor, IFetchLogger logger)
{
    private static readonly Regex SectionPattern = new(@"^\[pkg\.(?<package>[^.\]]+)(?:\.target\.(?<target>[^\]]+))?\]$", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new("^(?<kind>xz_)?url\\s*=\\s*\\\"(?<url>[^\\\"]+)\\\"$", RegexOptions.Compiled);
    private static readonly Regex HashPattern = new("^(?<kind>xz_)?hash\\s*=\\s*\\\"(?<hash>[a-fA-F0-9]{64})\\\"$", RegexOptions.Compiled);

    /// <summary>Downloads a channel manifest and selected target archives into a source directory.</summary>
    public async Task FetchToDirectoryAsync(string toolchain, string target, string destinationDirectory, Uri? source = null, int maxAttempts = 4, TimeSpan? retryDelay = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchain);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var baseUri = source ?? new Uri("https://static.rust-lang.org/dist/");
        var channel = toolchain.Equals("stable", StringComparison.OrdinalIgnoreCase) || toolchain.Equals("beta", StringComparison.OrdinalIgnoreCase) || toolchain.Equals("nightly", StringComparison.OrdinalIgnoreCase) ? toolchain.ToLowerInvariant() : toolchain;
        var manifestPath = $"channel-rust-{channel}.toml";
        logger.Info($"rustup: downloading channel manifest {manifestPath}");
        var manifestText = await retryExecutor.ExecuteAsync(manifestPath, async () =>
        {
            using var response = await httpClient.GetAsync(new Uri(baseUri, manifestPath), cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }, maxAttempts, retryDelay ?? TimeSpan.FromSeconds(3), cancellationToken);
        var manifestHashPath = $"{manifestPath}.sha256";
        logger.Info($"rustup: downloading channel checksum {manifestHashPath}");
        var manifestHashText = await retryExecutor.ExecuteAsync(manifestHashPath, async () =>
        {
            using var response = await httpClient.GetAsync(new Uri(baseUri, manifestHashPath), cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }, maxAttempts, retryDelay ?? TimeSpan.FromSeconds(3), cancellationToken);
        var expectedManifestHash = ParseSha256(manifestHashText, manifestHashPath);
        var actualManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestText))).ToLowerInvariant();
        if (!actualManifestHash.Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"SHA-256 mismatch for {manifestPath}");
        var archives = ParseSelectedArchives(manifestText, target);
        var staging = Path.Combine(destinationDirectory, "rustup", "dist");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, manifestPath), manifestText, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, manifestHashPath), manifestHashText, cancellationToken);
        foreach (var archive in archives)
        {
            var uri = new Uri(baseUri, archive.Url);
                var relative = uri.AbsolutePath;
                if (uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal)) relative = uri.AbsolutePath[baseUri.AbsolutePath.Length..];
                relative = relative.TrimStart('/');
                var targetPath = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                logger.Info($"rustup: downloading {relative}");
                await retryExecutor.ExecuteAsync(relative, async () =>
                {
                    using var archiveResponse = await httpClient.GetAsync(uri, cancellationToken);
                    archiveResponse.EnsureSuccessStatusCode();
                    await using (var input = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                    }
                    await using var hashStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
                    if (archive.Hash is not null && !actualHash.Equals(archive.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException($"SHA-256 mismatch for {relative}");
                    return true;
            }, maxAttempts, retryDelay ?? TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private static IReadOnlyList<ArchiveReference> ParseSelectedArchives(string manifest, string target)
    {
        var selected = new Dictionary<string, (string Url, string? Hash, bool Compressed)>(StringComparer.OrdinalIgnoreCase);
        string? package = null;
        string? sectionTarget = null;
        string? url = null;
        string? hash = null;
        var compressed = false;
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.Trim();
            var section = SectionPattern.Match(line);
            if (section.Success)
            {
                SaveCandidate();
                package = section.Groups["package"].Value;
                sectionTarget = section.Groups["target"].Success ? section.Groups["target"].Value : null;
                url = null;
                hash = null;
                compressed = false;
                continue;
            }
            var urlMatch = UrlPattern.Match(line);
            if (urlMatch.Success)
            {
                if (urlMatch.Groups["kind"].Success || url is null)
                {
                    url = urlMatch.Groups["url"].Value;
                    compressed = urlMatch.Groups["kind"].Success;
                }
                continue;
            }
            var hashMatch = HashPattern.Match(line);
            if (hashMatch.Success && (hashMatch.Groups["kind"].Success || hash is null)) hash = hashMatch.Groups["hash"].Value;
        }
        SaveCandidate();
        return selected.Values.Select(item => new ArchiveReference(item.Url, item.Hash)).ToArray();

        void SaveCandidate()
        {
            if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(url)) return;
            var targetSelected = sectionTarget is null || sectionTarget.Equals(target, StringComparison.OrdinalIgnoreCase);
            if (!targetSelected) return;
            var key = $"{package}:{sectionTarget}";
            if (!selected.TryGetValue(key, out var existing) || compressed && !existing.Compressed) selected[key] = (url, hash, compressed);
        }
    }

    private static string ParseSha256(string content, string path)
    {
        var hash = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException($"Invalid SHA-256 file: {path}");
        return hash;
    }

    private sealed record ArchiveReference(string Url, string? Hash);
}
