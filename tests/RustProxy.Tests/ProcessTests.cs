using System.Diagnostics;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustProxy.Core.Tools;

namespace RustProxy.Tests;

[TestClass]
public sealed class ProcessTests
{
    [TestMethod]
    public async Task CancellationTerminatesAChildProcess()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("This fixture uses the POSIX shell."); return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();
        var action = () => ExternalCommandRunner.RunAsync("/bin/sh", ["-c", "sleep 30"], cancellationToken: cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
