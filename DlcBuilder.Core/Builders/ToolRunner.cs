using System.Diagnostics;

namespace DlcBuilder.Builders;

/// Result of invoking an external tool: exit code + captured stdout/stderr.
public sealed record ToolResult
{
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public required bool TimedOut { get; init; }
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// Thin wrapper around Process.Start with our standard policy: redirect both
/// streams, capture them fully, no shell, no console window, optional timeout.
/// Multiple modules (PSF packer, BIG packager) shell out to native tools, so
/// this lives next to the other primitives instead of duplicating boilerplate.
public static class ToolRunner
{
    /// Default timeout used when callers don't pass one.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public static ToolResult Run(
        string exePath,
        IEnumerable<string> args,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(args);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {exePath}");

        // Async reads avoid deadlocks when both stdout and stderr fill their pipe buffers.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: false); } catch { /* best effort */ }
            return new ToolResult
            {
                ExitCode = -1,
                StdOut = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty,
                StdErr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty,
                TimedOut = true,
            };
        }

        stdoutTask.Wait();
        stderrTask.Wait();
        return new ToolResult
        {
            ExitCode = p.ExitCode,
            StdOut = stdoutTask.Result,
            StdErr = stderrTask.Result,
            TimedOut = false,
        };
    }
}
