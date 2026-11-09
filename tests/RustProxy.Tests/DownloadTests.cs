using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Core.Download;
using RustProxy.Core.Sync;
using RustProxy.Core.Tools;

namespace RustProxy.Tests;

[TestClass]
public sealed class DownloadTests
{
    [DataTestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public async Task LockfileNewlinesAndShortCratesProduceCorrectPrefixes(string newline)
    {
        using var workspace = new TestWorkspace();
        var names = new[] { "a", "ab", "abc", "abcd" };
        await File.WriteAllTextAsync(workspace.PathFor("Cargo.lock"), string.Join("", names.Select(x => LockPackage(x, "1.0.0"))).Replace("\n", newline));
        var handler = new FixtureHandler(request => request.RequestUri!.AbsolutePath == "/config.json"
            ? new StringContent("{\"dl\":\"https://fixture.invalid/{prefix}/{crate}/{version}\"}") : new StringContent("crate"));
        using var client = new HttpClient(handler);
        var logger = new SilentLogger();
        var result = await new CargoDependencyFetcher(client, new RetryExecutor(logger), logger).FetchAsync(workspace.Root, workspace.PathFor("output"), "https://fixture.invalid/", maxAttempts: 1);
        result.Should().HaveCount(4);
        var paths = handler.Paths.ToArray();
        paths.Should().Contain("/1/a/1.0.0").And.Contain("/2/ab/1.0.0").And.Contain("/3/a/abc/1.0.0").And.Contain("/ab/cd/abcd/1.0.0");
    }

    [TestMethod]
    public async Task CrateChecksumsAreMandatoryAndIndexesAreDeduplicated()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("Cargo.lock"), LockPackage("demo", "1.0.0") + LockPackage("demo", "2.0.0"));
        var handler = new FixtureHandler(request => new StringContent(request.RequestUri!.AbsolutePath == "/config.json" ? "{\"dl\":\"https://fixture.invalid/crates\"}" : "crate"));
        using var client = new HttpClient(handler);
        var logger = new SilentLogger();
        await new CargoDependencyFetcher(client, new RetryExecutor(logger), logger).FetchAsync(workspace.Root, workspace.PathFor("output"), "https://fixture.invalid/", maxAttempts: 1);
        handler.Paths.Count(x => x == "/de/mo/demo").Should().Be(1);
        handler.Paths.Should().Contain("/crates/demo/1.0.0/download");
        await File.WriteAllTextAsync(workspace.PathFor("Cargo.lock"), LockPackage("demo", "1.0.0").Replace("checksum", "ignored"));
        var action = () => new CargoDependencyFetcher(client, new RetryExecutor(logger), logger).FetchAsync(workspace.Root, workspace.PathFor("invalid"), "https://fixture.invalid/", maxAttempts: 1);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task IndexServerErrorsAreRetriedInsideOperation()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("Cargo.lock"), LockPackage("demo", "1.0.0"));
        var attempts = 0;
        var handler = new FixtureHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/config.json" && Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable);
            return new StringContent(request.RequestUri.AbsolutePath == "/config.json" ? "{\"dl\":\"https://fixture.invalid/crates\"}" : "crate");
        });
        using var client = new HttpClient(handler);
        var logger = new SilentLogger();
        await new CargoDependencyFetcher(client, new RetryExecutor(logger), logger).FetchAsync(workspace.Root, workspace.PathFor("output"), "https://fixture.invalid/", maxAttempts: 2, retryDelay: TimeSpan.Zero);
        attempts.Should().Be(2);
    }

    [TestMethod]
    public async Task FixedAndDatedToolchainsUseIndependentUpdaterAndVerifiedInstallers()
    {
        using var workspace = new TestWorkspace();
        var checksum = TestWorkspace.Hash("archive");
        var manifest = $"[pkg.rust.target.x86_64-unknown-linux-gnu]\navailable=true\nxz_url='https://fixture.invalid/dist/2026-01-01/rust.tar.xz'\nxz_hash='{checksum}'\n";
        var handler = new FixtureHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("rustup-init.sha256")) return new StringContent(TestWorkspace.Hash("installer") + " rustup-init");
            if (path.EndsWith("rustup-init")) return new StringContent("installer");
            if (path.EndsWith(".sha256")) return new StringContent(TestWorkspace.Hash(manifest));
            if (path.Contains("channel-rust")) return new StringContent(manifest);
            return new StringContent(path.EndsWith(".tar.xz") ? "archive" : "version='1.0.0'");
        });
        using var client = new HttpClient(handler);
        var logger = new SilentLogger();
        var fetcher = new RustupToolchainFetcher(client, new RetryExecutor(logger), logger);
        foreach (var channel in new[] { "1.88.0", "nightly-2026-01-01" })
            await fetcher.FetchToDirectoryAsync(channel, "x86_64-unknown-linux-gnu", workspace.PathFor("output"), new Uri("https://fixture.invalid/dist/"), maxAttempts: 1);
        handler.Paths.Should().Contain("/rustup/release-stable.toml").And.NotContain("/rustup/release-1.88.0.toml");
        handler.Paths.Should().Contain("/dist/2026-01-01/channel-rust-nightly.toml");
        handler.Paths.Count(x => x.EndsWith("rustup-init")).Should().Be(1);
        handler.Paths.Count(x => x.EndsWith("rust.tar.xz")).Should().Be(1);
    }

    [TestMethod]
    public async Task FailedDownloadPreservesExistingDestination()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("archive"), "good");
        using var client = new HttpClient(new FixtureHandler(_ => new StringContent("bad")));
        var action = () => HttpDownload.DownloadAsync(client, new Uri("https://fixture.invalid/archive"), workspace.PathFor("archive"), TestWorkspace.Hash("good"));
        await action.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(workspace.PathFor("archive"))).Should().Be("good");
    }

    [TestMethod]
    public async Task ConflictingLockfileChecksumsAreRejectedBeforeNetworkAccess()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("Cargo.lock"), LockPackage("demo", "1.0.0") +
            LockPackage("demo", "1.0.0").Replace(TestWorkspace.Hash("crate"), TestWorkspace.Hash("different")));
        var handler = new FixtureHandler(_ => new StringContent("unexpected"));
        using var client = new HttpClient(handler);
        var logger = new SilentLogger();
        var action = () => new CargoDependencyFetcher(client, new RetryExecutor(logger), logger)
            .FetchAsync(workspace.Root, workspace.PathFor("output"), "https://fixture.invalid/");
        await action.Should().ThrowAsync<InvalidDataException>();
        handler.Paths.Should().BeEmpty();
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AutomaticallyRedirectedResponsesCannotDowngradeHttps(bool metadata)
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("untrusted"), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://fixture.invalid/final")
        }));
        var action = async () =>
        {
            if (metadata) await HttpDownload.ReadBytesAsync(client, new Uri("https://fixture.invalid/original"));
            else await HttpDownload.DownloadAsync(client, new Uri("https://fixture.invalid/original"), workspace.PathFor("output"), null);
        };
        await action.Should().ThrowAsync<InvalidDataException>();
        File.Exists(workspace.PathFor("output")).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("http://fixture.invalid/unsafe")]
    [DataRow("https://another.invalid/unsafe")]
    [DataRow("file:///tmp/unsafe")]
    public async Task UnsafeManualRedirectIsRejectedBeforeFollowingIt(string location)
    {
        using var workspace = new TestWorkspace();
        var requests = 0;
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri(location);
            return response;
        }));
        var action = () => HttpDownload.DownloadAsync(client, new Uri("https://fixture.invalid/original"), workspace.PathFor("output"), null, sameOrigin: true);
        await action.Should().ThrowAsync<InvalidDataException>();
        requests.Should().Be(1);
    }

    [TestMethod]
    public async Task RelativeRedirectUsesTheCurrentResponseOrigin()
    {
        using var workspace = new TestWorkspace();
        var requests = new List<string>();
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("fixture") };
            if (requests.Count == 1)
            {
                response.StatusCode = HttpStatusCode.Redirect;
                response.Headers.Location = new Uri("../final", UriKind.Relative);
            }
            return response;
        }));
        await HttpDownload.DownloadAsync(client, new Uri("https://fixture.invalid/parent/original"), workspace.PathFor("output"), TestWorkspace.Hash("fixture"), sameOrigin: true);
        requests.Should().Equal("/parent/original", "/final");
        (await File.ReadAllTextAsync(workspace.PathFor("output"))).Should().Be("fixture");
    }

    [TestMethod]
    public async Task ExplicitDownloadRejectsAbsoluteAndEncodedPaths()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FixtureHandler(_ => new StringContent("fixture")));
        foreach (var path in new[] { "https://evil.invalid/file", "%2e%2e/file", "../file", "/root" })
        {
            var action = () => new SourceDownloader(client).DownloadAsync(new RustProxy.Core.Package.SourceDownloadOptions
            { Source = new Uri("https://fixture.invalid/"), OutputPath = workspace.PathFor("output.rpk"), Name = "fixture", Paths = [path] });
            await action.Should().ThrowAsync<InvalidDataException>();
        }
    }

    private static string LockPackage(string name, string version) => $"[[package]]\nname='{name}'\nversion='{version}'\nsource='registry+https://github.com/rust-lang/crates.io-index'\nchecksum='{TestWorkspace.Hash("crate")}'\n";

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpContent> content) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(request), RequestMessage = request });
        }
    }

    private sealed class SilentLogger : IFetchLogger
    {
        public void Info(string message) { }
        public void Retry(string operation, int attempt, int maxAttempts, Exception exception, TimeSpan delay) { }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
