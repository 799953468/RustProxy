using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using RustProxy.Core.Download;
using RustProxy.Core.Storage;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Fetches byte-verified Rust toolchains independently of the rustup updater release channel.</summary>
public sealed partial class RustupToolchainFetcher(HttpClient httpClient, RetryExecutor retryExecutor, IFetchLogger logger)
{
    private readonly Dictionary<string, string> _downloaded = new(StringComparer.Ordinal);

    [GeneratedRegex("^(stable|beta|nightly)(?:-([0-9]{4}-[0-9]{2}-[0-9]{2}))?$")]
    private static partial Regex ChannelPattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex VersionPattern();

    /// <summary>Maps exact versions and dated channels onto rustup's distribution manifest paths.</summary>
    public static string GetManifestPath(string toolchain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchain);
        if (VersionPattern().IsMatch(toolchain)) return $"channel-rust-{toolchain}.toml";
        var match = ChannelPattern().Match(toolchain);
        if (!match.Success) throw new InvalidDataException("Use stable, beta, nightly, x.y.z, or channel-YYYY-MM-DD.");
        var channel = match.Groups[1].Value;
        if (!match.Groups[2].Success) return $"channel-rust-{channel}.toml";
        var date = match.Groups[2].Value;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new InvalidDataException("Invalid toolchain date.");
        return $"{date}/channel-rust-{channel}.toml";
    }

    /// <summary>Downloads manifests, verified installers and target archives into a portable source tree.</summary>
    public async Task FetchToDirectoryAsync(string toolchain, string target, string destinationDirectory, Uri? source = null,
        int maxAttempts = 4, TimeSpan? retryDelay = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? rustupInitTargets = null)
    {
        var manifestPath = GetManifestPath(toolchain);
        FetchConfiguration.ValidateTarget(target);
        var baseUri = HttpDownload.Validate(source ?? new Uri("https://static.rust-lang.org/dist/"));
        if (!baseUri.AbsolutePath.EndsWith('/')) throw new InvalidDataException("The rustup distribution URL must end with '/'.");
        var outputRoot = Path.Combine(destinationDirectory, "rustup", "dist");
        Directory.CreateDirectory(outputRoot);
        var delay = retryDelay ?? TimeSpan.FromSeconds(3);

        // The rustup updater has its own stable release; it does not use Rust's version number.
        var release = await ReadAsync(new Uri(baseUri, "../rustup/release-stable.toml"), maxAttempts, delay, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(destinationDirectory, "rustup", "release-stable.toml"), release, cancellationToken);
        foreach (var installerTarget in (rustupInitTargets ?? [target]).Distinct(StringComparer.Ordinal))
        {
            FetchConfiguration.ValidateTarget(installerTarget);
            var name = installerTarget.Contains("windows", StringComparison.Ordinal) ? "rustup-init.exe" : "rustup-init";
            var relative = $"{installerTarget}/{name}";
            var uri = new Uri(baseUri, "../rustup/dist/" + relative);
            var hash = ParseSha256(Encoding.UTF8.GetString(await ReadAsync(new Uri(uri.AbsoluteUri + ".sha256"), maxAttempts, delay, cancellationToken)));
            await FetchFileAsync(uri, PathSafety.Resolve(outputRoot, relative), hash, maxAttempts, delay, cancellationToken);
        }

        var manifest = await ReadAsync(new Uri(baseUri, manifestPath), maxAttempts, delay, cancellationToken);
        var checksum = await ReadAsync(new Uri(baseUri, manifestPath + ".sha256"), maxAttempts, delay, cancellationToken);
        var expected = ParseSha256(Encoding.UTF8.GetString(checksum));
        if (!Convert.ToHexString(SHA256.HashData(manifest)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for {manifestPath}");
        var targetManifest = PathSafety.Resolve(outputRoot, manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetManifest)!);
        await File.WriteAllBytesAsync(targetManifest, manifest, cancellationToken);
        await File.WriteAllBytesAsync(targetManifest + ".sha256", checksum, cancellationToken);

        foreach (var archive in ParseSelectedArchives(Encoding.UTF8.GetString(manifest), target))
        {
            var uri = HttpDownload.Validate(new Uri(baseUri, archive.Url));
            // Preserve the upstream dist path, including date directories.
            if (!uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal) || uri.AbsolutePath.Contains('%'))
                throw new InvalidDataException("Component URL must refer to the distribution path.");
            var relative = PathSafety.Normalize(uri.AbsolutePath[baseUri.AbsolutePath.Length..]);
            await FetchFileAsync(uri, PathSafety.Resolve(outputRoot, relative), archive.Hash, maxAttempts, delay, cancellationToken);
        }
    }

    private Task<byte[]> ReadAsync(Uri uri, int attempts, TimeSpan delay, CancellationToken token) =>
        retryExecutor.ExecuteAsync("rustup metadata", () => HttpDownload.ReadBytesAsync(httpClient, uri, token), attempts, delay, token);

    private async Task FetchFileAsync(Uri uri, string path, string hash, int attempts, TimeSpan delay, CancellationToken token)
    {
        if (_downloaded.TryGetValue(Path.GetFullPath(path), out var previous) && previous == hash && File.Exists(path)) return;
        logger.Info($"rustup: downloading {Path.GetFileName(path)}");
        await retryExecutor.ExecuteAsync("rustup archive", async () =>
        {
            await HttpDownload.DownloadAsync(httpClient, uri, path, hash, token);
            return true;
        }, attempts, delay, token);
        _downloaded[Path.GetFullPath(path)] = hash;
    }

    private static ArchiveReference[] ParseSelectedArchives(string manifest, string target)
    {
        var document = Toml.Parse(manifest);
        if (document.HasErrors) throw new InvalidDataException($"Invalid rustup manifest: {document.Diagnostics}");
        var model = document.ToModel();
        if (!model.TryGetValue("pkg", out var packagesValue) || packagesValue is not TomlTable packages) throw new InvalidDataException("Rustup manifest has no packages.");
        var result = new List<ArchiveReference>();
        foreach (var package in packages)
        {
            if (package.Value is not TomlTable details || !details.TryGetValue("target", out var targetsValue) || targetsValue is not TomlTable targets) continue;
            foreach (var item in targets)
            {
                if (item.Key != target && !(package.Key == "rust-src" && item.Key == "*")) continue;
                if (item.Value is not TomlTable entry || !entry.TryGetValue("available", out var available) || available is not true) continue;
                var prefix = entry.ContainsKey("xz_url") ? "xz_" : "";
                if (!entry.TryGetValue(prefix + "url", out var urlValue) || urlValue is not string url ||
                    !entry.TryGetValue(prefix + "hash", out var hashValue) || hashValue is not string hash || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Available rustup components require URL and SHA-256.");
                result.Add(new ArchiveReference(url, hash.ToLowerInvariant()));
            }
        }
        if (result.Count == 0) throw new InvalidDataException($"No available components for target {target}.");
        return result.DistinctBy(x => (x.Url, x.Hash)).ToArray();
    }

    private static string ParseSha256(string content)
    {
        var value = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid SHA-256 metadata.");
        return value.ToLowerInvariant();
    }

    private sealed record ArchiveReference(string Url, string Hash);
}
