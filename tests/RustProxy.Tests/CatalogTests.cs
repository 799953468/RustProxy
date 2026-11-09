using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Server.Entities;
using RustProxy.Server.Services;
using SqlSugar;

namespace RustProxy.Tests;

[TestClass]
public sealed class CatalogTests
{
    [TestMethod]
    public async Task AnonymousImportIsRejectedBeforeReadingUpload()
    {
        using var fixture = new CatalogFixture();
        using var archive = TestWorkspace.Package();
        var action = () => fixture.Catalog.ImportAsync(archive, "fixture.rpk", new ClaimsPrincipal());
        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        archive.Position.Should().Be(0);
    }

    [TestMethod]
    public async Task ReservedFileCannotReplaceDatabase()
    {
        using var fixture = new CatalogFixture();
        await File.WriteAllTextAsync(fixture.Workspace.PathFor("data/rustproxy.db"), "sentinel");
        using var archive = TestWorkspace.Package("rustproxy.db", "malicious");
        var action = () => fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
        await action.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(fixture.Workspace.PathFor("data/rustproxy.db"))).Should().Be("sentinel");
    }

    [TestMethod]
    public async Task PackageFileReadsRequireExactMembership()
    {
        using var fixture = new CatalogFixture();
        using var first = TestWorkspace.Package("cargo/index/a", "one");
        using var second = TestWorkspace.Package("cargo/index/b", "two");
        var imported = await fixture.Catalog.ImportAsync(first, "a.rpk", TestWorkspace.Administrator);
        await fixture.Catalog.ImportAsync(second, "b.rpk", TestWorkspace.Administrator);
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "rustproxy.db")).Should().BeNull();
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/b")).Should().BeNull();
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "../metadata/private")).Should().BeNull();
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/a")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task LaterImportsPublishImmediatelyAndPreserveOldPackageBytes()
    {
        using var fixture = new CatalogFixture();
        (await fixture.Catalog.FindSourceFileAsync("cargo", "index/a")).Should().BeNull();
        using var first = TestWorkspace.Package("cargo/index/a", "one");
        var original = await fixture.Catalog.ImportAsync(first, "a.rpk", TestWorkspace.Administrator);
        (await fixture.Catalog.FindSourceFileAsync("cargo", "index/a")).Should().NotBeNull();
        using var second = TestWorkspace.Package("cargo/index/a", "two");
        await fixture.Catalog.ImportAsync(second, "b.rpk", TestWorkspace.Administrator);
        var old = await fixture.Catalog.FindPackageFileAsync(original.Id, "cargo/index/a");
        var latest = await fixture.Catalog.FindSourceFileAsync("cargo", "index/a");
        (await File.ReadAllTextAsync(old!.Path)).Should().Be("one");
        (await File.ReadAllTextAsync(latest!.Path)).Should().Be("two");
        (await fixture.Catalog.AuditAsync(default)).Should().Be(0);
        (await fixture.Catalog.FindAsync(original.Id)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task NullOptionalMetadataAndOverviewQueriesWork()
    {
        using var fixture = new CatalogFixture();
        using var archive = TestWorkspace.Package();
        var imported = await fixture.Catalog.ImportAsync(archive, "nullable.rpk", TestWorkspace.Administrator);
        (await fixture.Catalog.FindAsync(imported.Id))!.Source.Should().BeNull();
        (await fixture.Catalog.GetOverviewAsync()).FileCount.Should().Be(1);
        (await fixture.Catalog.GetFilesAsync(imported.Id, 0, 10)).Should().ContainSingle();
        (await fixture.Catalog.CountFilesAsync(imported.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task ConcurrentImportsKeepPackageMembershipIsolated()
    {
        using var fixture = new CatalogFixture();
        using var first = TestWorkspace.Package("cargo/index/first", "one");
        using var second = TestWorkspace.Package("cargo/index/second", "two");
        var results = await Task.WhenAll(
            fixture.Catalog.ImportAsync(first, "first.rpk", TestWorkspace.Administrator),
            fixture.Catalog.ImportAsync(second, "second.rpk", TestWorkspace.Administrator));
        (await fixture.Catalog.CountFilesAsync(results[0].Id)).Should().Be(1);
        (await fixture.Catalog.CountFilesAsync(results[1].Id)).Should().Be(1);
        (await fixture.Catalog.FindPackageFileAsync(results[0].Id, "cargo/index/second")).Should().BeNull();
    }

    [TestMethod]
    public async Task LegacyMigrationRetainsHistoryAndCopiesOnlyMatchingBytes()
    {
        using var fixture = new CatalogFixture();
        var id = SnowFlakeSingle.Instance.NextId();
        await fixture.Database.Insertable(new PackageMetadata { Id = id, Name = "legacy", Source = null }).ExecuteCommandAsync();
        await fixture.Database.Insertable(new PackageFileMetadata { Id = SnowFlakeSingle.Instance.NextId(), PackageId = id, Path = "cargo/index/legacy", Length = 3, Sha256 = TestWorkspace.Hash("old") }).ExecuteCommandAsync();
        Directory.CreateDirectory(fixture.Workspace.PathFor("data/cargo/index"));
        await File.WriteAllTextAsync(fixture.Workspace.PathFor("data/cargo/index/legacy"), "old");
        await fixture.Catalog.MigrateLegacyFilesAsync(default);
        var file = await fixture.Catalog.FindPackageFileAsync(id, "cargo/index/legacy");
        (await File.ReadAllTextAsync(file!.Path)).Should().Be("old");
        File.Delete(file.Path);
        (await fixture.Catalog.AuditAsync(default)).Should().Be(1);
        (await fixture.Catalog.FindAsync(id)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task DatabaseMigrationCopiesLegacyDataAndUpdatesNullableSchema()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("data"));
        using (var legacy = new SqlSugarScope(new ConnectionConfig { ConnectionString = $"Data Source={workspace.PathFor("data/rustproxy.db")}", DbType = DbType.Sqlite, IsAutoCloseConnection = true }))
        {
            legacy.Ado.ExecuteCommand("CREATE TABLE Packages (Id INTEGER PRIMARY KEY, FormatVersion TEXT NOT NULL, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, Source TEXT NOT NULL)");
            await legacy.Insertable(new PackageMetadata { Id = 1, Name = "legacy", Source = "original" }).ExecuteCommandAsync();
        }
        var path = DatabaseStorage.Prepare(workspace.Root);
        path.Should().Be(workspace.PathFor("data/metadata/rustproxy.db"));
        File.Exists(workspace.PathFor("data/rustproxy.db")).Should().BeTrue();
        using var database = new SqlSugarScope(new ConnectionConfig { ConnectionString = $"Data Source={path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true });
        database.CodeFirst.InitTables<PackageMetadata, PackageFileMetadata, PackageCargoDependencyMetadata, PackageToolchainMetadata>();
        DatabaseStorage.UpgradeOptionalColumns(database.CurrentConnectionConfig.ConnectionString);
        using var catalog = new PackageCatalog(new TestEnvironment(workspace.Root), database, NullLogger<PackageCatalog>.Instance);
        (await catalog.FindAsync(1))!.Name.Should().Be("legacy");
        using var package = TestWorkspace.Package();
        var imported = await catalog.ImportAsync(package, "new.rpk", TestWorkspace.Administrator);
        (await catalog.FindAsync(imported.Id))!.Source.Should().BeNull();
    }

    private sealed class CatalogFixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public SqlSugarScope Database { get; }
        public PackageCatalog Catalog { get; }
        public CatalogFixture()
        {
            Directory.CreateDirectory(Workspace.PathFor("data"));
            Database = new SqlSugarScope(new ConnectionConfig { ConnectionString = $"Data Source={Workspace.PathFor("test.sqlite")}", DbType = DbType.Sqlite, IsAutoCloseConnection = true });
            Database.CodeFirst.InitTables<PackageMetadata, PackageFileMetadata, PackageCargoDependencyMetadata, PackageToolchainMetadata>();
            Catalog = new PackageCatalog(new TestEnvironment(Workspace.Root), Database, NullLogger<PackageCatalog>.Instance);
        }
        public void Dispose() { Catalog.Dispose(); Database.Dispose(); Workspace.Dispose(); }
    }
}
