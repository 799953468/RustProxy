using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;

namespace RustProxy.Core.Storage;

/// <summary>Creates portable packages and validates complete imports before publishing files.</summary>
public static class PackageStore
{
    /// <summary>Creates an archive beside the destination and atomically replaces it after success.</summary>
    public static async Task CreateFromDirectoryAsync(string sourceDirectory, string outputPath, string name, string? source, PackageManifest? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var root = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(outputPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var relativeOutput = Path.GetRelativePath(root, destination);
        if (relativeOutput != ".." && !relativeOutput.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relativeOutput))
            throw new ArgumentException("The output package must be outside the source directory.", nameof(outputPath));
        PathSafety.RejectLink(root);
        PathSafety.RejectLink(destination);
        var files = EnumerateFiles(root).Select(file => (File: file, Relative: PathSafety.Normalize(Path.GetRelativePath(root, file)))).ToArray();
        if (files.Any(x => x.Relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("manifest.json is reserved by the package format.");
        var manifest = metadata ?? new PackageManifest();
        manifest.Name = name;
        manifest.Source = source;
        manifest.Files = [];
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var checkedPath = PathSafety.Resolve(root, file.Relative);
                    var extension = Path.GetExtension(file.File);
                    var compression = extension is ".xz" or ".gz" or ".zip" or ".crate" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                    var entry = archive.CreateEntry(file.Relative, compression);
                    await using var input = new FileStream(checkedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    await using var entryStream = await entry.OpenAsync(cancellationToken);
                    var result = await StreamTransfer.CopyAsync(input, entryStream, new PackageLimits().MaxFileBytes, cancellationToken);
                    manifest.Files.Add(new PackageFile { Path = file.Relative, Length = result.Length, Sha256 = result.Sha256 });
                }
                ValidateManifest(manifest, new PackageLimits());
                await using var manifestStream = await archive.CreateEntry("manifest.json", CompressionLevel.Fastest).OpenAsync(cancellationToken);
                await JsonSerializer.SerializeAsync(manifestStream, manifest, RustProxyJsonContext.Default.PackageManifest, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { DeleteTemporary(temporary); }
    }

    /// <summary>Reads a bounded, structurally validated package manifest.</summary>
    public static async Task<PackageManifest> ReadManifestAsync(string packagePath, CancellationToken cancellationToken = default, PackageLimits? limits = null)
    {
        limits ??= new PackageLimits();
        limits.Validate();
        await using var input = OpenPackage(packagePath, limits);
        await using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        return await ReadManifestAsync(archive, limits, cancellationToken);
    }

    /// <summary>Validates all bytes in staging, then publishes with rollback on commit failure.</summary>
    public static async Task<PackageManifest> ImportAsync(ImportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Limits.Validate();
        await using var input = OpenPackage(options.PackagePath, options.Limits);
        await using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = await ReadManifestAsync(archive, options.Limits, cancellationToken);
        var destination = Path.GetFullPath(options.DestinationDirectory);
        PathSafety.RejectLink(destination);
        foreach (var file in manifest.Files) PathSafety.Resolve(destination, file.Path);
        var operation = Path.Combine(Path.GetDirectoryName(destination)!, ".rustproxy-import-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(operation, "validated");
        var backup = Path.Combine(operation, "backup");
        var committed = new List<(string Target, string? Backup)>();
        var preserveRecovery = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in manifest.Files)
            {
                var target = PathSafety.Resolve(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = await archive.GetEntry(file.Path)!.OpenAsync(cancellationToken);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var copied = await StreamTransfer.CopyAsync(source, output, file.Length, cancellationToken);
                if (copied.Length != file.Length || !copied.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Checksum mismatch: {file.Path}");
            }
            Directory.CreateDirectory(destination);
            try
            {
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = PathSafety.Resolve(destination, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string? saved = null;
                    if (File.Exists(target))
                    {
                        saved = PathSafety.Resolve(backup, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                        File.Move(target, saved);
                    }
                    committed.Add((target, saved));
                    File.Move(PathSafety.Resolve(staging, file.Path), target);
                }
            }
            catch (Exception original)
            {
                var errors = new List<Exception> { original };
                foreach (var item in committed.AsEnumerable().Reverse())
                {
                    try
                    {
                        File.Delete(item.Target);
                        if (item.Backup is not null) File.Move(item.Backup, item.Target);
                    }
                    catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException) { errors.Add(recovery); }
                }
                if (errors.Count > 1)
                {
                    preserveRecovery = true;
                    throw new AggregateException($"Import rollback failed; recovery files are retained at {operation}", errors);
                }
                throw;
            }
            return manifest;
        }
        finally { if (!preserveRecovery) DeleteTemporary(operation); }
    }

    /// <summary>Validates collections, paths, aliases, lengths, checksums and aggregate limits.</summary>
    public static void ValidateManifest(PackageManifest manifest, PackageLimits limits, bool sourceOnly = false)
    {
        limits.Validate();
        if (manifest.FormatVersion != "1" || manifest.CreatedAt.Year < 1970 || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 256 ||
            manifest.Source?.Length > 4096 || manifest.Files is null || manifest.CargoDependencies is null || manifest.Toolchains is null ||
            manifest.Files.Count > limits.MaxFiles || manifest.CargoDependencies.Count > limits.MaxFiles || manifest.Toolchains.Count > 1000)
            throw new InvalidDataException("Invalid package manifest or manifest quota exceeded.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Null file entry.");
            var normalized = PathSafety.Normalize(file.Path, sourceOnly);
            if (normalized.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || !paths.Add(normalized) ||
                file.Length < 0 || file.Length > limits.MaxFileBytes || file.Length > limits.MaxExpandedBytes - total ||
                file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Invalid or duplicate file entry: {file.Path}");
            file.Path = normalized;
            file.Sha256 = file.Sha256.ToLowerInvariant();
            total += file.Length;
        }
        foreach (var path in paths)
        {
            var parent = path;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (paths.Contains(parent)) throw new InvalidDataException("A file cannot also be a directory.");
            }
        }
        if (manifest.CargoDependencies.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 256 || string.IsNullOrWhiteSpace(x.Version) || x.Version.Length > 256 ||
                x.Checksum is not null && (x.Checksum.Length != 64 || !x.Checksum.All(Uri.IsHexDigit))) ||
            manifest.Toolchains.Any(x => x is null || string.IsNullOrWhiteSpace(x.Version) || x.Version.Length > 128 || string.IsNullOrWhiteSpace(x.Target) || x.Target.Length > 128))
            throw new InvalidDataException("Invalid dependency or toolchain metadata.");
    }

    private static async Task<PackageManifest> ReadManifestAsync(ZipArchive archive, PackageLimits limits, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > limits.MaxFiles + 1) throw new InvalidDataException("Too many archive entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            if (!names.Add(PathSafety.Normalize(entry.FullName))) throw new InvalidDataException("Duplicate archive entry.");
        var entryManifest = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Package does not contain manifest.json.");
        if (entryManifest.Length > limits.MaxManifestBytes) throw new InvalidDataException("Manifest is too large.");
        using var bytes = new MemoryStream();
        await using (var stream = await entryManifest.OpenAsync(cancellationToken))
            await StreamTransfer.CopyAsync(stream, bytes, limits.MaxManifestBytes, cancellationToken);
        bytes.Position = 0;
        PackageManifest manifest;
        try { manifest = await JsonSerializer.DeserializeAsync(bytes, RustProxyJsonContext.Default.PackageManifest, cancellationToken) ?? throw new InvalidDataException("Empty manifest."); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid package JSON.", exception); }
        ValidateManifest(manifest, limits);
        if (archive.Entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Archive and manifest entries differ.");
        foreach (var file in manifest.Files)
        {
            var entry = archive.GetEntry(file.Path) ?? throw new InvalidDataException($"Missing file: {file.Path}");
            if (entry.Length != file.Length) throw new InvalidDataException($"Length mismatch: {file.Path}");
        }
        return manifest;
    }

    private static FileStream OpenPackage(string path, PackageLimits limits)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length <= limits.MaxPackageBytes) return input;
        input.Dispose();
        throw new InvalidDataException("Compressed package exceeds the upload quota.");
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            PathSafety.RejectLink(entry);
            if (Directory.Exists(entry))
            {
                foreach (var nested in EnumerateFiles(entry)) yield return nested;
            }
            else yield return entry;
        }
    }

    /// <summary>Removes temporary artifacts, reporting cleanup failures through diagnostics.</summary>
    public static void DeleteTemporary(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Could not remove temporary artifact {0}: {1}", path, exception.Message); }
    }
}
