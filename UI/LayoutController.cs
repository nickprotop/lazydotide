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
    public int OutputPanelHeight { get; set; } = 12;

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

    // Minimum usable width for a side column (columns).
    private const int MinSideColumnWidth = 10;
    private const int MinEditorWidth = 20;

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

        // Wire splitter moved event to track output panel height
        _ctx.OutputSplitter.SplitterMoved += OnSplitterMoved;

        // Track column widths from splitter drags (see ExplorerColumnWidth).
        if (_explorerSplitter != null)
            _explorerSplitter.SplitterMoved += OnExplorerSplitterMoved;
        if (_sidePanelSplitter != null)
            _sidePanelSplitter.SplitterMoved += OnSidePanelSplitterMoved;
    }

    // Widths tracked from splitter drags. ColumnContainer.Width cannot be used:
    // a drag stores a value derived from the combined space of both adjacent
    // columns, so it can far exceed the column's real on-screen width. And
    // ActualWidth is only set when a column is painted, which never happens for
    // the side panel column, leaving it at 0. Tracking the drags ourselves is
    // the only reliable source. 0 means "unknown" — keep the saved value.
    private int _explorerWidth;
    private int _sidePanelWidth;

    public int ExplorerColumnWidth =>
        _explorerCol is { Visible: true }
            ? (_explorerWidth > 0 ? _explorerWidth
               : _explorerCol.ActualWidth > 0 ? _explorerCol.ActualWidth : 0)
            : 0;

    public int SidePanelColumnWidth =>
        _sidePanelCol is { Visible: true } && _sidePanelWidth > 0 ? _sidePanelWidth : 0;

    private void OnExplorerSplitterMoved(object? sender, SplitterMovedEventArgs e)
    {
        // The explorer is this splitter's left column.
        if (e.LeftColumnWidth > 0) _explorerWidth = e.LeftColumnWidth;
    }

    private void OnSidePanelSplitterMoved(object? sender, SplitterMovedEventArgs e)
    {
        // The side panel is this splitter's right column.
        if (e.RightColumnWidth > 0) _sidePanelWidth = e.RightColumnWidth;
    }

    public void SetExplorerColumnWidth(int width)
    {
        if (_explorerCol == null) return;
        int w = ClampSideColumnWidth(width);
        _explorerCol.Width = w;
        _explorerWidth = w;
    }

    public void SetSidePanelColumnWidth(int width)
    {
        if (_sidePanelCol == null) return;
        int w = ClampSideColumnWidth(width);
        _sidePanelCol.Width = w;
        _sidePanelWidth = w;
    }

    /// <summary>
    /// Restores both side-column widths together, so the columns never
    /// over-commit the grid. Setting one column's width alone leaves the centre
    /// editor at its previous size: the widths then sum to more than the
    /// desktop, the side panel is pushed off-screen, and splitter drags stop
    /// working because they are computed from the over-committed widths.
    /// </summary>
    public void RestoreColumnWidths(int explorerWidth, int sidePanelWidth)
    {
        int desktopWidth = _ctx.WindowSystem.DesktopDimensions.Width;

        int explorer = _explorerCol is { Visible: true } && explorerWidth > 0
            ? ClampSideColumnWidth(explorerWidth) : 0;
        int side = _sidePanelCol is { Visible: true } && sidePanelWidth > 0
            ? ClampSideColumnWidth(sidePanelWidth) : 0;

        // Shrink the side columns proportionally if they leave no room for the
        // editor once the splitters are accounted for.
        if (desktopWidth > 0)
        {
            int splitters = (_explorerSplitter is { Visible: true } ? 1 : 0)
                          + (_sidePanelSplitter is { Visible: true } ? 1 : 0);
            int budget = desktopWidth - splitters - MinEditorWidth;
            if (budget > 0 && explorer + side > budget)
            {
                double scale = (double)budget / (explorer + side);
                if (explorer > 0) explorer = Math.Max(MinSideColumnWidth, (int)(explorer * scale));
                if (side > 0) side = Math.Max(MinSideColumnWidth, (int)(side * scale));
            }
        }

        if (explorer > 0 && _explorerCol != null)
        {
            _explorerCol.Width = explorer;
            _explorerWidth = explorer;
        }
        if (side > 0 && _sidePanelCol != null)
        {
            _sidePanelCol.Width = side;
            _sidePanelWidth = side;
        }

        // The centre editor column is left flexible so it absorbs the remaining
        // space; SplitterControl handles flex columns and clamps drags against
        // the grid width, so it must not be pinned to an explicit width here.
    }

    // Enforces only a lower bound. The upper bound is handled by
    // RestoreColumnWidths, which scales both side columns together so the
    // editor always keeps MinEditorWidth; capping each column independently
    // here would truncate legitimate widths (a panel wider than half the
    // screen is a valid layout when the other column is narrow).
    private int ClampSideColumnWidth(int width)
    {
        int desktopWidth = _ctx.WindowSystem.DesktopDimensions.Width;
        if (desktopWidth <= 0) return Math.Max(width, MinSideColumnWidth);
        int max = Math.Max(MinSideColumnWidth, desktopWidth - MinEditorWidth);
        return Math.Clamp(width, MinSideColumnWidth, max);
    }

    public void OnScreenResized(object? sender, SharpConsoleUI.Helpers.Size size)
    {
        var desktop = _ctx.WindowSystem.DesktopDimensions;
        _ctx.MainWindow?.SetSize(desktop.Width, desktop.Height);
        // Re-fit the fixed side columns: after a shrink they may no longer
        // leave a usable editor between them.
        RestoreColumnWidths(_explorerWidth, _sidePanelWidth);
    }

    private void OnSplitterMoved(object? sender, HorizontalSplitterMovedEventArgs e)
    {
        OutputPanelHeight = e.BelowControlHeight;
    }

    public void ToggleExplorer()
    {
        ExplorerVisible = !ExplorerVisible;
        if (_explorerCol != null)
            _explorerCol.Visible = ExplorerVisible;
        if (_explorerSplitter != null)
            _explorerSplitter.Visible = ExplorerVisible;
        _ctx.MainWindow?.ForceRebuildLayout();
    }

    public void ToggleOutput()
    {
        OutputVisible = !OutputVisible;
        _ctx.OutputSplitter.Visible = OutputVisible;
        _ctx.OutputPanel.TabControl.Visible = OutputVisible;
        _ctx.MainWindow?.ForceRebuildLayout();
    }

    public void ShowSourceControl()
    {
        if (!SidePanelVisible)
        {
            SidePanelVisible = true;
            if (_sidePanelCol != null) _sidePanelCol.Visible = true;
            if (_sidePanelSplitter != null) _sidePanelSplitter.Visible = true;
            _ctx.MainWindow?.ForceRebuildLayout();
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
        _ctx.MainWindow?.ForceRebuildLayout();
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
                _ctx.PendingUiActions.Enqueue(() =>
                {
                    UpdateDashboard();
                    _aboutRefresh?.Invoke();
                });
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

    public void ApplyRestoredOutputHeight(int height)
    {
        OutputPanelHeight = height;
        _ctx.OutputPanel.TabControl.Height = height;
        _ctx.MainWindow?.ForceRebuildLayout();
    }

    public void ForceRebuildLayout()
    {
        _ctx.MainWindow?.ForceRebuildLayout();
    }
}
