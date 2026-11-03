using SqlSugar;

namespace RustProxy.Server.Entities;

/// <summary>Stores the scalar metadata of an imported package.</summary>
[SugarTable("Packages")]
public sealed class PackageMetadata
{
    /// <summary>Gets or sets the database-generated package identifier.</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    /// <summary>Gets or sets the package format version.</summary>
    [SugarColumn(IsNullable = false)]
    public string FormatVersion { get; set; } = "1";

    /// <summary>Gets or sets the package name from the imported manifest.</summary>
    [SugarColumn(IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the package creation time in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the source URL used to build the package.</summary>
    public string? Source { get; set; }
}

/// <summary>Stores a Cargo dependency belonging to a package.</summary>
[SugarTable("PackageCargoDependencies")]
public sealed class PackageCargoDependencyMetadata
{
    /// <summary>Gets or sets the database-generated row identifier.</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    /// <summary>Gets or sets the owning package identifier.</summary>
    public long PackageId { get; set; }

    /// <summary>Gets or sets the crate name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the crate version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the registry checksum.</summary>
    public string? Checksum { get; set; }
}

/// <summary>Stores a Rust toolchain belonging to a package.</summary>
[SugarTable("PackageToolchains")]
public sealed class PackageToolchainMetadata
{
    /// <summary>Gets or sets the database-generated row identifier.</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    /// <summary>Gets or sets the owning package identifier.</summary>
    public long PackageId { get; set; }

    /// <summary>Gets or sets the Rust version or channel.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the target triple.</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>Stores a file entry belonging to a package.</summary>
[SugarTable("PackageFiles")]
public sealed class PackageFileMetadata
{
    /// <summary>Gets or sets the database-generated row identifier.</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    /// <summary>Gets or sets the owning package identifier.</summary>
    public long PackageId { get; set; }

    /// <summary>Gets or sets the path relative to the source root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file length in bytes.</summary>
    public long Length { get; set; }

    /// <summary>Gets or sets the SHA-256 checksum.</summary>
    public string Sha256 { get; set; } = string.Empty;
}
