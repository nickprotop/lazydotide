using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

internal class LayoutController
{
    private readonly AppContext _ctx;
    private readonly LspCoordinator _lspCoord;
    private readonly DebugCoordinator _debugCoord;

    // Layout controls
    private readonly ColumnContainer? _explorerCol;
    private readonly SplitterControl? _explorerSplitter;
    private readonly ColumnContainer? _sidePanelCol;
    private readonly SplitterControl? _sidePanelSplitter;
    private readonly HorizontalGridControl? _mainContent;
    private readonly MarkupControl? _dashboard;

    // Panel visibility state
    public bool ExplorerVisible { get; set; } = true;
    public bool OutputVisible { get; set; } = true;
    public bool SidePanelVisible { get; set; }
    public double SplitRatio { get; set; } = 0.68;
    public bool ResizeCoupling { get; set; }

    // About dialog state
    private bool _aboutOpen;
    private Action? _aboutRefresh;
    public Action? AboutRefresh => _aboutRefresh;

    // Find/replace state
    private bool _findReplaceOpen;

    // Side panel shell count
    private int _sidePanelShellCount;

    // Minimum usable height for each panel (rows)
    private const int MinMainHeight = 8;
    private const int MinOutputHeight = 4;

    public LayoutController(
        AppContext ctx,
        LspCoordinator lspCoord,
        DebugCoordinator debugCoord,
        ColumnContainer? explorerCol,
        SplitterControl? explorerSplitter,
        ColumnContainer? sidePanelCol,
        SplitterControl? sidePanelSplitter,
        HorizontalGridControl? mainContent,
        MarkupControl? dashboard)
    {
        _ctx = ctx;
        _lspCoord = lspCoord;
        _debugCoord = debugCoord;
        _explorerCol = explorerCol;
        _explorerSplitter = explorerSplitter;
        _sidePanelCol = sidePanelCol;
        _sidePanelSplitter = sidePanelSplitter;
        _mainContent = mainContent;
        _dashboard = dashboard;
    }

    public int ExplorerColumnWidth =>
        _explorerCol?.Width ?? _explorerCol?.ActualWidth ?? 26;

    public int SidePanelColumnWidth =>
        _sidePanelCol?.Width ?? _sidePanelCol?.ActualWidth ?? 30;

    public void SetExplorerColumnWidth(int width)
    {
        if (_explorerCol != null) _explorerCol.Width = width;
    }

    public void SetSidePanelColumnWidth(int width)
    {
        if (_sidePanelCol != null) _sidePanelCol.Width = width;
    }

    public void OnScreenResized(object? sender, SharpConsoleUI.Helpers.Size size)
    {
        var desktop = _ctx.WindowSystem.DesktopDimensions;
        ResizeCoupling = true;
        try
        {
            if (OutputVisible)
            {
                int mainH = (int)(desktop.Height * SplitRatio);
                int outH = desktop.Height - mainH;
                _ctx.MainWindow?.SetSize(desktop.Width, mainH);
                _ctx.OutputWindow?.SetSize(desktop.Width, outH);
                _ctx.OutputWindow?.SetPosition(new Point(0, mainH));
            }
            else
            {
                _ctx.MainWindow?.SetSize(desktop.Width, desktop.Height);
            }
        }
        finally { ResizeCoupling = false; }
    }

    public void OnMainWindowResized(object? sender, EventArgs e)
    {
        if (ResizeCoupling || !OutputVisible || _ctx.MainWindow == null || _ctx.OutputWindow == null) return;
        ResizeCoupling = true;
        try
        {
            var desktop = _ctx.WindowSystem.DesktopDimensions;
            int newDivider = Math.Clamp(_ctx.MainWindow.Height,
                MinMainHeight,
                desktop.Height - MinOutputHeight);
            int newOutH = desktop.Height - newDivider;
            _ctx.MainWindow.SetSize(desktop.Width, newDivider);
            _ctx.OutputWindow.SetPosition(new Point(0, newDivider));
            _ctx.OutputWindow.SetSize(desktop.Width, newOutH);
            SplitRatio = newDivider / (double)desktop.Height;
        }
        finally { ResizeCoupling = false; }
    }

    public void OnOutputWindowResized(object? sender, EventArgs e)
    {
        if (ResizeCoupling || !OutputVisible || _ctx.MainWindow == null || _ctx.OutputWindow == null) return;
        ResizeCoupling = true;
        try
        {
            var desktop = _ctx.WindowSystem.DesktopDimensions;
            int newDivider = Math.Clamp(_ctx.OutputWindow.Top,
                MinMainHeight,
                desktop.Height - MinOutputHeight);
            _ctx.MainWindow.SetSize(desktop.Width, newDivider);
            _ctx.OutputWindow.SetPosition(new Point(0, newDivider));
            _ctx.OutputWindow.SetSize(desktop.Width, desktop.Height - newDivider);
            SplitRatio = newDivider / (double)desktop.Height;
        }
        finally { ResizeCoupling = false; }
    }

