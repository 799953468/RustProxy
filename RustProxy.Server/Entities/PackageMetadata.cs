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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the source URL used to build the package.</summary>
    [SugarColumn(IsNullable = true)]
    public string? Source { get; set; }
}

/// <summary>Stores a Cargo dependency belonging to a package.</summary>
[SugarTable("PackageCargoDependencies")]
[SugarIndex("IX_CargoDependencies_PackageId", nameof(PackageId), OrderByType.Asc)]
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
    [SugarColumn(IsNullable = true)]
    public string? Checksum { get; set; }
}

/// <summary>Stores a Rust toolchain belonging to a package.</summary>
[SugarTable("PackageToolchains")]
[SugarIndex("IX_Toolchains_PackageId", nameof(PackageId), OrderByType.Asc)]
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
[SugarIndex("IX_PackageFiles_PackagePath", nameof(PackageId), OrderByType.Asc, nameof(Path), OrderByType.Asc)]
[SugarIndex("IX_PackageFiles_PathPackage", nameof(Path), OrderByType.Asc, nameof(PackageId), OrderByType.Desc)]
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
