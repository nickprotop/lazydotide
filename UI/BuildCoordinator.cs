using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Layout;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

internal class BuildCoordinator
{
    private readonly BuildService _buildService;
    private readonly ProjectService _projectService;
    private readonly EditorManager _editorManager;
    private readonly OutputPanel _outputPanel;
    private readonly IdeConfig _config;
    private readonly ConcurrentQueue<string> _buildLines;
    private readonly ConcurrentQueue<string> _testLines;
    private readonly ConcurrentQueue<Action> _pendingUiActions;
    private readonly CancellationToken _ct;

    private int _lazyNuGetTabIndex = -1;
    private int _shellTabCount;
    private int _outputShellCount;
    private readonly Dictionary<int, int> _toolTabIndices = new();
    private bool _hasLazyNuGet;

    // Events for communicating with IdeApp
    public event EventHandler<List<BuildDiagnostic>>? DiagnosticsUpdated;

    /// <summary>Called when the output panel needs to become visible.</summary>
    public Action? OutputRequired;

    private ConsoleWindowSystem? _ws;
    private Window? _outputWindow;
    private SidePanel? _sidePanel;

    // Public accessors needed by other components
    public bool HasLazyNuGet => _hasLazyNuGet;
    public int OutputShellCount { get => _outputShellCount; set => _outputShellCount = value; }

    public BuildCoordinator(
        BuildService buildService,
        ProjectService projectService,
        EditorManager editorManager,
        OutputPanel outputPanel,
        IdeConfig config,
        ConcurrentQueue<string> buildLines,
        ConcurrentQueue<string> testLines,
        ConcurrentQueue<Action> pendingUiActions,
        CancellationToken ct)
    {
        _buildService = buildService;
        _projectService = projectService;
        _editorManager = editorManager;
        _outputPanel = outputPanel;
        _config = config;
        _buildLines = buildLines;
        _testLines = testLines;
        _pendingUiActions = pendingUiActions;
        _ct = ct;
        _hasLazyNuGet = DetectLazyNuGet() != null;
    }

    public void SetWindowSystem(ConsoleWindowSystem ws, Window outputWindow, SidePanel sidePanel)
    {
        _ws = ws;
        _outputWindow = outputWindow;
        _sidePanel = sidePanel;
    }

    public async Task BuildProjectAsync()
    {
        var target = _projectService.FindBuildTarget();
        if (target == null) return;

        _outputPanel.ClearBuildOutput();
        _outputPanel.SwitchToBuildTab();

        var result = await _buildService.BuildAsync(
            target,
            line => _buildLines.Enqueue(line),
            _ct);

        _pendingUiActions.Enqueue(() =>
        {
            _outputPanel.PopulateProblems(result.Diagnostics);
            DiagnosticsUpdated?.Invoke(this, result.Diagnostics);
        });
    }

    public async Task TestProjectAsync()
    {
        var target = _projectService.FindBuildTarget();
        if (target == null) return;

        _outputPanel.ClearTestOutput();
        _outputPanel.SwitchToTestTab();

        var result = await _buildService.TestAsync(
            target,
            line => _testLines.Enqueue(line),
            _ct);

        _pendingUiActions.Enqueue(() => DiagnosticsUpdated?.Invoke(this, result.Diagnostics));
    }

    public async Task CleanProjectAsync()
    {
        var target = _projectService.FindBuildTarget();
        if (target == null) return;

        _outputPanel.ClearBuildOutput();
        _outputPanel.SwitchToBuildTab();

        await _buildService.RunAsync(
            "dotnet", ["clean", target, "--nologo"],
            line => _buildLines.Enqueue(line),
            _ct);
    }

    public void RunProject()
    {
        var target = _projectService.FindRunTarget();
        if (target == null)
        {
            _ws?.LogService.LogInfo("No runnable project found");
            return;
        }

        if (IdeConstants.IsDesktopOs)
            Controls.Terminal("dotnet").WithArgs("run", "--project", target).Open(_ws!);
    }