    public void ToggleExplorer()
    {
        ExplorerVisible = !ExplorerVisible;
        if (_explorerCol != null)
            _explorerCol.Visible = ExplorerVisible;
        if (_explorerSplitter != null)
            _explorerSplitter.Visible = ExplorerVisible;
        _ctx.MainWindow?.ForceRebuildLayout();
        _ctx.MainWindow?.Invalidate(true);
    }

    public void ToggleOutput()
    {
        OutputVisible = !OutputVisible;
        var desktop = _ctx.WindowSystem.DesktopDimensions;
        ResizeCoupling = true;
        try
        {
            if (OutputVisible)
            {
                int mainH = (int)(desktop.Height * SplitRatio);
                int outH = desktop.Height - mainH;
                _ctx.MainWindow?.SetSize(desktop.Width, mainH);
                _ctx.OutputWindow?.SetSize(desktop.Width, outH);
                _ctx.OutputWindow?.SetPosition(new Point(0, mainH));
            }
            else
            {
                _ctx.MainWindow?.SetSize(desktop.Width, desktop.Height);
                _ctx.OutputWindow?.SetPosition(new Point(0, desktop.Height + 100));
            }
        }
        finally { ResizeCoupling = false; }
    }

    public void ShowSourceControl()
    {
        if (!SidePanelVisible)
        {
            SidePanelVisible = true;
            if (_sidePanelCol != null) _sidePanelCol.Visible = true;
            if (_sidePanelSplitter != null) _sidePanelSplitter.Visible = true;
            _ctx.MainWindow?.ForceRebuildLayout();
            _ctx.MainWindow?.Invalidate(true);
        }
        _ctx.SidePanel.SwitchToGitTab();
    }

    public void ToggleSidePanel()
    {
        SidePanelVisible = !SidePanelVisible;
        if (_sidePanelCol != null)
            _sidePanelCol.Visible = SidePanelVisible;
        if (_sidePanelSplitter != null)
            _sidePanelSplitter.Visible = SidePanelVisible;
        _ctx.MainWindow?.ForceRebuildLayout();
        _ctx.MainWindow?.Invalidate(true);
        if (SidePanelVisible)
        {
            _ctx.SidePanel.SwitchToSymbolsTab();
            _lspCoord.RefreshSymbolsForFile(_ctx.EditorManager.CurrentFilePath);
        }
    }

    public void FocusSymbolsTab()
    {
        if (!SidePanelVisible)
            ToggleSidePanel();
        _ctx.SidePanel.SwitchToSymbolsTab();
    }

