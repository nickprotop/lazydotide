using System.Collections.Concurrent;
using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public class IdeApp : IDisposable
{
    private readonly ConsoleWindowSystem _ws;
    private readonly ProjectService _projectService;
    private readonly BuildService _buildService;
    private readonly GitService _gitService;
    private LspClient? _lsp;

    private Window? _mainWindow;
    private Window? _outputWindow;
    private ExplorerPanel? _explorer;
    private EditorManager? _editorManager;
    private OutputPanel? _outputPanel;
    private FileMiddlewarePipeline? _pipeline;

    // Status bar
    private MarkupControl? _statusLeft;   // git + error combined
    private MarkupControl? _cursorStatus;
    private MarkupControl? _syntaxStatus;
    private string _gitMarkup   = "[dim] git: --[/]";
    private string _errorMarkup = "";
    private MarkupControl? _dashboard;

    // Panel visibility state
    private ColumnContainer? _explorerCol;
    private SplitterControl? _explorerSplitter;
    private bool _explorerVisible = true;
    private bool _outputVisible = true;

    // System bottom status bar
    private readonly IdeStatusBar _bottomBar = new();

    // Thread-safe queues for streaming build/test output
    private readonly ConcurrentQueue<string> _buildLines = new();
    private readonly ConcurrentQueue<string> _testLines = new();
    private readonly ConcurrentQueue<Action> _pendingUiActions = new();

    // File system watcher
    private FileWatcher? _fileWatcher;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Split layout state
    private double _splitRatio = 0.68;   // mainH / totalHeight
    private bool _resizeCoupling = false; // re-entrancy guard for coupled resize

    // Tool tab indices
    private int _lazyNuGetTabIndex = -1;
    private int _shellTabIndex = -1;
    private readonly Dictionary<int, int> _toolTabIndices = new(); // config tool index → editor tab index

    // Config
    private IdeConfig _config = new();

    // Command registry
    private readonly CommandRegistry _commandRegistry = new();
    private bool _commandPaletteOpen;
    private bool _aboutOpen;
    private Action? _aboutRefresh;

    // Dashboard LSP state (set in PostInitAsync)
    private string? _detectedLspExe;
    private bool _lspStarted;
    private bool _lspDetectionDone;

    // Navigation history for Go to Definition / Navigate Back
    private readonly Stack<(string FilePath, int Line, int Col)> _navHistory = new();

    // Side panel (symbols browser)
    private SidePanel? _sidePanel;
    private ColumnContainer? _sidePanelCol;
    private SplitterControl? _sidePanelSplitter;
    private HorizontalGridControl? _mainContent;
    private bool _sidePanelVisible;
    private Timer? _symbolRefreshDebounce;

    // LSP portal overlays (rendered inside _mainWindow, not as separate windows)
    private LspCompletionPortalContent? _completionPortal;
    private LayoutNode? _completionPortalNode;
    private LspTooltipPortalContent? _tooltipPortal;
    private LayoutNode? _tooltipPortalNode;
    private LspLocationListPortalContent? _locationPortal;
    private LayoutNode? _locationPortalNode;

    // Context menu portal
    private ContextMenuPortal? _contextMenuPortal;
    private LayoutNode? _contextMenuPortalNode;
    private IWindowControl? _contextMenuOwner;

    // Completion filter tracking — column at which completion was triggered (1-indexed)
    private int _completionTriggerColumn;
    private int _completionTriggerLine;

    // Debounce timer for dot-triggered auto-completion
    private Timer? _dotTriggerDebounce;
    private Timer? _tooltipAutoDismiss;
    private int _tooltipAutoDismissGeneration;

    public IdeApp(string projectPath)
    {
        _ws = new ConsoleWindowSystem(
            new NetConsoleDriver(RenderMode.Buffer),
            options: new ConsoleWindowSystemOptions(
                StatusBarOptions: new StatusBarOptions(ShowTaskBar: false)));
        _projectService = new ProjectService(projectPath);
        _buildService = new BuildService();
        _gitService = new GitService();

        InitBottomStatus();  // Must be before CreateLayout so DesktopDimensions accounts for the bar
        CreateLayout();
        InitializeCommands();

        // Async post-init: git status + optional LSP
        _ = PostInitAsync(projectPath);
    }

    public void Run() => _ws.Run();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _dotTriggerDebounce?.Dispose();
        _symbolRefreshDebounce?.Dispose();
        _fileWatcher?.Dispose();
        DismissContextMenu();
        DismissCompletionPortal();
        DismissTooltipPortal();
        DismissLocationPortal();
        _ws.ConsoleDriver.KeyPressed   -= OnGlobalDriverKeyPressed;
        _ws.ConsoleDriver.ScreenResized -= OnScreenResized;
        _ = _lsp?.DisposeAsync().AsTask();
    }

    // ──────────────────────────────────────────────────────────────
    // Layout
    // ──────────────────────────────────────────────────────────────

    private void CreateLayout()
    {
        ConfigService.EnsureDefaultConfig();
        _config = ConfigService.Load();

        var desktop = _ws.DesktopDimensions;
        int mainH = (int)(desktop.Height * _splitRatio);
        int outH = desktop.Height - mainH;

        _explorer = new ExplorerPanel(_ws, _projectService);

        _pipeline = new FileMiddlewarePipeline();
        _pipeline.Register(new CSharpFileMiddleware());
        _pipeline.Register(new MarkdownFileMiddleware());
        _pipeline.Register(new JsonFileMiddleware());
        _pipeline.Register(new XmlFileMiddleware());
        _pipeline.Register(new YamlFileMiddleware());
        _pipeline.Register(new DockerfileMiddleware());
        _pipeline.Register(new SlnFileMiddleware());
        _pipeline.Register(new CssFileMiddleware());
        _pipeline.Register(new HtmlFileMiddleware());
        _pipeline.Register(new JsFileMiddleware());
        _pipeline.Register(new RazorFileMiddleware());
        _pipeline.Register(new DefaultFileMiddleware());

        _editorManager = new EditorManager(_ws, _pipeline);
        _outputPanel = new OutputPanel(_ws);
        _sidePanel = new SidePanel();

        BuildMainWindow(desktop.Width, mainH);
        BuildOutputWindow(desktop.Width, outH, mainH);

        _ws.AddWindow(_mainWindow!);
        _ws.AddWindow(_outputWindow!);

        _ws.ConsoleDriver.ScreenResized += OnScreenResized;

        _fileWatcher = new FileWatcher();
        _fileWatcher.FileChanged      += (_, path) => _pendingUiActions.Enqueue(() => HandleExternalFileChanged(path));
        _fileWatcher.StructureChanged += (_, _)    => _pendingUiActions.Enqueue(() => _ = RefreshExplorerAndGitAsync());
        _fileWatcher.Watch(_projectService.RootPath);

        // Open the shell tab at startup so it's ready immediately
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            _outputPanel!.LaunchShell(_projectService.RootPath);

        WireEvents();
    }

    private void BuildMainWindow(int width, int height)
    {
        _mainWindow = new WindowBuilder(_ws)
            .HideTitle()
            .WithBorderStyle(BorderStyle.Single)
            .HideTitleButtons()
            .WithSize(width, height)
            .AtPosition(0, 0)
            .WithResizeDirections(ResizeBorderDirections.Bottom)
            .Movable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithAsyncWindowThread(MainWindowThreadAsync)
            .Build();

        AddMenuBar();
        _mainWindow.AddControl(new RuleControl { StickyPosition = StickyPosition.Top });
        AddToolbar();
        _mainWindow.AddControl(new RuleControl { StickyPosition = StickyPosition.Top });
        AddMainContentArea();
        _mainWindow.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        AddStatusBar();
    }

    private void AddMenuBar()
    {
        var menu = Controls.Menu()
            .Horizontal()
            .Sticky()
            .AddItem("File", m => m
                .AddItem("Open Folder...", () => _ = OpenFolderAsync())
                .AddSeparator()
                .AddItem("New File", "Ctrl+N", () => { var dir = _explorer?.GetSelectedPath(); if (dir != null) { if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir); if (dir != null) _ = HandleNewFileAsync(dir); } })
                .AddItem("New Folder", "Ctrl+Shift+N", () => { var dir = _explorer?.GetSelectedPath(); if (dir != null) { if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir); if (dir != null) _ = HandleNewFolderAsync(dir); } })
                .AddItem("Rename", "F2", () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleRenameAsync(p); })
                .AddItem("Delete", "Del", () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleDeleteAsync(p); })
                .AddSeparator()
                .AddItem("Save", "Ctrl+S", () => _editorManager?.SaveCurrent())
                .AddItem("Close Tab", "Ctrl+W", () => CloseCurrentTab())
                .AddSeparator()
                .AddItem("Refresh Explorer", "F5", () => _ = RefreshExplorerAndGitAsync())
                .AddSeparator()
                .AddItem("Exit", "Alt+F4", () => _ws.Shutdown(0)))
            .AddItem("Edit", m =>
            {
                m.AddItem("Word Wrap", () => SetWrapMode(WrapMode.WrapWords))
                 .AddItem("Wrap (character)", () => SetWrapMode(WrapMode.Wrap))
                 .AddItem("No Wrap", () => SetWrapMode(WrapMode.NoWrap))
                 .AddSeparator()
                 .AddItem("Find...",    "Ctrl+F", () => ShowFindReplace())
                 .AddItem("Replace...", "Ctrl+H", () => ShowFindReplace())
                 .AddSeparator()
                 .AddItem("Syntax Highlighter", sub =>
                 {
                     foreach (var (name, highlighter) in _pipeline!.GetAvailableHighlighters())
                     {
                         var n = name;
                         var h = highlighter;
                         sub.AddItem(n, () => _editorManager?.SetSyntaxHighlighter(n, h));
                     }
                 });
            })
            .AddItem("Build", m => m
                .AddItem("Build", "F6", () => _ = BuildProjectAsync())
                .AddItem("Test", "F7", () => _ = TestProjectAsync())
                .AddSeparator()
                .AddItem("Clean", () => _ = CleanProjectAsync())
                .AddItem("Stop", "F4", () => _buildService.Cancel()))
            .AddItem("Run", m => m
                .AddItem("Run", "F5", () => RunProject())
                .AddItem("Stop", "F4", () => _buildService.Cancel()))
            .AddItem("View", m => m
                .AddItem("Toggle Explorer", "Ctrl+B", () => ToggleExplorer())
                .AddItem("Toggle Output Panel", "Ctrl+J", () => ToggleOutput())
                .AddItem("Toggle Side Panel", "Alt+;", () => ToggleSidePanel()))
            .AddItem("Git", m => m
                .AddItem("Source Control", "Alt+G", ShowSourceControl)
                .AddSeparator()
                .AddItem("Refresh Status", () => _ = RefreshGitStatusAsync())
                .AddSeparator()
                .AddItem("Stage All", () => _ = GitStageAllAsync())
                .AddItem("Unstage All", () => _ = GitUnstageAllAsync())
                .AddSeparator()
                .AddItem("Commit\u2026", "Ctrl+Enter", () => _ = GitCommitAsync())
                .AddSeparator()
                .AddItem("Pull", () => _ = GitCommandAsync("pull"))
                .AddItem("Push", () => _ = GitCommandAsync("push"))
                .AddSeparator()
                .AddItem("Stash\u2026", () => _ = GitStashAsync())
                .AddItem("Stash Pop", () => _ = GitStashPopAsync())
                .AddSeparator()
                .AddItem("Switch Branch\u2026", () => _ = GitSwitchBranchAsync())
                .AddItem("New Branch\u2026", () => _ = GitNewBranchAsync())
                .AddSeparator()
                .AddItem("Log", () => _ = GitShowLogAsync())
                .AddItem("Diff All", () => _ = GitShowDiffAllAsync())
                .AddSeparator()
                .AddItem("Discard All Changes\u2026", () => _ = GitDiscardAllAsync()))
            .AddItem("Tools", m =>
            {
                m.AddItem("Command Palette",  "Ctrl+P",         () => ShowCommandPalette())
                 .AddSeparator()
                 .AddItem("Go to Definition",    "F12",            () => _ = ShowGoToDefinitionAsync())
                 .AddItem("Go to Implementation","Ctrl+F12",       () => _ = ShowGoToImplementationAsync())
                 .AddItem("Find All References", "Shift+F12",      () => _ = ShowFindReferencesAsync())
                 .AddItem("Navigate Back",       "Alt+Left",       () => NavigateBack())
                 .AddSeparator()
                 .AddItem("Rename Symbol",    "Ctrl+F2",         () => _ = ShowRenameAsync())
                 .AddItem("Code Actions",     "Ctrl+.",          () => _ = ShowCodeActionsAsync())
                 .AddItem("Focus Symbols",    "Alt+O",    () => FocusSymbolsTab())
                 .AddSeparator()
                 .AddItem("Signature Help",   "F2",              () => _ = ShowSignatureHelpAsync())
                 .AddItem("Format Document",  "Alt+Shift+F",     () => _ = FormatDocumentAsync())
                 .AddItem("Reload from Disk", "Alt+Shift+R",     () => ReloadCurrentFromDisk())
                 .AddSeparator()
                 .AddItem("Add NuGet Package", () => ShowNuGetDialog())
                 .AddSeparator()
                 .AddItem("Shell",      "F8", () => OpenShell())
                 .AddItem("Shell Tab",  "",   () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenShellTab(); })
                 .AddItem("Side Shell", "Shift+F8", () => OpenSidePanelShell())
                 .AddItem("LazyNuGet",  "F9", () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenLazyNuGetTab(); });

                if (_config.Tools.Count > 0)
                {
                    m.AddSeparator();
                    for (int i = 0; i < _config.Tools.Count; i++)
                    {
                        int idx = i;  // capture for lambda
                        m.AddItem(_config.Tools[i].Name, "", () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenConfigToolTab(idx); });
                    }
                }

                m.AddSeparator();
                m.AddItem("Edit Config", "", () => OpenConfigFile());
            })
            .AddItem("Help", m => m
                .AddItem("About lazydotide\u2026", () => ShowAbout()))
            .Build();

        menu.StickyPosition = StickyPosition.Top;
        _mainWindow!.AddControl(menu);
    }

    private void AddToolbar()
    {
        var toolbar = Controls.Toolbar()
            .AddButton("Run F5", (_, _) => RunProject())
            .AddButton("Build F6", (_, _) => _ = BuildProjectAsync())
            .AddButton("Test F7", (_, _) => _ = TestProjectAsync())
            .AddButton("Stop F4", (_, _) => _buildService.Cancel())
            .AddButton("Shell F8", (_, _) => OpenShell())
            .AddButton("Shell Tab", (_, _) => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenShellTab(); })
            .AddButton("LazyNuGet F9", (_, _) => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenLazyNuGetTab(); })
            .AddButton("Explorer", (_, _) => ToggleExplorer())
            .AddButton("Output", (_, _) => ToggleOutput())
            .AddButton("Side Panel", (_, _) => ToggleSidePanel())
            .StickyTop()
            .Build();

        _mainWindow!.AddControl(toolbar);
    }

    private void AddMainContentArea()
    {
        _mainContent = new HorizontalGridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        var mainContent = _mainContent;

        _explorerCol = new ColumnContainer(mainContent)
        {
            Width = 26,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _explorerCol.AddContent(_explorer!.Control);
        mainContent.AddColumn(_explorerCol);

        var editorCol = new ColumnContainer(mainContent)
        {
            VerticalAlignment = VerticalAlignment.Fill
        };

        _dashboard = new MarkupControl(GetDashboardLines())
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        editorCol.AddContent(_dashboard);
        editorCol.AddContent(_editorManager!.TabControl);
        _editorManager.TabControl.Visible = false;

        mainContent.AddColumn(editorCol);

        _sidePanelCol = new ColumnContainer(mainContent)
        {
            Width = 30,
            VerticalAlignment = VerticalAlignment.Fill,
            Visible = false
        };
        _sidePanelCol.AddContent(_sidePanel!.TabControl);
        mainContent.AddColumn(_sidePanelCol);

        _explorerSplitter = new SplitterControl();
        mainContent.AddSplitter(0, _explorerSplitter);

        _sidePanelSplitter = new SplitterControl { Visible = false };
        mainContent.AddSplitter(1, _sidePanelSplitter);

        _sidePanel.SymbolActivated += (_, entry) => NavigateToLocation(entry);
        _sidePanel.GitStageRequested += (_, path) => _ = GitStageFileAsync(path);
        _sidePanel.GitUnstageRequested += (_, path) => _ = GitUnstageFileAsync(path);

        // Toolbar actions
        _sidePanel.GitCommitRequested += (_, _) => _ = GitCommitAsync();
        _sidePanel.GitRefreshRequested += (_, _) => _ = RefreshExplorerAndGitAsync();
        _sidePanel.GitStageAllRequested += (_, _) => _ = GitStageAllAsync();
        _sidePanel.GitUnstageAllRequested += (_, _) => _ = GitUnstageAllAsync();

        // File interactions
        _sidePanel.GitDiffRequested += (_, path) => _ = GitShowDiffAsync(path);
        _sidePanel.GitOpenFileRequested += (_, path) => _editorManager?.OpenFile(path);

        // Context menu
        _sidePanel.GitContextMenuRequested += OnGitPanelContextMenu;

        // Log entry
        _sidePanel.GitLogEntryActivated += (_, entry) => _ = ShowCommitDetailAsync(entry);

        // More menu
        _sidePanel.GitMoreMenuRequested += (_, _) => ShowGitMoreMenu();

        _mainWindow!.AddControl(mainContent);
    }

    private void AddStatusBar()
    {
        var statusBar = new HorizontalGridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            StickyPosition = StickyPosition.Bottom
        };

        // Left fill: git info + | + error count rendered as one MarkupControl
        var leftCol = new ColumnContainer(statusBar); // fill
        _statusLeft = new MarkupControl(new List<string> { "[dim] git: --[/]" });
        leftCol.AddContent(_statusLeft);
        statusBar.AddColumn(leftCol);

        // Middle fixed: syntax highlighter name
        var syntaxCol = new ColumnContainer(statusBar) { Width = 14 };
        _syntaxStatus = new MarkupControl(new List<string> { "[dim]Plain Text[/]" })
        {
            HorizontalAlignment = HorizontalAlignment.Right
        };
        syntaxCol.AddContent(_syntaxStatus);
        statusBar.AddColumn(syntaxCol);

        // Right fixed: cursor position
        var cursorCol = new ColumnContainer(statusBar) { Width = 22 };
        _cursorStatus = new MarkupControl(new List<string> { "Ln 1 Col 1 | UTF-8" })
        {
            HorizontalAlignment = HorizontalAlignment.Right
        };
        cursorCol.AddContent(_cursorStatus);
        statusBar.AddColumn(cursorCol);

        _mainWindow!.AddControl(statusBar);
    }

    private void UpdateInlineStatus()
    {
        var bar = new IdeStatusBar();
        bar.AddSegment(_gitMarkup, " ");
        if (!string.IsNullOrEmpty(_errorMarkup))
            bar.AddSegment("[dim] | [/]", " | ")
               .AddSegment(_errorMarkup, " ");
        _statusLeft?.SetContent(new List<string> { bar.Render() });
    }

    private void BuildOutputWindow(int width, int height, int topOffset)
    {
        _outputWindow = new WindowBuilder(_ws)
            .HideTitle()
            .WithBorderStyle(BorderStyle.Single)
            .HideTitleButtons()
            .Closable(false)
            .WithSize(width, height)
            .AtPosition(0, topOffset)
            .WithResizeDirections(ResizeBorderDirections.Top)
            .Movable(false)
            .Minimizable(false)
            .Maximizable(false)
            .Build();

        _outputWindow.AddControl(_outputPanel!.TabControl);
    }

    // ──────────────────────────────────────────────────────────────
    // Event wiring
    // ──────────────────────────────────────────────────────────────

    private void WireEvents()
    {
        _explorer!.FileOpenRequested += (_, path) => _editorManager?.OpenFile(path);

        _explorer.NewFileRequested += (_, dir) => _ = HandleNewFileAsync(dir);
        _explorer.NewFolderRequested += (_, dir) => _ = HandleNewFolderAsync(dir);
        _explorer.RenameRequested += (_, path) => _ = HandleRenameAsync(path);
        _explorer.DeleteRequested += (_, path) => _ = HandleDeleteAsync(path);
        _explorer.RefreshRequested += (_, _) => _ = RefreshExplorerAndGitAsync();
        _explorer.ContextMenuRequested += OnExplorerContextMenu;

        _editorManager!.TabContextMenuRequested += OnTabContextMenu;
        _editorManager!.EditorContextMenuRequested += OnEditorContextMenu;

        _editorManager!.TabCloseRequested += (_, index) =>
        {
            if (_editorManager.IsTabDirty(index))
            {
                var fileName = _editorManager.GetTabFilePath(index) is { } p
                    ? Path.GetFileName(p)
                    : "Untitled";
                _ = ConfirmSaveDialog.ShowAsync(_ws, fileName).ContinueWith(t =>
                {
                    if (t.Result == DialogResult.Cancel) return;
                    if (t.Result == DialogResult.Save)
                    {
                        _editorManager.TabControl.ActiveTabIndex = index;
                        _editorManager.SaveCurrent();
                    }
                    _editorManager.CloseTabAt(index);
                });
            }
            else
            {
                _editorManager.CloseTabAt(index);
            }
        };

        _editorManager!.CursorChanged += (_, pos) =>
        {
            _cursorStatus?.SetContent(new List<string>
            {
                $"Ln {pos.Line} Col {pos.Column} | UTF-8"
            });
        };

        _editorManager.SyntaxChanged += (_, name) =>
        {
            _syntaxStatus?.SetContent(new List<string> { $"[dim]{Markup.Escape(name)}[/]" });
        };

        _outputPanel!.DiagnosticNavigateRequested += (_, diag) =>
        {
            _editorManager?.OpenFile(diag.FilePath);
            _editorManager?.GoToLine(diag.Line);
            // Activate the main window and focus the editor so it receives keyboard input
            if (_mainWindow != null)
            {
                _ws.SetActiveWindow(_mainWindow);
                var editor = _editorManager?.CurrentEditor;
                if (editor != null)
                    _mainWindow.FocusControl(editor);
            }
        };

        _editorManager!.OpenFilesStateChanged += (_, _) =>
        {
            bool hasFiles = _editorManager.HasOpenFiles;
            _editorManager.TabControl.Visible = hasFiles;
            _dashboard!.Visible = !hasFiles;
        };

        _editorManager.ValidationWarnings += (_, warnings) =>
            _outputPanel!.ShowWarnings(warnings);

        _editorManager.DocumentOpened  += (_, a) =>
        {
            _ = _lsp?.DidOpenAsync(a.FilePath, a.Content);
            _ = RefreshGitDiffMarkersForFileAsync(a.FilePath);
        };
        _editorManager.DocumentChanged += (_, a) =>
        {
            _ = _lsp?.DidChangeAsync(a.FilePath, a.Content);
            TryScheduleDotCompletion(a.FilePath, a.Content);
            ScheduleSymbolRefresh(a.FilePath);
        };
        _editorManager.DocumentSaved   += (_, p) => _ = _lsp?.DidSaveAsync(p);
        _editorManager.DocumentSaved   += (_, savedPath) => _fileWatcher?.SuppressNext(savedPath);
        _editorManager.DocumentSaved   += (_, _) => _ = RefreshGitFileStatusesAsync();
        _editorManager.DocumentSaved   += (_, savedPath) => _ = RefreshGitDiffMarkersForFileAsync(savedPath);
        _editorManager.DocumentSaved   += (_, savedPath) =>
        {
            if (string.Equals(savedPath, ConfigService.GetConfigPath(), StringComparison.OrdinalIgnoreCase))
            {
                _config = ConfigService.Load();
                UpdateDashboard();
                _ws.NotificationStateService.ShowNotification(
                    "Config Reloaded",
                    "Config loaded. New/removed tools will appear after restart.",
                    SharpConsoleUI.Core.NotificationSeverity.Info);
            }
        };
        _editorManager.DocumentClosed  += (_, p) =>
        {
            DismissCompletionPortal();
            DismissTooltipPortal();
            DismissLocationPortal();
            _ = _lsp?.DidCloseAsync(p);
            // Clear diagnostics for the closed file
            _outputPanel?.PopulateLspDiagnostics(new List<BuildDiagnostic>());
            UpdateErrorCount(new List<BuildDiagnostic>());
        };

        _editorManager.ActiveFileChanged += (_, filePath) =>
        {
            DismissCompletionPortal();
            DismissTooltipPortal();
            DismissLocationPortal();
            if (filePath != null && filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Re-send content so LSP re-publishes fresh diagnostics for this file
                var editor = _editorManager.CurrentEditor;
                if (editor != null)
                    _ = _lsp?.DidChangeAsync(filePath, editor.Content);
            }
            else
            {
                // Non-C# tab or no file — clear problems
                _outputPanel?.PopulateLspDiagnostics(new List<BuildDiagnostic>());
                UpdateErrorCount(new List<BuildDiagnostic>());
            }
            RefreshSymbolsForFile(filePath);
        };

        _mainWindow!.PreviewKeyPressed += OnMainWindowPreviewKey;
        _mainWindow!.KeyPressed        += OnMainWindowKeyPressed;
        _mainWindow!.OnResize          += OnMainWindowResized;
        _outputWindow!.OnResize        += OnOutputWindowResized;
        _ws.ConsoleDriver.KeyPressed   += OnGlobalDriverKeyPressed;
    }

    private void InitBottomStatus()
    {
        _bottomBar
            .AddHint("F5",     "Run",   RunProject)
            .AddHint("F6",     "Build", () => _ = BuildProjectAsync())
            .AddHint("F7",     "Test",  () => _ = TestProjectAsync())
            .AddHint("F8",     "Shell", OpenShell)
            .AddSegment("[dim]| [/]", "| ")
            .AddHint("Ctrl+S", "Save",  () => _editorManager?.SaveCurrent())
            .AddHint("Ctrl+W", "Close", CloseCurrentTab);

        _ws.StatusBarStateService.BottomStatus = _bottomBar.Render();
        _ws.StatusBarStateService.BottomStatusClickHandler = x => _bottomBar.HandleClick(x);
    }

    private void OnScreenResized(object? sender, SharpConsoleUI.Helpers.Size size)
    {
        var desktop = _ws.DesktopDimensions;
        _resizeCoupling = true;
        try
        {
            if (_outputVisible)
            {
                int mainH = (int)(desktop.Height * _splitRatio);
                int outH  = desktop.Height - mainH;
                _mainWindow?.SetSize(desktop.Width, mainH);
                _outputWindow?.SetSize(desktop.Width, outH);
                _outputWindow?.SetPosition(new Point(0, mainH));
            }
            else
            {
                _mainWindow?.SetSize(desktop.Width, desktop.Height);
            }
        }
        finally { _resizeCoupling = false; }
    }

    // Minimum usable height for each panel (rows)
    private const int MinMainHeight   = 8;
    private const int MinOutputHeight = 4;

    private void OnMainWindowResized(object? sender, EventArgs e)
    {
        if (_resizeCoupling || !_outputVisible || _mainWindow == null || _outputWindow == null) return;
        _resizeCoupling = true;
        try
        {
            var desktop    = _ws.DesktopDimensions;
            int newDivider = Math.Clamp(_mainWindow.Height,
                                        MinMainHeight,
                                        desktop.Height - MinOutputHeight);
            int newOutH = desktop.Height - newDivider;
            _mainWindow.SetSize(desktop.Width, newDivider);
            _outputWindow.SetPosition(new Point(0, newDivider));
            _outputWindow.SetSize(desktop.Width, newOutH);
            _splitRatio = newDivider / (double)desktop.Height;
        }
        finally { _resizeCoupling = false; }
    }

    private void OnOutputWindowResized(object? sender, EventArgs e)
    {
        if (_resizeCoupling || !_outputVisible || _mainWindow == null || _outputWindow == null) return;
        _resizeCoupling = true;
        try
        {
            var desktop    = _ws.DesktopDimensions;
            int newDivider = Math.Clamp(_outputWindow.Top,
                                        MinMainHeight,
                                        desktop.Height - MinOutputHeight);
            _mainWindow.SetSize(desktop.Width, newDivider);
            _outputWindow.SetPosition(new Point(0, newDivider));
            _outputWindow.SetSize(desktop.Width, desktop.Height - newDivider);
            _splitRatio = newDivider / (double)desktop.Height;
        }
        finally { _resizeCoupling = false; }
    }

    // Fires from the input thread for every keystroke, regardless of which window is active.
    // Only enqueue work — never touch UI directly from this handler.
    private void OnGlobalDriverKeyPressed(object? sender, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0)
            _pendingUiActions.Enqueue(ShowCommandPalette);
    }

    // PreviewKeyPressed fires BEFORE the focused control (editor) processes the key.
    // Use it to intercept keys that the editor would otherwise consume (arrows, Enter, Tab).
    private void OnMainWindowPreviewKey(object? sender, KeyPressedEventArgs e)
    {
        var key  = e.KeyInfo.Key;
        var mods = e.KeyInfo.Modifiers;

        // Dismiss tooltip on typing keys, Escape, or navigation — but not on
        // arrow keys (used for completion navigation), modifier-only presses, or Ctrl combos
        if (_tooltipPortal != null)
        {
            bool isModifierOnly = key is ConsoleKey.LeftWindows or ConsoleKey.RightWindows;
            bool isArrowKey = key is ConsoleKey.UpArrow or ConsoleKey.DownArrow
                                  or ConsoleKey.LeftArrow or ConsoleKey.RightArrow;
            bool isCtrlCombo = (mods & ConsoleModifiers.Control) != 0 && key != ConsoleKey.Escape;
            if (!isModifierOnly && !isArrowKey && !isCtrlCombo)
                DismissTooltipPortal();
        }

        // Context menu portal handles all keys
        if (_contextMenuPortal != null)
        {
            if (_contextMenuPortal.ProcessKey(e.KeyInfo))
            {
                e.Handled = true;
                return;
            }
        }

        // Escape: dismiss portals if open, then swallow — never let the editor exit editing mode
        if (key == ConsoleKey.Escape && mods == 0)
        {
            if (_locationPortal != null)
            {
                DismissLocationPortal();
                e.Handled = true;
                return;
            }
            if (_completionPortal != null)
                DismissCompletionPortal();
            e.Handled = true;
            return;
        }

        // Location list portal navigation (Find References / Go-to-Implementation)
        if (_locationPortal != null)
        {
            if (mods == 0)
            {
                if (key == ConsoleKey.UpArrow)
                {
                    _locationPortal.SelectPrev();
                    _mainWindow?.Invalidate(false);
                    e.Handled = true;
                    return;
                }
                if (key == ConsoleKey.DownArrow)
                {
                    _locationPortal.SelectNext();
                    _mainWindow?.Invalidate(false);
                    e.Handled = true;
                    return;
                }
                if (key == ConsoleKey.Enter)
                {
                    var selected = _locationPortal.GetSelected();
                    DismissLocationPortal();
                    if (selected != null)
                        NavigateToLocation(selected);
                    e.Handled = true;
                    return;
                }
            }
            // Any typing key dismisses the location portal
            char lch = e.KeyInfo.KeyChar;
            if (lch != '\0' && !char.IsControl(lch))
            {
                DismissLocationPortal();
                // fall through to let the editor handle the key
            }
        }

        if (_completionPortal == null) return;

        if (mods == 0)
        {
            if (key == ConsoleKey.UpArrow)
            {
                _completionPortal.SelectPrev();
                _mainWindow?.Invalidate(false);
                e.Handled = true;
                return;
            }
            if (key == ConsoleKey.DownArrow)
            {
                _completionPortal.SelectNext();
                _mainWindow?.Invalidate(false);
                e.Handled = true;
                return;
            }
            if (key == ConsoleKey.Enter || key == ConsoleKey.Tab)
            {
                var accepted = _completionPortal.GetSelected();
                int filterLen = _completionPortal.FilterText.Length;
                DismissCompletionPortal();
                if (accepted != null)
                {
                    var editor = _editorManager?.CurrentEditor;
                    if (editor != null)
                    {
                        // Delete the prefix the user typed since completion opened,
                        // then insert the full accepted text.
                        // Note: DeleteCharsBefore may temporarily place the cursor on a '.'
                        // which arms _dotTriggerDebounce — we cancel it after the full insert.
                        if (filterLen > 0)
                            editor.DeleteCharsBefore(filterLen);
                        editor.InsertText(accepted.InsertText ?? accepted.Label);

                        // Cancel any dot-trigger scheduled during the intermediate state
                        _dotTriggerDebounce?.Dispose();
                        _dotTriggerDebounce = null;
                    }
                }
                e.Handled = true;
                return;
            }
            if (key == ConsoleKey.Escape)
            {
                DismissCompletionPortal();
                e.Handled = true;
                return;
            }

            // Printable characters and Backspace: let them through to the editor.
            // The ContentChanged handler (OnEditorContentChangedForCompletion) will
            // update the filter automatically after the keystroke is processed.
            char ch = e.KeyInfo.KeyChar;
            bool isTypingKey = (ch != '\0' && !char.IsControl(ch)) || key == ConsoleKey.Backspace;
            if (isTypingKey)
                return; // Do NOT handle — editor processes the key, filter updates via ContentChanged
        }

        // Any other key or modifier combo: dismiss unless it's a completion shortcut.
        bool isCompletionShortcut =
            (key == ConsoleKey.Spacebar && mods == ConsoleModifiers.Control) ||
            key == ConsoleKey.F12;
        if (!isCompletionShortcut)
            DismissCompletionPortal();
    }

    private void OnMainWindowKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        var key  = e.KeyInfo.Key;
        var mods = e.KeyInfo.Modifiers;

        // Let explorer handle F2/Delete/Ctrl+N/Ctrl+Shift+N/F5 when it has focus
        if (_explorer != null && _explorer.HandleKey(key, mods))
        {
            e.Handled = true;
            return;
        }

        if (key == ConsoleKey.S && mods == ConsoleModifiers.Control)
        {
            _editorManager?.SaveCurrent();
            e.Handled = true;
        }
        else if (key == ConsoleKey.W && mods == ConsoleModifiers.Control)
        {
            CloseCurrentTab();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F5 && mods == 0)
        {
            RunProject();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F6 && mods == 0)
        {
            _ = BuildProjectAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F7 && mods == 0)
        {
            _ = TestProjectAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F8 && mods == 0)
        {
            OpenShell();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F8 && mods == ConsoleModifiers.Shift)
        {
            OpenSidePanelShell();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F9 && mods == 0)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
                OpenLazyNuGetTab();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F4 && mods == 0)
        {
            _buildService.Cancel();
            e.Handled = true;
        }
        else if (key == ConsoleKey.B && mods == ConsoleModifiers.Control)
        {
            ToggleExplorer();
            e.Handled = true;
        }
        else if (key == ConsoleKey.J && mods == ConsoleModifiers.Control)
        {
            ToggleOutput();
            e.Handled = true;
        }
        else if (key == ConsoleKey.K && mods == ConsoleModifiers.Control)
        {
            _ = ShowHoverAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.Spacebar && mods == ConsoleModifiers.Control)
        {
            _ = _lsp?.FlushPendingChangeAsync();
            _ = ShowCompletionAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F12 && mods == 0)
        {
            _ = ShowGoToDefinitionAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F12 && mods == ConsoleModifiers.Control)
        {
            _ = ShowGoToImplementationAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F12 && mods == ConsoleModifiers.Shift)
        {
            _ = ShowFindReferencesAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.LeftArrow && mods == ConsoleModifiers.Alt)
        {
            NavigateBack();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F2 && mods == ConsoleModifiers.Control)
        {
            _ = ShowRenameAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F2 && mods == 0)
        {
            _ = ShowSignatureHelpAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.OemPeriod && mods == ConsoleModifiers.Control)
        {
            _ = ShowCodeActionsAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.O && mods == ConsoleModifiers.Alt)
        {
            FocusSymbolsTab();
            e.Handled = true;
        }
        else if (key == ConsoleKey.Oem1 && mods == ConsoleModifiers.Alt) // Alt+;
        {
            ToggleSidePanel();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F && mods == (ConsoleModifiers.Alt | ConsoleModifiers.Shift))
        {
            _ = FormatDocumentAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.R && mods == (ConsoleModifiers.Alt | ConsoleModifiers.Shift))
        {
            ReloadCurrentFromDisk();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F && mods == ConsoleModifiers.Control)
        {
            ShowFindReplace();
            e.Handled = true;
        }
        else if (key == ConsoleKey.H && mods == ConsoleModifiers.Control)
        {
            ShowFindReplace();
            e.Handled = true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Window thread — drains build/test line queues on UI thread
    // ──────────────────────────────────────────────────────────────

    private async Task MainWindowThreadAsync(Window window, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                while (_buildLines.TryDequeue(out var line))
                    _outputPanel?.AppendBuildLine(line);
                while (_testLines.TryDequeue(out var line))
                    _outputPanel?.AppendTestLine(line);
                while (_pendingUiActions.TryDequeue(out var action))
                    action();

                await Task.Delay(80, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Post-init (async, runs after Run() starts)
    // ──────────────────────────────────────────────────────────────

    private async Task PostInitAsync(string projectPath)
    {
        await RefreshGitStatusAsync();

        var lspServer = LspDetector.Find(projectPath, _config.Lsp);
        if (lspServer != null)
        {
            _detectedLspExe = lspServer.Exe;
            _lsp = new LspClient();
            bool started = await _lsp.StartAsync(lspServer, projectPath);
            if (started)
            {
                _lspStarted = true;
                _lsp.DiagnosticsReceived += OnLspDiagnostics;
                _ws.LogService.LogInfo("LSP server started: " + lspServer.Exe);

                // Send DidOpen for any files that were opened before the LSP started
                if (_editorManager != null)
                {
                    foreach (var (filePath, content) in _editorManager.GetOpenDocuments())
                        await _lsp.DidOpenAsync(filePath, content);
                }
            }
            else
            {
                await _lsp.DisposeAsync();
                _lsp = null;
                _ws.LogService.LogInfo("LSP server unavailable — running without IntelliSense");
            }
        }

        _lspDetectionDone = true;
        _aboutRefresh?.Invoke();
        UpdateDashboard();
    }

    private void OnLspDiagnostics(object? sender, (string Uri, List<LspDiagnostic> Diags) args)
    {
        var mapped = args.Diags.Select(d => new BuildDiagnostic(
            FilePath: LspClient.UriToPath(args.Uri),
            Line: d.Range.Start.Line + 1,
            Column: d.Range.Start.Character + 1,
            Code: d.Code ?? "",
            Severity: d.Severity == 1 ? "error" : "warning",
            Message: d.Message)).ToList();

        _outputPanel?.PopulateLspDiagnostics(mapped);
        UpdateErrorCount(mapped);
    }

    // ──────────────────────────────────────────────────────────────
    // Build / Test / Run / Git
    // ──────────────────────────────────────────────────────────────

    private async Task BuildProjectAsync()
    {
        var target = _projectService.FindBuildTarget();
        if (target == null) return;

        _outputPanel?.ClearBuildOutput();
        _outputPanel?.SwitchToBuildTab();

        var result = await _buildService.BuildAsync(
            target,
            line => _buildLines.Enqueue(line),
            _cts.Token);

        _outputPanel?.PopulateProblems(result.Diagnostics);
        UpdateErrorCount(result.Diagnostics);
    }

    private async Task TestProjectAsync()
    {
        var target = _projectService.FindBuildTarget();
        if (target == null) return;

        _outputPanel?.ClearTestOutput();
        _outputPanel?.SwitchToTestTab();

        var result = await _buildService.TestAsync(
            target,
            line => _testLines.Enqueue(line),
            _cts.Token);

        UpdateErrorCount(result.Diagnostics);
    }

    private async Task CleanProjectAsync()
    {
        var target = _projectService.FindBuildTarget();
        if (target == null) return;

        _outputPanel?.ClearBuildOutput();
        _outputPanel?.SwitchToBuildTab();

        await _buildService.RunAsync(
            $"clean {target} --nologo",
            line => _buildLines.Enqueue(line),
            _cts.Token);
    }

    private void RunProject()
    {
        var target = _projectService.FindRunTarget();
        if (target == null)
        {
            _ws.LogService.LogInfo("No runnable project found");
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            Controls.Terminal("dotnet").WithArgs("run", "--project", target).Open(_ws);
    }

    private void OpenShell()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;
        var terminal = _outputPanel?.LaunchShell(_projectService.RootPath);
        if (terminal == null || _outputWindow == null) return;
        // Activate the output window so key events flow to it, then focus the terminal
        _ws.SetActiveWindow(_outputWindow);
        _outputWindow.FocusControl(terminal);
    }

    private static string? DetectLazyNuGet()
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
        catch { }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenLazyNuGetTab()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;

        // Switch to existing tab if still alive
        if (_lazyNuGetTabIndex >= 0 &&
            _lazyNuGetTabIndex < _editorManager!.TabControl.TabCount)
        {
            _editorManager.TabControl.ActiveTabIndex = _lazyNuGetTabIndex;
            return;
        }

        string? exe = DetectLazyNuGet();
        if (exe == null)
        {
            _ws.NotificationStateService.ShowNotification(
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

        _lazyNuGetTabIndex = _editorManager!.OpenControlTab("LazyNuGet", terminal, isClosable: true);
        terminal.ProcessExited += (_, _) => _lazyNuGetTabIndex = -1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenShellTab()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;

        if (_shellTabIndex >= 0 && _shellTabIndex < _editorManager!.TabControl.TabCount)
        {
            _editorManager.TabControl.ActiveTabIndex = _shellTabIndex;
            return;
        }

        string exe = OperatingSystem.IsWindows() ? "cmd.exe" : "bash";
        var terminal = Controls.Terminal(exe)
            .WithWorkingDirectory(_projectService.RootPath)
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment   = VerticalAlignment.Fill;

        _shellTabIndex = _editorManager!.OpenControlTab("Shell", terminal, isClosable: true);
        terminal.ProcessExited += (_, _) => _shellTabIndex = -1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenConfigToolTab(int toolIndex)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;
        if (toolIndex < 0 || toolIndex >= _config.Tools.Count) return;

        if (_toolTabIndices.TryGetValue(toolIndex, out int existingTab) &&
            existingTab >= 0 && existingTab < _editorManager!.TabControl.TabCount)
        {
            _editorManager.TabControl.ActiveTabIndex = existingTab;
            return;
        }

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

        int tabIdx = _editorManager!.OpenControlTab(tool.Name, terminal, isClosable: true);
        _toolTabIndices[toolIndex] = tabIdx;
        terminal.ProcessExited += (_, _) => _toolTabIndices.Remove(toolIndex);
    }

    private void OpenConfigFile()
    {
        ConfigService.EnsureDefaultConfig();
        _editorManager!.OpenFile(ConfigService.GetConfigPath());
    }

    private void HandleExternalFileChanged(string path)
    {
        if (!(_editorManager?.IsFileOpen(path) ?? false)) return;

        if (_editorManager.IsFileDirty(path))
        {
            _editorManager.MarkFileConflict(path);
            _ws.NotificationStateService.ShowNotification(
                "External Change",
                Path.GetFileName(path) + " was modified externally. Local edits preserved. Alt+Shift+R to reload.",
                SharpConsoleUI.Core.NotificationSeverity.Warning);
        }
        else
        {
            _editorManager.ReloadFile(path);
        }
    }

    private void ReloadCurrentFromDisk()
    {
        var path = _editorManager?.CurrentFilePath;
        if (path == null) return;
        _fileWatcher?.SuppressNext(path);
        _editorManager!.ReloadFile(path);
    }

    private async Task OpenFolderAsync()
    {
        var selected = await SharpConsoleUI.Dialogs.FileDialogs.ShowFolderPickerAsync(
            _ws, _projectService.RootPath, _mainWindow);
        if (string.IsNullOrEmpty(selected)) return;

        _projectService.ChangeRootPath(selected);
        _editorManager?.CloseAll();
        _explorer?.Refresh();
        _ = RefreshGitStatusAsync();
        _fileWatcher?.Watch(selected);

        if (_lsp != null)
        {
            await _lsp.ShutdownAsync();
            _lsp = null;
            var lspServer = LspDetector.Find(selected, _config.Lsp);
            if (lspServer != null)
            {
                _lsp = new LspClient();
                _lsp.DiagnosticsReceived += OnLspDiagnostics;
                await _lsp.StartAsync(lspServer, selected);
            }
        }
    }

    private async Task RefreshGitStatusAsync()
    {
        var branch = await _gitService.GetBranchAsync(_projectService.RootPath);
        var status = await _gitService.GetStatusSummaryAsync(_projectService.RootPath);

        var bar = new IdeStatusBar();

        if (string.IsNullOrEmpty(branch))
        {
            bar.AddSegment("[dim] git: none[/]", " git: none");
        }
        else
        {
            // Truncate very long branch names so they never wrap
            var displayBranch = branch.Length > 22
                ? branch[..19] + "..."
                : branch;

            if (string.IsNullOrEmpty(status))
            {
                bar.AddSegment($"[green] git:{Markup.Escape(displayBranch)}[/]",
                               $" git:{displayBranch}");
            }
            else
            {
                bar.AddSegment($"[yellow] git:{Markup.Escape(displayBranch)}[/]",
                               $" git:{displayBranch}")
                   .AddSegment($"[dim]  {Markup.Escape(status)}[/]",
                               $"  {status}");
            }
        }

        _gitMarkup = bar.Render();
        UpdateInlineStatus();

        await RefreshGitFileStatusesAsync();
    }

    private async Task RefreshGitFileStatusesAsync()
    {
        var (detailedFiles, workingDir) = await _gitService.GetDetailedFileStatusesAsync(_projectService.RootPath);

        // Build the simple dictionary for the explorer tree decorations
        var fileStatuses = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in detailedFiles)
        {
            if (!fileStatuses.ContainsKey(f.RelativePath))
                fileStatuses[f.RelativePath] = f.Status;
        }

        _explorer?.UpdateGitStatuses(fileStatuses, workingDir);

        // Refresh diff gutter markers for all open editors
        if (_editorManager != null)
        {
            var root = _projectService.RootPath;
            await _editorManager.UpdateAllGitDiffMarkersAsync(
                path => _gitService.GetLineDiffMarkersAsync(root, path)!);
        }

        // Update the side panel Git tab
        if (_sidePanel != null)
        {
            var branch = await _gitService.GetBranchAsync(_projectService.RootPath);
            var log = await _gitService.GetLogAsync(_projectService.RootPath, 15);
            var sidePanelFiles = detailedFiles
                .Select(f => (f.RelativePath, f.AbsolutePath, f.Status, f.IsStaged))
                .ToList();
            _sidePanel.UpdateGitPanel(branch, sidePanelFiles, log);
        }
    }

    private async Task RefreshGitDiffMarkersForFileAsync(string filePath)
    {
        if (_editorManager == null) return;
        var markers = await _gitService.GetLineDiffMarkersAsync(_projectService.RootPath, filePath);
        _editorManager.UpdateGitDiffMarkers(filePath, markers);
    }

    private async Task RefreshExplorerAndGitAsync()
    {
        _explorer?.Refresh();
        await RefreshGitFileStatusesAsync();
    }

    private async Task GitCommandAsync(string command)
    {
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.SwitchToBuildTab();

        await _buildService.RunAsync(
            $"git -C {_projectService.RootPath} {command}",
            line => _buildLines.Enqueue(line),
            _cts.Token);

        await RefreshGitStatusAsync();
    }

    // ──────────────────────────────────────────────────────────────
    // Git Operations
    // ──────────────────────────────────────────────────────────────

    private async Task GitStageFileAsync(string absolutePath)
    {
        await _gitService.StageAsync(_projectService.RootPath, absolutePath);
        await RefreshGitStatusAsync();
    }

    private async Task GitUnstageFileAsync(string absolutePath)
    {
        await _gitService.UnstageAsync(_projectService.RootPath, absolutePath);
        await RefreshGitStatusAsync();
    }

    private async Task GitStageAllAsync()
    {
        await _gitService.StageAllAsync(_projectService.RootPath);
        await RefreshGitStatusAsync();
    }

    private async Task GitUnstageAllAsync()
    {
        await _gitService.UnstageAllAsync(_projectService.RootPath);
        await RefreshGitStatusAsync();
    }

    private async Task GitDiscardFileAsync(string absolutePath)
    {
        var confirmed = await GitDiscardConfirmDialog.ShowAsync(_ws, absolutePath);
        if (!confirmed) return;
        await _gitService.DiscardChangesAsync(_projectService.RootPath, absolutePath);
        ReloadIfOpen(absolutePath);
        await RefreshExplorerAndGitAsync();
    }

    private async Task GitDiscardAllAsync()
    {
        var confirmed = await GitDiscardConfirmDialog.ShowAllAsync(_ws);
        if (!confirmed) return;
        await _gitService.DiscardAllAsync(_projectService.RootPath);
        ReloadAllOpenFiles();
        await RefreshExplorerAndGitAsync();
    }

    private async Task GitShowDiffAsync(string absolutePath)
    {
        var diff = await _gitService.GetDiffAsync(_projectService.RootPath, absolutePath);
        if (string.IsNullOrEmpty(diff))
        {
            // Try staged diff
            diff = await _gitService.GetStagedDiffAsync(_projectService.RootPath, absolutePath);
        }
        if (string.IsNullOrEmpty(diff)) return;

        var fileName = Path.GetFileName(absolutePath);
        OpenReadOnlyTab($"Diff: {fileName}", diff, new DiffSyntaxHighlighter());
    }

    private async Task GitShowDiffAllAsync()
    {
        var diff = await _gitService.GetDiffAllAsync(_projectService.RootPath);
        if (string.IsNullOrEmpty(diff)) return;
        OpenReadOnlyTab("Diff: All Changes", diff, new DiffSyntaxHighlighter());
    }

    private async Task GitCommitAsync()
    {
        var status = await _gitService.GetStatusSummaryAsync(_projectService.RootPath);
        var message = await GitCommitDialog.ShowAsync(_ws, status);
        if (message == null) return;

        var result = await _gitService.CommitAsync(_projectService.RootPath, message);
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.AppendBuildLine(result.StartsWith("Error")
            ? result
            : $"Committed: {result}");
        _outputPanel?.SwitchToBuildTab();
        await RefreshGitStatusAsync();
    }

    private async Task GitStashAsync()
    {
        var message = await GitStashDialog.ShowAsync(_ws);
        if (message == null) return;

        var result = await _gitService.StashAsync(_projectService.RootPath, message);
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.AppendBuildLine(result);
        _outputPanel?.SwitchToBuildTab();
        await RefreshExplorerAndGitAsync();
    }

    private async Task GitStashPopAsync()
    {
        var result = await _gitService.StashPopAsync(_projectService.RootPath);
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.AppendBuildLine(result);
        _outputPanel?.SwitchToBuildTab();
        ReloadAllOpenFiles();
        await RefreshExplorerAndGitAsync();
    }

    private async Task GitSwitchBranchAsync()
    {
        var branches = await _gitService.GetBranchesAsync(_projectService.RootPath);
        if (branches.Count == 0) return;
        var current = branches.Count > 0 ? branches[0] : "";
        var selected = await GitBranchPickerDialog.ShowAsync(_ws, branches, current);
        if (selected == null) return;

        var result = await _gitService.CheckoutAsync(_projectService.RootPath, selected);
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.AppendBuildLine(result.StartsWith("Error")
            ? result
            : $"Switched to branch: {result}");
        _outputPanel?.SwitchToBuildTab();
        ReloadAllOpenFiles();
        await RefreshExplorerAndGitAsync();
    }

    private async Task GitNewBranchAsync()
    {
        var name = await GitNewBranchDialog.ShowAsync(_ws);
        if (name == null) return;

        var result = await _gitService.CreateBranchAsync(_projectService.RootPath, name);
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.AppendBuildLine(result.StartsWith("Error")
            ? result
            : $"Created branch: {result}");
        _outputPanel?.SwitchToBuildTab();
        await RefreshGitStatusAsync();
    }

    private async Task ShowCommitDetailAsync(GitLogEntry entry)
    {
        var detail = await _gitService.GetCommitDetailAsync(_projectService.RootPath, entry.Sha);
        OpenReadOnlyTab($"Commit: {entry.ShortSha}", detail, new CommitDetailSyntaxHighlighter());
    }

    private async Task GitShowLogAsync()
    {
        var entries = await _gitService.GetLogAsync(_projectService.RootPath);
        if (entries.Count == 0) return;
        var lines = entries.Select(e => $"{e.ShortSha}  {e.Author,-16}  {e.When:yyyy-MM-dd HH:mm}  {e.MessageShort}");
        OpenReadOnlyTab("Git Log", string.Join('\n', lines));
    }

    private async Task GitShowFileLogAsync(string absolutePath)
    {
        var entries = await _gitService.GetFileLogAsync(_projectService.RootPath, absolutePath);
        if (entries.Count == 0) return;
        var fileName = Path.GetFileName(absolutePath);
        var lines = entries.Select(e => $"{e.ShortSha}  {e.Author,-16}  {e.When:yyyy-MM-dd HH:mm}  {e.MessageShort}");
        OpenReadOnlyTab($"Log: {fileName}", string.Join('\n', lines));
    }

    private async Task GitShowBlameAsync(string absolutePath)
    {
        var blameLines = await _gitService.GetBlameAsync(_projectService.RootPath, absolutePath);
        if (blameLines.Count == 0) return;

        // Read the file content to pair blame with source lines
        string[] sourceLines;
        try { sourceLines = await File.ReadAllLinesAsync(absolutePath); }
        catch { return; }

        var output = new List<string>();
        for (int i = 0; i < sourceLines.Length; i++)
        {
            var blame = i < blameLines.Count ? blameLines[i] : null;
            var prefix = blame != null
                ? $"{blame.ShortSha} {blame.Author,-12} {blame.When:yy-MM-dd}"
                : new string(' ', 27);
            output.Add($"{prefix} | {sourceLines[i]}");
        }

        var fileName = Path.GetFileName(absolutePath);
        OpenReadOnlyTab($"Blame: {fileName}", string.Join('\n', output));
    }

    private void OpenReadOnlyTab(string title, string content, ISyntaxHighlighter? highlighter = null)
    {
        if (_editorManager == null) return;
        _editorManager.OpenReadOnlyTab(title, content, highlighter);
    }

    private void ReloadIfOpen(string absolutePath)
    {
        if (_editorManager == null) return;
        var idx = _editorManager.GetTabIndexForPath(absolutePath);
        if (idx >= 0)
            _editorManager.ReloadTabFromDisk(idx);
    }

    private void ReloadAllOpenFiles()
    {
        if (_editorManager == null) return;
        for (int i = 0; i < _editorManager.TabCount; i++)
            _editorManager.ReloadTabFromDisk(i);
    }

    // ──────────────────────────────────────────────────────────────
    // File Actions
    // ──────────────────────────────────────────────────────────────

    private async Task HandleNewFileAsync(string parentDir)
    {
        var path = await NewFileDialog.ShowAsync(_ws, parentDir);
        if (path == null) return;

        try
        {
            await File.Create(path).DisposeAsync();
            await RefreshExplorerAndGitAsync();
            _editorManager?.OpenFile(path);
        }
        catch (Exception ex)
        {
            _ws.LogService.LogError($"Failed to create file: {ex.Message}");
        }
    }

    private async Task HandleNewFolderAsync(string parentDir)
    {
        var path = await NewFileDialog.ShowAsync(_ws, parentDir, isFolder: true);
        if (path == null) return;

        try
        {
            Directory.CreateDirectory(path);
            await RefreshExplorerAndGitAsync();
        }
        catch (Exception ex)
        {
            _ws.LogService.LogError($"Failed to create folder: {ex.Message}");
        }
    }

    private async Task HandleRenameAsync(string currentPath)
    {
        var newPath = await FileRenameDialog.ShowAsync(_ws, currentPath);
        if (newPath == null) return;

        try
        {
            bool isDir = Directory.Exists(currentPath);
            bool isOpenInEditor = !isDir && _editorManager?.IsFileOpen(currentPath) == true;

            if (isDir)
                Directory.Move(currentPath, newPath);
            else
                File.Move(currentPath, newPath);

            // If file was open in editor, close old tab and open new path
            if (isOpenInEditor)
            {
                // Find and close the tab by path
                for (int i = 0; i < (_editorManager?.TabControl.TabCount ?? 0); i++)
                {
                    if (_editorManager!.GetTabFilePath(i) == currentPath)
                    {
                        _editorManager.CloseTabAt(i);
                        break;
                    }
                }
                _editorManager?.OpenFile(newPath);
            }

            await RefreshExplorerAndGitAsync();
        }
        catch (Exception ex)
        {
            _ws.LogService.LogError($"Failed to rename: {ex.Message}");
        }
    }

    private async Task HandleDeleteAsync(string path)
    {
        var confirmed = await DeleteConfirmDialog.ShowAsync(_ws, path);
        if (!confirmed) return;

        try
        {
            bool isDir = Directory.Exists(path);

            // Close any open editor tabs for this file/directory
            if (_editorManager != null)
            {
                for (int i = _editorManager.TabControl.TabCount - 1; i >= 0; i--)
                {
                    var tabPath = _editorManager.GetTabFilePath(i);
                    if (tabPath != null &&
                        (tabPath == path ||
                         (isDir && tabPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                    {
                        _editorManager.CloseTabAt(i);
                    }
                }
            }

            if (isDir)
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);

            await RefreshExplorerAndGitAsync();
        }
        catch (Exception ex)
        {
            _ws.LogService.LogError($"Failed to delete: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Dialogs
    // ──────────────────────────────────────────────────────────────

    private void CloseCurrentTab()
    {
        if (_editorManager == null) return;

        if (_editorManager.IsCurrentTabDirty())
        {
            var fileName = _editorManager.CurrentFilePath != null
                ? Path.GetFileName(_editorManager.CurrentFilePath)
                : "Untitled";
            _ = ConfirmSaveDialog.ShowAsync(_ws, fileName).ContinueWith(t =>
            {
                if (t.Result == DialogResult.Cancel) return;
                if (t.Result == DialogResult.Save) _editorManager.SaveCurrent();
                _editorManager.CloseCurrentTab();
            });
            return;
        }

        _editorManager.CloseCurrentTab();
    }

    private void ShowNuGetDialog()
    {
        _ = NuGetDialog.ShowAsync(_ws).ContinueWith(t =>
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
    }

    private void InitializeCommands()
    {
        // File
        _commandRegistry.Register(new IdeCommand { Id = "file.save",            Category = "File",  Label = "Save",             Keybinding = "Ctrl+S",     Execute = () => _editorManager?.SaveCurrent(),                                Priority = 90 });
        _commandRegistry.Register(new IdeCommand { Id = "file.close-tab",       Category = "File",  Label = "Close Tab",         Keybinding = "Ctrl+W",     Execute = CloseCurrentTab,                                                    Priority = 85 });
        _commandRegistry.Register(new IdeCommand { Id = "file.open-folder",     Category = "File",  Label = "Open Folder\u2026",                            Execute = () => _ = OpenFolderAsync(),                                        Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "file.refresh-explorer",Category = "File",  Label = "Refresh Explorer",  Keybinding = "F5",         Execute = () => _ = RefreshExplorerAndGitAsync(),                             Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "file.new-file",       Category = "File",  Label = "New File",          Keybinding = "Ctrl+N",     Execute = () => { var d = _explorer?.GetSelectedPath(); if (d != null) { if (!Directory.Exists(d)) d = Path.GetDirectoryName(d); if (d != null) _ = HandleNewFileAsync(d); } }, Priority = 75 });
        _commandRegistry.Register(new IdeCommand { Id = "file.new-folder",     Category = "File",  Label = "New Folder",        Keybinding = "Ctrl+Shift+N", Execute = () => { var d = _explorer?.GetSelectedPath(); if (d != null) { if (!Directory.Exists(d)) d = Path.GetDirectoryName(d); if (d != null) _ = HandleNewFolderAsync(d); } }, Priority = 74 });
        _commandRegistry.Register(new IdeCommand { Id = "file.rename",         Category = "File",  Label = "Rename",            Keybinding = "F2",         Execute = () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleRenameAsync(p); }, Priority = 73 });
        _commandRegistry.Register(new IdeCommand { Id = "file.delete",         Category = "File",  Label = "Delete",                                       Execute = () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleDeleteAsync(p); }, Priority = 72 });
        _commandRegistry.Register(new IdeCommand { Id = "file.exit",            Category = "File",  Label = "Exit",              Keybinding = "Alt+F4",     Execute = () => _ws.Shutdown(0),                                              Priority = 10 });

        // Edit
        _commandRegistry.Register(new IdeCommand { Id = "edit.find",            Category = "Edit",  Label = "Find\u2026",        Keybinding = "Ctrl+F",     Execute = ShowFindReplace,                                                    Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "edit.replace",         Category = "Edit",  Label = "Replace\u2026",     Keybinding = "Ctrl+H",     Execute = ShowFindReplace,                                                    Priority = 75 });
        _commandRegistry.Register(new IdeCommand { Id = "edit.reload",          Category = "Edit",  Label = "Reload from Disk",  Keybinding = "Alt+Shift+R",Execute = ReloadCurrentFromDisk,                                              Priority = 60 });
        _commandRegistry.Register(new IdeCommand { Id = "edit.wrap-word",       Category = "Edit",  Label = "Word Wrap",                                    Execute = () => SetWrapMode(WrapMode.WrapWords),                               Priority = 50 });
        _commandRegistry.Register(new IdeCommand { Id = "edit.wrap-char",       Category = "Edit",  Label = "Character Wrap",                               Execute = () => SetWrapMode(WrapMode.Wrap),                                   Priority = 50 });
        _commandRegistry.Register(new IdeCommand { Id = "edit.no-wrap",         Category = "Edit",  Label = "No Wrap",                                      Execute = () => SetWrapMode(WrapMode.NoWrap),                                 Priority = 50 });

        // Build
        _commandRegistry.Register(new IdeCommand { Id = "build.build",          Category = "Build", Label = "Build",             Keybinding = "F6",         Execute = () => _ = BuildProjectAsync(),                                      Priority = 90 });
        _commandRegistry.Register(new IdeCommand { Id = "build.test",           Category = "Build", Label = "Test",              Keybinding = "F7",         Execute = () => _ = TestProjectAsync(),                                       Priority = 85 });
        _commandRegistry.Register(new IdeCommand { Id = "build.clean",          Category = "Build", Label = "Clean",                                        Execute = () => _ = CleanProjectAsync(),                                      Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "build.stop",           Category = "Build", Label = "Stop",              Keybinding = "F4",         Execute = () => _buildService.Cancel(),                                       Priority = 80 });

        // Run
        _commandRegistry.Register(new IdeCommand { Id = "run.run",              Category = "Run",   Label = "Run",               Keybinding = "F5",         Execute = RunProject,                                                         Priority = 90 });

        // View
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-explorer", Category = "View",  Label = "Toggle Explorer",   Keybinding = "Ctrl+B",     Execute = ToggleExplorer,                                                     Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-output",   Category = "View",  Label = "Toggle Output Panel",Keybinding = "Ctrl+J",    Execute = ToggleOutput,                                                       Priority = 75 });
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-side-panel",Category = "View", Label = "Toggle Side Panel", Keybinding = "Alt+;",      Execute = ToggleSidePanel,                                                    Priority = 70 });

        // Git
        _commandRegistry.Register(new IdeCommand { Id = "git.source-control",    Category = "Git",   Label = "Source Control",    Keybinding = "Alt+G", Execute = ShowSourceControl,                                                Priority = 90 });
        _commandRegistry.Register(new IdeCommand { Id = "git.refresh",          Category = "Git",   Label = "Refresh Status",                               Execute = () => _ = RefreshGitStatusAsync(),                                  Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "git.stage-file",       Category = "Git",   Label = "Stage Current File",                           Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = GitStageFileAsync(p); }, Priority = 68 });
        _commandRegistry.Register(new IdeCommand { Id = "git.unstage-file",     Category = "Git",   Label = "Unstage Current File",                         Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = GitUnstageFileAsync(p); }, Priority = 67 });
        _commandRegistry.Register(new IdeCommand { Id = "git.stage-all",        Category = "Git",   Label = "Stage All",                                    Execute = () => _ = GitStageAllAsync(),                                       Priority = 66 });
        _commandRegistry.Register(new IdeCommand { Id = "git.unstage-all",      Category = "Git",   Label = "Unstage All",                                  Execute = () => _ = GitUnstageAllAsync(),                                     Priority = 65 });
        _commandRegistry.Register(new IdeCommand { Id = "git.commit",           Category = "Git",   Label = "Commit\u2026",          Keybinding = "Ctrl+Enter", Execute = () => _ = GitCommitAsync(),                                      Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "git.pull",             Category = "Git",   Label = "Pull",                                         Execute = () => _ = GitCommandAsync("pull"),                                   Priority = 64 });
        _commandRegistry.Register(new IdeCommand { Id = "git.push",             Category = "Git",   Label = "Push",                                         Execute = () => _ = GitCommandAsync("push"),                                   Priority = 63 });
        _commandRegistry.Register(new IdeCommand { Id = "git.diff-file",        Category = "Git",   Label = "Diff Current File",                            Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = GitShowDiffAsync(p); }, Priority = 60 });
        _commandRegistry.Register(new IdeCommand { Id = "git.diff-all",         Category = "Git",   Label = "Diff All Changes",                             Execute = () => _ = GitShowDiffAllAsync(),                                    Priority = 59 });
        _commandRegistry.Register(new IdeCommand { Id = "git.discard-file",     Category = "Git",   Label = "Discard Changes (Current File)",                Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = GitDiscardFileAsync(p); }, Priority = 40 });
        _commandRegistry.Register(new IdeCommand { Id = "git.discard-all",      Category = "Git",   Label = "Discard All Changes",                          Execute = () => _ = GitDiscardAllAsync(),                                     Priority = 35 });
        _commandRegistry.Register(new IdeCommand { Id = "git.stash",            Category = "Git",   Label = "Stash\u2026",                                  Execute = () => _ = GitStashAsync(),                                          Priority = 55 });
        _commandRegistry.Register(new IdeCommand { Id = "git.stash-pop",        Category = "Git",   Label = "Stash Pop",                                    Execute = () => _ = GitStashPopAsync(),                                       Priority = 54 });
        _commandRegistry.Register(new IdeCommand { Id = "git.log",              Category = "Git",   Label = "Log",                                          Execute = () => _ = GitShowLogAsync(),                                        Priority = 50 });
        _commandRegistry.Register(new IdeCommand { Id = "git.log-file",         Category = "Git",   Label = "Log (Current File)",                           Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = GitShowFileLogAsync(p); }, Priority = 49 });
        _commandRegistry.Register(new IdeCommand { Id = "git.blame",            Category = "Git",   Label = "Blame (Current File)",                         Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = GitShowBlameAsync(p); }, Priority = 48 });
        _commandRegistry.Register(new IdeCommand { Id = "git.switch-branch",    Category = "Git",   Label = "Switch Branch\u2026",                          Execute = () => _ = GitSwitchBranchAsync(),                                   Priority = 45 });
        _commandRegistry.Register(new IdeCommand { Id = "git.new-branch",       Category = "Git",   Label = "New Branch\u2026",                             Execute = () => _ = GitNewBranchAsync(),                                      Priority = 44 });

        // LSP
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-def",         Category = "LSP",   Label = "Go to Definition",    Keybinding = "F12",          Execute = () => _ = ShowGoToDefinitionAsync(),                              Priority = 90 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-impl",        Category = "LSP",   Label = "Go to Implementation",Keybinding = "Ctrl+F12",     Execute = () => _ = ShowGoToImplementationAsync(),                          Priority = 88 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.references",       Category = "LSP",   Label = "Find All References", Keybinding = "Shift+F12",    Execute = () => _ = ShowFindReferencesAsync(),                              Priority = 87 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.nav-back",         Category = "LSP",   Label = "Navigate Back",       Keybinding = "Alt+\u2190",   Execute = NavigateBack,                                                    Priority = 85 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.rename",           Category = "LSP",   Label = "Rename Symbol",       Keybinding = "Ctrl+F2",      Execute = () => _ = ShowRenameAsync(),                                      Priority = 83 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.code-action",      Category = "LSP",   Label = "Code Actions",        Keybinding = "Ctrl+.",        Execute = () => _ = ShowCodeActionsAsync(),                                 Priority = 82 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.document-symbols", Category = "LSP",   Label = "Focus Symbols",       Keybinding = "Alt+O", Execute = FocusSymbolsTab,                                                  Priority = 81 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.hover",            Category = "LSP",   Label = "Hover Tooltip",       Keybinding = "Ctrl+K",       Execute = () => _ = ShowHoverAsync(),                                       Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.signature",        Category = "LSP",   Label = "Signature Help",      Keybinding = "F2",           Execute = () => _ = ShowSignatureHelpAsync(),                                Priority = 75 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.format",           Category = "LSP",   Label = "Format Document",     Keybinding = "Alt+Shift+F",  Execute = () => _ = FormatDocumentAsync(),                                   Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.complete",         Category = "LSP",   Label = "Show Completions",    Keybinding = "Ctrl+Space",   Execute = () => _ = ShowCompletionAsync(),                                   Priority = 65 });

        // Tools
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell",          Category = "Tools", Label = "Open Shell",        Keybinding = "F8",         Execute = OpenShell,                                                          Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell-tab",      Category = "Tools", Label = "Open Shell Tab",                               Execute = () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenShellTab(); },      Priority = 75 });
        _commandRegistry.Register(new IdeCommand { Id = "tools.side-shell",    Category = "Tools", Label = "Side Panel Shell",  Keybinding = "Shift+F8",   Execute = OpenSidePanelShell,                                                 Priority = 73 });
        _commandRegistry.Register(new IdeCommand { Id = "tools.lazynuget",      Category = "Tools", Label = "LazyNuGet",         Keybinding = "F9",         Execute = () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenLazyNuGetTab(); }, Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "tools.nuget",          Category = "Tools", Label = "Add NuGet Package\u2026",                      Execute = ShowNuGetDialog,                                                    Priority = 65 });
        _commandRegistry.Register(new IdeCommand { Id = "tools.config",         Category = "Tools", Label = "Edit Config",                                  Execute = OpenConfigFile,                                                     Priority = 60 });

        // Help
        _commandRegistry.Register(new IdeCommand { Id = "help.about",           Category = "Help",  Label = "About lazydotide\u2026",                       Execute = ShowAbout,                                                          Priority = 10 });

        // Dynamic tool commands from config
        for (int i = 0; i < _config.Tools.Count; i++)
        {
            int idx = i;
            var tool = _config.Tools[i];
            _commandRegistry.Register(new IdeCommand
            {
                Id       = $"tools.custom.{idx}",
                Category = "Tools",
                Label    = tool.Name,
                Execute  = () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenConfigToolTab(idx); },
                Priority = 55
            });
        }
    }

    private void ShowCommandPalette()
    {
        if (_commandPaletteOpen) return;
        _commandPaletteOpen = true;
        CommandPaletteDialog.Show(_ws, _commandRegistry, cmd =>
        {
            _commandPaletteOpen = false;
            if (cmd == null) return;
            cmd.Execute();
            var editor = _editorManager?.CurrentEditor;
            if (editor != null) _mainWindow?.FocusControl(editor);
        });
    }

    private bool _findReplaceOpen = false;

    private void ShowFindReplace()
    {
        if (_findReplaceOpen || _editorManager == null) return;
        _findReplaceOpen = true;
        _ = FindReplaceDialog.ShowAsync(_ws, _editorManager)
            .ContinueWith(_ => _findReplaceOpen = false);
    }

    private async Task RunNuGetAsync(string args)
    {
        _outputPanel?.ClearBuildOutput();
        _outputPanel?.SwitchToBuildTab();

        await _buildService.RunAsync(
            "dotnet " + args,
            line => _buildLines.Enqueue(line),
            _cts.Token);
    }

    // ──────────────────────────────────────────────────────────────
    // Panel toggle
    // ──────────────────────────────────────────────────────────────

    private void SetWrapMode(WrapMode mode)
    {
        if (_editorManager != null)
            _editorManager.WrapMode = mode;
    }

    private void ToggleExplorer()
    {
        _explorerVisible = !_explorerVisible;
        if (_explorerCol != null)
            _explorerCol.Visible = _explorerVisible;
        if (_explorerSplitter != null)
            _explorerSplitter.Visible = _explorerVisible;
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
    }

    private void ToggleOutput()
    {
        _outputVisible = !_outputVisible;
        var desktop = _ws.DesktopDimensions;
        _resizeCoupling = true;
        try
        {
            if (_outputVisible)
            {
                int mainH = (int)(desktop.Height * _splitRatio);
                int outH  = desktop.Height - mainH;
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
        finally { _resizeCoupling = false; }
    }

    private void ShowSourceControl()
    {
        if (!_sidePanelVisible)
        {
            _sidePanelVisible = true;
            if (_sidePanelCol != null) _sidePanelCol.Visible = true;
            if (_sidePanelSplitter != null) _sidePanelSplitter.Visible = true;
            _mainWindow?.ForceRebuildLayout();
            _mainWindow?.Invalidate(true);
        }
        _sidePanel?.SwitchToGitTab();
    }

    private void ToggleSidePanel()
    {
        _sidePanelVisible = !_sidePanelVisible;
        if (_sidePanelCol != null)
            _sidePanelCol.Visible = _sidePanelVisible;
        if (_sidePanelSplitter != null)
            _sidePanelSplitter.Visible = _sidePanelVisible;
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
        if (_sidePanelVisible)
        {
            _sidePanel?.SwitchToSymbolsTab();
            RefreshSymbolsForFile(_editorManager?.CurrentFilePath);
        }
    }

    private void FocusSymbolsTab()
    {
        if (!_sidePanelVisible)
            ToggleSidePanel();
        _sidePanel?.SwitchToSymbolsTab();
    }

    private void OpenSidePanelShell()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;
        if (!_sidePanelVisible)
            ToggleSidePanel();
        var terminal = _sidePanel?.LaunchShell(_projectService.RootPath);
        if (terminal != null)
        {
            InvalidateSidePanel();
            _mainWindow?.FocusControl(terminal);
        }
    }

    private void InvalidateSidePanel()
    {
        _mainContent?.Invalidate();
        _mainWindow?.ForceRebuildLayout();
        _mainWindow?.Invalidate(true);
    }

    private void RefreshSymbolsForFile(string? filePath)
    {
        if (filePath == null || _lsp == null || _sidePanel == null || !_sidePanelVisible)
            return;
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _sidePanel.ClearSymbols();
            InvalidateSidePanel();
            return;
        }
        _ = RefreshSymbolsAsync(filePath);
    }

    private void ScheduleSymbolRefresh(string filePath)
    {
        if (!_sidePanelVisible || _sidePanel == null) return;
        _symbolRefreshDebounce?.Dispose();
        _symbolRefreshDebounce = new Timer(_ =>
        {
            _pendingUiActions.Enqueue(() => RefreshSymbolsForFile(filePath));
        }, null, 500, Timeout.Infinite);
    }

    private async Task RefreshSymbolsAsync(string filePath)
    {
        if (_lsp == null || _sidePanel == null) return;
        try
        {
            var symbols = await _lsp.DocumentSymbolAsync(filePath);
            _sidePanel.UpdateSymbols(filePath, symbols);
        }
        catch
        {
            _sidePanel.ClearSymbols();
        }
        InvalidateSidePanel();
    }

    // ──────────────────────────────────────────────────────────────
    // LSP: Hover & Completion
    // ──────────────────────────────────────────────────────────────

    /// <summary>Common guard for LSP requests: checks LSP is running and editor is ready.</summary>
    private async Task<T?> LspRequestAsync<T>(Func<Task<T?>> request, string featureName) where T : class
    {
        if (_lsp == null)
        {
            ShowTransientTooltip("Language server not running.");
            return null;
        }
        var result = await request();
        if (result == null)
            ShowTransientTooltip($"No {featureName.ToLower()} available.");
        return result;
    }

    private async Task ShowHoverAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null)
        {
            ShowTransientTooltip("Language server not running.");
            return;
        }
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var result = await _lsp.HoverAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (result == null || string.IsNullOrWhiteSpace(result.Contents))
        {
            ShowTransientTooltip("No type info at cursor.");
            return;
        }

        var lines = LspMarkdownHelper.ConvertToSpectreMarkup(result.Contents);
        if (lines.Count == 0) return;

        ShowTooltipPortal(lines);
    }

    private async Task ShowCompletionAsync(bool silent = false)
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path   = _editorManager.CurrentFilePath;
        if (path == null) return;

        // Capture position BEFORE the await — the user may type more while the LSP responds
        int requestLine = editor.CurrentLine;
        int requestCol  = editor.CurrentColumn;

        var items = await _lsp.CompletionAsync(path, requestLine - 1, requestCol - 1);
        if (items.Count == 0)
        {
            if (!silent) ShowTransientTooltip("No completions at cursor.");
            return;
        }

        // If the user navigated to a different line during the await, abort
        if (editor.CurrentLine != requestLine) return;

        DismissCompletionPortal();

        // If the cursor is mid-word (e.g. user typed "GetFo" then pressed Ctrl+Space),
        // walk back to the word start and use the partial word as the initial filter.
        // This also positions _completionTriggerColumn so subsequent keystrokes keep filtering.
        var lineContent = editor.Content.Split('\n');
        int lineIdx = requestLine - 1;
        int cursorCol0 = editor.CurrentColumn - 1;  // 0-indexed position in the line (may have advanced)
        int wordStart0 = cursorCol0;
        if (lineIdx >= 0 && lineIdx < lineContent.Length)
        {
            var currentLine = lineContent[lineIdx];
            while (wordStart0 > 0 && IsIdentifierChar(currentLine[wordStart0 - 1]))
                wordStart0--;
        }
        string initialFilter = string.Empty;
        if (lineIdx >= 0 && lineIdx < lineContent.Length && wordStart0 < cursorCol0)
            initialFilter = lineContent[lineIdx].Substring(wordStart0, cursorCol0 - wordStart0);

        // Anchor trigger at word start so filterLen covers the already-typed prefix
        _completionTriggerColumn = wordStart0 + 1;  // back to 1-indexed
        _completionTriggerLine   = editor.CurrentLine;

        // Position popup at the word-start column, not the cursor column
        var screenCol = Math.Max(0, editor.ActualX + editor.GutterWidth + (wordStart0 - editor.HorizontalScrollOffset));
        var screenRow = editor.ActualY + Math.Max(0, editor.CurrentLine - 1 - editor.VerticalScrollOffset);

        var portal = new LspCompletionPortalContent(
            items, screenCol, screenRow,
            _mainWindow.Width, _mainWindow.Height);

        if (initialFilter.Length > 0)
            portal.SetFilter(initialFilter);

        // Set Container BEFORE CreatePortal so the first Invalidate() during creation works.
        portal.Container = _mainWindow;
        _completionPortal     = portal;
        _completionPortalNode = _mainWindow.CreatePortal(editor, portal);

        // Mouse click on an item: accept and insert, same as Enter/Tab.
        portal.ItemAccepted += (_, item) =>
        {
            int filterLen = _completionPortal?.FilterText.Length ?? 0;
            DismissCompletionPortal();
            if (filterLen > 0) editor.DeleteCharsBefore(filterLen);
            editor.InsertText(item.InsertText ?? item.Label);
            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = null;
        };

        // Library auto-dismisses portal on outside click; clean up local state
        portal.DismissRequested += (_, _) => DismissCompletionPortal();

        // Subscribe to content changes so the filter updates as the user types
        editor.ContentChanged += OnEditorContentChangedForCompletion;
    }

    private async Task ShowGoToDefinitionAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var locations = await _lsp.DefinitionAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (locations.Count == 0)
        {
            ShowTransientTooltip("No definition found at current position.");
            return;
        }

        if (locations.Count == 1)
        {
            var loc = locations[0];
            NavigateToLocation(new LspLocationEntry(
                LspClient.UriToPath(loc.Uri),
                loc.Range.Start.Line + 1,
                loc.Range.Start.Character + 1,
                ""));
        }
        else
        {
            ShowLocationPortal(LocationsToEntries(locations), NavigateToLocation);
        }
    }

    private void NavigateBack()
    {
        if (_navHistory.Count == 0) return;
        var (prevPath, prevLine, prevCol) = _navHistory.Pop();
        _editorManager?.OpenFile(prevPath);
        var editor = _editorManager?.CurrentEditor;
        if (editor != null)
        {
            editor.GoToLine(prevLine);
            editor.SetLogicalCursorPosition(new System.Drawing.Point(prevCol - 1, prevLine - 1));
        }
    }

    private async Task ShowSignatureHelpAsync(bool silent = false)
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path   = _editorManager.CurrentFilePath;
        if (path == null) return;

        var sig = await _lsp.SignatureHelpAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (sig == null || sig.Signatures.Count == 0)
        {
            if (!silent) ShowTransientTooltip("No signature at cursor. Position inside function arguments.");
            return;
        }

        var activeSig = sig.Signatures[Math.Min(sig.ActiveSignature, sig.Signatures.Count - 1)];
        string sigLabel = activeSig.Label;

        // Highlight the active parameter in bold
        string line1;
        if (sig.ActiveParameter >= 0 && sig.ActiveParameter < activeSig.Parameters.Count)
        {
            var paramLabel = activeSig.Parameters[sig.ActiveParameter].Label;
            int idx = sigLabel.IndexOf(paramLabel, StringComparison.Ordinal);
            line1 = idx >= 0
                ? Markup.Escape(sigLabel[..idx]) + $"[bold yellow]{Markup.Escape(paramLabel)}[/]" + Markup.Escape(sigLabel[(idx + paramLabel.Length)..])
                : Markup.Escape(sigLabel);
        }
        else
        {
            line1 = Markup.Escape(sigLabel);
        }

        var lines = new List<string> { line1 };
        if (!string.IsNullOrWhiteSpace(activeSig.Documentation))
            lines.AddRange(LspMarkdownHelper.ConvertToSpectreMarkup(activeSig.Documentation!));

        ShowTooltipPortal(lines);
    }

    private void ShowTooltipPortal(List<string> lines, bool preferAbove = true)
    {
        DismissTooltipPortal(); // also bumps generation to invalidate stale auto-dismiss
        ++_tooltipAutoDismissGeneration;
        var editor = _editorManager?.CurrentEditor;
        if (editor == null || _mainWindow == null) return;
        var cursor = _editorManager!.GetCursorBounds();
        var portal = new LspTooltipPortalContent(lines, cursor.X, cursor.Y,
            _mainWindow.Width, _mainWindow.Height, preferAbove);
        portal.Container   = _mainWindow;
        portal.Clicked    += (_, _) => DismissTooltipPortal();
        portal.DismissRequested += (_, _) => DismissTooltipPortal();
        _tooltipPortal     = portal;
        _tooltipPortalNode = _mainWindow.CreatePortal(editor, portal);
    }

    private void ShowTransientTooltip(string message, int dismissMs = 2000)
    {
        _tooltipAutoDismiss?.Dispose();
        _tooltipAutoDismiss = null;

        ShowTooltipPortal(new List<string> { Markup.Escape(message) });

        int gen = ++_tooltipAutoDismissGeneration;
        _tooltipAutoDismiss = new Timer(_ =>
        {
            _pendingUiActions.Enqueue(() =>
            {
                // Only dismiss if no newer tooltip has been shown since this timer was set
                if (_tooltipAutoDismissGeneration == gen)
                    DismissTooltipPortal();
            });
        }, null, dismissMs, Timeout.Infinite);
    }

    // ── Portal helpers ─────────────────────────────────────────────────────────

    private void DismissContextMenu()
    {
        if (_contextMenuPortalNode != null && _mainWindow != null && _contextMenuOwner != null)
        {
            _mainWindow.RemovePortal(_contextMenuOwner, _contextMenuPortalNode);
            _contextMenuPortalNode = null;
            _contextMenuPortal = null;
            _contextMenuOwner = null;
        }
    }

    private void ShowContextMenu(List<ContextMenuItem> items, int anchorX, int anchorY, IWindowControl? owner = null)
    {
        DismissContextMenu();
        if (_mainWindow == null || items.Count == 0) return;

        // Use provided owner or fall back to current editor or explorer
        var portalOwner = owner ?? _editorManager?.CurrentEditor ?? _explorer?.Control;
        if (portalOwner == null) return;

        var portal = new ContextMenuPortal(items, anchorX, anchorY,
            _mainWindow.Width, _mainWindow.Height);
        portal.Container = _mainWindow;
        _contextMenuPortal = portal;
        _contextMenuOwner = portalOwner;
        _contextMenuPortalNode = _mainWindow.CreatePortal(portalOwner, portal);

        portal.ItemSelected += (_, item) =>
        {
            DismissContextMenu();
            item.Action?.Invoke();
        };

        portal.Dismissed += (_, _) =>
        {
            DismissContextMenu();
        };

        // Library auto-dismisses portal on outside click; clean up local state
        portal.DismissRequested += (_, _) =>
        {
            _contextMenuPortalNode = null;
            _contextMenuPortal = null;
            _contextMenuOwner = null;
        };
    }

    private void OnGitPanelContextMenu(object? sender, GitContextMenuEventArgs e)
    {
        var items = new List<ContextMenuItem>();
        switch (e.Target)
        {
            case GitContextMenuTarget.StagedFile:
                items.Add(new("Unstage", null, () => _ = GitUnstageFileAsync(e.FilePath!)));
                items.Add(new("Open File", null, () => _editorManager?.OpenFile(e.FilePath!)));
                items.Add(new("Diff", null, () => _ = GitShowDiffAsync(e.FilePath!)));
                items.Add(new("-"));
                items.Add(new("File Log", null, () => _ = GitShowFileLogAsync(e.FilePath!)));
                items.Add(new("Blame", null, () => _ = GitShowBlameAsync(e.FilePath!)));
                break;
            case GitContextMenuTarget.UnstagedFile:
                items.Add(new("Stage", null, () => _ = GitStageFileAsync(e.FilePath!)));
                items.Add(new("Open File", null, () => _editorManager?.OpenFile(e.FilePath!)));
                items.Add(new("Diff", null, () => _ = GitShowDiffAsync(e.FilePath!)));
                items.Add(new("-"));
                items.Add(new("Discard Changes", null, () => _ = GitDiscardFileAsync(e.FilePath!)));
                items.Add(new("-"));
                items.Add(new("File Log", null, () => _ = GitShowFileLogAsync(e.FilePath!)));
                items.Add(new("Blame", null, () => _ = GitShowBlameAsync(e.FilePath!)));
                break;
            case GitContextMenuTarget.CommitEntry:
                items.Add(new("Copy SHA", null, () => ClipboardHelper.SetText(e.LogEntry!.Sha)));
                items.Add(new("Show Full Log", null, () => _ = GitShowLogAsync()));
                break;
        }
        ShowContextMenu(items, e.ScreenX, e.ScreenY, _sidePanel?.TabControl);
    }

    private void ShowGitMoreMenu()
    {
        var items = new List<ContextMenuItem>
        {
            new("Stash…", null, () => _ = GitStashAsync()),
            new("Stash Pop", null, () => _ = GitStashPopAsync()),
            new("-"),
            new("Switch Branch…", null, () => _ = GitSwitchBranchAsync()),
            new("New Branch…", null, () => _ = GitNewBranchAsync()),
            new("-"),
            new("Discard All Changes", null, () => _ = GitDiscardAllAsync()),
            new("-"),
            new("Diff All", null, () => _ = GitShowDiffAllAsync()),
            new("Full Log", null, () => _ = GitShowLogAsync()),
        };
        var sp = _sidePanel!.TabControl;
        ShowContextMenu(items, sp.ActualX + 2, sp.ActualY + 3, sp);
    }

    private void OnExplorerContextMenu(object? sender, (string Path, System.Drawing.Point ScreenPosition, bool IsDirectory) e)
    {
        _ = BuildExplorerContextMenuAsync(e);
    }

    private async Task BuildExplorerContextMenuAsync((string Path, System.Drawing.Point ScreenPosition, bool IsDirectory) e)
    {
        var items = new List<ContextMenuItem>();
        var path = e.Path;
        var parentDir = e.IsDirectory ? path : Path.GetDirectoryName(path);

        items.Add(new ContextMenuItem("New File", "Ctrl+N",
            () => { if (parentDir != null) _ = HandleNewFileAsync(parentDir); }));
        items.Add(new ContextMenuItem("New Folder", "Ctrl+Shift+N",
            () => { if (parentDir != null) _ = HandleNewFolderAsync(parentDir); }));
        items.Add(new ContextMenuItem("-"));
        items.Add(new ContextMenuItem("Rename", "F2",
            () => _ = HandleRenameAsync(path)));
        items.Add(new ContextMenuItem("Delete", "Del",
            () => _ = HandleDeleteAsync(path)));

        // Git section — query file status
        if (!e.IsDirectory)
        {
            var isStaged = await _gitService.IsStagedAsync(_projectService.RootPath, path);
            var hasChanges = await _gitService.HasWorkingChangesAsync(_projectService.RootPath, path);
            var gitStatus = await _gitService.GetFileStatusAsync(_projectService.RootPath, path);

            if (isStaged || hasChanges || gitStatus != null)
            {
                items.Add(new ContextMenuItem("-"));
                if (hasChanges || gitStatus == GitFileStatus.Untracked)
                    items.Add(new ContextMenuItem("Git: Stage", null, () => _ = GitStageFileAsync(path)));
                if (isStaged)
                    items.Add(new ContextMenuItem("Git: Unstage", null, () => _ = GitUnstageFileAsync(path)));
                if (hasChanges || isStaged)
                    items.Add(new ContextMenuItem("Git: Diff", null, () => _ = GitShowDiffAsync(path)));
                if (hasChanges)
                    items.Add(new ContextMenuItem("Git: Discard Changes", null, () => _ = GitDiscardFileAsync(path)));
            }

            // Always show log/blame for tracked files
            if (gitStatus != GitFileStatus.Untracked || gitStatus == null)
            {
                if (!items.Any(i => i.Label.StartsWith("Git:")))
                    items.Add(new ContextMenuItem("-"));
                items.Add(new ContextMenuItem("Git: Log", null, () => _ = GitShowFileLogAsync(path)));
                items.Add(new ContextMenuItem("Git: Blame", null, () => _ = GitShowBlameAsync(path)));
            }
        }
        else
        {
            // Directory-level git
            items.Add(new ContextMenuItem("-"));
            items.Add(new ContextMenuItem("Git: Stage Folder", null, () => _ = GitStageFileAsync(path)));
            items.Add(new ContextMenuItem("Git: Unstage Folder", null, () => _ = GitUnstageFileAsync(path)));
        }

        items.Add(new ContextMenuItem("-"));
        items.Add(new ContextMenuItem("Copy Path", null,
            () => ClipboardHelper.SetText(path)));
        items.Add(new ContextMenuItem("Copy Relative Path", null,
            () => CopyRelativePath(path)));

        if (e.IsDirectory)
        {
            items.Add(new ContextMenuItem("-"));
            items.Add(new ContextMenuItem("Refresh", "F5",
                () => _ = RefreshExplorerAndGitAsync()));
        }

        ShowContextMenu(items, e.ScreenPosition.X, e.ScreenPosition.Y, _explorer?.Control);
    }

    private void OnTabContextMenu(object? sender, (string FilePath, System.Drawing.Point ScreenPosition) e)
    {
        _ = BuildTabContextMenuAsync(e);
    }

    private async Task BuildTabContextMenuAsync((string FilePath, System.Drawing.Point ScreenPosition) e)
    {
        var filePath = e.FilePath;
        var tabIndex = _editorManager!.GetTabIndexForPath(filePath);

        var items = new List<ContextMenuItem>
        {
            new("Close", "Ctrl+W", () =>
            {
                if (tabIndex >= 0) _editorManager.CloseTabAt(tabIndex);
            }),
            new("Close Others", null, () => _editorManager.CloseOthers(filePath)),
            new("Close All", null, () => _editorManager.CloseAll()),
            new("-"),
            new("Save", "Ctrl+S", () =>
            {
                if (tabIndex >= 0) _editorManager.SaveTabAt(tabIndex);
            }),
        };

        // Git section for the tab's file
        if (!filePath.StartsWith("__readonly:"))
        {
            var isStaged = await _gitService.IsStagedAsync(_projectService.RootPath, filePath);
            var hasChanges = await _gitService.HasWorkingChangesAsync(_projectService.RootPath, filePath);

            if (isStaged || hasChanges)
            {
                items.Add(new ContextMenuItem("-"));
                if (hasChanges)
                    items.Add(new ContextMenuItem("Git: Stage", null, () => _ = GitStageFileAsync(filePath)));
                if (isStaged)
                    items.Add(new ContextMenuItem("Git: Unstage", null, () => _ = GitUnstageFileAsync(filePath)));
                items.Add(new ContextMenuItem("Git: Diff", null, () => _ = GitShowDiffAsync(filePath)));
                if (hasChanges)
                    items.Add(new ContextMenuItem("Git: Discard Changes", null, () => _ = GitDiscardFileAsync(filePath)));
            }
        }

        items.Add(new ContextMenuItem("-"));
        items.Add(new ContextMenuItem("Copy Path", null, () => ClipboardHelper.SetText(filePath)));
        items.Add(new ContextMenuItem("Copy Relative Path", null, () => CopyRelativePath(filePath)));

        ShowContextMenu(items, e.ScreenPosition.X, e.ScreenPosition.Y, _editorManager?.TabControl);
    }

    private void OnEditorContextMenu(object? sender, (string? FilePath, System.Drawing.Point ScreenPosition) e)
    {
        var editor = _editorManager?.CurrentEditor;
        if (editor == null) return;

        bool hasLsp = _lsp != null;

        var items = new List<ContextMenuItem>
        {
            new("Cut", "Ctrl+X", () => editor.ProcessKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, true))),
            new("Copy", "Ctrl+C", () => editor.ProcessKey(new ConsoleKeyInfo('c', ConsoleKey.C, false, false, true))),
            new("Paste", "Ctrl+V", () => editor.ProcessKey(new ConsoleKeyInfo('v', ConsoleKey.V, false, false, true))),
            new("-"),
            new("Select All", "Ctrl+A", () => editor.ProcessKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, true))),
            new("-"),
            new("Go to Definition", "F12", () => _ = ShowGoToDefinitionAsync(), Enabled: hasLsp),
            new("Find References", "Shift+F12", () => _ = ShowFindReferencesAsync(), Enabled: hasLsp),
            new("Rename Symbol", "Ctrl+F2", () => _ = ShowRenameAsync(), Enabled: hasLsp),
            new("Hover Info", "Ctrl+K", () => _ = ShowHoverAsync(), Enabled: hasLsp),
        };

        // Git items for the current file
        if (e.FilePath != null && !e.FilePath.StartsWith("__readonly:"))
        {
            var filePath = e.FilePath;
            items.Add(new ContextMenuItem("-"));
            items.Add(new ContextMenuItem("Git: Diff", null, () => _ = GitShowDiffAsync(filePath)));
            items.Add(new ContextMenuItem("Git: Blame", null, () => _ = GitShowBlameAsync(filePath)));
        }

        ShowContextMenu(items, e.ScreenPosition.X, e.ScreenPosition.Y, editor);
    }

    private void CopyRelativePath(string fullPath)
    {
        var root = _projectService.RootPath;
        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ClipboardHelper.SetText(relative);
        }
        else
        {
            ClipboardHelper.SetText(fullPath);
        }
    }

    private void DismissCompletionPortal()
    {
        // Unsubscribe filter handler before clearing the portal reference
        var editor = _editorManager?.CurrentEditor;
        if (editor != null)
            editor.ContentChanged -= OnEditorContentChangedForCompletion;

        if (_completionPortalNode != null && _mainWindow != null)
        {
            _mainWindow.RemovePortal(editor ?? (IWindowControl)_mainWindow, _completionPortalNode);
            _completionPortalNode = null;
            _completionPortal = null;
        }
    }

    private void DismissTooltipPortal()
    {
        _tooltipAutoDismiss?.Dispose();
        _tooltipAutoDismiss = null;

        if (_tooltipPortalNode != null && _mainWindow != null)
        {
            _mainWindow.RemovePortal(_editorManager?.CurrentEditor ?? (IWindowControl)_mainWindow, _tooltipPortalNode);
            _tooltipPortalNode = null;
            _tooltipPortal = null;
        }
    }

    private void DismissLocationPortal()
    {
        if (_locationPortalNode != null && _mainWindow != null)
        {
            var editor = _editorManager?.CurrentEditor;
            _mainWindow.RemovePortal(editor ?? (IWindowControl)_mainWindow, _locationPortalNode);
            _locationPortalNode = null;
            _locationPortal = null;
        }
    }

    private void ShowLocationPortal(List<LspLocationEntry> entries, Action<LspLocationEntry> onAccepted)
    {
        DismissLocationPortal();
        var editor = _editorManager?.CurrentEditor;
        if (editor == null || _mainWindow == null) return;
        var cursor = _editorManager!.GetCursorBounds();
        var portal = new LspLocationListPortalContent(
            entries, cursor.X, cursor.Y,
            _mainWindow.Width, _mainWindow.Height);
        portal.Container = _mainWindow;
        portal.DismissRequested += (_, _) => DismissLocationPortal();
        _locationPortal = portal;
        _locationPortalNode = _mainWindow.CreatePortal(editor, portal);

        portal.ItemAccepted += (_, entry) =>
        {
            DismissLocationPortal();
            onAccepted(entry);
        };
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

    private void OnEditorContentChangedForCompletion(object? sender, string content)
    {
        var editor = _editorManager?.CurrentEditor;
        if (editor == null || _completionPortal == null) return;

        // If cursor moved to a different line, dismiss
        if (editor.CurrentLine != _completionTriggerLine)
        {
            DismissCompletionPortal();
            return;
        }

        int filterLen = editor.CurrentColumn - _completionTriggerColumn;
        if (filterLen < 0)
        {
            // User backspaced past the trigger point
            DismissCompletionPortal();
            return;
        }

        // Extract filter text directly from editor content
        string filterText = string.Empty;
        if (filterLen > 0)
        {
            var lines = content.Split('\n');
            int lineIdx = editor.CurrentLine - 1;
            if (lineIdx >= 0 && lineIdx < lines.Length)
            {
                var line = lines[lineIdx];
                int start = _completionTriggerColumn - 1;
                int len   = Math.Min(filterLen, line.Length - start);
                if (len > 0 && start >= 0 && start + len <= line.Length)
                    filterText = line.Substring(start, len);
            }
        }

        _completionPortal.SetFilter(filterText);

        if (!_completionPortal.HasVisibleItems)
            DismissCompletionPortal();
        else
            _mainWindow?.Invalidate(false);
    }

    private void TryScheduleDotCompletion(string filePath, string content)
    {
        // Only trigger auto-complete on '.' and signature help on '(' / ',' in C# files
        if (_lsp == null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        var editor = _editorManager?.CurrentEditor;
        if (editor == null) return;

        int col = editor.CurrentColumn - 1;   // 0-based
        var lines = content.Split('\n');
        int lineIdx = editor.CurrentLine - 1;
        if (lineIdx < 0 || lineIdx >= lines.Length) return;

        string currentLine = lines[lineIdx];
        if (col <= 0 || col > currentLine.Length) return;

        char lastChar = currentLine[col - 1];

        if (lastChar == '.')
        {
            // Flush the pending DidChange immediately so the LSP has the dot before
            // we request completions.  Then wait ~300 ms for the server to process it.
            _ = _lsp.FlushPendingChangeAsync();

            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = new Timer(
                _ => _ = ShowCompletionAsync(silent: true),
                null, 350, Timeout.Infinite);
        }
        else if (lastChar is '(' or ',')
        {
            // Auto-trigger signature help when entering function arguments
            _ = _lsp.FlushPendingChangeAsync();

            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = new Timer(
                _ => _ = ShowSignatureHelpAsync(silent: true),
                null, 250, Timeout.Infinite);
        }
        else if (IsIdentifierChar(lastChar) && _completionPortal == null)
        {
            // Count identifier chars back from cursor to find word length
            int wordLen = 0;
            int i = col - 1;
            while (i >= 0 && IsIdentifierChar(currentLine[i])) { wordLen++; i--; }

            // If the word is preceded by a dot, skip — dot-trigger already handles this
            bool afterDot = i >= 0 && currentLine[i] == '.';

            if (wordLen >= 3 && !afterDot)
            {
                _dotTriggerDebounce?.Dispose();
                _dotTriggerDebounce = new Timer(
                    _ => _ = ShowCompletionAsync(silent: true),
                    null, 300, Timeout.Infinite);
            }
        }
    }

    private async Task FormatDocumentAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var edits = await _lsp.FormattingAsync(path);
        if (edits.Count == 0) return;

        var lines = editor.Content.Split('\n').ToList();

        // Apply edits in reverse order to preserve offsets
        var sortedEdits = edits
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character)
            .ToList();

        foreach (var edit in sortedEdits)
        {
            int startLine = Math.Min(edit.Range.Start.Line, lines.Count - 1);
            int startChar = edit.Range.Start.Character;
            int endLine   = Math.Min(edit.Range.End.Line, lines.Count - 1);
            int endChar   = edit.Range.End.Character;

            if (startLine == endLine)
            {
                var line = lines[startLine];
                startChar = Math.Min(startChar, line.Length);
                endChar   = Math.Min(endChar, line.Length);
                lines[startLine] = line[..startChar] + edit.NewText + line[endChar..];
            }
            else
            {
                var startLineStr = lines[startLine];
                var endLineStr   = lines[endLine];
                startChar = Math.Min(startChar, startLineStr.Length);
                endChar   = Math.Min(endChar, endLineStr.Length);
                var combined = startLineStr[..startChar] + edit.NewText + endLineStr[endChar..];
                lines.RemoveRange(startLine, endLine - startLine + 1);
                lines.InsertRange(startLine, combined.Split('\n'));
            }
        }

        editor.Content = string.Join('\n', lines);
    }

    // ──────────────────────────────────────────────────────────────
    // LSP: Find References, Go-to-Impl, Rename, Code Actions, Symbols
    // ──────────────────────────────────────────────────────────────

    private void NavigateToLocation(LspLocationEntry entry)
    {
        var editor = _editorManager?.CurrentEditor;
        var currentPath = _editorManager?.CurrentFilePath;
        if (currentPath != null && editor != null)
            _navHistory.Push((currentPath, editor.CurrentLine, editor.CurrentColumn));

        _editorManager?.OpenFile(entry.FilePath);
        var targetEditor = _editorManager?.CurrentEditor;
        if (targetEditor != null)
        {
            targetEditor.GoToLine(entry.Line);
            targetEditor.SetLogicalCursorPosition(
                new System.Drawing.Point(entry.Column - 1, entry.Line - 1));
        }
    }

    private List<LspLocationEntry> LocationsToEntries(List<LspLocation> locations)
    {
        return locations.Select(loc =>
        {
            var filePath = LspClient.UriToPath(loc.Uri);
            var contextLine = TryReadLineFromFile(filePath, loc.Range.Start.Line);
            return new LspLocationEntry(
                filePath,
                loc.Range.Start.Line + 1,
                loc.Range.Start.Character + 1,
                contextLine ?? "(location)");
        }).ToList();
    }

    private async Task ShowFindReferencesAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var locations = await _lsp.ReferencesAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (locations.Count == 0)
        {
            ShowTransientTooltip("No references found at cursor.");
            return;
        }

        ShowLocationPortal(LocationsToEntries(locations), NavigateToLocation);
    }

    private async Task ShowGoToImplementationAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var locations = await _lsp.ImplementationAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (locations.Count == 0)
        {
            ShowTransientTooltip("No implementation found at cursor.");
            return;
        }

        if (locations.Count == 1)
        {
            var loc = locations[0];
            NavigateToLocation(new LspLocationEntry(
                LspClient.UriToPath(loc.Uri),
                loc.Range.Start.Line + 1,
                loc.Range.Start.Character + 1,
                ""));
        }
        else
        {
            ShowLocationPortal(LocationsToEntries(locations), NavigateToLocation);
        }
    }

    private async Task ShowRenameAsync()
    {
        try
        {
            if (_lsp == null || _editorManager?.CurrentEditor == null)
            {
                ShowTransientTooltip("LSP not running.");
                return;
            }
            var editor = _editorManager.CurrentEditor;
            var path = _editorManager.CurrentFilePath;
            if (path == null) return;

            // Extract the word under cursor (fast, no LSP round-trip)
            string currentName = ExtractWordAtCursor(editor);
            if (string.IsNullOrEmpty(currentName))
            {
                ShowTransientTooltip("No symbol at cursor.");
                return;
            }

            // Show dialog immediately with the locally extracted name
            var newName = await RenameDialog.ShowAsync(_ws, currentName);
            if (newName == null) return;

            var workspaceEdit = await _lsp.RenameAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1, newName);
            if (workspaceEdit?.Changes == null || workspaceEdit.Changes.Count == 0)
            {
                ShowTransientTooltip("LSP returned no edits.");
                return;
            }

            ApplyWorkspaceEdit(workspaceEdit);
            _ws.NotificationStateService.ShowNotification(
                "Rename", $"Renamed '{currentName}' to '{newName}' in {workspaceEdit.Changes.Count} file(s).",
                SharpConsoleUI.Core.NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            _ws.NotificationStateService.ShowNotification(
                "Rename Error", ex.Message, SharpConsoleUI.Core.NotificationSeverity.Danger);
        }
    }

    private static string ExtractWordAtCursor(MultilineEditControl editor)
    {
        var lines = editor.Content.Split('\n');
        int lineIdx = editor.CurrentLine - 1;
        if (lineIdx < 0 || lineIdx >= lines.Length) return "";
        var line = lines[lineIdx];
        int col = Math.Min(editor.CurrentColumn - 1, line.Length);
        int start = col, end = col;
        while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
        while (end < line.Length && IsIdentifierChar(line[end])) end++;
        return start < end ? line[start..end] : "";
    }

    private async Task ShowCodeActionsAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null || _mainWindow == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        int line = editor.CurrentLine - 1;
        int col = editor.CurrentColumn - 1;

        var actions = await _lsp.CodeActionAsync(path, line, col, line, col);
        if (actions.Count == 0)
        {
            ShowTransientTooltip("No code actions available at cursor.");
            return;
        }

        // Show as a completion-style portal list
        var items = actions.Select(a => new CompletionItem(a.Title, a.Kind, null, 1)).ToList();

        DismissCompletionPortal();
        var cursor = _editorManager.GetCursorBounds();
        var portal = new LspCompletionPortalContent(
            items, cursor.X, cursor.Y,
            _mainWindow.Width, _mainWindow.Height);

        portal.Container = _mainWindow;
        _completionPortal = portal;
        _completionPortalNode = _mainWindow.CreatePortal(editor, portal);
        _completionTriggerColumn = editor.CurrentColumn;
        _completionTriggerLine = editor.CurrentLine;

        portal.DismissRequested += (_, _) => DismissCompletionPortal();

        portal.ItemAccepted += (_, item) =>
        {
            DismissCompletionPortal();
            // Find the matching action by title
            var action = actions.FirstOrDefault(a => a.Title == item.Label);
            if (action?.Edit != null)
            {
                ApplyWorkspaceEdit(action.Edit);
                _ws.NotificationStateService.ShowNotification(
                    "Code Action", $"Applied: {action.Title}",
                    SharpConsoleUI.Core.NotificationSeverity.Info);
            }
        };
    }

    private async Task ShowDocumentSymbolsAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null) return;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var symbols = await _lsp.DocumentSymbolAsync(path);
        if (symbols.Count == 0)
        {
            ShowTransientTooltip("No symbols found in document.");
            return;
        }

        // Flatten symbol tree into a list
        var flat = new List<(string Display, DocumentSymbol Symbol, int Depth)>();
        void Flatten(List<DocumentSymbol> syms, int depth)
        {
            foreach (var s in syms)
            {
                flat.Add((s.Name, s, depth));
                if (s.Children != null)
                    Flatten(s.Children, depth + 1);
            }
        }
        Flatten(symbols, 0);

        // Build command palette style using the same registry/dialog patterns
        var tempRegistry = new CommandRegistry();
        foreach (var (display, sym, depth) in flat)
        {
            var indent = new string(' ', depth * 2);
            var kindName = GetSymbolKindName(sym.Kind);
            var s = sym; // capture
            tempRegistry.Register(new IdeCommand
            {
                Id = $"sym.{sym.SelectionRange.Start.Line}.{sym.Name}",
                Category = kindName,
                Label = $"{indent}{sym.Name}",
                Keybinding = $"Ln {sym.SelectionRange.Start.Line + 1}",
                Execute = () => NavigateToLocation(new LspLocationEntry(
                    path!, s.SelectionRange.Start.Line + 1,
                    s.SelectionRange.Start.Character + 1, s.Name)),
                Priority = 100 - sym.SelectionRange.Start.Line  // sort by position
            });
        }

        CommandPaletteDialog.Show(_ws, tempRegistry, cmd =>
        {
            if (cmd == null) return;
            cmd.Execute();
            var editor = _editorManager?.CurrentEditor;
            if (editor != null) _mainWindow?.FocusControl(editor);
        });
    }

    // ── Workspace edit application ───────────────────────────────────────────────

    private void ApplyWorkspaceEdit(WorkspaceEdit edit)
    {
        if (edit.Changes == null) return;

        foreach (var (uri, textEdits) in edit.Changes)
        {
            var filePath = LspClient.UriToPath(uri);

            // Check if file is open in the editor
            var openEditor = GetEditorForFile(filePath);
            if (openEditor != null)
            {
                ApplyTextEdits(openEditor, textEdits);
            }
            else
            {
                // Apply to file on disk
                try
                {
                    var content = FileService.ReadFile(filePath);
                    var lines = content.Split('\n').ToList();
                    ApplyTextEditsToLines(lines, textEdits);
                    FileService.WriteFile(filePath, string.Join('\n', lines));
                }
                catch { }
            }
        }
    }

    private MultilineEditControl? GetEditorForFile(string filePath)
    {
        if (_editorManager == null) return null;
        // Try to find an open editor tab for this file path
        foreach (var (fp, content) in _editorManager.GetOpenDocuments())
        {
            if (string.Equals(fp, filePath, StringComparison.OrdinalIgnoreCase))
            {
                _editorManager.OpenFile(filePath); // Switch to it
                return _editorManager.CurrentEditor;
            }
        }
        return null;
    }

    private static void ApplyTextEdits(MultilineEditControl editor, List<TextEdit> edits)
    {
        var lines = editor.Content.Split('\n').ToList();
        ApplyTextEditsToLines(lines, edits);
        editor.Content = string.Join('\n', lines);
    }

    private static void ApplyTextEditsToLines(List<string> lines, List<TextEdit> edits)
    {
        // Apply in reverse order to preserve offsets
        var sorted = edits
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character)
            .ToList();

        foreach (var edit in sorted)
        {
            int startLine = Math.Min(edit.Range.Start.Line, lines.Count - 1);
            int startChar = edit.Range.Start.Character;
            int endLine = Math.Min(edit.Range.End.Line, lines.Count - 1);
            int endChar = edit.Range.End.Character;

            if (startLine < 0) startLine = 0;
            if (endLine < 0) endLine = 0;

            if (startLine == endLine)
            {
                var line = lines[startLine];
                startChar = Math.Min(startChar, line.Length);
                endChar = Math.Min(endChar, line.Length);
                lines[startLine] = line[..startChar] + edit.NewText + line[endChar..];
            }
            else
            {
                var startLineStr = lines[startLine];
                var endLineStr = lines[endLine];
                startChar = Math.Min(startChar, startLineStr.Length);
                endChar = Math.Min(endChar, endLineStr.Length);
                var combined = startLineStr[..startChar] + edit.NewText + endLineStr[endChar..];
                lines.RemoveRange(startLine, endLine - startLine + 1);
                lines.InsertRange(startLine, combined.Split('\n'));
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string? TryReadLineFromFile(string filePath, int lineIndex)
    {
        try
        {
            var content = FileService.ReadFile(filePath);
            var lines = content.Split('\n');
            if (lineIndex >= 0 && lineIndex < lines.Length)
                return lines[lineIndex].Trim();
        }
        catch { }
        return null;
    }

    private static string GetSymbolKindName(int kind) => kind switch
    {
        1 => "File",
        2 => "Module",
        3 => "Namespace",
        4 => "Package",
        5 => "Class",
        6 => "Method",
        7 => "Property",
        8 => "Field",
        9 => "Constructor",
        10 => "Enum",
        11 => "Interface",
        12 => "Function",
        13 => "Variable",
        14 => "Constant",
        15 => "String",
        16 => "Number",
        17 => "Boolean",
        18 => "Array",
        19 => "Object",
        22 => "Struct",
        23 => "Event",
        24 => "Operator",
        25 => "TypeParam",
        _ => "Symbol"
    };

    // ──────────────────────────────────────────────────────────────
    // Status bar updates
    // ──────────────────────────────────────────────────────────────

    private void UpdateDashboard()
    {
        _dashboard?.SetContent(GetDashboardLines());
    }

    private List<string> GetDashboardLines()
    {
        var projectName = new DirectoryInfo(_projectService.RootPath).Name;
        var rootPath    = _projectService.RootPath;

        List<string> lspLines;
        if (!_lspDetectionDone)
            lspLines = new List<string> { "[dim]  LSP      ○ detecting…[/]" };
        else if (_lspStarted)
            lspLines = new List<string> { $"[dim]  LSP      [/][green]● {Markup.Escape(_detectedLspExe!)}[/]" };
        else if (_detectedLspExe != null)
            lspLines = new List<string> { $"[dim]  LSP      ○ {Markup.Escape(_detectedLspExe)} (failed to start)[/]" };
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

    private void ShowAbout()
    {
        if (_aboutOpen) return;
        _aboutOpen = true;
        _aboutRefresh = AboutDialog.Show(_ws, () => new AboutInfo(
            LspStarted:       _lspStarted,
            LspDetectionDone: _lspDetectionDone,
            DetectedLspExe:   _detectedLspExe,
            Tools:            _config.Tools,
            ProjectPath:      _projectService.RootPath),
            () => { _aboutOpen = false; _aboutRefresh = null; });
    }

    private void UpdateErrorCount(List<BuildDiagnostic> diagnostics)
    {
        int errors = diagnostics.Count(d => d.Severity == "error");
        int warnings = diagnostics.Count(d => d.Severity == "warning");

        string text = "";
        if (errors > 0 && warnings > 0)
            text = $"[red]● {errors} error{(errors != 1 ? "s" : "")}[/]  [yellow]▲ {warnings} warn[/]";
        else if (errors > 0)
            text = $"[red]● {errors} error{(errors != 1 ? "s" : "")}[/]";
        else if (warnings > 0)
            text = $"[yellow]▲ {warnings} warn[/]";
        else
            text = "[green]✓ Build OK[/]";

        _errorMarkup = text;
        UpdateInlineStatus();
    }
}
