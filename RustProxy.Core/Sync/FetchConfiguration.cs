using System.ComponentModel.DataAnnotations;

namespace RustProxy.Core.Sync;

/// <summary>Describes Cargo dependencies and rustup toolchains to fetch.</summary>
public sealed class FetchConfiguration
{
    /// <summary>Gets or sets the resulting package name.</summary>
    [Required]
    public string Name { get; set; } = "offline-rust";

    /// <summary>Gets or sets the output package path.</summary>
    [Required]
    public string Output { get; set; } = "offline-rust.rpk";

    /// <summary>Gets or sets the project directory containing Cargo.toml and optionally Cargo.lock.</summary>
    public string? CargoProject { get; set; }

    /// <summary>Gets or sets the sparse Cargo registry index base URL.</summary>
    public string CargoIndexUrl { get; set; } = "https://index.crates.io/";

    /// <summary>Gets or sets the upstream Cargo download URL template.</summary>
    public string? CargoDownloadUrl { get; set; }

    /// <summary>Gets or sets rustup toolchain definitions.</summary>
    public List<ToolchainConfiguration> Toolchains { get; set; } = [];

    /// <summary>Gets or sets the maximum attempts for retryable operations.</summary>
    [Range(1, 20)]
    public int RetryCount { get; set; } = 4;

    /// <summary>Gets or sets the initial retry delay in seconds.</summary>
    [Range(1, 300)]
    public int RetryDelaySeconds { get; set; } = 3;
}

/// <summary>Describes a rustup toolchain and target to fetch.</summary>
public sealed class ToolchainConfiguration
{
    /// <summary>Gets or sets the rustup channel or exact version.</summary>
    [Required]
    public string Version { get; set; } = "stable";

    /// <summary>Gets or sets the target triple.</summary>
    [Required]
    public string Target { get; set; } = string.Empty;
}
