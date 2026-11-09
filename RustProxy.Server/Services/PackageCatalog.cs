using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using RustProxy.Core.Package;
using RustProxy.Core.Storage;
using RustProxy.Server.Entities;
using SqlSugar;

namespace RustProxy.Server.Services;

/// <summary>Publishes immutable file objects and transactionally records their package membership.</summary>
public sealed class PackageCatalog(IWebHostEnvironment environment, ISqlSugarClient database, ILogger<PackageCatalog> logger, PackageLimits? limits = null) : IDisposable
{
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data");
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private readonly SemaphoreSlim _uploads = new(2, 2);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    private readonly SemaphoreSlim _overviewLock = new(1, 1);
    private long _version;
    private int _auditFailures = -1;

    /// <summary>Gets upload and extraction quotas enforced by the service.</summary>
    public PackageLimits Limits { get; } = limits ?? new();

    /// <summary>Reports the last integrity audit outcome, or -1 while no audit has completed.</summary>
    public int AuditFailures => Volatile.Read(ref _auditFailures);

    /// <summary>Gets the private storage root; HTTP paths are never resolved against this directory.</summary>
    public string RootDirectory => _root;

    /// <summary>Gets lightweight catalog aggregates without loading file manifests.</summary>
    public async Task<CatalogOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var key = $"overview:{Volatile.Read(ref _version)}";
        if (_cache.TryGetValue<CatalogOverview>(key, out var cached)) return cached!;
        await _overviewLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue<CatalogOverview>(key, out cached)) return cached!;
            var dependencies = await database.Queryable<PackageCargoDependencyMetadata>()
                .Select(x => new CargoDependencyInfo { Name = x.Name, Version = x.Version, Checksum = x.Checksum }).Distinct().ToListAsync(cancellationToken);
            var toolchains = await database.Queryable<PackageToolchainMetadata>()
                .Select(x => new RustToolchainInfo { Version = x.Version, Target = x.Target }).Distinct().ToListAsync(cancellationToken);
            var files = await database.Queryable<PackageFileMetadata>().GroupBy(x => x.Path).Select(x => x.Path).CountAsync(cancellationToken);
            var result = new CatalogOverview(dependencies, toolchains, files);
            _cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30), Size = 256L + dependencies.Count * 256L + toolchains.Count * 256L });
            return result;
        }
        finally { _overviewLock.Release(); }
    }

    /// <summary>Loads one manifest; file lists can be omitted for paginated UI queries.</summary>
    public async Task<PackageManifest?> FindAsync(long id, CancellationToken cancellationToken = default, bool includeFiles = true)
    {
        if (id <= 0) return null;
        var item = await database.Queryable<PackageMetadata>().FirstAsync(x => x.Id == id, cancellationToken);
        if (item is null) return null;
        return new PackageManifest
        {
            Name = item.Name, Source = item.Source, CreatedAt = item.CreatedAt, FormatVersion = item.FormatVersion,
            CargoDependencies = await database.Queryable<PackageCargoDependencyMetadata>().Where(x => x.PackageId == id)
                .Select(x => new CargoDependencyInfo { Name = x.Name, Version = x.Version, Checksum = x.Checksum }).ToListAsync(cancellationToken),
            Toolchains = await database.Queryable<PackageToolchainMetadata>().Where(x => x.PackageId == id)
                .Select(x => new RustToolchainInfo { Version = x.Version, Target = x.Target }).ToListAsync(cancellationToken),
            Files = includeFiles ? await GetFilesAsync(id, 0, Limits.MaxFiles, cancellationToken) : []
        };
    }

    /// <summary>Returns an ordered page of manifest file entries.</summary>
    public Task<List<PackageFile>> GetFilesAsync(long id, int start, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return database.Queryable<PackageFileMetadata>().Where(x => x.PackageId == id).OrderBy(x => x.Path)
            .Skip(start).Take(Math.Min(count, Limits.MaxFiles))
            .Select(x => new PackageFile { Path = x.Path, Length = x.Length, Sha256 = x.Sha256 }).ToListAsync(cancellationToken);
    }

    /// <summary>Counts file entries belonging to one package.</summary>
    public Task<int> CountFilesAsync(long id, CancellationToken cancellationToken = default) =>
        database.Queryable<PackageFileMetadata>().Where(x => x.PackageId == id).CountAsync(cancellationToken);

    /// <summary>Imports an administrator's bounded upload, then atomically publishes its database records.</summary>
    public async Task<(long Id, PackageManifest Manifest)> ImportAsync(Stream package, string fileName, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (actor.Identity?.IsAuthenticated != true || !actor.IsInRole("Administrator")) throw new UnauthorizedAccessException("Administrator access is required.");
        if (!fileName.EndsWith(".rpk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only .rpk files can be imported.");
        if (!await _uploads.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("The import queue is full. Try again after an active import completes.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        var operation = PathSafety.Resolve(_root, "staging/" + Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(operation, "upload.rpk");
        var staging = Path.Combine(operation, "source");
        try
        {
            Directory.CreateDirectory(operation);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await StreamTransfer.CopyAsync(package, output, Limits.MaxPackageBytes, token);
            var manifest = await PackageStore.ReadManifestAsync(temporary, token, Limits);
            PackageStore.ValidateManifest(manifest, Limits, sourceOnly: true);
            var drive = new DriveInfo(Path.GetPathRoot(operation)!);
            var required = manifest.Files.Sum(x => x.Length);
            if (drive.AvailableFreeSpace < required + 64L * 1024 * 1024) throw new IOException("Insufficient free disk space for extraction.");
            await PackageStore.ImportAsync(new ImportOptions { PackagePath = temporary, DestinationDirectory = staging, Limits = Limits }, token);
            await _commitLock.WaitAsync(token);
            try
            {
                // Objects are never overwritten. Unreferenced objects from an interrupted import are harmless.
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var target = ObjectPath(file.Sha256);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Move(PathSafety.Resolve(staging, file.Path), target);
                    else if (new FileInfo(target).Length != file.Length) throw new InvalidDataException("Stored object length does not match its checksum.");
                }
                var id = SnowFlakeSingle.Instance.NextId();
                using var transaction = database.Ado.UseTran();
                await database.Insertable(new PackageMetadata
                {
                    Id = id, Name = manifest.Name, Source = manifest.Source, FormatVersion = manifest.FormatVersion, CreatedAt = manifest.CreatedAt
                }).ExecuteCommandAsync(token);
                if (manifest.CargoDependencies.Count > 0)
                    await database.Insertable(manifest.CargoDependencies.Select(x => new PackageCargoDependencyMetadata
                    {
                        Id = SnowFlakeSingle.Instance.NextId(), PackageId = id, Name = x.Name, Version = x.Version, Checksum = x.Checksum
                    }).ToList()).ExecuteCommandAsync(token);
                if (manifest.Toolchains.Count > 0)
                    await database.Insertable(manifest.Toolchains.Select(x => new PackageToolchainMetadata
                    {
                        Id = SnowFlakeSingle.Instance.NextId(), PackageId = id, Version = x.Version, Target = x.Target
                    }).ToList()).ExecuteCommandAsync(token);
                if (manifest.Files.Count > 0)
                    await database.Insertable(manifest.Files.Select(x => new PackageFileMetadata
                    {
                        Id = SnowFlakeSingle.Instance.NextId(), PackageId = id, Path = x.Path, Length = x.Length, Sha256 = x.Sha256
                    }).ToList()).ExecuteCommandAsync(token);
                transaction.CommitTran();
                Interlocked.Increment(ref _version);
                logger.LogInformation("Administrator {Actor} imported package {PackageId} ({Name}), {FileCount} files", actor.Identity.Name, id, manifest.Name, manifest.Files.Count);
                return (id, manifest);
            }
            finally { _commitLock.Release(); }
        }
        finally
        {
            PackageStore.DeleteTemporary(operation);
            _uploads.Release();
        }
    }

    /// <summary>Resolves the latest published version of a source path without caching arbitrary request keys.</summary>
    public async Task<StoredSourceFile?> FindSourceFileAsync(string source, string? path, CancellationToken cancellationToken = default)
    {
        if (source is not ("cargo" or "rustup") || !TryPath($"{source}/{path}", out var normalized)) return null;
        var file = await database.Queryable<PackageFileMetadata>().Where(x => x.Path == normalized).OrderByDescending(x => x.PackageId).FirstAsync(cancellationToken);
        return ResolveObject(file);
    }

    /// <summary>Resolves only an exact member of the requested immutable package.</summary>
    public async Task<StoredSourceFile?> FindPackageFileAsync(long id, string? path, CancellationToken cancellationToken = default)
    {
        if (id <= 0 || !TryPath(path, out var normalized)) return null;
        var file = await database.Queryable<PackageFileMetadata>().Where(x => x.PackageId == id && x.Path == normalized).FirstAsync(cancellationToken);
        return ResolveObject(file);
    }

    /// <summary>Migrates matching legacy resources without removing old files or historical metadata.</summary>
    public async Task MigrateLegacyFilesAsync(CancellationToken cancellationToken)
    {
        var offset = 0;
        while (true)
        {
            var page = await database.Queryable<PackageFileMetadata>().OrderBy(x => x.Id).Skip(offset).Take(500).ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            foreach (var file in page)
            {
                if (!TryPath(file.Path, out var normalized) || !IsHash(file.Sha256)) continue;
                var target = ObjectPath(file.Sha256);
                if (File.Exists(target)) continue;
                var legacy = PathSafety.Resolve(_root, normalized);
                if (!File.Exists(legacy)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = File.OpenRead(legacy))
                    await using (var output = File.Create(temporary))
                    {
                        var result = await StreamTransfer.CopyAsync(input, output, Limits.MaxFileBytes, cancellationToken);
                        if (result.Length != file.Length || !result.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogWarning("Legacy package {PackageId} has unavailable original content for {Resource}; metadata is retained", file.PackageId, file.Path);
                            continue;
                        }
                    }
                    File.Move(temporary, target, overwrite: false);
                }
                finally { PackageStore.DeleteTemporary(temporary); }
            }
            offset += page.Count;
        }
    }

    /// <summary>Audits unique immutable objects in bounded pages without deleting catalog history.</summary>
    public async Task<int> AuditAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var offset = 0;
        while (true)
        {
            var files = await database.Queryable<PackageFileMetadata>().GroupBy(x => new { x.Sha256, x.Length })
                .Select(x => new PackageFile { Sha256 = x.Sha256, Length = x.Length }).OrderBy(x => x.Sha256).Skip(offset).Take(500).ToListAsync(cancellationToken);
            if (files.Count == 0) { Volatile.Write(ref _auditFailures, failures); return failures; }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var valid = false;
                if (IsHash(file.Sha256))
                {
                    var path = ObjectPath(file.Sha256);
                    if (File.Exists(path) && new FileInfo(path).Length == file.Length)
                    {
                        await using var input = File.OpenRead(path);
                        valid = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (!valid)
                {
                    failures++;
                    logger.LogError("Stored object {Checksum} is missing or corrupted; package metadata was retained", file.Sha256);
                }
            }
            offset += files.Count;
        }
    }

    private StoredSourceFile? ResolveObject(PackageFileMetadata? file)
    {
        if (file is null || !IsHash(file.Sha256)) return null;
        var path = ObjectPath(file.Sha256);
        return File.Exists(path) && new FileInfo(path).Length == file.Length ? new StoredSourceFile(path, file.Sha256.ToLowerInvariant()) : null;
    }

    private string ObjectPath(string hash) => PathSafety.Resolve(_root, $"objects/{hash[..2].ToLowerInvariant()}/{hash.ToLowerInvariant()}");
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool TryPath(string? path, out string normalized)
    {
        try { normalized = PathSafety.Normalize(path, sourceOnly: true); return true; }
        catch (InvalidDataException) { normalized = ""; return false; }
    }

    /// <inheritdoc />
    public void Dispose() { _cache.Dispose(); _overviewLock.Dispose(); _commitLock.Dispose(); _uploads.Dispose(); }
}

/// <summary>Catalog aggregates used by the home page.</summary>
public sealed record CatalogOverview(IReadOnlyList<CargoDependencyInfo> Dependencies, IReadOnlyList<RustToolchainInfo> Toolchains, int FileCount);

/// <summary>A validated immutable object and its content digest.</summary>
public sealed record StoredSourceFile(string Path, string Sha256);
