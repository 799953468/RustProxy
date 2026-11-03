using System.Net.Http.Headers;

namespace RustProxy.Core.Download;

/// <summary>Provides a configured HTTP client for source downloads.</summary>
public sealed class SourceHttpClient : HttpClient
{
    /// <summary>Initializes the client with a long download timeout and an identifying user agent.</summary>
    public SourceHttpClient()
    {
        Timeout = TimeSpan.FromMinutes(30);
        DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RustProxy", "1.0"));
        DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }
}
