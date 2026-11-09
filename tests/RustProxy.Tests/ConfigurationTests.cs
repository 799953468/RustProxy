using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Core.Sync;

namespace RustProxy.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public async Task CargoTomlUsesSpecializedLoader()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("Cargo.toml"), "[package]\nname='demo'\nversion='1.0.0'");
        var config = await ConfigurationConverter.ConvertAsync(workspace.PathFor("Cargo.toml"));
        config.CargoProject.Should().Be(workspace.Root);
    }

    [TestMethod]
    public async Task MultilineTomlParsesAndResolvesRelativePaths()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("fetch.toml"), """
            name = 'fixture'
            output = 'artifacts/output.rpk'
            cargoProject = './project'
            toolchains = [
              '1.88.0/x86_64-unknown-linux-gnu', # inline comment
              'nightly-2026-01-01/aarch64-apple-darwin',
            ]
            rustupInitTargets = ['aarch64-apple-darwin']
            """);
        var config = await ConfigurationConverter.ConvertAsync(workspace.PathFor("fetch.toml"));
        config.Toolchains.Should().HaveCount(2);
        config.Output.Should().Be(workspace.PathFor("artifacts/output.rpk"));
        config.CargoProject.Should().Be(workspace.PathFor("project"));
    }

    [TestMethod]
    public async Task TomlTableArraysAndInvalidRangesAreHandled()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("fetch.toml"), "[[toolchains]]\nversion = 'stable'\ntarget = 'x86_64-unknown-linux-gnu'\n");
        (await ConfigurationConverter.ConvertAsync(workspace.PathFor("fetch.toml"))).Toolchains.Should().ContainSingle();
        await File.WriteAllTextAsync(workspace.PathFor("fetch.json"), "{\"cargoProject\":\".\",\"retryCount\":0}");
        var action = () => ConfigurationConverter.ConvertAsync(workspace.PathFor("fetch.json"));
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [DataTestMethod]
    [DataRow("stable", "channel-rust-stable.toml")]
    [DataRow("1.88.0", "channel-rust-1.88.0.toml")]
    [DataRow("nightly-2026-01-01", "2026-01-01/channel-rust-nightly.toml")]
    public void ToolchainPathsMatchProtocol(string input, string expected) => RustupToolchainFetcher.GetManifestPath(input).Should().Be(expected);

    [DataTestMethod]
    [DataRow("../stable")]
    [DataRow("nightly-2026-02-30")]
    [DataRow("stable/path")]
    public void UnsafeToolchainsAreRejected(string input) => ((Action)(() => RustupToolchainFetcher.GetManifestPath(input))).Should().Throw<InvalidDataException>();

    [TestMethod]
    public async Task CancellationIsNotSwallowedByLoader()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.PathFor("fetch.json"), "{}");
        var action = () => ConfigurationConverter.ConvertAsync(workspace.PathFor("fetch.json"), new CancellationToken(true));
        await action.Should().ThrowAsync<OperationCanceledException>();
    }
}
