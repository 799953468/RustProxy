using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;

namespace RustProxy.Core.Storage;

/// <summary>Creates and imports portable RustProxy ZIP packages.</summary>
public static class PackageStore
{
    /// <summary>Creates a package from files in a local source directory.</summary>
    public static async Task CreateFromDirectoryAsync(string sourceDirectory, string outputPath, string name, string? source, PackageManifest? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var manifest = metadata ?? new PackageManifest { Name = name, Source = source };
        manifest.Name = name;
        manifest.Source = source;
        manifest.Files.Clear();
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        await using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Normalize(Path.GetRelativePath(root, file));
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await using var entryStream = await entry.OpenAsync(cancellationToken);
            await input.CopyToAsync(entryStream, cancellationToken);
            manifest.Files.Add(new PackageFile { Path = relative, Length = input.Length, Sha256 = await HashFileAsync(file, cancellationToken) });
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = await manifestEntry.OpenAsync(cancellationToken);
        await JsonSerializer.SerializeAsync(manifestStream, manifest, RustProxyJsonContext.Default.PackageManifest, cancellationToken);
    }

    /// <summary>Reads and validates the manifest from a package without extracting it.</summary>
    public static async Task<PackageManifest> ReadManifestAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        await using var input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        return await ReadArchiveManifestAsync(archive, cancellationToken);
    }

    /// <summary>Imports and validates a package into a local source directory.</summary>
    public static async Task<PackageManifest> ImportAsync(ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var input = new FileStream(options.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = await ReadArchiveManifestAsync(archive, cancellationToken);
        var destination = Path.GetFullPath(options.DestinationDirectory);
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            var normalized = Normalize(file.Path);
            if (normalized.Contains("../", StringComparison.Ordinal) ||
                normalized.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(normalized)) throw new InvalidDataException($"Unsafe package path: {file.Path}");
            var entry = archive.GetEntry(normalized) ?? throw new InvalidDataException($"Missing package file: {normalized}");
            var target =
                Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsafe package path: {normalized}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var source = await entry.OpenAsync(cancellationToken))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                await source.CopyToAsync(output, cancellationToken);
            }

            var actualLength = new FileInfo(target).Length;
            if (actualLength != file.Length || !string.Equals(await HashFileAsync(target, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch: {normalized}");
        }

        return manifest;
    }

    private static async Task<PackageManifest> ReadArchiveManifestAsync(ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Package does not contain manifest.json.");
        await using var stream = await manifestEntry.OpenAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync(stream, RustProxyJsonContext.Default.PackageManifest, cancellationToken);
        if (manifest is null || manifest.FormatVersion != "1" || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("Invalid package manifest.");
        return manifest;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
