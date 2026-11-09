using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using RustProxy.Core.Package;
using RustProxy.Core.Storage;
using RustProxy.Server.Entities;

namespace RustProxy.Server.Services;

public sealed partial class PackageCatalog
{
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly SemaphoreSlim[] _objectLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<(string Hash, long Length), byte> _unavailableObjects = new();
    private int _auditCompleted;

    /// <summary>Reports known integrity failures, or -1 until a complete audit succeeds.</summary>
    public int AuditFailures => Volatile.Read(ref _auditCompleted) == 0 ? -1 : _unavailableObjects.Count;

    /// <summary>Rechecks unique objects in bounded pages, including objects already verified for downloads.</summary>
    public async Task<int> AuditAsync(CancellationToken cancellationToken)
    {
        await _auditLock.WaitAsync(cancellationToken);
        try
        {
            var offset = 0;
            while (true)
            {
                var files = await database.Queryable<PackageFileMetadata>().GroupBy(x => new { x.Sha256, x.Length })
                    .Select(x => new PackageFile { Sha256 = x.Sha256, Length = x.Length })
                    .OrderBy(x => x.Sha256).OrderBy(x => x.Length).Skip(offset).Take(500).ToListAsync(cancellationToken);
                if (files.Count == 0)
                {
                    Volatile.Write(ref _auditCompleted, 1);
                    return _unavailableObjects.Count;
                }
                foreach (var file in files)
                    await VerifyObjectAsync(file.Sha256, file.Length, true, cancellationToken);
                offset += files.Count;
            }
        }
        catch
        {
            Volatile.Write(ref _auditCompleted, 0);
            throw;
        }
        finally { _auditLock.Release(); }
    }

    private SemaphoreSlim ObjectLock(string hash) => _objectLocks[Convert.ToByte(hash[..2], 16) % _objectLocks.Length];

    private async Task<bool> VerifyObjectAsync(string hash, long length, bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHash(hash)) { RecordAvailability(hash, length, false); return false; }
        hash = hash.ToLowerInvariant();
        // Immutable files with unchanged metadata can be served while a forced background audit is hashing them.
        if (!force && CachedValidity(hash, length) is { } cached) return cached;
        var gate = ObjectLock(hash);
        await gate.WaitAsync(cancellationToken);
        try { return await VerifyObjectCoreAsync(hash, length, force, cancellationToken); }
        finally { gate.Release(); }
    }

    private bool? CachedValidity(string hash, long length)
    {
        if (!_cache.TryGetValue<VerifiedObject>("object:" + hash, out var cached)) return null;
        try
        {
            var info = new FileInfo(ObjectPath(hash));
            return info.Exists && info.Length == length && ObjectSnapshot.Read(info) == cached!.Snapshot ? cached.Valid : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    // Callers hold the checksum's lock, so a repair cannot race an audit or a download verification.
    private async Task<bool> VerifyObjectCoreAsync(string hash, long length, bool force, CancellationToken cancellationToken, bool trackFailure = true)
    {
        var valid = false;
        var canceled = false;
        var key = "object:" + hash;
        try
        {
            var info = new FileInfo(ObjectPath(hash));
            if (!info.Exists || info.Length != length) return false;
            var before = ObjectSnapshot.Read(info);
            if (!force && _cache.TryGetValue<VerifiedObject>(key, out var cached) && cached!.Snapshot == before)
                return valid = cached.Valid;

            await using (var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true))
                valid = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).Equals(hash, StringComparison.OrdinalIgnoreCase);
            info.Refresh();
            valid &= info.Exists && ObjectSnapshot.Read(info) == before;
            _cache.Set(key, new VerifiedObject(before, valid), new MemoryCacheEntryOptions
            { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30), Size = 256 });
            return valid;
        }
        catch (OperationCanceledException) { canceled = true; throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            valid = false;
            _cache.Remove(key);
            return false;
        }
        finally { if (!canceled && (valid || trackFailure)) RecordAvailability(hash, length, valid); }
    }

    private void RecordAvailability(string hash, long length, bool valid)
    {
        var key = (hash, length);
        if (valid) _unavailableObjects.TryRemove(key, out _);
        else if (_unavailableObjects.TryAdd(key, 0))
            logger.LogError("Stored object {Checksum} is missing or corrupted; package metadata was retained", hash);
    }

    private async Task<bool> PublishObjectAsync(string staging, PackageFile file, CancellationToken cancellationToken)
    {
        var gate = ObjectLock(file.Sha256);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var target = ObjectPath(file.Sha256);
            var existed = File.Exists(target);
            if (existed && await VerifyObjectCoreAsync(file.Sha256, file.Length, true, cancellationToken, trackFailure: false)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(PathSafety.Resolve(staging, file.Path), target, overwrite: true);
            _cache.Set("object:" + file.Sha256, new VerifiedObject(ObjectSnapshot.Read(new FileInfo(target)), true),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30), Size = 256 });
            RecordAvailability(file.Sha256, file.Length, true);
            return !existed;
        }
        finally { gate.Release(); }
    }

    private sealed record VerifiedObject(ObjectSnapshot Snapshot, bool Valid);
    private readonly record struct ObjectSnapshot(long Length, DateTime LastWriteTime, DateTime CreationTime)
    {
        public static ObjectSnapshot Read(FileInfo info) => new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }
}
