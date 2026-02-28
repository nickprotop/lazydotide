using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

internal class LayoutController
{
    private readonly ConsoleWindowSystem _ws;
    private readonly ProjectService _projectService;
    private readonly EditorManager _editorManager;
    private readonly SidePanel _sidePanel;
    private readonly LspCoordinator _lspCoord;
    private readonly IdeConfig _config;

    // Layout controls (set via SetControls)
    private Window? _mainWindow;
    private Window? _outputWindow;
    private ColumnContainer? _explorerCol;
    private SplitterControl? _explorerSplitter;
    private ColumnContainer? _sidePanelCol;
    private SplitterControl? _sidePanelSplitter;
    private HorizontalGridControl? _mainContent;
    private MarkupControl? _dashboard;

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
        ConsoleWindowSystem ws,
        ProjectService projectService,
        EditorManager editorManager,
        SidePanel sidePanel,
        LspCoordinator lspCoord,
        IdeConfig config)
    {
        _ws = ws;
        _projectService = projectService;
        _editorManager = editorManager;
        _sidePanel = sidePanel;
        _lspCoord = lspCoord;
        _config = config;
    }

    public void SetControls(
        Window mainWindow,
        Window outputWindow,
        ColumnContainer? explorerCol,
        SplitterControl? explorerSplitter,
        ColumnContainer? sidePanelCol,
        SplitterControl? sidePanelSplitter,
        HorizontalGridControl? mainContent,
        MarkupControl? dashboard)
    {
        _mainWindow = mainWindow;
        _outputWindow = outputWindow;
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
        var desktop = _ws.DesktopDimensions;
        ResizeCoupling = true;
        try
        {
            if (OutputVisible)
            {
                int mainH = (int)(desktop.Height * SplitRatio);
                int outH = desktop.Height - mainH;
                _mainWindow?.SetSize(desktop.Width, mainH);
                _outputWindow?.SetSize(desktop.Width, outH);
                _outputWindow?.SetPosition(new Point(0, mainH));
            }
            else
            {
                _mainWindow?.SetSize(desktop.Width, desktop.Height);
            }
        }
        finally { ResizeCoupling = false; }
    }

    public void OnMainWindowResized(object? sender, EventArgs e)
    {
        if (ResizeCoupling || !OutputVisible || _mainWindow == null || _outputWindow == null) return;
        ResizeCoupling = true;
        try
        {
            var desktop = _ws.DesktopDimensions;
            int newDivider = Math.Clamp(_mainWindow.Height,
                MinMainHeight,
                desktop.Height - MinOutputHeight);
            int newOutH = desktop.Height - newDivider;
            _mainWindow.SetSize(desktop.Width, newDivider);
            _outputWindow.SetPosition(new Point(0, newDivider));
            _outputWindow.SetSize(desktop.Width, newOutH);
            SplitRatio = newDivider / (double)desktop.Height;
        }
        finally { ResizeCoupling = false; }
    }

    public void OnOutputWindowResized(object? sender, EventArgs e)
    {
        if (ResizeCoupling || !OutputVisible || _mainWindow == null || _outputWindow == null) return;
        ResizeCoupling = true;
        try
        {
            var desktop = _ws.DesktopDimensions;
            int newDivider = Math.Clamp(_outputWindow.Top,
                MinMainHeight,
                desktop.Height - MinOutputHeight);
            _mainWindow.SetSize(desktop.Width, newDivider);
            _outputWindow.SetPosition(new Point(0, newDivider));
            _outputWindow.SetSize(desktop.Width, desktop.Height - newDivider);
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
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
    }

    public void ToggleOutput()
    {
        OutputVisible = !OutputVisible;
        var desktop = _ws.DesktopDimensions;
        ResizeCoupling = true;
        try
        {
            if (OutputVisible)
            {
                int mainH = (int)(desktop.Height * SplitRatio);
                int outH = desktop.Height - mainH;
                _mainWindow?.SetSize(desktop.Width, mainH);
                _outputWindow?.SetSize(desktop.Width, outH);
                _outputWindow?.SetPosition(new Point(0, mainH));
            }
            else
            {
                _mainWindow?.SetSize(desktop.Width, desktop.Height);
                _outputWindow?.SetPosition(new Point(0, desktop.Height + 100));
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
            _mainWindow?.ForceRebuildLayout();
            _mainWindow?.Invalidate(true);
        }
        _sidePanel.SwitchToGitTab();
    }

    public void ToggleSidePanel()
    {
        SidePanelVisible = !SidePanelVisible;
        if (_sidePanelCol != null)
            _sidePanelCol.Visible = SidePanelVisible;
        if (_sidePanelSplitter != null)
            _sidePanelSplitter.Visible = SidePanelVisible;
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
        if (SidePanelVisible)
        {
            _sidePanel.SwitchToSymbolsTab();
            _lspCoord.RefreshSymbolsForFile(_editorManager.CurrentFilePath);
        }
    }

    public void FocusSymbolsTab()
    {
        if (!SidePanelVisible)
            ToggleSidePanel();
        _sidePanel.SwitchToSymbolsTab();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void OpenSidePanelShell()
    {
        if (!(IdeConstants.IsDesktopOs)) return;
        if (!SidePanelVisible)
            ToggleSidePanel();

        var terminal = Controls.Terminal()
            .WithWorkingDirectory(_projectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment = VerticalAlignment.Fill;

        _sidePanelShellCount++;
        string tabName = _sidePanelShellCount == 1 ? "Shell" : $"Shell {_sidePanelShellCount}";
        _sidePanel.TabControl.AddTab(tabName, terminal, isClosable: true);
        _sidePanel.TabControl.ActiveTabIndex = _sidePanel.TabControl.TabCount - 1;
        InvalidateSidePanel();
        _mainWindow?.FocusControl(terminal);
    }

    public void InvalidateSidePanel()
    {
        _mainContent?.Invalidate();
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
    }

    public void SetWrapMode(WrapMode mode)
    {
        _editorManager.WrapMode = mode;
    }

    public void ShowFindReplace()
    {
        if (_findReplaceOpen) return;
        _findReplaceOpen = true;
        _ = FindReplaceDialog.ShowAsync(_ws, _editorManager)
            .ContinueWith(_ => _findReplaceOpen = false);
    }

    public void ShowAbout()
    {
        if (_aboutOpen) return;
        _aboutOpen = true;
        _aboutRefresh = AboutDialog.Show(_ws, () => new AboutInfo(
            LspStarted: _lspCoord.LspStarted,
            LspDetectionDone: _lspCoord.LspDetectionDone,
            DetectedLspExe: _lspCoord.DetectedLspExe,
            Tools: _config.Tools,
            ProjectPath: _projectService.RootPath),
            () => { _aboutOpen = false; _aboutRefresh = null; });
    }

    public void UpdateDashboard()
    {
        _dashboard?.SetContent(GetDashboardLines());
    }

    private List<string> GetDashboardLines()
    {
        var projectName = new DirectoryInfo(_projectService.RootPath).Name;
        var rootPath = _projectService.RootPath;

        List<string> lspLines;
        if (!_lspCoord.LspDetectionDone)
            lspLines = new List<string> { "[dim]  LSP      ○ detecting…[/]" };
        else if (_lspCoord.LspStarted)
            lspLines = new List<string> { $"[dim]  LSP      [/][green]● {Markup.Escape(_lspCoord.DetectedLspExe!)}[/]" };
        else if (_lspCoord.DetectedLspExe != null)
            lspLines = new List<string> { $"[dim]  LSP      ○ {Markup.Escape(_lspCoord.DetectedLspExe)} (failed to start)[/]" };
        else
            lspLines = new List<string>
            {
                "[dim]  LSP      ○ not found[/]",
                "[dim]           Enables: IntelliSense · Go to Definition · References[/]",
                "[dim]                    Rename · Code Actions · Signature Help[/]",
                "[yellow]           Install: [/][italic]dotnet tool install -g csharp-ls[/]",
                "[dim]           Alt:     [/][dim italic]OmniSharp  (omnisharp.net)[/]",
                $"[dim]           Config:  [/][dim italic]{Markup.Escape(ConfigService.GetConfigPath())}[/]",
            };

        string toolsLine = _config.Tools.Count == 0
            ? "[dim]  Tools    0 loaded  →  Tools › Edit Config[/]"
            : $"[dim]  Tools    [/][green]{_config.Tools.Count} loaded[/][dim]  ({string.Join(", ", _config.Tools.Select(t => t.Name))})[/]";

        var lines = new List<string>
        {
            "",
            $"[bold]  lazydotide[/]  [dim]{Markup.Escape(projectName)}[/]",
            $"[dim]  {Markup.Escape(rootPath)}[/]",
            "",
            "[dim]  ────────────────────────────[/]",
        };
        lines.AddRange(lspLines);
        lines.Add(toolsLine);
        lines.AddRange(new[]
        {
            "",
            "[dim]  ────────────────────────────[/]",
            "[dim]  F5  Run       F6  Build[/]",
            "[dim]  F7  Test      F8  Shell / Shell Tab[/]",
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
        var desktop = _ws.DesktopDimensions;
        int mainH = (int)(desktop.Height * SplitRatio);
        int outH = desktop.Height - mainH;
        ResizeCoupling = true;
        try
        {
            _mainWindow?.SetSize(desktop.Width, mainH);
            _outputWindow?.SetSize(desktop.Width, outH);
            _outputWindow?.SetPosition(new Point(0, mainH));
        }
        finally { ResizeCoupling = false; }
    }

    public void ForceRebuildLayout()
    {
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
    }
}