    public void OpenShell()
    {
        if (!(IdeConstants.IsDesktopOs)) return;
        if (_outputWindow == null) return;

        var terminal = Controls.Terminal()
            .WithWorkingDirectory(_projectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _outputShellCount++;
        string tabName = _outputShellCount == 1 ? "Shell" : $"Shell {_outputShellCount}";
        _outputPanel.TabControl.AddTab(tabName, terminal, isClosable: true);
        _outputPanel.TabControl.ActiveTabIndex = _outputPanel.TabControl.TabCount - 1;

        OutputRequired?.Invoke();
        _ws!.SetActiveWindow(_outputWindow);
        _outputWindow.FocusControl(terminal);
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
            _lazyNuGetTabIndex < _editorManager.TabControl.TabCount)
        {
            _editorManager.TabControl.ActiveTabIndex = _lazyNuGetTabIndex;
            return;
        }

        string? exe = DetectLazyNuGet();
        if (exe == null)
        {
            _ws?.NotificationStateService.ShowNotification(
                "LazyNuGet Not Found",
                "lazynuget not found in PATH. Install: dotnet tool install -g lazynuget",
                SharpConsoleUI.Core.NotificationSeverity.Warning);
            return;
        }

        var terminal = Controls.Terminal(exe)
            .WithWorkingDirectory(_projectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _lazyNuGetTabIndex = _editorManager.OpenControlTab("LazyNuGet", terminal, isClosable: true);
        terminal.ProcessExited += (_, _) => _lazyNuGetTabIndex = -1;
    }

    public void OpenShellTab()
    {
        string shellExe = OperatingSystem.IsWindows() ? "cmd.exe" : "bash";
        if (!(IdeConstants.IsDesktopOs)) return;

        var terminal = Controls.Terminal(shellExe)
            .WithWorkingDirectory(_projectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _shellTabCount++;
        string tabName = _shellTabCount == 1 ? "Shell" : $"Shell {_shellTabCount}";
        _editorManager.OpenControlTab(tabName, terminal, isClosable: true);
    }

    private TerminalControl? CreateToolTerminal(int toolIndex)
    {
        if (!IdeConstants.IsDesktopOs) return null;
        if (toolIndex < 0 || toolIndex >= _config.Tools.Count) return null;

        var tool = _config.Tools[toolIndex];
        string workDir = string.IsNullOrEmpty(tool.WorkingDir)
            ? _projectService.RootPath
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
            existingTab >= 0 && existingTab < _editorManager.TabControl.TabCount)
        {
            _editorManager.TabControl.ActiveTabIndex = existingTab;
            return;
        }

        var terminal = CreateToolTerminal(toolIndex);
        if (terminal == null) return;

        var tool = _config.Tools[toolIndex];
        int tabIdx = _editorManager.OpenControlTab(tool.Name, terminal, isClosable: true);
        _toolTabIndices[toolIndex] = tabIdx;
        if (IdeConstants.IsDesktopOs) // Redundant guard for CA1416 — CreateToolTerminal already checks this
            terminal.ProcessExited += (_, _) => _toolTabIndices.Remove(toolIndex);
    }

    public void OpenConfigToolInOutputPanel(int toolIndex)
    {
        var terminal = CreateToolTerminal(toolIndex);
        if (terminal == null) return;

        var tool = _config.Tools[toolIndex];
        _outputPanel.TabControl.AddTab(tool.Name, terminal, isClosable: true);
        _outputPanel.TabControl.ActiveTabIndex = _outputPanel.TabControl.TabCount - 1;

        if (_outputWindow != null)
        {
            OutputRequired?.Invoke();
            _ws!.SetActiveWindow(_outputWindow);
            _outputWindow.FocusControl(terminal);
        }
    }

    public void OpenConfigToolInSidePanel(int toolIndex, Action toggleSidePanel, Action invalidateSidePanel)
    {
        if (_sidePanel == null) return;

        var terminal = CreateToolTerminal(toolIndex);
        if (terminal == null) return;

        var tool = _config.Tools[toolIndex];
        toggleSidePanel();
        _sidePanel.TabControl.AddTab(tool.Name, terminal, isClosable: true);
        _sidePanel.TabControl.ActiveTabIndex = _sidePanel.TabControl.TabCount - 1;
        invalidateSidePanel();
    }

    public void OpenConfigFile()
    {
        ConfigService.EnsureDefaultConfig();
        _editorManager.OpenFile(ConfigService.GetConfigPath());
    }

    public void ShowNuGetDialog()
    {
        _ = NuGetDialog.ShowAsync(_ws!).ContinueWith(t =>
        {
            _pendingUiActions.Enqueue(() =>
            {
                var (packageName, version) = t.Result;
                if (string.IsNullOrEmpty(packageName)) return;

                var target = _projectService.FindBuildTarget();
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
        _outputPanel.ClearBuildOutput();
        _outputPanel.SwitchToBuildTab();

        await _buildService.RunAsync(
            "dotnet", args.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            line => _buildLines.Enqueue(line),
            _ct);
    }

    public void CancelBuild() => _buildService.Cancel();
}
