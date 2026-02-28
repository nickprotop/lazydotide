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
    private const int RenderFrameMs = 80;

    private readonly ConsoleWindowSystem _ws;
    private readonly ProjectService _projectService;
    private readonly BuildService _buildService;
    private readonly GitService _gitService;

    private Window? _mainWindow;
    private Window? _outputWindow;
    private ExplorerPanel? _explorer;
    private EditorManager? _editorManager;
    private OutputPanel? _outputPanel;
    private FileMiddlewarePipeline? _pipeline;

    // Extracted handlers
    private GitCoordinator? _gitOps;
    private BuildCoordinator? _buildOps;
    private WorkspaceStateManager? _workspaceState;
    private LspCoordinator? _lspCoord;

    // Status bar
    private MarkupControl? _statusLeft;   // git + error combined
    private MarkupControl? _cursorStatus;
    private MarkupControl? _syntaxStatus;
    private string _errorMarkup = "";
    private MarkupControl? _dashboard;

    // Layout controls (referenced during CreateLayout, owned by LayoutController after init)
    private ColumnContainer? _explorerCol;
    private SplitterControl? _explorerSplitter;

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
    private ColumnContainer? _sidePanelCol;
    private SplitterControl? _sidePanelSplitter;
    private HorizontalGridControl? _mainContent;

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
                StatusBarOptions: new StatusBarOptions(ShowTaskBar: false)));
        _projectService = new ProjectService(projectPath);
        _buildService = new BuildService();
        _gitService = new GitService();

        InitBottomStatus();  // Must be before CreateLayout so DesktopDimensions accounts for the bar
        CreateLayout();

        // Create extracted handlers (after CreateLayout so UI components exist)
        _gitOps = new GitCoordinator(
            _gitService, _projectService, _buildService, _editorManager!, _outputPanel!,
            _sidePanel!, _explorer!, _buildLines, _pendingUiActions, _cts.Token);
        _gitOps.SetWindowSystem(_ws);
        _gitOps.SetFileWatcher(_fileWatcher!);

        _buildOps = new BuildCoordinator(
            _buildService, _projectService, _editorManager!, _outputPanel!,
            _config, _buildLines, _testLines, _pendingUiActions, _cts.Token);
        _buildOps.SetWindowSystem(_ws, _outputWindow!, _sidePanel!);

        // Open the shell tab at startup so it's ready immediately
        if (IdeConstants.IsDesktopOs)
        {
            _outputPanel!.LaunchShell(_projectService.RootPath);
            _buildOps.OutputShellCount = 1;
        }

        _lspCoord = new LspCoordinator(_editorManager!, _sidePanel!, _pendingUiActions);
        _lspCoord.SetMainWindow(_mainWindow!);

        _layout = new LayoutController(_ws, _projectService, _editorManager!, _sidePanel!, _lspCoord, _config);
        _layout.SetControls(_mainWindow!, _outputWindow!, _explorerCol, _explorerSplitter,
            _sidePanelCol, _sidePanelSplitter, _mainContent, _dashboard);
        _layout.UpdateDashboard();
        WireHandlerEvents();

        _menuBar = new MenuBarBuilder(_ws, _editorManager!, _explorer!, _sidePanel!,
            _buildService, _config, _pipeline!, _gitOps!, _buildOps!, _lspCoord, _layout);
        _menuBar.SetMainWindow(_mainWindow!);
        var fileOps = new FileOperationDelegates(
            HandleNewFileAsync, HandleNewFolderAsync,
            HandleRenameAsync, HandleDeleteAsync,
            () => _gitOps!.RefreshExplorerAndGitAsync());
        _menuBar.FileOps = fileOps;
        _menuBar.OpenFolderAsync = OpenFolderAsync;
        _menuBar.CloseCurrentTab = CloseCurrentTab;
        _menuBar.ReloadCurrentFromDisk = ReloadCurrentFromDisk;
        _menuBar.ShowCommandPalette = ShowCommandPalette;

        // Menu/toolbar were no-ops during BuildMainWindow (_menuBar was null).
        // Now that _menuBar is ready, insert them into their sticky-top slots.
        AddMenuBar();
        AddToolbar();

        _contextMenu = new ContextMenuBuilder(
            _gitService, _projectService, _editorManager!, _explorer!, _sidePanel!,
            _gitOps!, _lspCoord);
        _contextMenu.SetMainWindow(_mainWindow!, _ws);
        _contextMenu.FileOps = fileOps;

        _workspaceState = new WorkspaceStateManager(
            new WorkspaceService(projectPath), _editorManager!, _explorer!, _outputPanel!);
        _workspaceState.Load();
        RestoreWorkspaceState();
        InitializeCommands();

        // Async post-init: git status + optional LSP
        _ = PostInitAsync(projectPath);
    }

    public void Run() => _ws.Run();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { CaptureWorkspaceState(); _workspaceState?.Save(); } catch { } // Best effort — don't prevent shutdown
        _cts.Cancel();
        _fileWatcher?.Dispose();
        _contextMenu?.DismissContextMenu();
        _ws.ConsoleDriver.KeyPressed   -= OnGlobalDriverKeyPressed;
        _ = _lspCoord?.DisposeAsync().AsTask();
    }

    // ──────────────────────────────────────────────────────────────
    // Layout
    // ──────────────────────────────────────────────────────────────

    private void CreateLayout()
    {
        ConfigService.EnsureDefaultConfig();
        _config = ConfigService.Load();

        var desktop = _ws.DesktopDimensions;
        int mainH = (int)(desktop.Height * 0.68);
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

        _fileWatcher = new FileWatcher();
        _fileWatcher.Watch(_projectService.RootPath);

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

    private void AddMenuBar() => _menuBar?.AddMenuBar();
    private void AddToolbar() => _menuBar?.AddToolbar();

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

        _dashboard = new MarkupControl(new List<string> { "", "[bold]  lazydotide[/]  [dim]loading…[/]" })
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
        _statusLeft = new MarkupControl(new List<string> { IdeConstants.GitStatusDefault });
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
        bar.AddSegment(_gitOps?.GitMarkup ?? IdeConstants.GitStatusDefault, " ");
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
    private void OnDocumentSaved(string savedPath)
    {
        _ = _lspCoord?.DidSaveAsync(savedPath);
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
        };

        // Driver / file watcher
        _ws.ConsoleDriver.ScreenResized += (s, e) => _layout?.OnScreenResized(s, e);
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
            _ = _lspCoord?.DidOpenAsync(a.FilePath, a.Content);
            _ = _gitOps!.RefreshGitDiffMarkersForFileAsync(a.FilePath);
        };
        _editorManager.DocumentChanged += (_, a) =>
        {
            _ = _lspCoord?.DidChangeAsync(a.FilePath, a.Content);
            _lspCoord?.TryScheduleDotCompletion(a.FilePath, a.Content);
            _lspCoord?.ScheduleSymbolRefresh(a.FilePath, _layout!.SidePanelVisible);
        };
        _editorManager.DocumentSaved   += (_, p) => OnDocumentSaved(p);
        _editorManager.DocumentClosed  += (_, p) =>
        {
            _lspCoord?.DismissCompletionPortal();
            _lspCoord?.DismissTooltipPortal();
            _lspCoord?.DismissLocationPortal();
            _ = _lspCoord?.DidCloseAsync(p);
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
        _mainWindow!.OnResize          += (s, e) => _layout?.OnMainWindowResized(s, e);
        _outputWindow!.OnResize        += (s, e) => _layout?.OnOutputWindowResized(s, e);
        _ws.ConsoleDriver.KeyPressed   += OnGlobalDriverKeyPressed;

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
            .AddHint("F5",     "Run",   () => _buildOps!.RunProject())
            .AddHint("F6",     "Build", () => _ = _buildOps!.BuildProjectAsync())
            .AddHint("F7",     "Test",  () => _ = _buildOps!.TestProjectAsync())
            .AddHint("F8",     "Shell", () => _buildOps!.OpenShell())
            .AddSegment("[dim]| [/]", "| ")
            .AddHint("Ctrl+S", "Save",  () => _editorManager?.SaveCurrent())
            .AddHint("Ctrl+W", "Close", CloseCurrentTab);

        _ws.StatusBarStateService.BottomStatus = _bottomBar.Render();
        _ws.StatusBarStateService.BottomStatusClickHandler = x => _bottomBar.HandleClick(x);
    }

    // Resize handlers delegated to LayoutController

    // Fires from the input thread for every keystroke, regardless of which window is active.
    // Only enqueue work — never touch UI directly from this handler.
    private void OnGlobalDriverKeyPressed(object? sender, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0)
            _pendingUiActions.Enqueue(ShowCommandPalette);
    }

    private void OnMainWindowPreviewKey(object? sender, KeyPressedEventArgs e)
    {
        // Context menu portal handles all keys
        if (_contextMenu != null && _contextMenu.ProcessPreviewKey(e))
            return;

        // Delegate all LSP portal key handling to LspCoordinator
        if (_lspCoord != null && _lspCoord.ProcessPreviewKey(e))
            return;
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
            _buildOps!.RunProject();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F6 && mods == 0)
        {
            _ = _buildOps!.BuildProjectAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F7 && mods == 0)
        {
            _ = _buildOps!.TestProjectAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F8 && mods == 0)
        {
            _buildOps!.OpenShell();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F8 && mods == ConsoleModifiers.Shift)
        {
            if (IdeConstants.IsDesktopOs) _layout?.OpenSidePanelShell();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F9 && mods == 0)
        {
            if (IdeConstants.IsDesktopOs)
                _buildOps!.OpenLazyNuGetTab();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F4 && mods == 0)
        {
            _buildService.Cancel();
            e.Handled = true;
        }
        else if (key == ConsoleKey.B && mods == ConsoleModifiers.Control)
        {
            _layout?.ToggleExplorer();
            e.Handled = true;
        }
        else if (key == ConsoleKey.J && mods == ConsoleModifiers.Control)
        {
            _layout?.ToggleOutput();
            e.Handled = true;
        }
        else if (key == ConsoleKey.K && mods == ConsoleModifiers.Control)
        {
            _ = _lspCoord?.ShowHoverAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.Spacebar && mods == ConsoleModifiers.Control)
        {
            _ = _lspCoord?.FlushPendingChangeAsync();
            _ = _lspCoord?.ShowCompletionAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F12 && mods == 0)
        {
            _ = _lspCoord?.ShowGoToDefinitionAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F12 && mods == ConsoleModifiers.Control)
        {
            _ = _lspCoord?.ShowGoToImplementationAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F12 && mods == ConsoleModifiers.Shift)
        {
            _ = _lspCoord?.ShowFindReferencesAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.LeftArrow && mods == ConsoleModifiers.Alt)
        {
            _lspCoord?.NavigateBack();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F2 && mods == ConsoleModifiers.Control)
        {
            _ = _lspCoord?.ShowRenameAsync(_ws);
            e.Handled = true;
        }
        else if (key == ConsoleKey.F2 && mods == 0)
        {
            _ = _lspCoord?.ShowSignatureHelpAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.OemPeriod && mods == ConsoleModifiers.Control)
        {
            _ = _lspCoord?.ShowCodeActionsAsync(_ws);
            e.Handled = true;
        }
        else if (key == ConsoleKey.O && mods == ConsoleModifiers.Alt)
        {
            _layout?.FocusSymbolsTab();
            e.Handled = true;
        }
        else if (key == ConsoleKey.Oem1 && mods == ConsoleModifiers.Alt) // Alt+;
        {
            _layout?.ToggleSidePanel();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F && mods == (ConsoleModifiers.Alt | ConsoleModifiers.Shift))
        {
            _ = _lspCoord?.FormatDocumentAsync();
            e.Handled = true;
        }
        else if (key == ConsoleKey.R && mods == (ConsoleModifiers.Alt | ConsoleModifiers.Shift))
        {
            ReloadCurrentFromDisk();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F && mods == ConsoleModifiers.Control)
        {
            _layout?.ShowFindReplace();
            e.Handled = true;
        }
        else if (key == ConsoleKey.H && mods == ConsoleModifiers.Control)
        {
            _layout?.ShowFindReplace();
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
        await _lspCoord!.InitLspAsync(projectPath, _config.Lsp, _ws);
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
        _pendingUiActions.Enqueue(() => _editorManager?.CloseAll());
        _pendingUiActions.Enqueue(() => _explorer?.Refresh());
        _ = _gitOps!.RefreshGitStatusAsync();
        _fileWatcher?.Watch(selected);

        if (_lspCoord != null)
            await _lspCoord.ReinitLspAsync(selected, _config.Lsp);
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
        _commandRegistry.Register(new IdeCommand { Id = "file.save",            Category = "File",  Label = "Save",             Keybinding = "Ctrl+S",     Execute = () => _editorManager?.SaveCurrent(),                                Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "file.close-tab",       Category = "File",  Label = "Close Tab",         Keybinding = "Ctrl+W",     Execute = CloseCurrentTab,                                                    Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "file.open-folder",     Category = "File",  Label = "Open Folder\u2026",                            Execute = () => _ = OpenFolderAsync(),                                        Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "file.refresh-explorer",Category = "File",  Label = "Refresh Explorer",  Keybinding = "F5",         Execute = () => _ = _gitOps!.RefreshExplorerAndGitAsync(),                             Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "file.new-file",       Category = "File",  Label = "New File",          Keybinding = "Ctrl+N",     Execute = () => { var d = _explorer?.GetSelectedPath(); if (d != null) { if (!Directory.Exists(d)) d = Path.GetDirectoryName(d); if (d != null) _ = HandleNewFileAsync(d); } }, Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "file.new-folder",     Category = "File",  Label = "New Folder",        Keybinding = "Ctrl+Shift+N", Execute = () => { var d = _explorer?.GetSelectedPath(); if (d != null) { if (!Directory.Exists(d)) d = Path.GetDirectoryName(d); if (d != null) _ = HandleNewFolderAsync(d); } }, Priority = CommandPriority.NormalMinus });
        _commandRegistry.Register(new IdeCommand { Id = "file.rename",         Category = "File",  Label = "Rename",            Keybinding = "F2",         Execute = () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleRenameAsync(p); }, Priority = CommandPriority.NormalLow });
        _commandRegistry.Register(new IdeCommand { Id = "file.delete",         Category = "File",  Label = "Delete",                                       Execute = () => { var p = _explorer?.GetSelectedPath(); if (p != null) _ = HandleDeleteAsync(p); }, Priority = CommandPriority.NormalLowest });
        _commandRegistry.Register(new IdeCommand { Id = "file.exit",            Category = "File",  Label = "Exit",              Keybinding = "Alt+F4",     Execute = () => _ws.Shutdown(0),                                              Priority = CommandPriority.Least });

        // Edit
        _commandRegistry.Register(new IdeCommand { Id = "edit.find",            Category = "Edit",  Label = "Find\u2026",        Keybinding = "Ctrl+F",     Execute = () => _layout?.ShowFindReplace(),                                                    Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "edit.replace",         Category = "Edit",  Label = "Replace\u2026",     Keybinding = "Ctrl+H",     Execute = () => _layout?.ShowFindReplace(),                                                    Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "edit.reload",          Category = "Edit",  Label = "Reload from Disk",  Keybinding = "Alt+Shift+R",Execute = ReloadCurrentFromDisk,                                              Priority = CommandPriority.Lower });
        _commandRegistry.Register(new IdeCommand { Id = "edit.wrap-word",       Category = "Edit",  Label = "Word Wrap",                                    Execute = () => _layout?.SetWrapMode(WrapMode.WrapWords),                               Priority = CommandPriority.Minor });
        _commandRegistry.Register(new IdeCommand { Id = "edit.wrap-char",       Category = "Edit",  Label = "Character Wrap",                               Execute = () => _layout?.SetWrapMode(WrapMode.Wrap),                                   Priority = CommandPriority.Minor });
        _commandRegistry.Register(new IdeCommand { Id = "edit.no-wrap",         Category = "Edit",  Label = "No Wrap",                                      Execute = () => _layout?.SetWrapMode(WrapMode.NoWrap),                                 Priority = CommandPriority.Minor });

        // Build
        _commandRegistry.Register(new IdeCommand { Id = "build.build",          Category = "Build", Label = "Build",             Keybinding = "F6",         Execute = () => _ = _buildOps!.BuildProjectAsync(),                                      Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "build.test",           Category = "Build", Label = "Test",              Keybinding = "F7",         Execute = () => _ = _buildOps!.TestProjectAsync(),                                       Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "build.clean",          Category = "Build", Label = "Clean",                                        Execute = () => _ = _buildOps!.CleanProjectAsync(),                                      Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "build.stop",           Category = "Build", Label = "Stop",              Keybinding = "F4",         Execute = () => _buildService.Cancel(),                                       Priority = CommandPriority.Medium });

        // Run
        _commandRegistry.Register(new IdeCommand { Id = "run.run",              Category = "Run",   Label = "Run",               Keybinding = "F5",         Execute = () => _buildOps!.RunProject(),                                                         Priority = CommandPriority.Critical });

        // View
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-explorer", Category = "View",  Label = "Toggle Explorer",   Keybinding = "Ctrl+B",     Execute = () => _layout?.ToggleExplorer(),                                                     Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-output",   Category = "View",  Label = "Toggle Output Panel",Keybinding = "Ctrl+J",    Execute = () => _layout?.ToggleOutput(),                                                       Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "view.toggle-side-panel",Category = "View", Label = "Toggle Side Panel", Keybinding = "Alt+;",      Execute = () => _layout?.ToggleSidePanel(),                                                    Priority = CommandPriority.Default });

        // Git
        _commandRegistry.Register(new IdeCommand { Id = "git.source-control",    Category = "Git",   Label = "Source Control",    Keybinding = "Alt+G", Execute = () => _layout?.ShowSourceControl(),                                                Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "git.refresh",          Category = "Git",   Label = "Refresh Status",                               Execute = () => _ = _gitOps!.RefreshGitStatusAsync(),                                  Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "git.stage-file",       Category = "Git",   Label = "Stage Current File",                           Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitStageFileAsync(p); }, Priority = CommandPriority.GitStage });
        _commandRegistry.Register(new IdeCommand { Id = "git.unstage-file",     Category = "Git",   Label = "Unstage Current File",                         Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitUnstageFileAsync(p); }, Priority = CommandPriority.GitUnstage });
        _commandRegistry.Register(new IdeCommand { Id = "git.stage-all",        Category = "Git",   Label = "Stage All",                                    Execute = () => _ = _gitOps!.GitStageAllAsync(),                                       Priority = CommandPriority.GitStageAll });
        _commandRegistry.Register(new IdeCommand { Id = "git.unstage-all",      Category = "Git",   Label = "Unstage All",                                  Execute = () => _ = _gitOps!.GitUnstageAllAsync(),                                     Priority = CommandPriority.Low });
        _commandRegistry.Register(new IdeCommand { Id = "git.commit",           Category = "Git",   Label = "Commit\u2026",          Keybinding = "Ctrl+Enter", Execute = () => _ = _gitOps!.GitCommitAsync(),                                      Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "git.pull",             Category = "Git",   Label = "Pull",                                         Execute = () => _ = _gitOps!.GitCommandAsync("pull"),                                   Priority = CommandPriority.GitPull });
        _commandRegistry.Register(new IdeCommand { Id = "git.push",             Category = "Git",   Label = "Push",                                         Execute = () => _ = _gitOps!.GitCommandAsync("push"),                                   Priority = CommandPriority.GitPush });
        _commandRegistry.Register(new IdeCommand { Id = "git.diff-file",        Category = "Git",   Label = "Diff Current File",                            Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitShowDiffAsync(p); }, Priority = CommandPriority.Lower });
        _commandRegistry.Register(new IdeCommand { Id = "git.diff-all",         Category = "Git",   Label = "Diff All Changes",                             Execute = () => _ = _gitOps!.GitShowDiffAllAsync(),                                    Priority = CommandPriority.GitDiffAll });
        _commandRegistry.Register(new IdeCommand { Id = "git.discard-file",     Category = "Git",   Label = "Discard Changes (Current File)",                Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitDiscardFileAsync(p); }, Priority = CommandPriority.GitDiscard });
        _commandRegistry.Register(new IdeCommand { Id = "git.discard-all",      Category = "Git",   Label = "Discard All Changes",                          Execute = () => _ = _gitOps!.GitDiscardAllAsync(),                                     Priority = CommandPriority.GitDiscardAll });
        _commandRegistry.Register(new IdeCommand { Id = "git.stash",            Category = "Git",   Label = "Stash\u2026",                                  Execute = () => _ = _gitOps!.GitStashAsync(),                                          Priority = CommandPriority.CustomTool });
        _commandRegistry.Register(new IdeCommand { Id = "git.stash-pop",        Category = "Git",   Label = "Stash Pop",                                    Execute = () => _ = _gitOps!.GitStashPopAsync(),                                       Priority = CommandPriority.CustomToolAlt });
        _commandRegistry.Register(new IdeCommand { Id = "git.log",              Category = "Git",   Label = "Log",                                          Execute = () => _ = _gitOps!.GitShowLogAsync(),                                        Priority = CommandPriority.Minor });
        _commandRegistry.Register(new IdeCommand { Id = "git.log-file",         Category = "Git",   Label = "Log (Current File)",                           Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitShowFileLogAsync(p); }, Priority = CommandPriority.GitLogFile });
        _commandRegistry.Register(new IdeCommand { Id = "git.blame",            Category = "Git",   Label = "Blame (Current File)",                         Execute = () => { var p = _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitShowBlameAsync(p); }, Priority = CommandPriority.GitBlame });
        _commandRegistry.Register(new IdeCommand { Id = "git.switch-branch",    Category = "Git",   Label = "Switch Branch\u2026",                          Execute = () => _ = _gitOps!.GitSwitchBranchAsync(),                                   Priority = CommandPriority.GitBranch });
        _commandRegistry.Register(new IdeCommand { Id = "git.new-branch",       Category = "Git",   Label = "New Branch\u2026",                             Execute = () => _ = _gitOps!.GitNewBranchAsync(),                                      Priority = CommandPriority.GitNewBranch });
        _commandRegistry.Register(new IdeCommand { Id = "git.add-to-gitignore",    Category = "Git",   Label = "Add to .gitignore",                            Execute = () => { var p = _explorer?.GetSelectedPath() ?? _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitAddToGitignoreAsync(p, Directory.Exists(p)); }, Priority = CommandPriority.GitIgnoreAdd });
        _commandRegistry.Register(new IdeCommand { Id = "git.remove-from-gitignore", Category = "Git", Label = "Remove from .gitignore",                       Execute = () => { var p = _explorer?.GetSelectedPath() ?? _editorManager?.CurrentFilePath; if (p != null) _ = _gitOps!.GitRemoveFromGitignoreAsync(p); }, Priority = CommandPriority.GitIgnoreRemove });

        // LSP
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-def",         Category = "LSP",   Label = "Go to Definition",    Keybinding = "F12",          Execute = () => _ = _lspCoord!.ShowGoToDefinitionAsync(),                    Priority = CommandPriority.Critical });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-impl",        Category = "LSP",   Label = "Go to Implementation",Keybinding = "Ctrl+F12",     Execute = () => _ = _lspCoord!.ShowGoToImplementationAsync(),                Priority = CommandPriority.CriticalMinus });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.references",       Category = "LSP",   Label = "Find All References", Keybinding = "Shift+F12",    Execute = () => _ = _lspCoord!.ShowFindReferencesAsync(),                    Priority = CommandPriority.CriticalLow });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.nav-back",         Category = "LSP",   Label = "Navigate Back",       Keybinding = "Alt+\u2190",   Execute = () => _lspCoord!.NavigateBack(),                                  Priority = CommandPriority.High });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.rename",           Category = "LSP",   Label = "Rename Symbol",       Keybinding = "Ctrl+F2",      Execute = () => _ = _lspCoord!.ShowRenameAsync(_ws),                        Priority = CommandPriority.HighMinus });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.code-action",      Category = "LSP",   Label = "Code Actions",        Keybinding = "Ctrl+.",        Execute = () => _ = _lspCoord!.ShowCodeActionsAsync(_ws),                   Priority = CommandPriority.HighLow });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.document-symbols", Category = "LSP",   Label = "Focus Symbols",       Keybinding = "Alt+O", Execute = () => _layout?.FocusSymbolsTab(),                                                  Priority = CommandPriority.HighLowest });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.hover",            Category = "LSP",   Label = "Hover Tooltip",       Keybinding = "Ctrl+K",       Execute = () => _ = _lspCoord!.ShowHoverAsync(),                            Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.signature",        Category = "LSP",   Label = "Signature Help",      Keybinding = "F2",           Execute = () => _ = _lspCoord!.ShowSignatureHelpAsync(),                     Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.format",           Category = "LSP",   Label = "Format Document",     Keybinding = "Alt+Shift+F",  Execute = () => _ = _lspCoord!.FormatDocumentAsync(),                        Priority = CommandPriority.Default });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.complete",         Category = "LSP",   Label = "Show Completions",    Keybinding = "Ctrl+Space",   Execute = () => _ = _lspCoord!.ShowCompletionAsync(),                        Priority = CommandPriority.Low });

        // Terminal
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell",          Category = "Terminal", Label = "Bottom Shell",       Keybinding = "F8",         Execute = () => _buildOps!.OpenShell(),                                                          Priority = CommandPriority.Medium });
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell-tab",      Category = "Terminal", Label = "Editor Shell Tab",                             Execute = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenShellTab(); },      Priority = CommandPriority.Normal });
        _commandRegistry.Register(new IdeCommand { Id = "tools.side-shell",    Category = "Terminal", Label = "Side Panel Shell",   Keybinding = "Shift+F8",   Execute = () => { if (IdeConstants.IsDesktopOs) _layout?.OpenSidePanelShell(); },                                                 Priority = CommandPriority.NormalLow });

        // NuGet
        _commandRegistry.Register(new IdeCommand { Id = "tools.lazynuget",      Category = "NuGet",  Label = "LazyNuGet",         Keybinding = "F9",         Execute = () => { if (IdeConstants.IsDesktopOs) _buildOps!.OpenLazyNuGetTab(); }, Priority = CommandPriority.Default });
        if (!_buildOps!.HasLazyNuGet)
            _commandRegistry.Register(new IdeCommand { Id = "tools.nuget",      Category = "NuGet",  Label = "Add NuGet Package\u2026",                      Execute = () => _buildOps!.ShowNuGetDialog(),                                                    Priority = CommandPriority.Low });

        // Tools
        _commandRegistry.Register(new IdeCommand { Id = "tools.config",         Category = "Tools", Label = "Edit Config",                                  Execute = () => _buildOps!.OpenConfigFile(),                                                     Priority = CommandPriority.Lower });

        // Help
        _commandRegistry.Register(new IdeCommand { Id = "help.about",           Category = "Help",  Label = "About lazydotide\u2026",                       Execute = () => _layout?.ShowAbout(),                                                          Priority = CommandPriority.Least });

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
            SplitRatio = _layout.SplitRatio,
            ExplorerColumnWidth = _layout.ExplorerColumnWidth,
            SidePanelColumnWidth = _layout.SidePanelColumnWidth,
        });
    }

    private void RestoreWorkspaceState()
    {
        if (_workspaceState == null || _layout == null) return;
        var snapshot = _workspaceState.RestoreWorkspaceState(
            _layout.ExplorerVisible, _layout.OutputVisible, _layout.SidePanelVisible);

        if (snapshot.NeedToggleExplorer) _layout.ToggleExplorer();
        if (snapshot.NeedToggleOutput) _layout.ToggleOutput();
        if (snapshot.NeedToggleSidePanel) _layout.ToggleSidePanel();

        if (snapshot.ExplorerColumnWidth > 0)
            _layout.SetExplorerColumnWidth(snapshot.ExplorerColumnWidth);
        if (snapshot.SidePanelColumnWidth > 0)
            _layout.SetSidePanelColumnWidth(snapshot.SidePanelColumnWidth);

        if (snapshot.SplitRatio > 0.1 && snapshot.SplitRatio < 0.95)
            _layout.ApplyRestoredSplitRatio(snapshot.SplitRatio);

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
