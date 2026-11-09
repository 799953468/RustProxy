using RustProxy.Core.Package;
using RustProxy.Core.Storage;

namespace RustProxy.Core.Download;

/// <summary>Downloads selected files from a Rust source into a staging directory and packages them.</summary>
public sealed class SourceDownloader(HttpClient httpClient)
{
    /// <summary>Downloads the requested paths and creates a portable package.</summary>
    public async Task DownloadAsync(SourceDownloadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        HttpDownload.Validate(options.Source);
        var staging = Path.Combine(Path.GetTempPath(), "rustproxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var path in options.Paths)
            {
                var normalized = PathSafety.Normalize(path);
                if (normalized.Contains('%') || normalized.Contains('#')) throw new InvalidDataException("Encoded or fragment resource paths are not allowed.");
                var uri = new Uri(options.Source, normalized);
                if (uri.Authority != options.Source.Authority || uri.Scheme != options.Source.Scheme) throw new InvalidDataException("Source paths must stay on the configured origin.");
                await HttpDownload.DownloadAsync(httpClient, uri, PathSafety.Resolve(staging, normalized), null, cancellationToken, sameOrigin: true);
            }

            await PackageStore.CreateFromDirectoryAsync(staging, options.OutputPath, options.Name,
                options.Source.ToString(), cancellationToken: cancellationToken);
        }
        finally
        {
            PackageStore.DeleteTemporary(staging);
        }
    }
}
