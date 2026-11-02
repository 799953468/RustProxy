using System.Diagnostics;

namespace RustProxy.Core.Tools;

/// <summary>Runs installed command-line tools asynchronously.</summary>
public sealed class ExternalCommandRunner
{
    /// <summary>Runs a command and returns its standard output.</summary>
    public async Task<CommandResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var result = new CommandResult(process.ExitCode, await outputTask, await errorTask);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        return result;
    }
}

/// <summary>Captures an external command result.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
