namespace RustProxy.Core.Download;

/// <summary>Provides a configured HTTP client for source downloads.</summary>
public sealed class SourceHttpClient : HttpClient
{
    /// <summary>Initializes the client with a long download timeout.</summary>
    public SourceHttpClient() => Timeout = TimeSpan.FromMinutes(30);
}
