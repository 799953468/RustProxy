using System.Text;
using RustProxy.Core.Storage;

namespace RustProxy.Core.Download;

/// <summary>Downloads bounded HTTP resources using streaming completion and transfer deadlines.</summary>
public static class HttpDownload
{
    /// <summary>Rejects non-HTTP schemes and embedded credentials.</summary>
    public static Uri Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Downloads require an HTTP(S) URI without credentials.");
        return uri;
    }

    /// <summary>Downloads into a private temporary file and publishes only verified bytes.</summary>
    public static async Task DownloadAsync(HttpClient client, Uri uri, string path, string? expectedHash, CancellationToken cancellationToken = default,
        long maximumBytes = 4L * 1024 * 1024 * 1024, bool sameOrigin = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        Validate(uri);
        using var response = await SendAsync(client, uri, sameOrigin, token);
        var final = Validate(response.RequestMessage?.RequestUri ?? uri);
        if (sameOrigin && (final.Scheme != uri.Scheme || final.Authority != uri.Authority)) throw new InvalidDataException("Cross-origin download redirects are not allowed.");
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("HTTP resource exceeds download quota.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        PathSafety.RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            (long Length, string Sha256) result;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                result = await StreamTransfer.CopyAsync(input, output, maximumBytes, token);
            if (expectedHash is not null && !result.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Downloaded resource SHA-256 mismatch.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { PackageStore.DeleteTemporary(temporary); }
    }

    /// <summary>Reads bounded metadata bytes with a short end-to-end timeout.</summary>
    public static async Task<byte[]> ReadBytesAsync(HttpClient client, Uri uri, CancellationToken cancellationToken = default, long maximumBytes = 16 * 1024 * 1024)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await SendAsync(client, uri, false, deadline.Token);
        Validate(response.RequestMessage?.RequestUri ?? uri);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Metadata exceeds download quota.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        await StreamTransfer.CopyAsync(input, output, maximumBytes, deadline.Token);
        return output.ToArray();
    }

    /// <summary>Reads UTF-8 metadata without unbounded buffering.</summary>
    public static async Task<string> ReadTextAsync(HttpClient client, Uri uri, CancellationToken cancellationToken = default, long maximumBytes = 16 * 1024 * 1024) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(client, uri, cancellationToken, maximumBytes));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, bool sameOrigin, CancellationToken token)
    {
        var current = Validate(uri);
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirect == 5) throw new InvalidDataException("Invalid or excessive HTTP redirects.");
                var next = Validate(new Uri(current, location));
                if ((sameOrigin && (next.Scheme != uri.Scheme || next.Authority != uri.Authority)) || (current.Scheme == "https" && next.Scheme != "https"))
                    throw new InvalidDataException("Unsafe HTTP redirect.");
                current = next;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch (HttpRequestException exception)
            {
                var retry = response.Headers.RetryAfter;
                var wait = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                if (wait > TimeSpan.Zero) exception.Data["RetryAfter"] = wait.Value;
                response.Dispose();
                throw;
            }
        }
        throw new InvalidDataException("Too many HTTP redirects.");
    }
}
