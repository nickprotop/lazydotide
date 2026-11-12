using System.Diagnostics;

namespace DotNetIDE;

/// <summary>
/// Installs the csharp-ls language server, the counterpart to
/// <see cref="NetcoredbgInstaller"/> for the LSP side.
/// </summary>
/// <remarks>
/// Much simpler than the debugger installer: csharp-ls ships as a .NET global tool, so there is
/// no release feed to query, no archive to unpack and no platform asset matrix. It is a single
/// <c>dotnet tool</c> invocation, which also means the .NET SDK is the only prerequisite and
/// every platform the IDE runs on is supported.
///
/// Install and update are the same flow with a different verb: <c>install</c> fails when the
/// tool is already present, so an existing install is upgraded instead.
/// </remarks>
public static class CsharpLsInstaller
{
    /// <summary>The NuGet package id, which is also the command name it installs.</summary>
    public const string PackageId = "csharp-ls";

    /// <summary>
    /// Installs csharp-ls as a .NET global tool, or updates it when already present.
    /// </summary>
    /// <param name="progress">Receives each line of tool output as it arrives.</param>
    /// <param name="ct">Cancels the install; the child process is killed with its tree.</param>
    /// <exception cref="InvalidOperationException">
    /// The .NET SDK is missing, or the tool command exited non-zero.
    /// </exception>
    public static async Task InstallAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Checking for the .NET SDK...");

        var sdk = await RunAsync("dotnet", "--version", null, ct);
        if (sdk.ExitCode != 0)
            throw new InvalidOperationException(
                "The .NET SDK is required to install csharp-ls, and 'dotnet --version' failed. "
                + "Install the SDK from https://dotnet.microsoft.com/download and try again.");

        progress?.Report($"Found .NET SDK {sdk.Output.Trim()}");

        // `tool install` is an error when the tool is already there, so decide up front which
        // verb to use rather than parsing the failure afterwards.
        bool alreadyInstalled = await IsInstalledAsync(ct);
        string verb = alreadyInstalled ? "update" : "install";

        progress?.Report(alreadyInstalled
            ? $"{PackageId} is already installed; updating to the latest version..."
            : $"Installing {PackageId}...");

        var result = await RunAsync("dotnet", $"tool {verb} --global {PackageId}", progress, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"'dotnet tool {verb} --global {PackageId}' exited with code {result.ExitCode}. "
                + "See the log above for details.");

        progress?.Report("Verifying the installation...");

        // A global tool lands in ~/.dotnet/tools, which is not on PATH for an already-running
        // process. Report it rather than failing: the install did succeed, and the next run of
        // the IDE will find it.
        var version = await RunAsync(PackageId, "--version", null, ct);
        if (version.ExitCode == 0)
            progress?.Report($"{PackageId} {version.Output.Trim()} is ready");
        else
            progress?.Report(
                $"{PackageId} installed. It is not on this process's PATH yet, "
                + $"so restart {nameof(DotNetIDE)} to pick it up.");
    }

    /// <summary>Whether csharp-ls is already present as a global tool.</summary>
    public static async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        var list = await RunAsync("dotnet", "tool list --global", null, ct);
        if (list.ExitCode != 0) return false;

        foreach (var line in list.Output.Split('\n'))
        {
            // Rows are "packageid   version   commands", space-padded into columns.
            var first = line.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(first, PackageId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private readonly record struct RunResult(int ExitCode, string Output);

    /// <summary>
    /// Runs a command, streaming combined stdout/stderr to <paramref name="progress"/> and
    /// returning it as well. A non-zero exit is returned, not thrown: callers decide.
    /// </summary>
    private static async Task<RunResult> RunAsync(
        string exe, string args, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var lines = new List<string>();
        void Capture(string? line)
        {
            if (line == null) return;
            lock (lines) lines.Add(line);
            if (!string.IsNullOrWhiteSpace(line)) progress?.Report(line.Trim());
        }

        p.OutputDataReceived += (_, e) => Capture(e.Data);
        p.ErrorDataReceived += (_, e) => Capture(e.Data);

        try
        {
            if (!p.Start())
                return new RunResult(-1, $"could not start {exe}");
        }
        catch (Exception ex)
        {
            // A missing executable is an ordinary outcome here (no SDK, tool not on PATH yet),
            // so report it as a failed run rather than letting it escape as an exception.
            return new RunResult(-1, ex.Message);
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        string output;
        lock (lines) output = string.Join('\n', lines);
        return new RunResult(p.ExitCode, output);
    }
}
