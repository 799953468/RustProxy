using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Core.Package;
using RustProxy.Core.Storage;

namespace RustProxy.Tests;

[TestClass]
public sealed class PackageSecurityTests
{
    [DataTestMethod]
    [DataRow("../escape")]
    [DataRow("/absolute")]
    [DataRow("cargo/../rustproxy.db")]
    [DataRow("cargo/a:stream")]
    [DataRow("cargo/CON.txt")]
    [DataRow("cargo/file.")]
    [DataRow("cargo//file")]
    public void InvalidPortablePathsAreRejected(string path) => ((Action)(() => PathSafety.Normalize(path))).Should().Throw<InvalidDataException>();

    [TestMethod]
    public async Task ChecksumFailurePreservesExistingFiles()
    {
        using var workspace = new TestWorkspace();
        var destination = workspace.PathFor("destination");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "original"), "good");
        await using (var output = File.Create(workspace.PathFor("broken.rpk")))
        await using (var archive = TestWorkspace.Package("original", "bad", new string('0', 64))) await archive.CopyToAsync(output);
        var action = () => PackageStore.ImportAsync(new ImportOptions { PackagePath = workspace.PathFor("broken.rpk"), DestinationDirectory = destination });
        await action.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(Path.Combine(destination, "original"))).Should().Be("good");
    }

    [TestMethod]
    public async Task IncorrectDeclaredLengthIsRejectedBeforeExtraction()
    {
        using var workspace = new TestWorkspace();
        await using (var file = File.Create(workspace.PathFor("expanded.rpk")))
        await using (var archive = TestWorkspace.Package("expanded", new string('x', 1024 * 1024), length: 1)) await archive.CopyToAsync(file);
        var action = () => PackageStore.ImportAsync(new ImportOptions { PackagePath = workspace.PathFor("expanded.rpk"), DestinationDirectory = workspace.PathFor("destination") });
        await action.Should().ThrowAsync<InvalidDataException>();
        File.Exists(workspace.PathFor("destination/expanded")).Should().BeFalse();
    }

    [TestMethod]
    public async Task ExpandedQuotaIsEnforced()
    {
        using var workspace = new TestWorkspace();
        await using (var file = File.Create(workspace.PathFor("large.rpk")))
        await using (var archive = TestWorkspace.Package(content: "too large")) await archive.CopyToAsync(file);
        var action = () => PackageStore.ImportAsync(new ImportOptions { PackagePath = workspace.PathFor("large.rpk"), DestinationDirectory = workspace.PathFor("destination"), Limits = new PackageLimits { MaxExpandedBytes = 2 } });
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task ExistingSymlinkCannotEscapeDestination()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Creating links requires platform privileges."); return; }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("destination"));
        Directory.CreateDirectory(workspace.PathFor("outside"));
        Directory.CreateSymbolicLink(workspace.PathFor("destination/linked"), workspace.PathFor("outside"));
        await using (var file = File.Create(workspace.PathFor("linked.rpk")))
        await using (var archive = TestWorkspace.Package("linked/file")) await archive.CopyToAsync(file);
        var action = () => PackageStore.ImportAsync(new ImportOptions { PackagePath = workspace.PathFor("linked.rpk"), DestinationDirectory = workspace.PathFor("destination") });
        await action.Should().ThrowAsync<InvalidDataException>();
        File.Exists(workspace.PathFor("outside/file")).Should().BeFalse();
    }

    [TestMethod]
    public void DuplicateAliasesAndNullCollectionsAreRejected()
    {
        var file = new PackageFile { Path = "cargo/a", Length = 0, Sha256 = TestWorkspace.Hash("") };
        var manifest = new PackageManifest { Name = "fixture", Files = [file, new PackageFile { Path = "cargo/A", Sha256 = file.Sha256 }] };
        ((Action)(() => PackageStore.ValidateManifest(manifest, new PackageLimits()))).Should().Throw<InvalidDataException>();
        manifest.Files = null!;
        ((Action)(() => PackageStore.ValidateManifest(manifest, new PackageLimits()))).Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public async Task PackRejectsOutputInsideInputAndReservedManifest()
    {
        using var workspace = new TestWorkspace();
        var action = () => PackageStore.CreateFromDirectoryAsync(workspace.Root, workspace.PathFor("self.rpk"), "fixture", null);
        await action.Should().ThrowAsync<ArgumentException>();
        Directory.CreateDirectory(workspace.PathFor("source"));
        await File.WriteAllTextAsync(workspace.PathFor("source/manifest.json"), "{}");
        action = () => PackageStore.CreateFromDirectoryAsync(workspace.PathFor("source"), workspace.PathFor("output.rpk"), "fixture", null);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task ValidPackAndImportRoundTrip()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("source/cargo"));
        await File.WriteAllTextAsync(workspace.PathFor("source/cargo/config.json"), "{}");
        await PackageStore.CreateFromDirectoryAsync(workspace.PathFor("source"), workspace.PathFor("valid.rpk"), "fixture", null);
        var manifest = await PackageStore.ImportAsync(new ImportOptions { PackagePath = workspace.PathFor("valid.rpk"), DestinationDirectory = workspace.PathFor("destination") });
        manifest.Files.Should().ContainSingle();
        (await File.ReadAllTextAsync(workspace.PathFor("destination/cargo/config.json"))).Should().Be("{}");
    }
}
