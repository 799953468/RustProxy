using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Server.Services;

namespace RustProxy.Tests;

[TestClass]
public sealed class HttpIntegrationTests
{
    private const string TestKey = "test-only-administrator-secret-32-characters";

    [TestMethod]
    public async Task AdministrationChallengesAnonymousClientsAndAcceptsConfiguredCredentials()
    {
        using var workspace = new TestWorkspace();
        await using var factory = CreateFactory(workspace.Root);
        using var client = CreateClient(factory);
        using var anonymous = await client.GetAsync("/import");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.Should().Contain(x => x.Scheme == "Basic");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + TestKey)));
        using var authenticated = await client.GetAsync("/import");
        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task WrongCredentialsAreRejectedAndHealthRemainsPublic()
    {
        using var workspace = new TestWorkspace();
        await using var factory = CreateFactory(workspace.Root);
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong")));
        (await client.GetAsync("/import")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.Services.GetRequiredService<PackageCatalog>().AuditAsync(default);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task FileResponsesSupportRevalidationRangesAndPackageIsolation()
    {
        using var workspace = new TestWorkspace();
        await using var factory = CreateFactory(workspace.Root);
        using var client = CreateClient(factory);
        var catalog = factory.Services.GetRequiredService<PackageCatalog>();
        using var package = TestWorkspace.Package("cargo/index/a", "0123456789");
        var imported = await catalog.ImportAsync(package, "fixture.rpk", TestWorkspace.Administrator);
        using var initial = await client.GetAsync("/cargo/index/a");
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        initial.Headers.CacheControl!.NoCache.Should().BeTrue();
        var etag = initial.Headers.ETag!;
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/cargo/index/a");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"other\", W/" + etag);
        using var unchanged = await client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        using var range = new HttpRequestMessage(HttpMethod.Get, $"/packages/{imported.Id}/cargo/index/a");
        range.Headers.Range = new RangeHeaderValue(2, 4);
        range.Headers.IfRange = new RangeConditionHeaderValue(etag);
        using var partial = await client.SendAsync(range);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsStringAsync()).Should().Be("234");
        partial.Headers.CacheControl!.Extensions.Should().Contain(x => x.Name == "immutable");
        (await client.GetAsync($"/packages/{imported.Id}/rustproxy.db")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var manifest = await client.GetAsync($"/packages/{imported.Id}/manifest.json");
        manifest.StatusCode.Should().Be(HttpStatusCode.OK);
        using var parsed = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        parsed.RootElement.GetProperty("files").GetArrayLength().Should().Be(1);
    }

    [TestMethod]
    public async Task SparseIndexConfigurationUsesAnAbsoluteDownloadUrl()
    {
        using var workspace = new TestWorkspace();
        await using var factory = CreateFactory(workspace.Root);
        using var client = CreateClient(factory);
        using var package = TestWorkspace.Package("cargo/index/config.json", "{\"dl\":\"/cargo/crates/{crate}/{crate}-{version}.crate\"}");
        var imported = await factory.Services.GetRequiredService<PackageCatalog>().ImportAsync(package, "fixture.rpk", TestWorkspace.Administrator);
        var source = await client.GetStringAsync("/cargo/index/config.json");
        JsonDocument.Parse(source).RootElement.GetProperty("dl").GetString().Should().StartWith("https://localhost/cargo/crates/");
        var immutable = await client.GetStringAsync($"/packages/{imported.Id}/cargo/index/config.json");
        JsonDocument.Parse(immutable).RootElement.GetProperty("dl").GetString().Should().StartWith($"https://localhost/packages/{imported.Id}/cargo/crates/");
    }

    private static WebApplicationFactory<global::Program> CreateFactory(string root) => new WebApplicationFactory<global::Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(root);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Administration:ApiKey"] = TestKey }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}
