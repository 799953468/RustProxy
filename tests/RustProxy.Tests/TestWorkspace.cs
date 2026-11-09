using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;
using RustProxy.Core.Storage;
using RustProxy.Server.Security;

namespace RustProxy.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "rustproxy-tests-" + Guid.NewGuid().ToString("N"));
    public TestWorkspace() => Directory.CreateDirectory(Root);
    public string PathFor(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
    public void Dispose() => PackageStore.DeleteTemporary(Root);

    public const string AdministratorKey = "test-only-administrator-secret-32-characters";
    public static AdministratorAccess Access { get; } = new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Administration:ApiKey"] = AdministratorKey }).Build());
    public static ClaimsPrincipal Administrator => Access.Authenticate("admin", AdministratorKey)!;
    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static MemoryStream Package(string path = "cargo/index/example", string content = "fixture", string? checksum = null, long? length = null, string? source = null)
    {
        var manifest = new PackageManifest { Name = "fixture", Source = source, Files = [new PackageFile { Path = path, Length = length ?? Encoding.UTF8.GetByteCount(content), Sha256 = checksum ?? Hash(content) }] };
        return Package(manifest, new Dictionary<string, string> { [path] = content });
    }

    public static MemoryStream Package(PackageManifest manifest, Dictionary<string, string> files)
    {
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                using var bytes = zip.CreateEntry(file.Key).Open();
                bytes.Write(Encoding.UTF8.GetBytes(file.Value));
            }
            using var metadata = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(metadata, manifest, RustProxyJsonContext.Default.PackageManifest);
        }
        output.Position = 0;
        return output;
    }
}

internal sealed class TestEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "RustProxy.Server";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = root;
    public string WebRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
