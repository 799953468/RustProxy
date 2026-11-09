using RustProxy.Core.Storage;

namespace RustProxy.Server.Services;

/// <summary>Keeps publication and object cleanup exclusive to one server process per data directory.</summary>
public sealed class PackageStorageLease : IDisposable
{
    private readonly FileStream _handle;

    /// <summary>Acquires the directory's OS-backed lease before database initialization or migration.</summary>
    /// <param name="contentRoot">The server content root containing its private data directory.</param>
    public PackageStorageLease(string contentRoot)
    {
        var path = PathSafety.Resolve(Path.Combine(contentRoot, "data"), "metadata/server.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
