using System.Collections.Concurrent;
using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class IdeApp : IDisposable
{
    private const int RenderFrameMs = 80;

    private readonly ConsoleWindowSystem _ws;
    private readonly ProjectService _projectService;
    private readonly BuildService _buildService;
    private readonly GitService _gitService;

    private Window? _mainWindow;
    private ExplorerPanel? _explorer;
    private EditorManager? _editorManager;
    private OutputPanel? _outputPanel;
    private FileMiddlewarePipeline? _pipeline;

    // Extracted handlers
    private GitCoordinator? _gitOps;
    private BuildCoordinator? _buildOps;
    private WorkspaceStateManager? _workspaceState;
    private LspCoordinator? _lspCoord;
    private DebugCoordinator? _debugCoord;

    // Status bar
    private MarkupControl? _statusLeft;   // git + error combined
    private MarkupControl? _cursorStatus;
    private MarkupControl? _syntaxStatus;
    private MarkupControl? _lspStatus;
    private string _errorMarkup = "";
    private bool _lspBusy;
    private int _lspSpinnerFrame;
    private MarkupControl? _dashboard;

    // Layout controls (referenced during CreateLayout, owned by LayoutController after init)

    // System bottom status bar
    private readonly IdeStatusBar _bottomBar = new();
    private readonly SharpConsoleUI.Panel.StatusTextElement _bottomStatusElement = new("");

    // Thread-safe queues for streaming build/test output
    private readonly ConcurrentQueue<string> _buildLines = new();
    private readonly ConcurrentQueue<string> _testLines = new();
    private readonly ConcurrentQueue<Action> _pendingUiActions = new();

    // File search
    private readonly FileSearchService _fileSearchService = new();
    private CancellationTokenSource? _searchCts;

    // File system watcher
    private FileWatcher? _fileWatcher;

    // False when launched on a folder with no .sln/project file nearby (e.g. $HOME):
    // the IDE then avoids whole-tree work — recursive file watching and the LSP server.
    private bool _isDotNetWorkspace;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Layout controller
    private LayoutController? _layout;

    // Config
    private IdeConfig _config = new();

    // Menu bar
    private MenuBarBuilder? _menuBar;

    // Command registry
    private readonly CommandRegistry _commandRegistry = new();

    // Side panel (symbols browser)
    private SidePanel? _sidePanel;
    private GridControl? _mainContent;

    // Context menu
    private ContextMenuBuilder? _contextMenu;

    // Named constants for command priorities (higher = more important in palette ordering)
    private static class CommandPriority
    {
        public const int Critical      = 90;  // Save, Build, Run, Source Control, Go to Definition
        public const int CriticalMinus = 88;  // Go to Implementation
        public const int CriticalLow   = 87;  // Find All References
        public const int High          = 85;  // Close Tab, Test, Navigate Back
        public const int HighMinus     = 83;  // Rename Symbol
        public const int HighLow       = 82;  // Code Actions
        public const int HighLowest    = 81;  // Document Symbols
        public const int Medium        = 80;  // Open Folder, Stop, Commit, Find, Toggle Explorer, Shell, Hover
        public const int Normal        = 75;  // New File, Replace, Toggle Output, Shell Tab, Signature Help
        public const int NormalMinus   = 74;  // New Folder
        public const int NormalLow     = 73;  // Rename, Side Panel Shell
        public const int NormalLowest  = 72;  // Delete
        public const int Default       = 70;  // Refresh, Clean, Format, Toggle Side Panel, LazyNuGet
        public const int GitStage      = 68;  // Stage File
        public const int GitUnstage    = 67;  // Unstage File
        public const int GitStageAll   = 66;  // Stage All
        public const int Low           = 65;  // Completions, NuGet, Unstage All
        public const int GitPull       = 64;  // Pull
        public const int GitPush       = 63;  // Push
        public const int Lower         = 60;  // Reload, Edit Config, Diff File
        public const int GitDiffAll    = 59;  // Diff All Changes
        public const int CustomTool    = 55;  // Custom tool (tab)
        public const int CustomToolAlt = 54;  // Custom tool (bottom shell), Stash Pop
        public const int CustomToolSide = 53; // Custom tool (side panel)
        public const int Minor         = 50;  // Wrap modes, Log
        public const int GitLogFile    = 49;  // Log (Current File)
        public const int GitBlame      = 48;  // Blame
        public const int GitBranch     = 45;  // Switch Branch
        public const int GitNewBranch  = 44;  // New Branch
        public const int GitIgnoreAdd  = 42;  // Add to .gitignore
        public const int GitIgnoreRemove = 41; // Remove from .gitignore
        public const int GitDiscard    = 40;  // Discard Changes (Current File)
        public const int GitDiscardAll = 35;  // Discard All Changes
        public const int Least         = 10;  // Exit, About
    }

    public IdeApp(string projectPath)
    {
        _ws = new ConsoleWindowSystem(
            new NetConsoleDriver(RenderMode.Buffer),
            options: new ConsoleWindowSystemOptions(
                ShowTopPanel: false,
                ShowBottomPanel: false,
                BottomPanelConfig: panel => panel.Left(_bottomStatusElement),
                InstallSynchronizationContext: true));
        _projectService = new ProjectService(projectPath);
        _isDotNetWorkspace = _projectService.ContainsDotNetProject();
        _buildService = new BuildService();
        _gitService = new GitService();

        InitBottomStatus();  // Must be before CreateLayout so DesktopDimensions accounts for the bar
        CreateLayout();

        // Create shared context (after CreateLayout so UI components exist)
        var appCtx = new AppContext
        {
            WindowSystem = _ws,
            ProjectService = _projectService,
            BuildService = _buildService,
            GitService = _gitService,
            EditorManager = _editorManager!,
            Explorer = _explorer!,
            OutputPanel = _outputPanel!,
            SidePanel = _sidePanel!,
            Config = _config,
            MainWindow = _mainWindow!,
            MainGrid = _mainContent!,
            FileWatcher = _fileWatcher!,
            Pipeline = _pipeline!,
            WorkspaceService = new WorkspaceService(projectPath),
            PendingUiActions = _pendingUiActions,
            BuildLines = _buildLines,
            TestLines = _testLines,
            CancellationToken = _cts.Token,
        };

        _gitOps = new GitCoordinator(appCtx);
        _buildOps = new BuildCoordinator(appCtx);

        // Open the shell tab at startup so it's ready immediately
        if (IdeConstants.IsDesktopOs)
        {
            _outputPanel!.LaunchShell(_projectService.RootPath);
            _buildOps.OutputShellCount = 1;
        }

        _lspCoord = new LspCoordinator(appCtx);
        _debugCoord = new DebugCoordinator(appCtx);

        _layout = new LayoutController(appCtx, _lspCoord, _debugCoord, _mainContent, _dashboard);
        _layout.UpdateDashboard();
        WireHandlerEvents();

        _menuBar = new MenuBarBuilder(appCtx, _gitOps, _buildOps, _lspCoord, _debugCoord, _layout);
        var fileOps = new FileOperationDelegates(
            HandleNewFileAsync, HandleNewFolderAsync,
            HandleRenameAsync, HandleDeleteAsync,
            () => _gitOps!.RefreshExplorerAndGitAsync());
        _menuBar.FileOps = fileOps;
        _menuBar.OpenFolderAsync = OpenFolderAsync;
        _menuBar.CloseCurrentTab = CloseCurrentTab;
        _menuBar.ReloadCurrentFromDisk = ReloadCurrentFromDisk;
        _menuBar.ShowCommandPalette = ShowCommandPalette;
        _menuBar.HandleF5 = HandleF5;
        _menuBar.HandleF5WithProfile = HandleF5WithProfile;

        // Menu/toolbar were no-ops during BuildMainWindow (_menuBar was null).
        // Now that _menuBar is ready, place them into grid rows 0 and 1.
        AddMenuBar();
        AddToolbar();

        _contextMenu = new ContextMenuBuilder(appCtx, _gitOps, _lspCoord);
        _contextMenu.FileOps = fileOps;

        _workspaceState = new WorkspaceStateManager(appCtx);
        _workspaceState.DebugCoordinator = _debugCoord;
        _workspaceState.Load();
        RestoreWorkspaceState();
        InitializeCommands();

        // Session header: what was opened, so the feed has a starting point.
        var projName = new DirectoryInfo(_projectService.RootPath).Name;
        _outputPanel!.AppendHeader($"lazydotide — {projName}");
        _outputPanel.AppendOutputLine($"Workspace: {_projectService.RootPath}");
        if (!_isDotNetWorkspace)
            _pendingUiActions.Enqueue(ShowNoProjectNotice);

        // Async post-init: git status + optional LSP
        _ = PostInitAsync(projectPath);
    }

    public void Run() => _ws.Run();

    // ── File Search ─────────────────────────────────────────────

    private void ShowSearch()
    {
        if (_layout != null && !_layout.OutputVisible)
            _layout.ToggleOutput();
        _outputPanel?.SwitchToSearchTab();
    }

    private async Task RunFileSearchAsync(string term, bool caseSensitive)
    {
        // Cancel any previous search
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _pendingUiActions.Enqueue(() =>
        {
            _outputPanel?.ClearSearchResults();
            _outputPanel?.SetSearchStatus("[dim]Searching…[/]");
        });

        if (string.IsNullOrWhiteSpace(term))
        {
            _pendingUiActions.Enqueue(() => _outputPanel?.SetSearchStatus("[dim]Ready[/]"));
            return;
        }

        var rootPath = _projectService.RootPath;
        Func<string, bool>? isIgnored = null;
        try { isIgnored = _gitService.CreateIgnoreChecker(rootPath); }
        catch { } // graceful fallback: search without .gitignore filtering

        int count = 0;
        try
        {
            await foreach (var result in _fileSearchService.SearchAsync(rootPath, term, caseSensitive, isIgnored, ct))
            {
                var r = result;
                _pendingUiActions.Enqueue(() => _outputPanel?.AddSearchResult(r, rootPath));
                count++;
                if (count % 25 == 0)
                {
                    var c = count;
                    _pendingUiActions.Enqueue(() => _outputPanel?.SetSearchStatus($"[dim]{c} results…[/]"));
                }
            }
        }
        catch (OperationCanceledException) { return; }

        var final = count;
        _pendingUiActions.Enqueue(() =>
            _outputPanel?.SetSearchStatus(final == 0
                ? "[yellow]No results[/]"
                : $"[dim]{final} result{(final != 1 ? "s" : "")}[/]"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        try { CaptureWorkspaceState(); _workspaceState?.Save(); } catch { } // Best effort — don't prevent shutdown
        _cts.Cancel();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _fileWatcher?.Dispose();
        _contextMenu?.DismissContextMenu();

        // Block on LSP shutdown so the server receives the shutdown+exit sequence.
        // Timeout after 3 seconds to avoid hanging on a dead server.
        if (_lspCoord != null)
        {
            try
            {
                _lspCoord.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch { } // Best effort — don't prevent app exit
        }
        if (_debugCoord != null)
        {
            try
            {
                _debugCoord.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch { } // Best effort — don't prevent app exit
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Layout
    // ──────────────────────────────────────────────────────────────

    private void CreateLayout()
    {
        ConfigService.EnsureDefaultConfig();
        _config = ConfigService.Load();

        var desktop = _ws.DesktopDimensions;

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
        _outputPanel = new OutputPanel(_ws, _pendingUiActions);
        _sidePanel = new SidePanel();

        BuildMainWindow(desktop.Width, desktop.Height);

        _ws.AddWindow(_mainWindow!);
        _ws.SetActiveWindow(_mainWindow!);

        _fileWatcher = new FileWatcher();
        _fileWatcher.Watch(_projectService.RootPath, recursive: _isDotNetWorkspace);

        WireEvents();
    }

    private void BuildMainWindow(int width, int height)
    {
        _mainWindow = new WindowBuilder(_ws)
            .HideTitle()
            // Frameless, not Borderless: Borderless keeps the 1-cell frame
            // reserved and merely paints it invisibly, while Frameless reclaims
            // it so the grid fills the whole window rect. The IDE owns the
            // entire terminal and has its own menu, status bar and splitters,
            // so a window frame is two rows and two columns spent on nothing.
            .Frameless()
            .HideTitleButtons()
            .WithSize(width, height)
            .AtPosition(0, 0)
            .Movable(false)
            .Minimizable(false)
            .Maximizable(false)
            .Resizable(false)
            .WithAsyncWindowThread(MainWindowThreadAsync)
            .Build();

        // One grid owns the whole working area: menu, toolbar, the three
        // columns and the bottom panel. Panel sizes ARE track definitions, so
        // there is no parallel layout state to keep in sync. The status bar
        // stays docked below it (window chrome, never resized).
        AddMainContentArea();
        _mainWindow.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        AddStatusBar();
    }

    private void AddMenuBar() => _menuBar?.AddMenuBar();
    private void AddToolbar() => _menuBar?.AddToolbar();

    private void AddMainContentArea()
    {
        // 4 rows x 3 columns. Row 0 menu, row 1 toolbar (both spanning all
        // columns), row 2 the three resizable columns, row 3 the bottom panel.
        //
        // Every size below is a track definition, and every minimum is a track
        // Min that GridSplitterResize clamps drags against. That is the whole
        // point of the migration: OutputPanelHeight, TabControl.Height and the
        // SplitterMoved handler used to carry this state by hand.
        _mainContent = new GridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        var grid = _mainContent;

        // All three columns are Star for the same reason both split rows are:
        // GridSplitterResize holds a Fixed track and only rescales the Star one
        // (library test StarFixed_StarAbsorbs_FixedHeld), so a Fixed side column
        // simply could not be dragged. Star|Star conserves the weight sum, so
        // the boundary moves. Weights are column counts, set from the saved
        // widths by RestoreColumnWidths.
        grid.ColumnDefinitions.Add(GridLength.Star(IdeConstants.DefaultExplorerWidth, min: IdeConstants.MinSideColumnWidth));
        grid.ColumnDefinitions.Add(GridLength.Star(100, min: IdeConstants.MinEditorWidth));
        grid.ColumnDefinitions.Add(GridLength.Star(IdeConstants.DefaultSidePanelWidth, min: IdeConstants.MinSideColumnWidth));

        grid.RowDefinitions.Add(GridLength.Auto());                                                          // menu
        grid.RowDefinitions.Add(GridLength.Auto());                                                          // toolbar
        grid.RowDefinitions.Add(GridLength.Star(1, min: IdeConstants.MinMainHeight));                        // columns
        // The bottom panel is Star, not Cells, BECAUSE of the splitter: with a
        // Star above a Fixed track, GridSplitterResize deliberately holds the
        // fixed one and only rescales the Star's weight, which changes nothing
        // when it is the only Star (library test StarFixed_StarAbsorbs_FixedHeld).
        // Star|Star conserves the weight sum, so the boundary actually moves.
        // The weight is set from the desired row count in ApplyOutputHeight.
        grid.RowDefinitions.Add(GridLength.Star(1, min: IdeConstants.MinOutputHeight));                      // bottom panel

        // Explorer: row 2 col 0. Gains RowSpan 2 when full height, which also
        // moves the bottom panel out of column 0 (see ApplyExplorerFullHeight).
        grid.Place(_explorer!.Control, IdeConstants.ColumnsRow, IdeConstants.ExplorerCol);

        // The editor cell holds a dashboard (shown until a file is opened) over
        // the tab control. A nested single-column grid stacks them, which keeps
        // the whole layout in one model rather than reaching back for a
        // ColumnContainer (whose constructor only accepts HorizontalGridControl).
        var editorCell = new GridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        editorCell.ColumnDefinitions.Add(GridLength.Star(1));
        editorCell.RowDefinitions.Add(GridLength.Star(1));

        // Both occupy the SAME cell on purpose. They are mutually exclusive
        // (dashboard until a file is open, tabs after), so they never paint
        // over each other, and sharing one row means the visible one gets the
        // whole area. Separate rows would reserve space for the hidden one: a
        // Star row holding an invisible control still claims its share, which
        // left the tab control at half height starting halfway down the pane.
        _dashboard = new MarkupControl(new List<string> { "", "[bold]  lazydotide[/]  [dim]loading…[/]" })
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _editorManager!.TabControl.Visible = false;
        editorCell.Place(_dashboard, 0, 0);
        editorCell.Place(_editorManager.TabControl, 0, 0);
        grid.Place(editorCell, IdeConstants.ColumnsRow, IdeConstants.EditorCol);

        grid.Place(_sidePanel!.TabControl, IdeConstants.ColumnsRow, IdeConstants.SidePanelCol);

        grid.Place(_outputPanel!.TabControl, IdeConstants.OutputRow, IdeConstants.ExplorerCol,
            colSpan: IdeConstants.GridColumnCount);

        // Splitter geometry derives itself from the spans: a boundary blocked
        // by a spanning cell draws no handle and is not a Tab stop.
        grid.AddColumnSplitterAfter(IdeConstants.ExplorerCol);
        grid.AddColumnSplitterAfter(IdeConstants.EditorCol);
        grid.AddRowSplitterAfter(IdeConstants.ColumnsRow);

        _mainWindow!.AddControl(grid);

        // Menu and toolbar occupy rows 0 and 1, placed by MenuBarBuilder once
        // it exists (these calls are no-ops here, as they always were: _menuBar
        // is still null during CreateLayout, and InitializeAsync calls them
        // again once it is ready).
        AddMenuBar();
        AddToolbar();
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
        _statusLeft = new MarkupControl(new List<string> { IdeConstants.GitStatusDefault });
        leftCol.AddContent(_statusLeft);
        statusBar.AddColumn(leftCol);

        // LSP activity indicator
        var lspCol = new ColumnContainer(statusBar) { Width = 10 };
        _lspStatus = new MarkupControl(new List<string> { "" })
        {
            HorizontalAlignment = HorizontalAlignment.Right
        };
        lspCol.AddContent(_lspStatus);
        statusBar.AddColumn(lspCol);

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
        bar.AddSegment(_gitOps?.GitMarkup ?? IdeConstants.GitStatusDefault, " ");
        if (!string.IsNullOrEmpty(_errorMarkup))
            bar.AddSegment("[dim] | [/]", " | ")
               .AddSegment(_errorMarkup, " ");
        _statusLeft?.SetContent(new List<string> { bar.Render() });
    }


    // ──────────────────────────────────────────────────────────────
    private void OnDocumentSaved(string savedPath)
    {
        _ = _lspCoord?.DidSaveAsync(savedPath);
        _lspCoord?.ScheduleSemanticTokenRefresh(savedPath, immediate: true);
        _fileWatcher?.SuppressNext(savedPath);
        _ = _gitOps!.RefreshGitFileStatusesAsync();
        _ = _gitOps!.RefreshGitDiffMarkersForFileAsync(savedPath);
        if (string.Equals(savedPath, ConfigService.GetConfigPath(), StringComparison.OrdinalIgnoreCase))
        {
            _config = ConfigService.Load();
            _layout?.UpdateDashboard();
            _ws.NotificationStateService.ShowNotification(
                "Config Reloaded",
                "Config loaded. New/removed tools will appear after restart.",
                SharpConsoleUI.Core.NotificationSeverity.Info);
        }
    }

    // Event wiring
    // ──────────────────────────────────────────────────────────────

    private void WireHandlerEvents()
    {
        _gitOps!.GitStatusMarkupChanged += (_, _) => _pendingUiActions.Enqueue(() => UpdateInlineStatus());
        _buildOps!.DiagnosticsUpdated += (_, diags) => _pendingUiActions.Enqueue(() => UpdateErrorCount(diags));
        _buildOps.OutputRequired += () => { if (!_layout!.OutputVisible) _layout.ToggleOutput(); };
        _lspCoord!.DiagnosticsUpdated += (_, diags) =>
        {
            _pendingUiActions.Enqueue(() =>
            {
                _outputPanel!.PopulateLspDiagnostics(diags);
                UpdateErrorCount(diags);
            });
        };
        _lspCoord.LspInitCompleted += () =>
        {
            _layout?.AboutRefresh?.Invoke();
            _layout?.UpdateDashboard();
            _lspCoord.SetupSemanticHighlightersForOpenFiles();
        };
        _lspCoord.LspBusyChanged += busy =>
        {
            _lspBusy = busy;
            if (!busy)
                _lspStatus?.SetContent(new List<string> { "" });
        };
        _debugCoord!.StateChanged += () =>
        {
            _layout?.AboutRefresh?.Invoke();
            _layout?.UpdateDashboard();
        };

        // Driver / file watcher
        _ws.WindowResized += (s, e) => _layout?.OnScreenResized(s, e);
        _fileWatcher!.FileChanged      += (_, path) => _pendingUiActions.Enqueue(() => HandleExternalFileChanged(path));
        _fileWatcher.StructureChanged  += (_, _)    => _pendingUiActions.Enqueue(() => _ = _gitOps!.RefreshExplorerAndGitAsync());
        _fileWatcher.GitChanged        += (_, _)    => _pendingUiActions.Enqueue(() => _ = _gitOps!.RefreshGitStatusAsync());

        // Side panel
        _sidePanel!.SymbolActivated += (_, entry) => _lspCoord.NavigateToLocation(entry);
        _sidePanel.GitCommitRequested += (_, _) => _ = _gitOps!.GitCommitAsync();
        _sidePanel.GitPushRequested += (_, _) => _ = _gitOps!.GitCommandAsync("push");
        _sidePanel.GitPullRequested += (_, _) => _ = _gitOps!.GitCommandAsync("pull");
        _sidePanel.GitRefreshRequested += (_, _) => _ = _gitOps!.RefreshExplorerAndGitAsync();
        _sidePanel.GitDiffRequested += (_, path) => _ = _gitOps!.GitShowDiffAsync(path);
        _sidePanel.GitContextMenuRequested += (s, e) => _contextMenu?.HandleGitPanelContextMenu(s, e);
        _sidePanel.GitLogEntryActivated += (_, entry) => _ = _gitOps!.ShowCommitDetailAsync(entry);
        _sidePanel.GitMoreMenuRequested += (_, _) => _contextMenu!.ShowGitMoreMenu();
    }

    private void WireEvents()
    {
        // Explorer
        _explorer!.FileOpenRequested += (_, path) => _editorManager?.OpenFile(path);

        _explorer.NewFileRequested += (_, dir) => _ = HandleNewFileAsync(dir);
        _explorer.NewFolderRequested += (_, dir) => _ = HandleNewFolderAsync(dir);
        _explorer.RenameRequested += (_, path) => _ = HandleRenameAsync(path);
        _explorer.DeleteRequested += (_, path) => _ = HandleDeleteAsync(path);
        _explorer.RefreshRequested += (_, _) => _ = _gitOps!.RefreshExplorerAndGitAsync();
        _explorer.ContextMenuRequested += (s, e) => _contextMenu?.HandleExplorerContextMenu(s, e);

        _editorManager!.TabContextMenuRequested += (s, e) => _contextMenu?.HandleTabContextMenu(s, e);
        _editorManager!.EditorContextMenuRequested += (s, e) => _contextMenu?.HandleEditorContextMenu(s, e);
        _editorManager!.GutterClicked += (_, e) => _debugCoord?.ToggleBreakpoint(e.FilePath, e.SourceLineIndex);

        _editorManager!.TabCloseRequested += (_, index) =>
        {
            if (_editorManager.IsTabDirty(index))
            {
                var fileName = _editorManager.GetTabFilePath(index) is { } p
                    ? Path.GetFileName(p)
                    : "Untitled";
                _ = ConfirmSaveDialog.ShowAsync(_ws, fileName).ContinueWith(t =>
                {
                    _pendingUiActions.Enqueue(() =>
                    {
                        if (t.Result == DialogResult.Cancel) return;
                        if (t.Result == DialogResult.Save)
                        {
                            _editorManager.TabControl.ActiveTabIndex = index;
                            _editorManager.SaveCurrent();
                        }
                        _editorManager.CloseTabAt(index);
                    });
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
            _syntaxStatus?.SetContent(new List<string> { $"[dim]{MarkupParser.Escape(name)}[/]" });
        };

        _outputPanel!.DiagnosticNavigateRequested += (_, diag) =>
        {
            _editorManager?.OpenFile(diag.FilePath);
            _editorManager?.GoToLine(diag.Line);
            var editor = _editorManager?.CurrentEditor;
            if (editor != null)
                _mainWindow?.FocusControl(editor);
        };

        _outputPanel.SearchNavigateRequested += (_, result) =>
        {
            _editorManager?.OpenFile(result.FilePath);
            _editorManager?.GoToLine(result.Line);
            var editor = _editorManager?.CurrentEditor;
            if (editor != null)
                _mainWindow?.FocusControl(editor);
        };

        _outputPanel.SearchRequested += (_, args) =>
        {
            _ = RunFileSearchAsync(args.Term, args.CaseSensitive);
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
            _ = _lspCoord?.DidOpenAsync(a.FilePath, a.Content);
            _lspCoord?.SetupSemanticHighlighter(a.FilePath);
            _ = _gitOps!.RefreshGitDiffMarkersForFileAsync(a.FilePath);
            _debugCoord?.RegisterGutter(a.FilePath);
        };
        _editorManager.DocumentChanged += (_, a) =>
        {
            _ = _lspCoord?.DidChangeAsync(a.FilePath, a.Content);
            _lspCoord?.TryScheduleDotCompletion(a.FilePath, a.Content);
            _lspCoord?.ScheduleSymbolRefresh(a.FilePath, _layout!.SidePanelVisible);
            _lspCoord?.ScheduleSemanticTokenRefresh(a.FilePath);
        };
        _editorManager.DocumentSaved   += (_, p) => OnDocumentSaved(p);
        _editorManager.DocumentClosed  += (_, p) =>
        {
            _lspCoord?.DismissCompletionPortal();
            _lspCoord?.DismissTooltipPortal();
            _lspCoord?.DismissLocationPortal();
            _lspCoord?.RemoveSemanticHighlighter(p);
            _ = _lspCoord?.DidCloseAsync(p);
            _debugCoord?.UnregisterGutter(p);
            _outputPanel?.PopulateLspDiagnostics(new List<BuildDiagnostic>());
            UpdateErrorCount(new List<BuildDiagnostic>());
        };

        _editorManager.ActiveFileChanged += (_, filePath) =>
        {
            _lspCoord?.DismissCompletionPortal();
            _lspCoord?.DismissTooltipPortal();
            _lspCoord?.DismissLocationPortal();
            if (filePath != null && filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var editor = _editorManager.CurrentEditor;
                if (editor != null)
                    _ = _lspCoord?.DidChangeAsync(filePath, editor.Content);
            }
            else
            {
                _outputPanel?.PopulateLspDiagnostics(new List<BuildDiagnostic>());
                UpdateErrorCount(new List<BuildDiagnostic>());
            }
            _lspCoord?.RefreshSymbolsForFile(filePath);
        };

        _mainWindow!.PreviewKeyPressed += OnMainWindowPreviewKey;
        _mainWindow!.KeyPressed        += OnMainWindowKeyPressed;

        // Ctrl+P goes through the framework's global-shortcut registry rather
        // than ConsoleDriver.KeyPressed. The driver event is upstream of the
        // whole dispatch pipeline, so a key that did not come from the real
        // terminal never raises it: injected input (the diagnostics socket,
        // and so cxdbg) was invisible to this shortcut, which made the command
        // palette undrivable from a debugger. RegisterGlobalShortcut runs
        // inside InputCoordinator, where every key arrives however it got
        // there.
        _ws.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.P, () =>
            _pendingUiActions.Enqueue(ShowCommandPalette));

        // Rebuild menu when tabs change (for dynamic View > Editor Tabs / Side Panel submenus)
        _editorManager!.TabControl.TabAdded   += (_, _) => _pendingUiActions.Enqueue(AddMenuBar);
        _editorManager!.TabControl.TabRemoved += (_, _) => _pendingUiActions.Enqueue(AddMenuBar);
        _sidePanel!.TabControl.TabAdded       += (_, _) => _pendingUiActions.Enqueue(AddMenuBar);
        _sidePanel!.TabControl.TabRemoved     += (_, _) => _pendingUiActions.Enqueue(AddMenuBar);

        // Handle close button on output panel and side panel tabs
        _outputPanel!.TabControl.TabCloseRequested += (_, e) => _outputPanel.TabControl.RemoveTab(e.Index);
        _sidePanel!.TabControl.TabCloseRequested   += (_, e) => _sidePanel.TabControl.RemoveTab(e.Index);
    }

    private void InitBottomStatus()
    {
        _bottomBar
            .AddHint("F5",     "Debug", HandleF5)
            .AddHint("F6",     "Build", () => _ = _buildOps!.BuildProjectAsync())
            .AddHint("F7",     "Test",  () => _ = _buildOps!.TestProjectAsync())
            .AddHint("F8",     "Shell", () => _buildOps!.OpenShell())
            .AddSegment("[dim]| [/]", "| ")
            .AddHint("Ctrl+S", "Save",  () => _editorManager?.SaveCurrent())
            .AddHint("Ctrl+W", "Close", CloseCurrentTab);

        _bottomStatusElement.Text = _bottomBar.Render();
    }

    // Resize handlers delegated to LayoutController

    // Fires from the input thread for every keystroke, regardless of which window is active.
    // Only enqueue work — never touch UI directly from this handler.
    private void OnMainWindowPreviewKey(object? sender, KeyPressedEventArgs e)
    {
        // Context menu portal handles all keys
        if (_contextMenu != null && _contextMenu.ProcessPreviewKey(e))
            return;

        // Delegate all LSP portal key handling to LspCoordinator
        if (_lspCoord != null && _lspCoord.ProcessPreviewKey(e))
            return;

        // Editor focus/edit mode management (no portals open at this point).
        // MultilineEditControl.GotFocus doesn't fire (ConsoleEx bug), so we
        // handle edit mode transitions here via PreviewKey.
        if (_mainWindow!.FocusManager.FocusedControl is MultilineEditControl editor)
        {
            if (e.KeyInfo.Key == ConsoleKey.Escape && editor.IsEditing)
            {
                // Escape: exit edit mode → Tab/Shift+Tab propagate for focus navigation.
                // Enter re-enters edit mode (built-in MultilineEditControl behavior).
                editor.IsEditing = false;
                e.Handled = true;
            }
        }
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

        // Look up command by key combo from the registry
        var command = _commandRegistry.FindByKey(key, mods);
        if (command != null)
        {
            command.Execute();
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

                if (_lspBusy)
                {
                    var frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
                    _lspSpinnerFrame = (_lspSpinnerFrame + 1) % frames.Length;
                    _lspStatus?.SetContent(new List<string> { $"[cyan1]{frames[_lspSpinnerFrame]} LSP[/]" });
                }

                await Task.Delay(RenderFrameMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { } // Suppress render loop errors to keep UI responsive
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Post-init (async, runs after Run() starts)
    // ──────────────────────────────────────────────────────────────

    private async Task PostInitAsync(string projectPath)
    {
        await _gitOps!.RefreshGitStatusAsync();
        _debugCoord!.DetectDap();
        await _lspCoord!.InitLspAsync(projectPath, _config.Lsp, _ws, startServer: _isDotNetWorkspace);
    }

    private void ShowNoProjectNotice()
    {
        _outputPanel?.AppendOutputLine(
            "No .sln or project file found — LSP and recursive file watching are disabled. Use File → Open Folder to open a project.");
        _ws.NotificationStateService.ShowNotification(
            "No .NET Project Found",
            $"{_projectService.RootPath} does not contain a solution or project file. Use File → Open Folder… to open one.",
            SharpConsoleUI.Core.NotificationSeverity.Warning);
    }

    private void HandleF5()
    {
        if (_debugCoord == null) return;

        // If debugger detected and paused → continue
        if (_debugCoord.HasDebugger && _debugCoord.State == DebugSessionState.Paused)
        {
            _ = _debugCoord.ContinueAsync();
            return;
        }

        // If debugger detected and idle → build + debug
        if (_debugCoord.HasDebugger && _debugCoord.State == DebugSessionState.Idle)
        {
            _ = _debugCoord.StartDebuggingAsync(_buildService, _buildLines, _cts.Token);
            return;
        }

        // No debugger → run without debugging + one-time notification
        _buildOps!.RunProject();
        if (_debugCoord.ShowMissingDebuggerNotification())
        {
            _ws.NotificationStateService.ShowNotification(
                "Debugger Not Found",
                "Install netcoredbg for debugging support (breakpoints, stepping, variables). Use Help → Install netcoredbg.",
                SharpConsoleUI.Core.NotificationSeverity.Info);
        }
    }

    private void HandleF5WithProfile()
    {
        if (_debugCoord == null) return;
        if (_debugCoord.State != DebugSessionState.Idle) return;

        var projectDir = Path.GetDirectoryName(_projectService.FindBuildTarget() ?? _projectService.RootPath)
                         ?? _projectService.RootPath;

        _ = LaunchProfileDialog.ShowAsync(_ws, projectDir).ContinueWith(t =>
        {
            _pendingUiActions.Enqueue(() =>
            {
                var profile = t.Result;
                if (profile == null) return;

                if (_debugCoord.HasDebugger)
                    _ = _debugCoord.StartDebuggingAsync(_buildService, _buildLines, _cts.Token, profile);
                else
                    _buildOps!.RunProject(profile);
            });
        });
    }

    // ──────────────────────────────────────────────────────────────
    // Build / Test / Run / Git
    // ──────────────────────────────────────────────────────────────


    private void HandleExternalFileChanged(string path)
    {
        // .gitignore change affects explorer ignore state
        if (Path.GetFileName(path).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            _ = _gitOps!.RefreshExplorerAndGitAsync();

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
            // Silent otherwise: the buffer changes under the user with no trace.
            _outputPanel?.AppendOutputLine($"Reloaded from disk: {Path.GetFileName(path)}");
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
        _isDotNetWorkspace = _projectService.ContainsDotNetProject();
        _pendingUiActions.Enqueue(() => _editorManager?.CloseAll());
        _pendingUiActions.Enqueue(() => _explorer?.Refresh());
        _ = _gitOps!.RefreshGitStatusAsync();
        _fileWatcher?.Watch(selected, recursive: _isDotNetWorkspace);

        if (!_isDotNetWorkspace)
            _pendingUiActions.Enqueue(ShowNoProjectNotice);

        if (_lspCoord != null)
            await _lspCoord.ReinitLspAsync(selected, _config.Lsp, startServer: _isDotNetWorkspace);
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
            await _gitOps!.RefreshExplorerAndGitAsync();
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
            await _gitOps!.RefreshExplorerAndGitAsync();
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

            await _gitOps!.RefreshExplorerAndGitAsync();
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

            await _gitOps!.RefreshExplorerAndGitAsync();
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
                _pendingUiActions.Enqueue(() =>
                {
                    if (t.Result == DialogResult.Cancel) return;
                    if (t.Result == DialogResult.Save) _editorManager.SaveCurrent();
                    _editorManager.CloseCurrentTab();
                });
            });
            return;
        }

        _editorManager.CloseCurrentTab();
    }

    private void InitializeCommands()
    {
        // File
        _commandRegistry.Register(new IdeCommand { Id = "file.save",            Category = "File",  Label = "Save",             Keybinding = "Ctrl+S",       KeyCombo = new(ConsoleKey.S, ConsoleModifiers.Control),     Execute = () => _editorManager?.SaveCurrent(),                                Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "file.close-tab",       Category = "File",  Label = "Close Tab",         Keybinding = "Ctrl+W",      KeyCombo = new(ConsoleKey.W, ConsoleModifiers.Control),     Execute = CloseCurrentTab,                                                    Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "file.open-folder",     Category = "File",  Label = "Open Folder\u2026",                                                                                        Execute = () => _ = OpenFolderAsync(),                                        Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "file.refresh-explorer",Category = "File",  Label = "Refresh Explorer",                                                                                             Execute = () => _ = _gitOps!.RefreshExplorerAndGitAsync(),                             Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "file.new-file",       Category = "File",  Label = "New File",          Keybinding = "Ctrl+N",                                                                  Execute = () => { var d = _explorer?.GetSelectedPath(); if (d != null) { if (!Directory.Exists(d)) d = Path.GetDirectoryName(d); if (d != null) _ = HandleNewFileAsync(d); } }, Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "file.new-folder",     Category = "File",  Label = "New Folder",        Keybinding = "Ctrl+Shift+N",                                                            Execute = () => { var d = _explorer?.GetSelectedPath(); if (d != null) { if (!Directory.Exists(d)) d = Path.GetDirectoryName(d); if (d != null) _ = HandleNewFolderAsync(d); } }, Priority = CommandPriority.NormalMinus });
        _commandRegistry.Register(new IdeCommand { Id = "file.rename",         Category = "File",  Label = "Rename",            Keybinding = "F2",                                                                      Execute = () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleRenameAsync(p); }, Priority = CommandPriority.NormalLow });
        _commandRegistry.Register(new IdeCommand { Id = "file.delete",         Category = "File",  Label = "Delete",                                                                                                    Execute = () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleDeleteAsync(p); }, Priority = CommandPriority.NormalLowest });
        _commandRegistry.Register(new IdeCommand { Id = "file.exit",            Category = "File",  Label = "Exit",              Keybinding = "Alt+F4",      KeyCombo = new(ConsoleKey.F4, ConsoleModifiers.Alt),        Execute = () => _ws.Shutdown(0),                                              Priority = CommandPriority.Least });

        // Edit
        _commandRegistry.Register(new IdeCommand { Id = "edit.find",            Category = "Edit",  Label = "Find\u2026",        Keybinding = "Ctrl+F",      KeyCombo = new(ConsoleKey.F, ConsoleModifiers.Control),     Execute = () => _layout?.ShowFindReplace(),                                                    Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "edit.replace",         Category = "Edit",  Label = "Replace\u2026",     Keybinding = "Ctrl+H",      KeyCombo = new(ConsoleKey.H, ConsoleModifiers.Control),     Execute = () => _layout?.ShowFindReplace(),                                                    Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "edit.search-files",    Category = "Edit",  Label = "Search in Files",   Keybinding = "Ctrl+Shift+F", KeyCombo = new(ConsoleKey.F, ConsoleModifiers.Control | ConsoleModifiers.Shift), Execute = ShowSearch,                         Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "edit.reload",          Category = "Edit",  Label = "Reload from Disk",  Keybinding = "Alt+Shift+R", KeyCombo = new(ConsoleKey.R, ConsoleModifiers.Alt | ConsoleModifiers.Shift), Execute = ReloadCurrentFromDisk,                  Priority = CommandPriority.Lower });
        _commandRegistry.Register(new IdeCommand { Id = "edit.wrap-word",       Category = "Edit",  Label = "Word Wrap",                                                                                                Execute = () => _layout?.SetWrapMode(WrapMode.WrapWords),                               Priority = CommandPriority.Minor });
        _commandRegistry.Register(new IdeCommand { Id = "edit.wrap-char",       Category = "Edit",  Label = "Character Wrap",                                                                                           Execute = () => _layout?.SetWrapMode(WrapMode.Wrap),                                   Priority = CommandPriority.Minor });
        _commandRegistry.Register(new IdeCommand { Id = "edit.no-wrap",         Category = "Edit",  Label = "No Wrap",                                                                                                  Execute = () => _layout?.SetWrapMode(WrapMode.NoWrap),                                 Priority = CommandPriority.Minor });

        // Build
        _commandRegistry.Register(new IdeCommand { Id = "build.build",          Category = "Build", Label = "Build",             Keybinding = "F6",          KeyCombo = new(ConsoleKey.F6),                              Execute = () => _ = _buildOps!.BuildProjectAsync(),                                      Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "build.test",           Category = "Build", Label = "Test",              Keybinding = "F7",          KeyCombo = new(ConsoleKey.F7),                              Execute = () => _ = _buildOps!.TestProjectAsync(),                                       Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "build.clean",          Category = "Build", Label = "Clean",                                                                                                    Execute = () => _ = _buildOps!.CleanProjectAsync(),                                      Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "build.stop",           Category = "Build", Label = "Stop",              Keybinding = "F4",          KeyCombo = new(ConsoleKey.F4),                              Execute = () => _buildService.Cancel(),                                       Priority = CommandPriority.Medium });

        // Run / Debug
        _commandRegistry.Register(new IdeCommand { Id = "debug.start",          Category = "Debug", Label = "Start Debugging",   Keybinding = "F5",          KeyCombo = new(ConsoleKey.F5),                              Execute = HandleF5,                                                                              Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "debug.run-no-debug",   Category = "Debug", Label = "Run Without Debugging", Keybinding = "Ctrl+F5", KeyCombo = new(ConsoleKey.F5, ConsoleModifiers.Control),    Execute = () => _buildOps!.RunProject(),                                                          Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "debug.selectProfile",   Category = "Debug", Label = "Start with Profile…",                                                                              Execute = HandleF5WithProfile,                                                                   Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "debug.stop",           Category = "Debug", Label = "Stop Debugging",    Keybinding = "Shift+F5",    KeyCombo = new(ConsoleKey.F5, ConsoleModifiers.Shift),      Execute = () => _ = _debugCoord!.StopDebuggingAsync(),                                            Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "debug.breakpoint",     Category = "Debug", Label = "Toggle Breakpoint", Keybinding = "F9",          KeyCombo = new(ConsoleKey.F9),                              Execute = () => _debugCoord!.ToggleBreakpointAtCursor(),                                          Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "debug.step-over",      Category = "Debug", Label = "Step Over",         Keybinding = "F10",         KeyCombo = new(ConsoleKey.F10),                             Execute = () => _ = _debugCoord!.StepOverAsync(),                                                Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "debug.step-into",      Category = "Debug", Label = "Step Into",         Keybinding = "F11",         KeyCombo = new(ConsoleKey.F11),                             Execute = () => _ = _debugCoord!.StepIntoAsync(),                                                Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "debug.step-out",       Category = "Debug", Label = "Step Out",          Keybinding = "Shift+F11",   KeyCombo = new(ConsoleKey.F11, ConsoleModifiers.Shift),     Execute = () => _ = _debugCoord!.StepOutAsync(),                                                 Priority = CommandPriority.Medium });

        // View
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-explorer", Category = "View",  Label = "Toggle Explorer",   Keybinding = "Ctrl+B",      KeyCombo = new(ConsoleKey.B, ConsoleModifiers.Control),     Execute = () => _layout?.ToggleExplorer(),                                                     Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-output",   Category = "View",  Label = "Toggle Output Panel",Keybinding = "Ctrl+J",     KeyCombo = new(ConsoleKey.J, ConsoleModifiers.Control),     Execute = () => _layout?.ToggleOutput(),                                                       Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-side-panel",Category = "View", Label = "Toggle Side Panel", Keybinding = "Alt+;",       KeyCombo = new(ConsoleKey.Oem1, ConsoleModifiers.Alt),      Execute = () => _layout?.ToggleSidePanel(),                                                    Priority = CommandPriority.Default });

        // Git
        _commandRegistry.Register(new IdeCommand { Id = "git.source-control",    Category = "Git",   Label = "Source Control",    Keybinding = "Alt+G",      KeyCombo = new(ConsoleKey.G, ConsoleModifiers.Alt),         Execute = () => _layout?.ShowSourceControl(),                                                Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "git.refresh",          Category = "Git",   Label = "Refresh Status",                                                                                           Execute = () => _ = _gitOps!.RefreshGitStatusAsync(),                                  Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "git.stage-file",       Category = "Git",   Label = "Stage Current File",                                                                                       Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitStageFileAsync(p); }, Priority = CommandPriority.GitStage });
        _commandRegistry.Register(new IdeCommand { Id = "git.unstage-file",     Category = "Git",   Label = "Unstage Current File",                                                                                     Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitUnstageFileAsync(p); }, Priority = CommandPriority.GitUnstage });
        _commandRegistry.Register(new IdeCommand { Id = "git.stage-all",        Category = "Git",   Label = "Stage All",                                                                                                Execute = () => _ = _gitOps!.GitStageAllAsync(),                                       Priority = CommandPriority.GitStageAll });
        _commandRegistry.Register(new IdeCommand { Id = "git.unstage-all",      Category = "Git",   Label = "Unstage All",                                                                                              Execute = () => _ = _gitOps!.GitUnstageAllAsync(),                                     Priority = CommandPriority.Low });
        _commandRegistry.Register(new IdeCommand { Id = "git.commit",           Category = "Git",   Label = "Commit\u2026",      Keybinding = "Ctrl+Enter",  KeyCombo = new(ConsoleKey.Enter, ConsoleModifiers.Control), Execute = () => _ = _gitOps!.GitCommitAsync(),                                      Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "git.pull",             Category = "Git",   Label = "Pull",                                                                                                     Execute = () => _ = _gitOps!.GitCommandAsync("pull"),                                   Priority = CommandPriority.GitPull });
        _commandRegistry.Register(new IdeCommand { Id = "git.push",             Category = "Git",   Label = "Push",                                                                                                     Execute = () => _ = _gitOps!.GitCommandAsync("push"),                                   Priority = CommandPriority.GitPush });
        _commandRegistry.Register(new IdeCommand { Id = "git.diff-file",        Category = "Git",   Label = "Diff Current File",                                                                                        Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitShowDiffAsync(p); }, Priority = CommandPriority.Lower });
        _commandRegistry.Register(new IdeCommand { Id = "git.diff-all",         Category = "Git",   Label = "Diff All Changes",                                                                                         Execute = () => _ = _gitOps!.GitShowDiffAllAsync(),                                    Priority = CommandPriority.GitDiffAll });
        _commandRegistry.Register(new IdeCommand { Id = "git.discard-file",     Category = "Git",   Label = "Discard Changes (Current File)",                                                                           Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitDiscardFileAsync(p); }, Priority = CommandPriority.GitDiscard });
        _commandRegistry.Register(new IdeCommand { Id = "git.discard-all",      Category = "Git",   Label = "Discard All Changes",                                                                                      Execute = () => _ = _gitOps!.GitDiscardAllAsync(),                                     Priority = CommandPriority.GitDiscardAll });
        _commandRegistry.Register(new IdeCommand { Id = "git.stash",            Category = "Git",   Label = "Stash\u2026",                                                                                              Execute = () => _ = _gitOps!.GitStashAsync(),                                          Priority = CommandPriority.CustomTool });
        _commandRegistry.Register(new IdeCommand { Id = "git.stash-pop",        Category = "Git",   Label = "Stash Pop",                                                                                                Execute = () => _ = _gitOps!.GitStashPopAsync(),                                       Priority = CommandPriority.CustomToolAlt });
        _commandRegistry.Register(new IdeCommand { Id = "git.log",              Category = "Git",   Label = "Log",                                                                                                      Execute = () => _ = _gitOps!.GitShowLogAsync(),                                        Priority = CommandPriority.Minor });
        _commandRegistry.Register(new IdeCommand { Id = "git.log-file",         Category = "Git",   Label = "Log (Current File)",                                                                                       Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitShowFileLogAsync(p); }, Priority = CommandPriority.GitLogFile });
        _commandRegistry.Register(new IdeCommand { Id = "git.blame",            Category = "Git",   Label = "Blame (Current File)",                                                                                     Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitShowBlameAsync(p); }, Priority = CommandPriority.GitBlame });
        _commandRegistry.Register(new IdeCommand { Id = "git.switch-branch",    Category = "Git",   Label = "Switch Branch\u2026",                                                                                      Execute = () => _ = _gitOps!.GitSwitchBranchAsync(),                                   Priority = CommandPriority.GitBranch });
        _commandRegistry.Register(new IdeCommand { Id = "git.new-branch",       Category = "Git",   Label = "New Branch\u2026",                                                                                         Execute = () => _ = _gitOps!.GitNewBranchAsync(),                                      Priority = CommandPriority.GitNewBranch });
        _commandRegistry.Register(new IdeCommand { Id = "git.add-to-gitignore",    Category = "Git",   Label = "Add to .gitignore",                                                                                     Execute = () => { var p = _explorer?.GetSelectedPath() ?? _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitAddToGitignoreAsync(p, Directory.Exists(p)); }, Priority = CommandPriority.GitIgnoreAdd });
        _commandRegistry.Register(new IdeCommand { Id = "git.remove-from-gitignore", Category = "Git", Label = "Remove from .gitignore",                                                                                Execute = () => { var p = _explorer?.GetSelectedPath() ?? _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitRemoveFromGitignoreAsync(p); }, Priority = CommandPriority.GitIgnoreRemove });

        // LSP
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-def",         Category = "LSP",   Label = "Go to Definition",    Keybinding = "F12",          KeyCombo = new(ConsoleKey.F12),                             Execute = () => _ = _lspCoord!.ShowGoToDefinitionAsync(),                    Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-impl",        Category = "LSP",   Label = "Go to Implementation",Keybinding = "Ctrl+F12",     KeyCombo = new(ConsoleKey.F12, ConsoleModifiers.Control),   Execute = () => _ = _lspCoord!.ShowGoToImplementationAsync(),                Priority = CommandPriority.CriticalMinus });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.references",       Category = "LSP",   Label = "Find All References", Keybinding = "Shift+F12",    KeyCombo = new(ConsoleKey.F12, ConsoleModifiers.Shift),     Execute = () => _ = _lspCoord!.ShowFindReferencesAsync(),                    Priority = CommandPriority.CriticalLow });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.nav-back",         Category = "LSP",   Label = "Navigate Back",       Keybinding = "Alt+\u2190",   KeyCombo = new(ConsoleKey.LeftArrow, ConsoleModifiers.Alt), Execute = () => _lspCoord!.NavigateBack(),                                  Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.rename",           Category = "LSP",   Label = "Rename Symbol",       Keybinding = "Ctrl+F2",      KeyCombo = new(ConsoleKey.F2, ConsoleModifiers.Control),    Execute = () => _ = _lspCoord!.ShowRenameAsync(_ws),                        Priority = CommandPriority.HighMinus });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.code-action",      Category = "LSP",   Label = "Code Actions",        Keybinding = "Ctrl+.",       KeyCombo = new(ConsoleKey.OemPeriod, ConsoleModifiers.Control), Execute = () => _ = _lspCoord!.ShowCodeActionsAsync(_ws),               Priority = CommandPriority.HighLow });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.document-symbols", Category = "LSP",   Label = "Focus Symbols",       Keybinding = "Alt+O",        KeyCombo = new(ConsoleKey.O, ConsoleModifiers.Alt),         Execute = () => _layout?.FocusSymbolsTab(),                                                  Priority = CommandPriority.HighLowest });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.hover",            Category = "LSP",   Label = "Hover Tooltip",       Keybinding = "Ctrl+K",       KeyCombo = new(ConsoleKey.K, ConsoleModifiers.Control),     Execute = () => _ = _lspCoord!.ShowHoverAsync(),                            Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.signature",        Category = "LSP",   Label = "Signature Help",      Keybinding = "F2",           KeyCombo = new(ConsoleKey.F2),                              Execute = () => _ = _lspCoord!.ShowSignatureHelpAsync(),                     Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.format",           Category = "LSP",   Label = "Format Document",     Keybinding = "Alt+Shift+F",  KeyCombo = new(ConsoleKey.F, ConsoleModifiers.Alt | ConsoleModifiers.Shift), Execute = () => _ = _lspCoord!.FormatDocumentAsync(), Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.complete",         Category = "LSP",   Label = "Show Completions",    Keybinding = "Ctrl+Space",   KeyCombo = new(ConsoleKey.Spacebar, ConsoleModifiers.Control), Execute = () => { _ = _lspCoord?.FlushPendingChangeAsync(); _ = _lspCoord?.ShowCompletionAsync(); }, Priority = CommandPriority.Low });

        // Terminal
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell",          Category = "Terminal", Label = "Bottom Shell",       Keybinding = "F8",        KeyCombo = new(ConsoleKey.F8),                              Execute = () => _buildOps!.OpenShell(),                                                          Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell-tab",      Category = "Terminal", Label = "Editor Shell Tab",                                                                                      Execute = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenShellTab(); },      Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "tools.side-shell",    Category = "Terminal", Label = "Side Panel Shell",   Keybinding = "Shift+F8",   KeyCombo = new(ConsoleKey.F8, ConsoleModifiers.Shift),      Execute = () => { if (IdeConstants.IsDesktopOs) _layout?.OpenSidePanelShell(); },                                                 Priority = CommandPriority.NormalLow });

        // NuGet
        _commandRegistry.Register(new IdeCommand { Id = "tools.lazynuget",      Category = "NuGet",  Label = "LazyNuGet",                                                                                                Execute = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenLazyNuGetTab(); }, Priority = CommandPriority.Default });
        if (!_buildOps!.HasLazyNuGet)
            _commandRegistry.Register(new IdeCommand { Id = "tools.nuget",      Category = "NuGet",  Label = "Add NuGet Package\u2026",                                                                                Execute = () => _buildOps!.ShowNuGetDialog(),                                                    Priority = CommandPriority.Low });

        // Tools
        _commandRegistry.Register(new IdeCommand { Id = "tools.config",         Category = "Tools", Label = "Edit Config",                                  Execute = () => _buildOps!.OpenConfigFile(),                                                     Priority = CommandPriority.Lower });

        // Help
        _commandRegistry.Register(new IdeCommand { Id = "help.about",           Category = "Help",  Label = "About lazydotide\u2026",                       Execute = () => _layout?.ShowAbout(),                                                          Priority = CommandPriority.Least });
        _commandRegistry.Register(new IdeCommand { Id = "help.installLsp",      Category = "Help",  Label = "Install csharp-ls (LSP)\u2026",               Execute = () => _layout?.InstallLsp(),                                                         Priority = CommandPriority.Least });
        _commandRegistry.Register(new IdeCommand { Id = "help.installDebugger", Category = "Help",  Label = "Install netcoredbg\u2026",                     Execute = () => _layout?.InstallDebugger(),                                                    Priority = CommandPriority.Least });

        // Dynamic tool commands from config
        for (int i = 0; i < _config.Tools.Count; i++)
        {
            int idx = i;
            var tool = _config.Tools[i];
            _commandRegistry.Register(new IdeCommand
            {
                Id       = $"tools.custom.{idx}",
                Category = "Tools",
                Label    = tool.Name + " (Tab)",
                Execute  = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenConfigToolTab(idx); },
                Priority = CommandPriority.CustomTool
            });
            _commandRegistry.Register(new IdeCommand
            {
                Id       = $"tools.custom.{idx}.bottom",
                Category = "Tools",
                Label    = tool.Name + " (Bottom Shell)",
                Execute  = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenConfigToolInOutputPanel(idx); },
                Priority = CommandPriority.CustomToolAlt
            });
            _commandRegistry.Register(new IdeCommand
            {
                Id       = $"tools.custom.{idx}.side",
                Category = "Tools",
                Label    = tool.Name + " (Side Panel)",
                Execute  = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenConfigToolInSidePanel(idx, () => { if (!_layout!.SidePanelVisible) _layout.ToggleSidePanel(); }, () => _layout?.InvalidateSidePanel()); },
                Priority = CommandPriority.CustomToolSide
            });
        }
    }

    private void ShowCommandPalette() => _lspCoord?.ShowCommandPalettePortal(_commandRegistry);

    private void CaptureWorkspaceState()
    {
        if (_layout == null) return;
        _workspaceState?.CaptureWorkspaceState(new LayoutSnapshot
        {
            ExplorerVisible = _layout.ExplorerVisible,
            OutputVisible = _layout.OutputVisible,
            SidePanelVisible = _layout.SidePanelVisible,
            OutputPanelHeight = _layout.OutputPanelHeight,
            ExplorerColumnWidth = _layout.ExplorerColumnWidth,
            SidePanelColumnWidth = _layout.SidePanelColumnWidth,
            ExplorerFullHeight = _layout.ExplorerFullHeight,
        });
    }

    private void RestoreWorkspaceState()
    {
        if (_workspaceState == null || _layout == null) return;
        var snapshot = _workspaceState.RestoreWorkspaceState(
            _layout.ExplorerVisible, _layout.OutputVisible, _layout.SidePanelVisible);

        // Placement first: it rebuilds the cells, so doing it after the toggles
        // would re-place the panels without their collapsed track state.
        _layout.SetExplorerFullHeight(snapshot.ExplorerFullHeight);

        if (snapshot.NeedToggleExplorer) _layout.ToggleExplorer();
        if (snapshot.NeedToggleOutput) _layout.ToggleOutput();
        if (snapshot.NeedToggleSidePanel) _layout.ToggleSidePanel();

        // Apply both widths together so the columns cannot over-commit the grid.
        _layout.RestoreColumnWidths(snapshot.ExplorerColumnWidth, snapshot.SidePanelColumnWidth);

        if (snapshot.OutputPanelHeight > 0)
            _layout.ApplyRestoredOutputHeight(snapshot.OutputPanelHeight);

        if (_editorManager != null && Enum.TryParse<WrapMode>(snapshot.WrapMode, out var wm))
            _editorManager.WrapMode = wm;

        _layout.ForceRebuildLayout();
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
