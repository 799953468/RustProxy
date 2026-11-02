using RustProxy.Core.Package;
using RustProxy.Core.Storage;

namespace RustProxy.Core.Download;

/// <summary>Downloads selected files from a Rust source into a staging directory and packages them.</summary>
public sealed class SourceDownloader(HttpClient httpClient, PackageStore packageStore)
{
    /// <summary>Downloads the requested paths and creates a portable package.</summary>
    public async Task DownloadAsync(SourceDownloadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var staging = Path.Combine(Path.GetTempPath(), "rustproxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var path in options.Paths)
            {
                var normalized = path.Replace('\\', '/').TrimStart('/');
                if (normalized.Contains("..", StringComparison.Ordinal)) throw new ArgumentException($"Unsafe source path: {path}", nameof(options));
                using var response = await httpClient.GetAsync(new Uri(options.Source, normalized), cancellationToken);
                response.EnsureSuccessStatusCode();
                var target = Path.Combine(staging, normalized.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                await source.CopyToAsync(output, cancellationToken);
            }
            await packageStore.CreateFromDirectoryAsync(staging, options.OutputPath, options.Name, options.Source.ToString(), cancellationToken);
        }
        finally { try { Directory.Delete(staging, recursive: true); } catch { } }
    }
}
