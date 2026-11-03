using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;
using RustProxy.Core.Storage;
using RustProxy.Server.Entities;
using SqlSugar;

namespace RustProxy.Server.Services;

/// <summary>Manages validated package metadata and files stored by the server.</summary>
public sealed class PackageCatalog(IWebHostEnvironment environment, IMemoryCache cache, ISqlSugarClient database)
{
    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data");
    private long _cacheVersion;

    /// <summary>Gets all imported package manifests.</summary>
    public async Task<IReadOnlyList<PackageManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        var key = $"catalog:list:{Volatile.Read(ref _cacheVersion)}";
        return await cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return Task.FromResult<IReadOnlyList<PackageManifest>>(database.Queryable<PackageMetadata>()
                .ToList()
                .Select(metadata => BuildManifest(metadata.Id, metadata))
                .OrderByDescending(x => x.CreatedAt)
                .ToArray());
        }) ?? [];
    }

    /// <summary>Finds a stored package manifest by its database identifier.</summary>
    public async Task<PackageManifest?> FindAsync(long id, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:manifest:{id}";
        if (cache.TryGetValue<PackageManifest?>(key, out var cached)) return cached;
        var metadata = database.Queryable<PackageMetadata>().First(x => x.Id == id);
        var manifest = metadata is null ? null : BuildManifest(id, metadata);
        cache.Set(key, manifest, TimeSpan.FromSeconds(30));
        return manifest;
    }

    /// <summary>Imports an uploaded package into the shared source tree and metadata database.</summary>
    public async Task<(long Id, PackageManifest Manifest)> ImportAsync(Stream package, string fileName, CancellationToken cancellationToken = default)
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
            var files = manifest.Files.Select(file => (ManifestFile: file, Relative: NormalizeAndValidatePath(file.Path))).ToArray();
            foreach (var (file, relative) in files)
            {
                var targetPath = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetPath) && !await HasChecksumAsync(targetPath, file, cancellationToken)) throw new InvalidDataException($"Conflicting file already exists: {relative}");
            }
            foreach (var (_, relative) in files)
            {
                var sourcePath = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                var targetPath = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetPath)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath);
            }
            var metadata = new PackageMetadata { Id = SnowFlakeSingle.Instance.NextId(), FormatVersion = manifest.FormatVersion, Name = manifest.Name, CreatedAt = manifest.CreatedAt, Source = manifest.Source };
            using var transaction = database.Ado.UseTran();
            database.Insertable(metadata).ExecuteCommand();
            database.Insertable(manifest.CargoDependencies.Select(x => new PackageCargoDependencyMetadata { Id = SnowFlakeSingle.Instance.NextId(), PackageId = metadata.Id, Name = x.Name, Version = x.Version, Checksum = x.Checksum }).ToList()).ExecuteCommand();
            database.Insertable(manifest.Toolchains.Select(x => new PackageToolchainMetadata { Id = SnowFlakeSingle.Instance.NextId(), PackageId = metadata.Id, Version = x.Version, Target = x.Target }).ToList()).ExecuteCommand();
            database.Insertable(manifest.Files.Select(x => new PackageFileMetadata { Id = SnowFlakeSingle.Instance.NextId(), PackageId = metadata.Id, Path = x.Path, Length = x.Length, Sha256 = x.Sha256 }).ToList()).ExecuteCommand();
            transaction.CommitTran();
            Interlocked.Increment(ref _cacheVersion);
            return (metadata.Id, manifest);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
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
    public async Task<string?> FindPackageFileAsync(long id, string? path, CancellationToken cancellationToken = default)
    {
        var key = $"catalog:package:{id}:{path ?? string.Empty}";
        return await cache.GetOrCreateAsync<string?>(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return FindPackageFileCoreAsync(id, path);
        });
    }

    private Task<string?> FindSourceFileCoreAsync(string source, string? path)
    {
        if (source is not ("cargo" or "rustup")) return Task.FromResult<string?>(null);
        var relative = NormalizeAndValidatePath(Path.Combine(source, path ?? string.Empty));
        var fullPath = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return Task.FromResult(IsWithin(_root, fullPath) && File.Exists(fullPath) ? fullPath : null);
    }

    private async Task<string?> FindPackageFileCoreAsync(long id, string? path)
    {
        var manifest = await FindAsync(id);
        if (manifest is null) return null;
        if ((path ?? string.Empty).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(Path.GetTempPath(), $"rustproxy-manifest-{id}.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, RustProxyJsonContext.Default.PackageManifest));
            return manifestPath;
        }
        var normalized = NormalizeAndValidatePath(path ?? string.Empty);
        var fullPath = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return IsWithin(_root, fullPath) && File.Exists(fullPath) ? fullPath : null;
    }

    private PackageManifest BuildManifest(long id, PackageMetadata metadata) => new()
    {
        FormatVersion = metadata.FormatVersion,
        Name = metadata.Name,
        CreatedAt = metadata.CreatedAt,
        Source = metadata.Source,
        CargoDependencies = database.Queryable<PackageCargoDependencyMetadata>().Where(x => x.PackageId == id).ToList().Select(x => new CargoDependencyInfo { Name = x.Name, Version = x.Version, Checksum = x.Checksum }).ToList(),
        Toolchains = database.Queryable<PackageToolchainMetadata>().Where(x => x.PackageId == id).ToList().Select(x => new RustToolchainInfo { Version = x.Version, Target = x.Target }).ToList(),
        Files = database.Queryable<PackageFileMetadata>().Where(x => x.PackageId == id).ToList().Select(x => new PackageFile { Path = x.Path, Length = x.Length, Sha256 = x.Sha256 }).ToList()
    };

    private static async Task<bool> HasChecksumAsync(string path, PackageFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != file.Length) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string NormalizeAndValidatePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("../", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(normalized)) throw new InvalidDataException($"Unsafe package path: {path}");
        return normalized;
    }

    /// <summary>Gets the data directory used for imported source files.</summary>
    public string RootDirectory => _root;
}
