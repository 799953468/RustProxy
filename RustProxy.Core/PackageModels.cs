using System.ComponentModel.DataAnnotations;

namespace RustProxy.Core.Package;

/// <summary>Describes the contents and provenance of a RustProxy package.</summary>
public sealed class PackageManifest
{
    /// <summary>Gets or sets the package format version.</summary>
    [Required]
    public string FormatVersion { get; set; } = "1";

    /// <summary>Gets or sets the package identifier.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the source URL used to build the package.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the Cargo dependencies included in the package.</summary>
    public List<CargoDependencyInfo> CargoDependencies { get; set; } = [];

    /// <summary>Gets or sets the rustup toolchains included in the package.</summary>
    public List<RustToolchainInfo> Toolchains { get; set; } = [];

    /// <summary>Gets or sets the package files.</summary>
    public List<PackageFile> Files { get; set; } = [];
}

/// <summary>Describes a vendored Cargo dependency.</summary>
public sealed class CargoDependencyInfo
{
    /// <summary>Gets or sets the crate name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the crate version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the registry checksum.</summary>
    public string? Checksum { get; set; }
}

/// <summary>Describes a rustup toolchain and target.</summary>
public sealed class RustToolchainInfo
{
    /// <summary>Gets or sets the Rust version or channel.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the target triple.</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>Describes one file stored in a package.</summary>
public sealed class PackageFile
{
    /// <summary>Gets or sets the path relative to the package root.</summary>
    [Required]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file length in bytes.</summary>
    public long Length { get; set; }

    /// <summary>Gets or sets the SHA-256 checksum in lowercase hexadecimal.</summary>
    [Required]
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Options for downloading a Cargo or rustup source.</summary>
public sealed class SourceDownloadOptions
{
    /// <summary>Gets or sets the source base URI.</summary>
    [Required]
    public required Uri Source { get; init; }

    /// <summary>Gets or sets the output package path.</summary>
    [Required]
    public required string OutputPath { get; init; }

    /// <summary>Gets or sets the package name.</summary>
    [Required]
    public required string Name { get; init; }

    /// <summary>Gets or sets the relative paths to download.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>Options for importing packages into a source directory.</summary>
public sealed class ImportOptions
{
    /// <summary>Gets or sets the package file.</summary>
    [Required]
    public required string PackagePath { get; init; }

    /// <summary>Gets or sets the destination source directory.</summary>
    [Required]
    public required string DestinationDirectory { get; init; }
}
