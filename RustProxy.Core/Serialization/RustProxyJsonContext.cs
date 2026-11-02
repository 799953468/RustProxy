using System.Text.Json.Serialization;
using RustProxy.Core.Package;
using RustProxy.Core.Sync;

namespace RustProxy.Core.Serialization;

/// <summary>Source-generated JSON metadata used by CLI and server serialization.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(PackageFile))]
[JsonSerializable(typeof(FetchConfiguration))]
[JsonSerializable(typeof(ToolchainConfiguration))]
public partial class RustProxyJsonContext : JsonSerializerContext
{
}
