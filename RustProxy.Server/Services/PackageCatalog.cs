using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;
using RustProxy.Core.Storage;

namespace RustProxy.Server.Services;

/// <summary>Manages validated RustProxy packages stored by the server.</summary>
public sealed class PackageCatalog(IWebHostEnvironment environment, IMemoryCache cache)
{
    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data");
    private readonly string _metadataRoot = Path.Combine(environment.ContentRootPath, "data", "packages");
    private long _cacheVersion;

    /// <summary>Gets all imported package manifests.</summary>
    public async Task<IReadOnlyList<PackageManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        var key = $"catalog:list:{Volatile.Read(ref _cacheVersion)}";
        return await cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return LoadManifestsAsync(cancellationToken);
        }) ?? [];
    }

    private async Task<IReadOnlyList<PackageManifest>> LoadManifestsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_metadataRoot);
        var manifests = new List<PackageManifest>();
        foreach (var path in Directory.EnumerateFiles(_metadataRoot, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                manifests.Add(await ReadStoredManifestAsync(path, cancellationToken));
            }
            catch (JsonException)
            {
            }
        }

        return manifests.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    /// <summary>Finds a stored package manifest by name.</summary>
    public async Task<PackageManifest?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:manifest:{name}";
        if (cache.TryGetValue<PackageManifest?>(key, out var cached)) return cached;
        var manifestPath = Path.Combine(GetPackageDirectory(name), "manifest.json");
        var manifest = File.Exists(manifestPath) ? await ReadStoredManifestAsync(manifestPath, cancellationToken) : null;
        cache.Set(key, manifest, TimeSpan.FromSeconds(30));
        return manifest;
    }

    /// <summary>Imports an uploaded package into the shared data tree.</summary>
    public async Task<PackageManifest> ImportAsync(Stream package, string fileName, CancellationToken cancellationToken = default)
    {
        if (!fileName.EndsWith(".rpk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only .rpk files can be imported.");
        var temporary = Path.Combine(Path.GetTempPath(), $"rustproxy-{Guid.NewGuid():N}.rpk");
        var staging = Path.Combine(Path.GetTempPath(), $"rustproxy-stage-{Guid.NewGuid():N}");
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await package.CopyToAsync(output, cancellationToken);
            var manifest = await PackageStore.ReadManifestAsync(temporary, cancellationToken);
            Directory.CreateDirectory(staging);
            await PackageStore.ImportAsync(new ImportOptions { PackagePath = temporary, DestinationDirectory = staging }, cancellationToken);

            Directory.CreateDirectory(_root);
            var files = manifest.Files
                .Select(file => (ManifestFile: file, Relative: NormalizeAndValidatePath(file.Path)))
                .ToArray();
            foreach (var (file, relative) in files)
            {
                var targetPath = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetPath) && !await HasChecksumAsync(targetPath, file, cancellationToken))
                    throw new InvalidDataException($"Conflicting file already exists: {relative}");
            }

            foreach (var (file, relative) in files)
            {
                var sourcePath = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                var targetPath = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetPath)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath);
            }

            var packageDirectory = GetPackageDirectory(manifest.Name);
            Directory.CreateDirectory(packageDirectory);
            var manifestPath = Path.Combine(packageDirectory, "manifest.json");
            var manifestText = JsonSerializer.Serialize(manifest, RustProxyJsonContext.Default.PackageManifest);
            var manifestTemp = manifestPath + ".tmp";
            await File.WriteAllTextAsync(manifestTemp, manifestText, cancellationToken);
            File.Move(manifestTemp, manifestPath, overwrite: true);
            Interlocked.Increment(ref _cacheVersion);
            return manifest;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch
            {
            }

            ImportLock.Release();
        }
    }

    /// <summary>Finds a source file in the shared Cargo or rustup tree.</summary>
    public async Task<string?> FindSourceFileAsync(string source, string? path, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:source:{source}:{path ?? string.Empty}";
        return await cache.GetOrCreateAsync<string?>(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return FindSourceFileCoreAsync(source, path);
        });
    }

    /// <summary>Finds a file in the shared source tree or a package manifest.</summary>
    public async Task<string?> FindPackageFileAsync(string name, string? path, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:package:{name}:{path ?? string.Empty}";
        return await cache.GetOrCreateAsync<string?>(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return FindPackageFileCoreAsync(name, path);
        });
    }

    private Task<string?> FindSourceFileCoreAsync(string source, string? path)
    {
        if (source is not ("cargo" or "rustup")) return Task.FromResult<string?>(null);
        var relative = NormalizeAndValidatePath(Path.Combine(source, path ?? string.Empty));
        var fullPath = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return Task.FromResult(IsWithin(_root, fullPath) && File.Exists(fullPath) ? fullPath : null);
    }

    private Task<string?> FindPackageFileCoreAsync(string name, string? path)
    {
        var relative = path ?? string.Empty;
        if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(GetPackageDirectory(name), "manifest.json");
            return Task.FromResult(File.Exists(manifestPath) ? manifestPath : null);
        }

        var normalized = NormalizeAndValidatePath(relative);
        var fullPath = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return Task.FromResult(IsWithin(_root, fullPath) && File.Exists(fullPath) ? fullPath : null);
    }

    private static async Task<bool> HasChecksumAsync(string path, PackageFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != file.Length) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string NormalizeAndValidatePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("../", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
            throw new InvalidDataException($"Unsafe package path: {path}");
        return normalized;
    }

    /// <summary>Gets the data directory used for imported source files.</summary>
    public string RootDirectory => _root;

    private string GetPackageDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("Invalid package name.", nameof(name));
        return Path.Combine(_metadataRoot, name);
    }

    private static async Task<PackageManifest> ReadStoredManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await JsonSerializer.DeserializeAsync(stream, RustProxyJsonContext.Default.PackageManifest, cancellationToken) ?? throw new InvalidDataException("Invalid stored manifest.");
    }
}
