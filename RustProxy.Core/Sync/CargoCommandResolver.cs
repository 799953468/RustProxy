using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RustProxy.Core.Tools;

namespace RustProxy.Core.Sync;

/// <summary>Finds or downloads a private Cargo executable and uses it to generate lockfiles.</summary>
internal sealed class CargoCommandResolver(IFetchLogger logger)
{
    private static readonly Regex SectionPattern = new(@"^\[pkg\.cargo(?:\.target\.(?<target>[^\]]+))?\]$", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new("^(?:xz_)?url\\s*=\\s*\\\"(?<url>[^\\\"]+)\\\"$", RegexOptions.Compiled);
    private static readonly Regex HashPattern = new("^xz_hash\\s*=\\s*\\\"(?<hash>[a-fA-F0-9]{64})\\\"$", RegexOptions.Compiled);

    /// <summary>Generates Cargo.lock with system Cargo or a cached private Cargo.</summary>
    public async Task GenerateLockAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var cargo = FindCargoOnPath();
        if (cargo is not null)
        {
            logger.Info($"Cargo: using installed executable {cargo}");
            await RunCargoAsync(cargo, projectDirectory, cancellationToken);
            return;
        }

        cargo = await GetCachedCargoAsync("stable", cancellationToken);
        await RunCargoAsync(cargo, projectDirectory, cancellationToken);
    }

    private async Task<string> GetCachedCargoAsync(string channel, CancellationToken cancellationToken)
    {
        var host = GetRustupHostTriple();
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rustproxy", "cargo", channel, host);
        var cargoPath = Path.Combine(cacheRoot, "bin", OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");
        if (File.Exists(cargoPath))
        {
            logger.Info($"Cargo: using cached executable {cargoPath}");
            return cargoPath;
        }

        logger.Info($"Cargo: downloading private {channel} Cargo for {host}");
        var manifestUri = new Uri($"https://static.rust-lang.org/dist/channel-rust-{channel}.toml");
        using var client = new HttpClient();
        using var manifestResponse = await client.GetAsync(manifestUri, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        var manifest = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
        var component = ParseCargoComponent(manifest, host) ?? throw new InvalidDataException($"Cargo component is unavailable for {host}.");

        var archivePath = Path.Combine(Path.GetTempPath(), $"rustproxy-{Guid.NewGuid():N}.tar.xz");
        try
        {
            using var archiveResponse = await client.GetAsync(new Uri(component.Url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            archiveResponse.EnsureSuccessStatusCode();
            await using (var input = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, cancellationToken);

            await using var hashStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            if (!actualHash.Equals(component.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA-256 mismatch for the Cargo component archive.");

            var staging = Path.Combine(Path.GetDirectoryName(cacheRoot)!, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                await ExtractArchiveAsync(archivePath, staging, cancellationToken);
                var extractedCargo = Directory.EnumerateFiles(staging, OperatingSystem.IsWindows() ? "cargo.exe" : "cargo", SearchOption.AllDirectories).FirstOrDefault();
                if (extractedCargo is null) throw new FileNotFoundException("Cargo component archive did not contain cargo executable.");
                var stagedBin = Path.Combine(staging, "bin");
                Directory.CreateDirectory(stagedBin);
                File.Move(extractedCargo, Path.Combine(stagedBin, Path.GetFileName(cargoPath)));
                Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
                if (!Directory.Exists(cacheRoot))
                {
                    try { Directory.Move(staging, cacheRoot); }
                    catch (IOException) when (File.Exists(cargoPath)) { }
                }
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } }
        }
        finally { try { File.Delete(archivePath); } catch (IOException) { } }

        if (!File.Exists(cargoPath)) throw new FileNotFoundException("Cargo was downloaded but the cached executable was not found.", cargoPath);
        logger.Info($"Cargo: cached executable at {cargoPath}");
        return cargoPath;
    }

    private static CargoComponent? ParseCargoComponent(string manifest, string host)
    {
        string? target = null;
        string? url = null;
        string? hash = null;
        foreach (var rawLine in manifest.Split('\n'))
        {
            var line = rawLine.Trim();
            var section = SectionPattern.Match(line);
            if (section.Success)
            {
                if (target is null || target.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    if (url is not null && hash is not null) return new CargoComponent(url, hash);
                }
                target = section.Groups["target"].Success ? section.Groups["target"].Value : null;
                url = null;
                hash = null;
                continue;
            }
            if (target is not null && !target.Equals(host, StringComparison.OrdinalIgnoreCase)) continue;
            var urlMatch = UrlPattern.Match(line);
            if (urlMatch.Success) url = urlMatch.Groups["url"].Value;
            var hashMatch = HashPattern.Match(line);
            if (hashMatch.Success) hash = hashMatch.Groups["hash"].Value;
        }
        return url is not null && hash is not null && (target is null || target.Equals(host, StringComparison.OrdinalIgnoreCase)) ? new CargoComponent(url, hash) : null;
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = "tar", WorkingDirectory = destination, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-xf");
        start.ArgumentList.Add(archivePath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start tar to extract the Cargo archive.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Cargo archive extraction failed: {error.Trim()}");
    }

    private static string? FindCargoOnPath()
    {
        var executable = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), executable))
            .FirstOrDefault(File.Exists);
    }

    private static string GetRustupHostTriple()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}.") };
        if (OperatingSystem.IsWindows()) return $"{architecture}-pc-windows-msvc";
        if (OperatingSystem.IsLinux()) return $"{architecture}-unknown-linux-gnu";
        if (OperatingSystem.IsMacOS()) return $"{architecture}-apple-darwin";
        throw new PlatformNotSupportedException("Unsupported operating system.");
    }

    private static async Task RunCargoAsync(string cargo, string projectDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = cargo, WorkingDirectory = projectDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("generate-lockfile");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Cargo.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var details = string.Join(Environment.NewLine, new[] { await output, await error }.Where(text => text.Trim().Length > 0).Select(text => text.Trim()));
        if (process.ExitCode != 0) throw new InvalidOperationException($"Cargo generate-lockfile failed with exit code {process.ExitCode}: {details}");
    }

    private sealed record CargoComponent(string Url, string Hash);
}
