using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Layout;

namespace DotNetIDE;

internal class BuildCoordinator
{
    private readonly AppContext _ctx;

    private int _lazyNuGetTabIndex = -1;
    private int _shellTabCount;
    private int _outputShellCount;
    private readonly Dictionary<int, int> _toolTabIndices = new();
    private bool _hasLazyNuGet;

    // Events for communicating with IdeApp
    public event EventHandler<List<BuildDiagnostic>>? DiagnosticsUpdated;

    /// <summary>Called when the output panel needs to become visible.</summary>
    public Action? OutputRequired;


    // Public accessors needed by other components
    public bool HasLazyNuGet => _hasLazyNuGet;
    public int OutputShellCount { get => _outputShellCount; set => _outputShellCount = value; }

    public BuildCoordinator(AppContext ctx)
    {
        _ctx = ctx;
        _hasLazyNuGet = DetectLazyNuGet() != null;
    }

    public async Task BuildProjectAsync()
    {
        var target = _ctx.ProjectService.FindBuildTarget();
        if (target == null) return;

        _ctx.OutputPanel.ClearBuildOutput();
        _ctx.OutputPanel.SwitchToBuildTab();

        var result = await _ctx.BuildService.BuildAsync(
            target,
            line => _ctx.BuildLines.Enqueue(line),
            _ctx.CancellationToken);

        _ctx.PendingUiActions.Enqueue(() =>
        {
            _ctx.OutputPanel.PopulateProblems(result.Diagnostics);
            DiagnosticsUpdated?.Invoke(this, result.Diagnostics);
        });
    }

    public async Task TestProjectAsync()
    {
        var target = _ctx.ProjectService.FindBuildTarget();
        if (target == null) return;

        _ctx.OutputPanel.ClearTestOutput();
        _ctx.OutputPanel.SwitchToTestTab();

        var result = await _ctx.BuildService.TestAsync(
            target,
            line => _ctx.TestLines.Enqueue(line),
            _ctx.CancellationToken);

        _ctx.PendingUiActions.Enqueue(() => DiagnosticsUpdated?.Invoke(this, result.Diagnostics));
    }

    public async Task CleanProjectAsync()
    {
        var target = _ctx.ProjectService.FindBuildTarget();
        if (target == null) return;

        _ctx.OutputPanel.ClearBuildOutput();
        _ctx.OutputPanel.SwitchToBuildTab();

        await _ctx.BuildService.RunAsync(
            "dotnet", ["clean", target, "--nologo"],
            line => _ctx.BuildLines.Enqueue(line),
            _ctx.CancellationToken);
    }

    public void RunProject(LaunchProfileEntry? profile = null)
    {
        var target = _ctx.ProjectService.FindRunTarget();
        if (target == null)
        {
            _ctx.WindowSystem?.LogService.LogInfo("No runnable project found");
            return;
        }

        if (!IdeConstants.IsDesktopOs) return;

        var runArgs = new List<string> { "run", "--project", target };
        if (profile?.Args is { Length: > 0 })
        {
            runArgs.Add("--");
            runArgs.AddRange(profile.Args);
        }

        var builder = Controls.Terminal("dotnet")
            .WithArgs(runArgs.ToArray())
            .WithWorkingDirectory(profile?.WorkingDirectory ?? _ctx.ProjectService.RootPath);

        var terminal = builder.Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment = VerticalAlignment.Fill;

        var tabName = "Run: " + Path.GetFileNameWithoutExtension(target);
        _ctx.EditorManager.OpenControlTab(tabName, terminal, isClosable: true, autoCloseOnExit: false);
    }

