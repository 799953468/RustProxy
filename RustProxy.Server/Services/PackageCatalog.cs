using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;
using RustProxy.Core.Storage;

namespace RustProxy.Server.Services;

/// <summary>Manages validated RustProxy packages stored by the server.</summary>
public sealed class PackageCatalog(PackageStore packageStore, IWebHostEnvironment environment, IMemoryCache cache)
{
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data", "packages");
    private readonly IMemoryCache _cache = cache;
    /// <summary>Gets all imported package manifests.</summary>
    public async Task<IReadOnlyList<PackageManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrCreateAsync("catalog:list", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return LoadManifestsAsync(cancellationToken);
        }) ?? [];
    }

    private async Task<IReadOnlyList<PackageManifest>> LoadManifestsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var manifests = new List<PackageManifest>();
        foreach (var path in Directory.EnumerateFiles(_root, "manifest.json", SearchOption.AllDirectories))
        {
            try { manifests.Add(await ReadStoredManifestAsync(path, cancellationToken)); }
            catch (JsonException) { }
        }
        return manifests.OrderByDescending(x => x.CreatedAt).ToArray();
    }


    public async Task<PackageManifest?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:manifest:{name}";
        if (_cache.TryGetValue<PackageManifest?>(key, out var cached)) return cached;
        var path = GetPackageDirectory(name);
        var manifestPath = Path.Combine(path, "manifest.json");
        var manifest = File.Exists(manifestPath) ? await ReadStoredManifestAsync(manifestPath, cancellationToken) : null;
        _cache.Set(key, manifest, TimeSpan.FromSeconds(30));
        return manifest;
    }

    /// <summary>Imports an uploaded package and returns its manifest.</summary>
    public async Task<PackageManifest> ImportAsync(Stream package, string fileName, CancellationToken cancellationToken = default)
    {
        if (!fileName.EndsWith(".rpk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only .rpk files can be imported.");
        var temporary = Path.Combine(Path.GetTempPath(), $"rustproxy-{Guid.NewGuid():N}.rpk");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await package.CopyToAsync(output, cancellationToken);
            var manifest = await packageStore.ReadManifestAsync(temporary, cancellationToken);
            var directory = GetPackageDirectory(manifest.Name);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);
            await packageStore.ImportAsync(new ImportOptions { PackagePath = temporary, DestinationDirectory = directory }, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, RustProxyJsonContext.Default.PackageManifest), cancellationToken);
            _cache.Remove("catalog:list");
            _cache.Remove($"catalog:manifest:{manifest.Name}");
            return manifest;
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    /// <summary>Finds a source file in the imported package catalog.</summary>
    public async Task<string?> FindSourceFileAsync(string source, string? path, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:source:{source}:{path ?? string.Empty}";
        return await _cache.GetOrCreateAsync<string?>(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return FindSourceFileCoreAsync(source, path, cancellationToken);
        });
    }

    /// <summary>Finds a file inside a named imported package.</summary>
    public async Task<string?> FindPackageFileAsync(string name, string? path, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:package:{name}:{path ?? string.Empty}";
        return await _cache.GetOrCreateAsync<string?>(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return FindPackageFileCoreAsync(name, path, cancellationToken);
        });
    }

    private async Task<string?> FindSourceFileCoreAsync(string source, string? path, CancellationToken cancellationToken)
    {
        var relative = Path.Combine(source, (path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        foreach (var manifest in await ListAsync(cancellationToken))
        {
            var packageRoot = Path.Combine(_root, manifest.Name);
            var fullPath = Path.GetFullPath(Path.Combine(packageRoot, relative));
            if (IsWithin(packageRoot, fullPath) && File.Exists(fullPath)) return fullPath;
        }
        return null;
    }

    private Task<string?> FindPackageFileCoreAsync(string name, string? path, CancellationToken cancellationToken)
    {
        var packageRoot = GetPackageDirectory(name);
        var fullPath = Path.GetFullPath(Path.Combine(packageRoot, (path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        return Task.FromResult<string?>(IsWithin(packageRoot, fullPath) && File.Exists(fullPath) ? fullPath : null);
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Gets the storage directory used for imported packages.</summary>
    public string RootDirectory => _root;

    private string GetPackageDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.')) throw new ArgumentException("Invalid package name.", nameof(name));
        return Path.Combine(_root, name);
    }

    private static async Task<PackageManifest> ReadStoredManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await JsonSerializer.DeserializeAsync(stream, RustProxyJsonContext.Default.PackageManifest, cancellationToken) ?? throw new InvalidDataException("Invalid stored manifest.");
    }
}
