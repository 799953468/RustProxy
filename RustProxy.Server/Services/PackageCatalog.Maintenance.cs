using System.Collections.Concurrent;
using RustProxy.Core.Storage;
using RustProxy.Server.Entities;
using SqlSugar;

namespace RustProxy.Server.Services;

public sealed partial class PackageCatalog
{
    private readonly ConcurrentDictionary<string, byte> _activeOperations = new(StringComparer.Ordinal);

    /// <summary>Reclaims abandoned uploads and unreferenced objects older than a day, preserving all package history.</summary>
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var staging = PathSafety.Resolve(_root, "staging");
        if (Directory.Exists(staging))
        {
            foreach (var operation in Directory.EnumerateDirectories(staging))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(operation), "N", out _) || _activeOperations.ContainsKey(operation)) continue;
                try
                {
                    PathSafety.RejectLink(operation);
                    if (Directory.GetLastWriteTimeUtc(operation) < cutoff) PackageStore.DeleteTemporary(operation);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                { logger.LogWarning(exception, "Could not clean abandoned upload {Operation}", operation); }
            }
        }

        var objects = PathSafety.Resolve(_root, "objects");
        if (!Directory.Exists(objects)) return;
        var batch = new List<string>(256);
        foreach (var prefix in Directory.EnumerateDirectories(objects))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(prefix);
            if (name.Length != 2 || !name.All(Uri.IsHexDigit)) continue;
            PathSafety.RejectLink(prefix);
            foreach (var path in Directory.EnumerateFiles(prefix))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Path.GetFileName(path);
                if (!IsHash(hash) || !hash.StartsWith(name, StringComparison.Ordinal) || hash != hash.ToLowerInvariant()) continue;
                if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                batch.Add(hash);
                if (batch.Count < 256) continue;
                await CleanBatchAsync(batch, cutoff, cancellationToken);
                batch.Clear();
            }
        }
        if (batch.Count > 0) await CleanBatchAsync(batch, cutoff, cancellationToken);
    }

    private async Task CleanBatchAsync(List<string> hashes, DateTime cutoff, CancellationToken cancellationToken)
    {
        await _commitLock.WaitAsync(cancellationToken);
        try { await RemoveUnreferencedObjectsAsync(hashes, cutoff, cancellationToken); }
        finally { _commitLock.Release(); }
    }

    // The commit lock is held so no package can acquire membership between the query and deletion.
    private async Task RemoveUnreferencedObjectsAsync(List<string> hashes, DateTime cutoff, CancellationToken cancellationToken)
    {
        foreach (var batch in hashes.Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var referenced = (await database.Queryable<PackageFileMetadata>().Where(x => batch.Contains(SqlFunc.ToLower(x.Sha256)))
                    .Select(x => x.Sha256).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var hash in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (referenced.Contains(hash)) continue;
                    var gate = ObjectLock(hash);
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        var path = ObjectPath(hash);
                        if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                        File.Delete(path);
                        _cache.Remove("object:" + hash);
                    }
                    finally { gate.Release(); }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogWarning(exception, "Unreferenced object cleanup failed; remaining objects are retained for the next maintenance pass"); }
        }
    }
}