    public void OpenShell()
    {
        if (!(IdeConstants.IsDesktopOs)) return;

        var terminal = Controls.Terminal()
            .WithWorkingDirectory(_ctx.ProjectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _outputShellCount++;
        string tabName = _outputShellCount == 1 ? "Shell" : $"Shell {_outputShellCount}";
        _ctx.OutputPanel.TabControl.AddTab(tabName, terminal, isClosable: true);
        _ctx.OutputPanel.TabControl.ActiveTabIndex = _ctx.OutputPanel.TabControl.TabCount - 1;

        OutputRequired?.Invoke();
        _ctx.MainWindow.FocusControl(terminal);
    }

    public static string? DetectLazyNuGet()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                OperatingSystem.IsWindows() ? "where" : "which", "lazynuget")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit();
            if (!string.IsNullOrEmpty(output))
                return "lazynuget";
        }
        catch { } // Expected when lazynuget is not installed
        return null;
    }

    public void OpenLazyNuGetTab()
    {
        if (!(IdeConstants.IsDesktopOs)) return;

        if (_lazyNuGetTabIndex >= 0 &&
            _lazyNuGetTabIndex < _ctx.EditorManager.TabControl.TabCount)
        {
            _ctx.EditorManager.TabControl.ActiveTabIndex = _lazyNuGetTabIndex;
            return;
        }

        string? exe = DetectLazyNuGet();
        if (exe == null)
        {
            _ctx.WindowSystem?.NotificationStateService.ShowNotification(
                "LazyNuGet Not Found",
                "lazynuget not found in PATH. Install: dotnet tool install -g lazynuget",
                SharpConsoleUI.Core.NotificationSeverity.Warning);
            return;
        }

        var terminal = Controls.Terminal(exe)
            .WithWorkingDirectory(_ctx.ProjectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _lazyNuGetTabIndex = _ctx.EditorManager.OpenControlTab("LazyNuGet", terminal, isClosable: true);
        terminal.ProcessExited += (_, _) => _lazyNuGetTabIndex = -1;
    }

    public void OpenShellTab()
    {
        string shellExe = OperatingSystem.IsWindows() ? "cmd.exe" : "bash";
        if (!(IdeConstants.IsDesktopOs)) return;

        var terminal = Controls.Terminal(shellExe)
            .WithWorkingDirectory(_ctx.ProjectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _shellTabCount++;
        string tabName = _shellTabCount == 1 ? "Shell" : $"Shell {_shellTabCount}";
        _ctx.EditorManager.OpenControlTab(tabName, terminal, isClosable: true);
    }

    private TerminalControl? CreateToolTerminal(int toolIndex)
    {
        if (!IdeConstants.IsDesktopOs) return null;
        if (toolIndex < 0 || toolIndex >= _ctx.Config.Tools.Count) return null;

        var tool = _ctx.Config.Tools[toolIndex];
        string workDir = string.IsNullOrEmpty(tool.WorkingDir)
            ? _ctx.ProjectService.RootPath
            : tool.WorkingDir;

        var builder = Controls.Terminal(tool.Command).WithWorkingDirectory(workDir);
        if (tool.Args?.Length > 0)
            builder = builder.WithArgs(tool.Args);

        var terminal = builder.Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;
        return terminal;
    }

    public void OpenConfigToolTab(int toolIndex)
    {
        if (_toolTabIndices.TryGetValue(toolIndex, out int existingTab) &&
            existingTab >= 0 && existingTab < _ctx.EditorManager.TabControl.TabCount)
        {
            _ctx.EditorManager.TabControl.ActiveTabIndex = existingTab;
            return;
        }

        var terminal = CreateToolTerminal(toolIndex);
        if (terminal == null) return;

        var tool = _ctx.Config.Tools[toolIndex];
        int tabIdx = _ctx.EditorManager.OpenControlTab(tool.Name, terminal, isClosable: true);
        _toolTabIndices[toolIndex] = tabIdx;
        if (IdeConstants.IsDesktopOs) // Redundant guard for CA1416 — CreateToolTerminal already checks this
            terminal.ProcessExited += (_, _) => _toolTabIndices.Remove(toolIndex);
    }

    public void OpenConfigToolInOutputPanel(int toolIndex)
    {
        var terminal = CreateToolTerminal(toolIndex);
        if (terminal == null) return;

        var tool = _ctx.Config.Tools[toolIndex];
        _ctx.OutputPanel.TabControl.AddTab(tool.Name, terminal, isClosable: true);
        _ctx.OutputPanel.TabControl.ActiveTabIndex = _ctx.OutputPanel.TabControl.TabCount - 1;

        OutputRequired?.Invoke();
        _ctx.MainWindow.FocusControl(terminal);
    }

    public void OpenConfigToolInSidePanel(int toolIndex, Action toggleSidePanel, Action invalidateSidePanel)
    {
        if (_ctx.SidePanel == null) return;

        var terminal = CreateToolTerminal(toolIndex);
        if (terminal == null) return;

        var tool = _ctx.Config.Tools[toolIndex];
        toggleSidePanel();
        _ctx.SidePanel.TabControl.AddTab(tool.Name, terminal, isClosable: true);
        _ctx.SidePanel.TabControl.ActiveTabIndex = _ctx.SidePanel.TabControl.TabCount - 1;
        invalidateSidePanel();
    }

    public void OpenConfigFile()
    {
        ConfigService.EnsureDefaultConfig();
        _ctx.EditorManager.OpenFile(ConfigService.GetConfigPath());
    }

    public void ShowNuGetDialog()
    {
        _ = NuGetDialog.ShowAsync(_ctx.WindowSystem).ContinueWith(t =>
        {
            _ctx.PendingUiActions.Enqueue(() =>
            {
                var (packageName, version) = t.Result;
                if (string.IsNullOrEmpty(packageName)) return;

                var target = _ctx.ProjectService.FindBuildTarget();
                if (target == null || !target.EndsWith(".csproj")) return;

                var cmdArgs = version != null
                    ? $"add {target} package {packageName} --version {version}"
                    : $"add {target} package {packageName}";

                _ = RunNuGetAsync(cmdArgs);
            });
        });
    }

    public async Task RunNuGetAsync(string args)
    {
        _ctx.OutputPanel.ClearBuildOutput();
        _ctx.OutputPanel.SwitchToBuildTab();

        await _ctx.BuildService.RunAsync(
            "dotnet", args.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            line => _ctx.BuildLines.Enqueue(line),
            _ctx.CancellationToken);
    }

    public void CancelBuild() => _ctx.BuildService.Cancel();
}
