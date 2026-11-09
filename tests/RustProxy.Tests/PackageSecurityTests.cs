using System.Buffers.Binary;
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
    [DataRow("cargo/COM¹.txt")]
    [DataRow("cargo/LPT²")]
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
    public void UnicodeAliasesCannotDescribeDifferentFilesOrDirectories()
    {
        var hash = TestWorkspace.Hash("");
        var manifest = new PackageManifest { Name = "fixture", Files = [
            new PackageFile { Path = "cargo/é", Sha256 = hash },
            new PackageFile { Path = "cargo/e\u0301", Sha256 = hash }
        ] };
        var action = () => PackageStore.ValidateManifest(manifest, new PackageLimits());
        action.Should().Throw<InvalidDataException>();
        manifest.Files[1].Path += "/child";
        action.Should().Throw<InvalidDataException>();
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
    public async Task PackEnforcesAllQuotasAndPreservesExistingOutput()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("source"));
        await File.WriteAllTextAsync(workspace.PathFor("source/first.crate"), "first");
        await File.WriteAllTextAsync(workspace.PathFor("source/second.crate"), "second");
        var output = workspace.PathFor("output.rpk");
        await File.WriteAllTextAsync(output, "previous package");
        foreach (var limits in new[]
        {
            new PackageLimits { MaxFiles = 1 },
            new PackageLimits { MaxFileBytes = 4 },
            new PackageLimits { MaxExpandedBytes = 10 },
            new PackageLimits { MaxManifestBytes = 32 },
            new PackageLimits { MaxDirectoryBytes = 1 },
            new PackageLimits { MaxPackageBytes = 64 }
        })
        {
            var action = () => PackageStore.CreateFromDirectoryAsync(workspace.PathFor("source"), output, "fixture", null, limits: limits);
            await action.Should().ThrowAsync<InvalidDataException>();
            (await File.ReadAllTextAsync(output)).Should().Be("previous package");
            Directory.GetFiles(workspace.Root, "*.tmp").Should().BeEmpty();
        }
    }

    [TestMethod]
    public async Task ForgedZipEntryCountIsRejectedByDirectoryPreflight()
    {
        using var workspace = new TestWorkspace();
        using var archive = TestWorkspace.Package();
        var bytes = archive.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 1);
        var package = workspace.PathFor("forged.rpk");
        await File.WriteAllBytesAsync(package, bytes);
        var action = () => PackageStore.ReadManifestAsync(package);
        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*entry count*");
    }

    [TestMethod]
    public async Task AmbiguousTrailerInsideACommentCannotBypassDirectoryPreflight()
    {
        using var workspace = new TestWorkspace();
        using var archive = TestWorkspace.Package();
        var original = archive.ToArray();
        // The outer valid trailer points at the real directory. A later fake trailer ends before EOF.
        var bytes = new byte[original.Length + 24];
        original.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(original.Length - 2), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(original.Length), 0x06054b50);
        var package = workspace.PathFor("ambiguous.rpk");
        await File.WriteAllBytesAsync(package, bytes);
        var action = () => PackageStore.ReadManifestAsync(package);
        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*Ambiguous ZIP trailer*");
    }

    [TestMethod]
    public async Task ZipDirectoryHasAnIndependentByteQuota()
    {
        using var workspace = new TestWorkspace();
        using var archive = TestWorkspace.Package();
        var package = workspace.PathFor("directory.rpk");
        await File.WriteAllBytesAsync(package, archive.ToArray());
        var action = () => PackageStore.ReadManifestAsync(package, limits: new PackageLimits { MaxDirectoryBytes = 1 });
        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*directory*quota*");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Zip64DirectoriesSupportValidImportsAndRejectOversizedCounts(bool oversized)
    {
        using var workspace = new TestWorkspace();
        using var archive = TestWorkspace.Package();
        var original = archive.ToArray();
        var end = original.Length - 22;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(end + 10));
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 12));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 16));
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(original, 0, end);
            writer.Write(0x06064b50u); writer.Write(44UL);
            writer.Write((ushort)45); writer.Write((ushort)45);
            writer.Write(0u); writer.Write(0u);
            writer.Write(oversized ? ulong.MaxValue : count); writer.Write(oversized ? ulong.MaxValue : count);
            writer.Write((ulong)directorySize); writer.Write((ulong)directoryOffset);
            writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)end); writer.Write(1u);
            BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(end + 8), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(end + 10), ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(end + 12), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(end + 16), uint.MaxValue);
            writer.Write(original, end, 22);
        }
        var package = workspace.PathFor("zip64.rpk");
        await File.WriteAllBytesAsync(package, bytes.ToArray());
        var action = () => PackageStore.ImportAsync(new ImportOptions { PackagePath = package, DestinationDirectory = workspace.PathFor("unpacked") });
        if (oversized)
        {
            await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*directory*quota*");
            Directory.Exists(workspace.PathFor("unpacked")).Should().BeFalse();
        }
        else
        {
            (await action()).Files.Should().ContainSingle();
            (await File.ReadAllTextAsync(workspace.PathFor("unpacked/cargo/index/example"))).Should().Be("fixture");
        }
    }

    [TestMethod]
    public async Task PackHonorsCancellationBeforeTouchingOutput()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("source"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = () => PackageStore.CreateFromDirectoryAsync(workspace.PathFor("source"), workspace.PathFor("output.rpk"), "fixture", null, cancellationToken: cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(workspace.PathFor("output.rpk")).Should().BeFalse();
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