    public void OpenSidePanelShell()
    {
        if (!(IdeConstants.IsDesktopOs)) return;
        if (!SidePanelVisible)
            ToggleSidePanel();

        var terminal = Controls.Terminal()
            .WithWorkingDirectory(_ctx.ProjectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment = VerticalAlignment.Fill;

        _sidePanelShellCount++;
        string tabName = _sidePanelShellCount == 1 ? "Shell" : $"Shell {_sidePanelShellCount}";
        _ctx.SidePanel.TabControl.AddTab(tabName, terminal, isClosable: true);
        _ctx.SidePanel.TabControl.ActiveTabIndex = _ctx.SidePanel.TabControl.TabCount - 1;
        InvalidateSidePanel();
        _ctx.MainWindow?.FocusControl(terminal);
    }

    public void InvalidateSidePanel()
    {
        _mainContent?.Invalidate();
        _ctx.MainWindow?.ForceRebuildLayout();
        _ctx.MainWindow?.Invalidate(true);
    }

    public void SetWrapMode(WrapMode mode)
    {
        _ctx.EditorManager.WrapMode = mode;
    }

    public void ShowFindReplace()
    {
        if (_findReplaceOpen) return;
        _findReplaceOpen = true;
        _ = FindReplaceDialog.ShowAsync(_ctx.WindowSystem, _ctx.EditorManager)
            .ContinueWith(_ => _findReplaceOpen = false);
    }

    public void ShowAbout()
    {
        if (_aboutOpen) return;
        _aboutOpen = true;
        _aboutRefresh = AboutDialog.Show(_ctx.WindowSystem, () => new AboutInfo(
            LspStarted: _lspCoord.LspStarted,
            LspDetectionDone: _lspCoord.LspDetectionDone,
            DetectedLspExe: _lspCoord.DetectedLspExe,
            DapDetected: _debugCoord.HasDebugger,
            DapDetectionDone: _debugCoord.DapDetectionDone,
            DetectedDapExe: _debugCoord.DetectedDapExe,
            Tools: _ctx.Config.Tools,
            ProjectPath: _ctx.ProjectService.RootPath,
            OnInstallDebugger: _debugCoord.HasDebugger ? null : () => InstallDebugger()),
            () => { _aboutOpen = false; _aboutRefresh = null; });
    }

    public void InstallDebugger()
    {
        _ = InstallDebuggerModal.ShowAsync(_ctx.WindowSystem).ContinueWith(t =>
        {
            if (t.Result)
            {
                _debugCoord.ReDetectDap();
                UpdateDashboard();
                _aboutRefresh?.Invoke();
            }
        }, TaskScheduler.Default);
    }

    public void UpdateDashboard()
    {
        _dashboard?.SetContent(GetDashboardLines());
    }

    private List<string> GetDashboardLines()
    {
        var projectName = new DirectoryInfo(_ctx.ProjectService.RootPath).Name;
        var rootPath = _ctx.ProjectService.RootPath;

        List<string> lspLines;
        if (!_lspCoord.LspDetectionDone)
            lspLines = new List<string> { "[dim]  LSP      ○ detecting…[/]" };
        else if (_lspCoord.LspStarted)
            lspLines = new List<string> { $"[dim]  LSP      [/][green]● {MarkupParser.Escape(_lspCoord.DetectedLspExe!)}[/]" };
        else if (_lspCoord.DetectedLspExe != null)
            lspLines = new List<string> { $"[dim]  LSP      ○ {MarkupParser.Escape(_lspCoord.DetectedLspExe)} (failed to start)[/]" };
        else
            lspLines = new List<string>
            {
                "[dim]  LSP      ○ not found[/]",
                "[dim]           Enables: IntelliSense · Go to Definition · References[/]",
                "[dim]                    Rename · Code Actions · Signature Help[/]",
                "[yellow]           Install: [/][italic]dotnet tool install -g csharp-ls[/]",
                "[dim]           Alt:     [/][dim italic]OmniSharp  (omnisharp.net)[/]",
                $"[dim]           Config:  [/][dim italic]{MarkupParser.Escape(ConfigService.GetConfigPath())}[/]",
            };

        // Debugger status
        List<string> dapLines;
        if (!_debugCoord.DapDetectionDone)
            dapLines = new List<string> { "[dim]  Debugger ○ detecting…[/]" };
        else if (_debugCoord.HasDebugger)
            dapLines = new List<string> { $"[dim]  Debugger [/][green]● {MarkupParser.Escape(_debugCoord.DetectedDapExe!)}[/]" };
        else
            dapLines = new List<string>
            {
                "[dim]  Debugger ○ not detected[/]",
                "[dim]           Enables: F5 debugging, breakpoints, stepping[/]",
                "[yellow]           Install: [/][italic]Help › Install netcoredbg  (auto-download)[/]",
            };

        string toolsLine = _ctx.Config.Tools.Count == 0
            ? "[dim]  Tools    0 loaded  →  Tools › Edit Config[/]"
            : $"[dim]  Tools    [/][green]{_ctx.Config.Tools.Count} loaded[/][dim]  ({string.Join(", ", _ctx.Config.Tools.Select(t => t.Name))})[/]";

        var lines = new List<string>
        {
            "",
            $"[bold]  lazydotide[/]  [dim]{MarkupParser.Escape(projectName)}[/]",
            $"[dim]  {MarkupParser.Escape(rootPath)}[/]",
            "",
            "[dim]  ────────────────────────────[/]",
        };
        lines.AddRange(lspLines);
        lines.AddRange(dapLines);
        lines.Add(toolsLine);
        lines.AddRange(new[]
        {
            "",
            "[dim]  ────────────────────────────[/]",
            "[dim]  F5  Debug     F6  Build    Ctrl+F5  Run[/]",
            "[dim]  F7  Test      F8  Shell    F9  Breakpoint[/]",
            "[dim]  F10  Step Over   F11  Step Into[/]",
            "[dim]  F12  Definition  Shift+F12  References[/]",
            "[dim]  Ctrl+F2  Rename  Ctrl+.  Actions[/]",
            "[dim]  Ctrl+S  Save  Ctrl+W  Close[/]",
            "[dim]  Ctrl+B  Explorer  Ctrl+J  Output[/]",
        });
        return lines;
    }

    public void ApplyRestoredSplitRatio(double splitRatio)
    {
        SplitRatio = splitRatio;
        var desktop = _ctx.WindowSystem.DesktopDimensions;
        int mainH = (int)(desktop.Height * SplitRatio);
        int outH = desktop.Height - mainH;
        ResizeCoupling = true;
        try
        {
            _ctx.MainWindow?.SetSize(desktop.Width, mainH);
            _ctx.OutputWindow?.SetSize(desktop.Width, outH);
            _ctx.OutputWindow?.SetPosition(new Point(0, mainH));
        }
        finally { ResizeCoupling = false; }
    }

    public void ForceRebuildLayout()
    {
        _ctx.MainWindow?.ForceRebuildLayout();
        _ctx.MainWindow?.Invalidate(true);
    }
}
