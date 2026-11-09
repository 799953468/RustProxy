using System.Diagnostics;

namespace RustProxy.Core.Tools;

/// <summary>Runs installed command-line tools asynchronously.</summary>
public sealed class ExternalCommandRunner
{
    /// <summary>Runs a command and returns its standard output.</summary>
    public static async Task<CommandResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
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
        var outputTask = ReadBoundedAsync(process.StandardOutput);
        var errorTask = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
        var result = new CommandResult(process.ExitCode, await outputTask, await errorTask);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            var take = Math.Min(read, 65536 - text.Length);
            if (take > 0) text.Append(buffer, 0, take);
        }
        return text.ToString();
    }
}

/// <summary>Captures an external command result.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
