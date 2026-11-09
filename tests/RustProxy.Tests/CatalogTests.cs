using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Core.Package;
using RustProxy.Server.Entities;
using RustProxy.Server.Services;
using RustProxy.Server.Security;
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
    public async Task MultiPageAuditAndRepairKeepAllPackageMembers()
    {
        using var fixture = new CatalogFixture();
        var contents = Enumerable.Range(0, 510).ToDictionary(x => $"cargo/index/file-{x:D4}", x => $"content-{x:D4}");
        var manifest = new PackageManifest { Name = "multi-page", Files = contents.Select(x => new PackageFile
        { Path = x.Key, Length = x.Value.Length, Sha256 = TestWorkspace.Hash(x.Value) }).ToList() };
        using var archive = TestWorkspace.Package(manifest, contents);
        var imported = await fixture.Catalog.ImportAsync(archive, "many.rpk", TestWorkspace.Administrator);
        (await fixture.Catalog.CountFilesAsync(imported.Id)).Should().Be(510);
        (await fixture.Catalog.GetFilesAsync(imported.Id, 500, 20)).Should().HaveCount(10);
        (await fixture.Catalog.AuditAsync(default)).Should().Be(0);
        foreach (var index in new[] { 0, 250, 509 })
        {
            var file = await fixture.Catalog.FindPackageFileAsync(imported.Id, $"cargo/index/file-{index:D4}");
            File.Delete(file!.Path);
        }
        (await fixture.Catalog.AuditAsync(default)).Should().Be(3);
        archive.Position = 0;
        await fixture.Catalog.ImportAsync(archive, "repair.rpk", TestWorkspace.Administrator);
        fixture.Catalog.AuditFailures.Should().Be(0);
        (await fixture.Catalog.AuditAsync(default)).Should().Be(0);
        (await fixture.Catalog.FindAsync(imported.Id))!.Files.Should().HaveCount(510);
    }

    [TestMethod]
    public async Task SameLengthCorruptionIsNotServedAndReimportRepairsExistingPackages()
    {
        using var fixture = new CatalogFixture();
        using var archive = TestWorkspace.Package(content: "original");
        var original = await fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
        (await fixture.Catalog.AuditAsync(default)).Should().Be(0);
        var stored = await fixture.Catalog.FindPackageFileAsync(original.Id, "cargo/index/example");
        await File.WriteAllTextAsync(stored!.Path, "tampered");
        File.SetLastWriteTimeUtc(stored.Path, DateTime.UtcNow.AddMinutes(1));
        (await fixture.Catalog.FindPackageFileAsync(original.Id, "cargo/index/example")).Should().BeNull();
        fixture.Catalog.AuditFailures.Should().Be(1);
        archive.Position = 0;
        await fixture.Catalog.ImportAsync(archive, "repair.rpk", TestWorkspace.Administrator);
        fixture.Catalog.AuditFailures.Should().Be(0);
        stored = await fixture.Catalog.FindPackageFileAsync(original.Id, "cargo/index/example");
        (await File.ReadAllTextAsync(stored!.Path)).Should().Be("original");
        (await fixture.Catalog.AuditAsync(default)).Should().Be(0);
    }

    [TestMethod]
    public async Task ForcedAuditDetectsCorruptionEvenWhenModificationTimeIsRestored()
    {
        using var fixture = new CatalogFixture();
        using var archive = TestWorkspace.Package(content: "original");
        var imported = await fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
        var stored = await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/example");
        var modified = File.GetLastWriteTimeUtc(stored!.Path);
        await File.WriteAllTextAsync(stored.Path, "tampered");
        File.SetLastWriteTimeUtc(stored.Path, modified);
        (await fixture.Catalog.AuditAsync(default)).Should().Be(1);
        (await fixture.Catalog.FindSourceFileAsync("cargo", "index/example")).Should().BeNull();
    }

    [TestMethod]
    public async Task MissingObjectCanBeRestoredWithoutRestartingTheService()
    {
        using var fixture = new CatalogFixture();
        using var archive = TestWorkspace.Package();
        var imported = await fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
        var stored = await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/example");
        File.Delete(stored!.Path);
        (await fixture.Catalog.AuditAsync(default)).Should().Be(1);
        archive.Position = 0;
        await fixture.Catalog.ImportAsync(archive, "repair.rpk", TestWorkspace.Administrator);
        fixture.Catalog.AuditFailures.Should().Be(0);
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/example")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task FailedDatabasePublicationRemovesOnlyNewObjects()
    {
        using var fixture = new CatalogFixture();
        using var original = TestWorkspace.Package(content: "retained");
        var imported = await fixture.Catalog.ImportAsync(original, "original.rpk", TestWorkspace.Administrator);
        fixture.Database.Ado.ExecuteCommand("CREATE TRIGGER reject_fixture BEFORE INSERT ON Packages BEGIN SELECT RAISE(ABORT, 'fixture rejection'); END");
        using var rejected = TestWorkspace.Package(content: "unreferenced");
        var action = () => fixture.Catalog.ImportAsync(rejected, "rejected.rpk", TestWorkspace.Administrator);
        await action.Should().ThrowAsync<Exception>();
        var objects = Directory.GetFiles(fixture.Workspace.PathFor("data/objects"), "*", SearchOption.AllDirectories);
        objects.Should().ContainSingle().Which.Should().EndWith(TestWorkspace.Hash("retained"));
        Directory.GetDirectories(fixture.Workspace.PathFor("data/staging")).Should().BeEmpty();
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/example")).Should().NotBeNull();
        (await fixture.Catalog.GetOverviewAsync()).FileCount.Should().Be(1);
    }

    [TestMethod]
    public async Task MaintenanceRemovesOnlyAbandonedUploadsAndOldUnreferencedObjects()
    {
        using var fixture = new CatalogFixture();
        using var archive = TestWorkspace.Package(content: "retained");
        var imported = await fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
        var stored = await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/example");
        var old = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(stored!.Path, old);
        // Historical metadata may contain uppercase digests. Cleanup must still recognize their membership.
        fixture.Database.Ado.ExecuteCommand("UPDATE PackageFiles SET Sha256 = upper(Sha256)");
        var orphanHash = TestWorkspace.Hash("abandoned");
        var orphan = fixture.Workspace.PathFor($"data/objects/{orphanHash[..2]}/{orphanHash}");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllTextAsync(orphan, "abandoned");
        File.SetLastWriteTimeUtc(orphan, old);
        var freshHash = TestWorkspace.Hash("fresh");
        var fresh = fixture.Workspace.PathFor($"data/objects/{freshHash[..2]}/{freshHash}");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        await File.WriteAllTextAsync(fresh, "fresh");
        var staleUpload = fixture.Workspace.PathFor("data/staging/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staleUpload);
        await File.WriteAllTextAsync(Path.Combine(staleUpload, "upload.rpk"), "partial");
        Directory.SetLastWriteTimeUtc(staleUpload, old);
        var freshUpload = fixture.Workspace.PathFor("data/staging/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshUpload);
        var unrelated = fixture.Workspace.PathFor("data/staging/operator-files");
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(unrelated, old);

        await fixture.Catalog.CleanupAsync(default);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(stored.Path).Should().BeTrue();
        File.Exists(fresh).Should().BeTrue();
        Directory.Exists(staleUpload).Should().BeFalse();
        Directory.Exists(freshUpload).Should().BeTrue();
        Directory.Exists(unrelated).Should().BeTrue();
        (await fixture.Catalog.FindPackageFileAsync(imported.Id, "cargo/index/example")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task RejectedStagingPathDoesNotConsumeUploadSlots()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Creating links requires platform privileges."); return; }
        using var fixture = new CatalogFixture();
        var staging = fixture.Workspace.PathFor("data/staging");
        var outside = fixture.Workspace.PathFor("outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(staging, outside);
        using var archive = TestWorkspace.Package();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var action = () => fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
            await action.Should().ThrowAsync<InvalidDataException>();
        }
        Directory.Delete(staging);
        await fixture.Catalog.ImportAsync(archive, "fixture.rpk", TestWorkspace.Administrator);
        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
    }

    [TestMethod]
    public async Task CredentialsRevokedDuringUploadCannotPublishObjectsOrMetadata()
    {
        var configuration = new ConfigurationManager { ["Administration:ApiKey"] = TestWorkspace.AdministratorKey };
        var access = new AdministratorAccess(configuration);
        using var fixture = new CatalogFixture(access);
        var actor = access.Authenticate("admin", TestWorkspace.AdministratorKey)!;
        using var package = TestWorkspace.Package();
        using var upload = new CallbackReadStream(package.ToArray(), () => configuration["Administration:ApiKey"] = null);
        var action = () => fixture.Catalog.ImportAsync(upload, "revoked.rpk", actor);
        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        (await fixture.Catalog.GetOverviewAsync()).FileCount.Should().Be(0);
        Directory.Exists(fixture.Workspace.PathFor("data/objects")).Should().BeFalse();
        Directory.GetDirectories(fixture.Workspace.PathFor("data/staging")).Should().BeEmpty();
    }

    [TestMethod]
    public void StorageDirectoryHasOneOwnerAndCanBeReopenedAfterShutdown()
    {
        using var workspace = new TestWorkspace();
        using (var lease = new PackageStorageLease(workspace.Root))
        {
            var acquire = () => new PackageStorageLease(workspace.Root);
            acquire.Should().Throw<IOException>();
        }
        using var reopened = new PackageStorageLease(workspace.Root);
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
        using var catalog = new PackageCatalog(new TestEnvironment(workspace.Root), database, NullLogger<PackageCatalog>.Instance, TestWorkspace.Access);
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
        public CatalogFixture(AdministratorAccess? access = null)
        {
            Directory.CreateDirectory(Workspace.PathFor("data"));
            Database = new SqlSugarScope(new ConnectionConfig { ConnectionString = $"Data Source={Workspace.PathFor("test.sqlite")}", DbType = DbType.Sqlite, IsAutoCloseConnection = true });
            Database.CodeFirst.InitTables<PackageMetadata, PackageFileMetadata, PackageCargoDependencyMetadata, PackageToolchainMetadata>();
            Catalog = new PackageCatalog(new TestEnvironment(Workspace.Root), Database, NullLogger<PackageCatalog>.Instance, access ?? TestWorkspace.Access);
        }
        public void Dispose() { Catalog.Dispose(); Database.Dispose(); Workspace.Dispose(); }
    }

    private sealed class CallbackReadStream(byte[] bytes, Action callback) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            callback();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
