namespace RustProxy.Core.Package;

/// <summary>Resource quotas enforced before and during package extraction.</summary>
public sealed class PackageLimits
{
    /// <summary>Maximum uploaded/compressed package bytes.</summary>
    public long MaxPackageBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>Maximum bytes in one extracted file.</summary>
    public long MaxFileBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>Maximum total extracted file bytes.</summary>
    public long MaxExpandedBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    /// <summary>Maximum JSON manifest bytes.</summary>
    public long MaxManifestBytes { get; init; } = 16L * 1024 * 1024;
    /// <summary>Maximum file entries.</summary>
    public int MaxFiles { get; init; } = 100_000;

    /// <summary>Rejects invalid quota configuration.</summary>
    public void Validate()
    {
        if (MaxPackageBytes <= 0 || MaxFileBytes <= 0 || MaxExpandedBytes <= 0 || MaxManifestBytes <= 0 || MaxFiles <= 0)
            throw new ArgumentException("All package limits must be positive.");
    }
}
