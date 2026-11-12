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
    private readonly GridControl? _grid;
    private readonly MarkupControl? _dashboard;

    // Panel visibility state
    public bool ExplorerVisible { get; set; } = true;
    public bool OutputVisible { get; set; } = true;
    public bool SidePanelVisible { get; set; }
    /// <summary>
    /// The bottom panel's height in rows. This is a view over
    /// RowDefinitions[OutputRow]: a row-splitter drag writes that definition
    /// directly, so there is no separate field to keep in sync.
    /// </summary>
    public int OutputPanelHeight
    {
        get
        {
            if (!OutputVisible) return _outputHeight;
            int h = _ctx.OutputPanel.TabControl.ActualHeight;
            return h > 0 ? h : _outputHeight;
        }
    }

    // Remembered height for collapse/restore, same reason as the column widths.
    private int _outputHeight = IdeConstants.DefaultOutputHeight;

    // About dialog state
    private bool _aboutOpen;
    private Action? _aboutRefresh;
    public Action? AboutRefresh => _aboutRefresh;

    // Find/replace state
    private bool _findReplaceOpen;

    // Side panel shell count
    private int _sidePanelShellCount;

    public LayoutController(
        AppContext ctx,
        LspCoordinator lspCoord,
        DebugCoordinator debugCoord,
        GridControl? grid,
        MarkupControl? dashboard)
    {
        _ctx = ctx;
        _lspCoord = lspCoord;
        _debugCoord = debugCoord;
        _grid = grid;
        _dashboard = dashboard;

        // No splitter-moved wiring: a grid splitter drag writes the track
        // definition itself (GridControl.ResizeSplitter -> RowDefinitions /
        // ColumnDefinitions), so the definitions ARE the live sizes and there
        // is nothing to mirror into application state.
    }

    // Remembered track sizes for collapse/restore. A collapsed track is set to
    // Cells(0) with NO Min: GridTrackSizer clamps a Fixed track to its Min
    // (sizes[i] = Clamp(def.Value, def.Min, def.Max)), so Cells(0, min: 10)
    // would size to 10 and the panel would not collapse at all. The Min is
    // reapplied on restore.
    private int _explorerWidth = IdeConstants.DefaultExplorerWidth;
    private int _sidePanelWidth = IdeConstants.DefaultSidePanelWidth;

    /// <summary>
    /// Whether the explorer and side panel run the full height of the working
    /// area, past the bottom panel. When true the bottom panel occupies only
    /// the editor column. Persisted; defaults off.
    /// </summary>
    public bool ExplorerFullHeight { get; private set; }

    // Sizes are read from what the layout ARRANGED, not from the track
    // definitions. A splitter drag rewrites a Star track's weight rather than a
    // cell count, so the definition is no longer a row/column count once the
    // user has dragged; the arranged size always is. These controls are painted
    // whenever visible, so ActualWidth/ActualHeight are live.
    //
    // 0 means "unknown, keep what was saved": a collapsed panel is not painted.
    public int ExplorerColumnWidth =>
        ExplorerVisible ? Math.Max(0, _ctx.Explorer.Control.ActualWidth) : 0;

    public int SidePanelColumnWidth =>
        SidePanelVisible ? Math.Max(0, _ctx.SidePanel.TabControl.ActualWidth) : 0;

    // Every column is Star, so a width is expressed as a weight. Weights are
    // kept in column units (a weight of 26 next to weights summing to W means
    // 26/W of the width), which makes the numbers readable and makes restoring
    // a saved width a matter of setting all three together.
    public void SetExplorerColumnWidth(int width)
    {
        _explorerWidth = ClampSideColumnWidth(width);
        ApplyColumnWeights();
    }

    public void SetSidePanelColumnWidth(int width)
    {
        _sidePanelWidth = ClampSideColumnWidth(width);
        ApplyColumnWeights();
    }

    /// <summary>
    /// Writes the three column tracks from the remembered side widths, giving
    /// the editor whatever is left of the working width.
    /// </summary>
    private void ApplyColumnWeights()
    {
        if (_grid == null) return;

        int available = _grid.ActualWidth;
        if (available <= 0)
            available = Math.Max(1, _ctx.WindowSystem.DesktopDimensions.Width - 2);

        int explorer = ExplorerVisible ? _explorerWidth : 0;
        int side = SidePanelVisible ? _sidePanelWidth : 0;
        int editor = Math.Max(IdeConstants.MinEditorWidth, available - explorer - side);

        _grid.ColumnDefinitions[IdeConstants.ExplorerCol] = ExplorerVisible
            ? GridLength.Star(explorer, min: IdeConstants.MinSideColumnWidth)
            : GridLength.Cells(0);
        _grid.ColumnDefinitions[IdeConstants.EditorCol] =
            GridLength.Star(editor, min: IdeConstants.MinEditorWidth);
        _grid.ColumnDefinitions[IdeConstants.SidePanelCol] = SidePanelVisible
            ? GridLength.Star(side, min: IdeConstants.MinSideColumnWidth)
            : GridLength.Cells(0);
    }

    /// <summary>
    /// Sets the bottom panel to <paramref name="rows"/> rows by weighting its
    /// Star track against the columns row.
    /// </summary>
    /// <remarks>
    /// Both rows are Star so that a splitter drag moves the boundary at all
    /// (Star over Fixed holds the fixed track by design). Expressing a row
    /// count therefore means choosing weights whose ratio yields that count
    /// over the working area, rather than writing the count directly.
    /// </remarks>
    private void SetOutputRowHeight(int rows)
    {
        if (_grid == null) return;

        int available = _grid.ActualHeight;
        if (available <= 0)
            available = Math.Max(1, _ctx.WindowSystem.DesktopDimensions.Height - NonBodyRows);

        int output = Math.Clamp(rows, IdeConstants.MinOutputHeight,
            Math.Max(IdeConstants.MinOutputHeight, available - IdeConstants.MinMainHeight));
        int main = Math.Max(IdeConstants.MinMainHeight, available - output);

        _grid.RowDefinitions[IdeConstants.ColumnsRow] =
            GridLength.Star(main, min: IdeConstants.MinMainHeight);
        _grid.RowDefinitions[IdeConstants.OutputRow] =
            GridLength.Star(output, min: IdeConstants.MinOutputHeight);
    }

    // Menu row + toolbar row + the window's own border and docked status bar.
    private const int NonBodyRows = 6;

    /// <summary>
    /// Restores both side-column widths together, so the columns never
    /// over-commit the grid.
    /// </summary>
    /// <remarks>
    /// Per-track Min is necessary but not sufficient, which is why this method
    /// survived the grid migration. Cells(26) + Cells(30) on a 50-column
    /// terminal still over-commits, and GridTrackSizer hands the Star editor
    /// track zero. The two side columns are therefore scaled TOGETHER against a
    /// budget, deliberately rather than capped independently: a panel wider than
    /// half the screen is a valid layout when the other column is narrow.
    /// </remarks>
    public void RestoreColumnWidths(int explorerWidth, int sidePanelWidth)
    {
        if (_grid == null) return;

        int desktopWidth = _ctx.WindowSystem.DesktopDimensions.Width;

        int explorer = ExplorerVisible && explorerWidth > 0 ? ClampSideColumnWidth(explorerWidth) : 0;
        int side = SidePanelVisible && sidePanelWidth > 0 ? ClampSideColumnWidth(sidePanelWidth) : 0;

        // Shrink the side columns proportionally if they leave no room for the
        // editor once the splitter gaps are accounted for.
        if (desktopWidth > 0)
        {
            int gaps = (ExplorerVisible ? 1 : 0) + (SidePanelVisible ? 1 : 0);
            int budget = desktopWidth - gaps - IdeConstants.MinEditorWidth;
            if (budget > 0 && explorer + side > budget)
            {
                double scale = (double)budget / (explorer + side);
                if (explorer > 0) explorer = Math.Max(IdeConstants.MinSideColumnWidth, (int)(explorer * scale));
                if (side > 0) side = Math.Max(IdeConstants.MinSideColumnWidth, (int)(side * scale));
            }
        }

        if (explorer > 0) SetExplorerColumnWidth(explorer);
        if (side > 0) SetSidePanelColumnWidth(side);

        // The editor column is Star, so it absorbs whatever is left; it is
        // never pinned to an explicit width here.
    }

    // Enforces only a lower bound. The upper bound is handled by
    // RestoreColumnWidths, which scales both side columns together so the
    // editor always keeps MinEditorWidth; capping each column independently
    // here would truncate legitimate widths (a panel wider than half the
    // screen is a valid layout when the other column is narrow).
    private int ClampSideColumnWidth(int width)
    {
        int desktopWidth = _ctx.WindowSystem.DesktopDimensions.Width;
        if (desktopWidth <= 0) return Math.Max(width, IdeConstants.MinSideColumnWidth);
        int max = Math.Max(IdeConstants.MinSideColumnWidth, desktopWidth - IdeConstants.MinEditorWidth);
        return Math.Clamp(width, IdeConstants.MinSideColumnWidth, max);
    }

    public void OnScreenResized(object? sender, SharpConsoleUI.Helpers.Size size)
    {
        var desktop = _ctx.WindowSystem.DesktopDimensions;
        _ctx.MainWindow?.SetSize(desktop.Width, desktop.Height);
        // Re-fit the fixed side columns: after a shrink they may no longer
        // leave a usable editor between them.
        RestoreColumnWidths(_explorerWidth, _sidePanelWidth);
    }

    public void ToggleExplorer()
    {
        ExplorerVisible = !ExplorerVisible;
        if (!ExplorerVisible)
            _explorerWidth = ExplorerColumnWidth > 0 ? ExplorerColumnWidth : _explorerWidth;
        ApplyColumnVisibility(IdeConstants.ExplorerCol, IdeConstants.ExplorerCol,
            ExplorerVisible, _explorerWidth);
    }

    // Collapsing a track to Cells(0) must drop the Min, or GridTrackSizer
    // clamps it straight back open. Removing the splitter too keeps a dead
    // handle out of the Tab order.
    private void ApplyColumnVisibility(int col, int splitterAfter, bool visible, int width)
    {
        if (_grid == null) return;
        ApplyColumnWeights();
        if (visible)
            _grid.AddColumnSplitterAfter(splitterAfter);
        else
            _grid.RemoveColumnSplitterAfter(splitterAfter);
    }

    public void ToggleOutput()
    {
        OutputVisible = !OutputVisible;
        if (_grid == null) return;

        if (!OutputVisible)
        {
            // Remember the arranged height before collapsing; a hidden panel
            // reports 0 and the saved value has to survive the round trip.
            int h = _ctx.OutputPanel.TabControl.ActualHeight;
            if (h > 0) _outputHeight = h;

            // Collapse with NO Min: GridTrackSizer clamps a track to its Min
            // (sizes[i] = Clamp(value, Min, Max)), so a zero track that kept
            // min: 4 would size straight back to 4 and never collapse.
            _grid.RowDefinitions[IdeConstants.OutputRow] = GridLength.Cells(0);
            _grid.RowDefinitions[IdeConstants.ColumnsRow] =
                GridLength.Star(1, min: IdeConstants.MinMainHeight);
            _grid.RemoveRowSplitterAfter(IdeConstants.ColumnsRow);
        }
        else
        {
            SetOutputRowHeight(_outputHeight);
            _grid.AddRowSplitterAfter(IdeConstants.ColumnsRow);
        }
    }

    /// <summary>
    /// Switches the explorer and side panel between occupying only the columns
    /// row and running the full height past the bottom panel.
    /// </summary>
    /// <remarks>
    /// Only the placements change. The splitter handles re-derive themselves
    /// from the spans: GridControl blocks a row boundary at any column covered
    /// by a row-spanning cell, so with the spans set the row splitter renders
    /// and drags only across the editor column.
    /// </remarks>
    public void SetExplorerFullHeight(bool fullHeight)
    {
        if (_grid == null) return;
        ExplorerFullHeight = fullHeight;
        ApplyExplorerFullHeight();
    }

    public void ToggleExplorerFullHeight() => SetExplorerFullHeight(!ExplorerFullHeight);

    private void ApplyExplorerFullHeight()
    {
        if (_grid == null) return;

        int span = ExplorerFullHeight ? 2 : 1;

        _grid.RemoveControl(_ctx.Explorer.Control);
        _grid.RemoveControl(_ctx.SidePanel.TabControl);
        _grid.RemoveControl(_ctx.OutputPanel.TabControl);

        _grid.Place(_ctx.Explorer.Control, IdeConstants.ColumnsRow, IdeConstants.ExplorerCol, rowSpan: span);
        _grid.Place(_ctx.SidePanel.TabControl, IdeConstants.ColumnsRow, IdeConstants.SidePanelCol, rowSpan: span);

        if (ExplorerFullHeight)
            _grid.Place(_ctx.OutputPanel.TabControl, IdeConstants.OutputRow, IdeConstants.EditorCol);
        else
            _grid.Place(_ctx.OutputPanel.TabControl, IdeConstants.OutputRow, IdeConstants.ExplorerCol,
                colSpan: IdeConstants.GridColumnCount);
    }

    public void ShowSourceControl()
    {
        if (!SidePanelVisible)
        {
            SidePanelVisible = true;
            ApplyColumnVisibility(IdeConstants.SidePanelCol, IdeConstants.EditorCol,
                true, _sidePanelWidth);
        }
        _ctx.SidePanel.SwitchToGitTab();
    }

    public void ToggleSidePanel()
    {
        SidePanelVisible = !SidePanelVisible;
        if (!SidePanelVisible)
            _sidePanelWidth = SidePanelColumnWidth > 0 ? SidePanelColumnWidth : _sidePanelWidth;
        ApplyColumnVisibility(IdeConstants.SidePanelCol, IdeConstants.EditorCol,
            SidePanelVisible, _sidePanelWidth);
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
            OnInstallDebugger: _debugCoord.HasDebugger ? null : () => InstallDebugger(),
            OnInstallLsp: _lspCoord.LspStarted ? null : () => InstallLsp()),
            () => { _aboutOpen = false; _aboutRefresh = null; });
    }

    /// <summary>
    /// Runs the csharp-ls install flow, then starts the server if it was not already running.
    /// </summary>
    public void InstallLsp()
    {
        _ = InstallLspModal.ShowAsync(_ctx.WindowSystem).ContinueWith(async t =>
        {
            if (!t.Result) return;

            // A freshly installed global tool lands in ~/.dotnet/tools, which is not on this
            // process's PATH, so detection may still miss it. Try anyway: when it works the
            // server comes up in this session, and when it does not the next run finds it.
            await _lspCoord.ReDetectLspAsync(
                _ctx.ProjectService.RootPath, _ctx.Config.Lsp, _ctx.WindowSystem);

            _ctx.PendingUiActions.Enqueue(() =>
            {
                UpdateDashboard();
                _aboutRefresh?.Invoke();
            });
        }, TaskScheduler.Default);
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
        if (_grid == null || height <= 0) return;
        _outputHeight = Math.Max(IdeConstants.MinOutputHeight, height);
        if (OutputVisible)
            SetOutputRowHeight(_outputHeight);
    }

    public void ForceRebuildLayout()
    {
        _ctx.MainWindow?.ForceRebuildLayout();
    }
}
