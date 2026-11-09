using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Readers;
using Tomlyn;
using Tomlyn.Model;
using RustProxy.Core.Download;
using RustProxy.Core.Storage;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Resolves Cargo without copying or rewriting workspace manifests.</summary>
internal sealed class CargoCommandResolver(IFetchLogger logger)
{
    private static readonly HttpClient Client = new SourceHttpClient();
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    public async Task GenerateLockAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var manifest = Path.Combine(projectDirectory, "Cargo.toml");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Cargo.toml is required.", manifest);
        var cargo = FindCargoOnPath() ?? await GetCachedCargoAsync(cancellationToken);
        // Cargo resolves workspace members, relative dependencies and custom targets from their actual locations.
        await ExternalCommandRunner.RunAsync(cargo, ["generate-lockfile", "--manifest-path", manifest], projectDirectory, cancellationToken);
        if (!File.Exists(Path.Combine(projectDirectory, "Cargo.lock")))
            throw new InvalidDataException("Cargo generated no Cargo.lock next to this manifest. For a workspace member, fetch the workspace root.");
    }

    private async Task<string> GetCachedCargoAsync(CancellationToken cancellationToken)
    {
        var host = GetRustupHostTriple();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rustproxy", "cargo", "stable", host);
        var executable = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        var cargoPath = PathSafety.Resolve(root, "bin/" + executable);
        await CacheLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cargoPath)) return cargoPath;
            var staging = root + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(staging);
                var manifestUri = new Uri("https://static.rust-lang.org/dist/channel-rust-stable.toml");
                var bytes = await HttpDownload.ReadBytesAsync(Client, manifestUri, cancellationToken);
                var checksumText = await HttpDownload.ReadTextAsync(Client, new Uri(manifestUri.AbsoluteUri + ".sha256"), cancellationToken);
                var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit) ||
                    !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Cargo manifest checksum mismatch.");
                var parsed = Toml.Parse(Encoding.UTF8.GetString(bytes));
                if (parsed.HasErrors) throw new InvalidDataException("Invalid Cargo distribution manifest.");
                var model = parsed.ToModel();
                if (model["pkg"] is not TomlTable packages || packages["cargo"] is not TomlTable cargo ||
                    cargo["target"] is not TomlTable targets || !targets.TryGetValue(host, out var hostValue) || hostValue is not TomlTable entry ||
                    !entry.TryGetValue("xz_url", out var urlValue) || urlValue is not string url ||
                    !entry.TryGetValue("xz_hash", out var hashValue) || hashValue is not string hash || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Cargo component is unavailable for {host}.");
                var archivePath = Path.Combine(staging, "component.tar.xz");
                await HttpDownload.DownloadAsync(Client, new Uri(url), archivePath, hash, cancellationToken);
                var bin = Path.Combine(staging, "bin");
                Directory.CreateDirectory(bin);
                var stagedCargo = Path.Combine(bin, executable);
                await ExtractCargoAsync(archivePath, stagedCargo, executable, cancellationToken);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stagedCargo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.Delete(archivePath);
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                // Never merge into an incomplete or concurrently modified cache directory.
                if (Directory.Exists(root)) throw new IOException("Cargo cache is incomplete. Remove the incomplete cache directory before retrying.");
                Directory.Move(staging, root);
                logger.Info($"Cargo: cached executable at {cargoPath}");
                return cargoPath;
            }
            finally { PackageStore.DeleteTemporary(staging); }
        }
        finally { CacheLock.Release(); }
    }

    private static async Task ExtractCargoAsync(string archivePath, string outputPath, string executable, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(input);
        var count = 0;
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 100_000) throw new InvalidDataException("Cargo archive has too many entries.");
            var entry = reader.Entry ?? throw new InvalidDataException("Invalid Cargo archive entry.");
            if (entry.IsDirectory) continue;
            var name = PathSafety.Normalize(entry.Key);
            if (!name.EndsWith("/bin/" + executable, StringComparison.Ordinal)) continue;
            if (entry.LinkTarget is not null && entry.LinkTarget.Length > 0) throw new InvalidDataException("Cargo executable cannot be a symbolic link.");
            await using var source = reader.OpenEntryStream();
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await StreamTransfer.CopyAsync(source, output, 256L * 1024 * 1024, cancellationToken);
            return;
        }
        throw new InvalidDataException("Cargo archive did not contain the expected executable.");
    }

    private static string? FindCargoOnPath()
    {
        var executable = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), executable)).FirstOrDefault(File.Exists);
    }

    private static string GetRustupHostTriple()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", _ => throw new PlatformNotSupportedException("Unsupported architecture.")
        };
        if (OperatingSystem.IsWindows()) return $"{architecture}-pc-windows-msvc";
        if (OperatingSystem.IsLinux()) return $"{architecture}-unknown-linux-gnu";
        if (OperatingSystem.IsMacOS()) return $"{architecture}-apple-darwin";
        throw new PlatformNotSupportedException("Unsupported operating system.");
    }
}
