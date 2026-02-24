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

    // Status bar
    private MarkupControl? _statusLeft;   // git + error combined
    private MarkupControl? _cursorStatus;
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

    // LSP portal overlays (rendered inside _mainWindow, not as separate windows)
    private LspCompletionPortalContent? _completionPortal;
    private LayoutNode? _completionPortalNode;
    private LspTooltipPortalContent? _tooltipPortal;
    private LayoutNode? _tooltipPortalNode;

    // Completion filter tracking — column at which completion was triggered (1-indexed)
    private int _completionTriggerColumn;
    private int _completionTriggerLine;

    // Debounce timer for dot-triggered auto-completion
    private Timer? _dotTriggerDebounce;

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
        _fileWatcher?.Dispose();
        DismissCompletionPortal();
        DismissTooltipPortal();
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

        var pipeline = new FileMiddlewarePipeline();
        pipeline.Register(new CSharpFileMiddleware());
        pipeline.Register(new MarkdownFileMiddleware());
        pipeline.Register(new JsonFileMiddleware());
        pipeline.Register(new XmlFileMiddleware());
        pipeline.Register(new DefaultFileMiddleware());

        _editorManager = new EditorManager(_ws, pipeline);
        _outputPanel = new OutputPanel(_ws);

        BuildMainWindow(desktop.Width, mainH);
        BuildOutputWindow(desktop.Width, outH, mainH);

        _ws.AddWindow(_mainWindow!);
        _ws.AddWindow(_outputWindow!);

        _ws.ConsoleDriver.ScreenResized += OnScreenResized;

        _fileWatcher = new FileWatcher();
        _fileWatcher.FileChanged      += (_, path) => _pendingUiActions.Enqueue(() => HandleExternalFileChanged(path));
        _fileWatcher.StructureChanged += (_, _)    => _pendingUiActions.Enqueue(() => _explorer?.Refresh());
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
                .AddItem("Save", "Ctrl+S", () => _editorManager?.SaveCurrent())
                .AddItem("Close Tab", "Ctrl+W", () => CloseCurrentTab())
                .AddSeparator()
                .AddItem("Refresh Explorer", () => _explorer?.Refresh())
                .AddSeparator()
                .AddItem("Exit", "Alt+F4", () => _ws.Shutdown(0)))
            .AddItem("Edit", m => m
                .AddItem("Word Wrap", () => SetWrapMode(WrapMode.WrapWords))
                .AddItem("Wrap (character)", () => SetWrapMode(WrapMode.Wrap))
                .AddItem("No Wrap", () => SetWrapMode(WrapMode.NoWrap))
                .AddSeparator()
                .AddItem("Find...",    "Ctrl+F", () => ShowFindReplace())
                .AddItem("Replace...", "Ctrl+H", () => ShowFindReplace()))
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
                .AddItem("Toggle Output Panel", "Ctrl+J", () => ToggleOutput()))
            .AddItem("Git", m => m
                .AddItem("Refresh Status", () => _ = RefreshGitStatusAsync())
                .AddItem("Pull", () => _ = GitCommandAsync("pull"))
                .AddItem("Push", () => _ = GitCommandAsync("push")))
            .AddItem("Tools", m =>
            {
                m.AddItem("Command Palette",  "Ctrl+P",         () => ShowCommandPalette())
                 .AddSeparator()
                 .AddItem("Go to Definition", "F12",            () => _ = ShowGoToDefinitionAsync())
                 .AddItem("Navigate Back",    "Alt+Left",       () => NavigateBack())
                 .AddSeparator()
                 .AddItem("Signature Help",   "F2",             () => _ = ShowSignatureHelpAsync())
                 .AddItem("Format Document",  "Alt+Shift+F",    () => _ = FormatDocumentAsync())
                 .AddItem("Reload from Disk", "Alt+Shift+R",    () => ReloadCurrentFromDisk())
                 .AddSeparator()
                 .AddItem("Add NuGet Package", () => ShowNuGetDialog())
                 .AddSeparator()
                 .AddItem("Shell",      "F8", () => OpenShell())
                 .AddItem("Shell Tab",  "",   () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenShellTab(); })
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
            .StickyTop()
            .Build();

        _mainWindow!.AddControl(toolbar);
    }

    private void AddMainContentArea()
    {
        var mainContent = new HorizontalGridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

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

        _explorerSplitter = new SplitterControl();
        mainContent.AddSplitter(0, _explorerSplitter);

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

        _editorManager.DocumentOpened  += (_, a) => _ = _lsp?.DidOpenAsync(a.FilePath, a.Content);
        _editorManager.DocumentChanged += (_, a) =>
        {
            _ = _lsp?.DidChangeAsync(a.FilePath, a.Content);
            TryScheduleDotCompletion(a.FilePath, a.Content);
        };
        _editorManager.DocumentSaved   += (_, p) => _ = _lsp?.DidSaveAsync(p);
        _editorManager.DocumentSaved   += (_, savedPath) => _fileWatcher?.SuppressNext(savedPath);
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
            _ = _lsp?.DidCloseAsync(p);
            // Clear diagnostics for the closed file
            _outputPanel?.PopulateLspDiagnostics(new List<BuildDiagnostic>());
            UpdateErrorCount(new List<BuildDiagnostic>());
        };

        _editorManager.ActiveFileChanged += (_, filePath) =>
        {
            DismissCompletionPortal();
            DismissTooltipPortal();
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

        // Dismiss tooltip on any key
        if (_tooltipPortal != null)
            DismissTooltipPortal();

        // Escape: dismiss portals if open, then swallow — never let the editor exit editing mode
        if (key == ConsoleKey.Escape && mods == 0)
        {
            if (_completionPortal != null)
                DismissCompletionPortal();
            e.Handled = true;
            return;
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
        else if (key == ConsoleKey.LeftArrow && mods == ConsoleModifiers.Alt)
        {
            NavigateBack();
            e.Handled = true;
        }
        else if (key == ConsoleKey.F2 && mods == 0)
        {
            _ = ShowSignatureHelpAsync();
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
        _commandRegistry.Register(new IdeCommand { Id = "file.refresh-explorer",Category = "File",  Label = "Refresh Explorer",                             Execute = () => _explorer?.Refresh(),                                         Priority = 70 });
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

        // Git
        _commandRegistry.Register(new IdeCommand { Id = "git.refresh",          Category = "Git",   Label = "Refresh Status",                               Execute = () => _ = RefreshGitStatusAsync(),                                  Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "git.pull",             Category = "Git",   Label = "Pull",                                         Execute = () => _ = GitCommandAsync("pull"),                                   Priority = 65 });
        _commandRegistry.Register(new IdeCommand { Id = "git.push",             Category = "Git",   Label = "Push",                                         Execute = () => _ = GitCommandAsync("push"),                                   Priority = 60 });

        // LSP
        _commandRegistry.Register(new IdeCommand { Id = "lsp.goto-def",         Category = "LSP",   Label = "Go to Definition",  Keybinding = "F12",        Execute = () => _ = ShowGoToDefinitionAsync(),                                Priority = 90 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.nav-back",         Category = "LSP",   Label = "Navigate Back",     Keybinding = "Alt+\u2190", Execute = NavigateBack,                                                       Priority = 85 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.hover",            Category = "LSP",   Label = "Hover Tooltip",     Keybinding = "Ctrl+K",     Execute = () => _ = ShowHoverAsync(),                                         Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.signature",        Category = "LSP",   Label = "Signature Help",    Keybinding = "F2",         Execute = () => _ = ShowSignatureHelpAsync(),                                 Priority = 75 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.format",           Category = "LSP",   Label = "Format Document",   Keybinding = "Alt+Shift+F",Execute = () => _ = FormatDocumentAsync(),                                    Priority = 70 });
        _commandRegistry.Register(new IdeCommand { Id = "lsp.complete",         Category = "LSP",   Label = "Show Completions",  Keybinding = "Ctrl+Space", Execute = () => _ = ShowCompletionAsync(),                                    Priority = 65 });

        // Tools
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell",          Category = "Tools", Label = "Open Shell",        Keybinding = "F8",         Execute = OpenShell,                                                          Priority = 80 });
        _commandRegistry.Register(new IdeCommand { Id = "tools.shell-tab",      Category = "Tools", Label = "Open Shell Tab",                               Execute = () => { if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) OpenShellTab(); },      Priority = 75 });
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

    // ──────────────────────────────────────────────────────────────
    // LSP: Hover & Completion
    // ──────────────────────────────────────────────────────────────

    private async Task ShowHoverAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null)
        {
            _ws.NotificationStateService.ShowNotification("LSP", "Language server not running.", SharpConsoleUI.Core.NotificationSeverity.Warning);
            return;
        }
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var result = await _lsp.HoverAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (result == null || string.IsNullOrWhiteSpace(result.Contents))
        {
            _ws.NotificationStateService.ShowNotification("Hover", "No type info at cursor.", SharpConsoleUI.Core.NotificationSeverity.Info);
            return;
        }

        var lines = result.Contents.Split('\n')
            .Select(l => Markup.Escape(l.TrimEnd()))  // escape so plain text survives markup pipeline
            .Where(l => !string.IsNullOrEmpty(l))
            .Take(8)
            .ToList();
        if (lines.Count == 0) return;

        DismissTooltipPortal();
        var cursor = _editorManager.GetCursorBounds();
        var portal = new LspTooltipPortalContent(
            lines, cursor.X, cursor.Y,
            _mainWindow!.Width, _mainWindow.Height,
            preferAbove: true);

        _tooltipPortal     = portal;
        _tooltipPortalNode = _mainWindow.CreatePortal(editor, portal);
    }

    private async Task ShowCompletionAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path   = _editorManager.CurrentFilePath;
        if (path == null) return;

        var items = await _lsp.CompletionAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (items.Count == 0)
        {
            _ws.NotificationStateService.ShowNotification(
                "Completion", "No completions at cursor.", SharpConsoleUI.Core.NotificationSeverity.Info);
            return;
        }

        DismissCompletionPortal();

        // If the cursor is mid-word (e.g. user typed "GetFo" then pressed Ctrl+Space),
        // walk back to the word start and use the partial word as the initial filter.
        // This also positions _completionTriggerColumn so subsequent keystrokes keep filtering.
        var lineContent = editor.Content.Split('\n');
        int lineIdx = editor.CurrentLine - 1;
        int cursorCol0 = editor.CurrentColumn - 1;  // 0-indexed position in the line
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

        _completionPortal     = portal;
        _completionPortalNode = _mainWindow.CreatePortal(editor, portal);

        // Give the portal a Container so its Invalidate() calls reach the window.
        // Portal content is not added via AddControl, so Container is never set automatically.
        portal.Container = _mainWindow;

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
            _ws.NotificationStateService.ShowNotification(
                "Definition Not Found", "No definition found at current position.",
                SharpConsoleUI.Core.NotificationSeverity.Info);
            return;
        }

        _navHistory.Push((path, editor.CurrentLine, editor.CurrentColumn));

        var loc = locations[0];
        _editorManager.OpenFile(LspClient.UriToPath(loc.Uri));

        var targetEditor = _editorManager.CurrentEditor;
        if (targetEditor != null)
        {
            targetEditor.GoToLine(loc.Range.Start.Line + 1);
            targetEditor.SetLogicalCursorPosition(
                new System.Drawing.Point(loc.Range.Start.Character, loc.Range.Start.Line));
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

    private async Task ShowSignatureHelpAsync()
    {
        if (_lsp == null || _editorManager?.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path   = _editorManager.CurrentFilePath;
        if (path == null) return;

        var sig = await _lsp.SignatureHelpAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (sig == null || sig.Signatures.Count == 0)
        {
            _ws.NotificationStateService.ShowNotification(
                "Signature Help", "No signature at cursor. Position inside function arguments.",
                SharpConsoleUI.Core.NotificationSeverity.Info);
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
            lines.Add(Markup.Escape(activeSig.Documentation!));

        DismissTooltipPortal();
        var cursor = _editorManager.GetCursorBounds();
        var portal = new LspTooltipPortalContent(
            lines, cursor.X, cursor.Y,
            _mainWindow.Width, _mainWindow.Height,
            preferAbove: true);

        _tooltipPortal     = portal;
        _tooltipPortalNode = _mainWindow.CreatePortal(editor, portal);
    }

    // ── Portal helpers ─────────────────────────────────────────────────────────

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
        if (_tooltipPortalNode != null && _mainWindow != null)
        {
            _mainWindow.RemovePortal(_editorManager?.CurrentEditor ?? (IWindowControl)_mainWindow, _tooltipPortalNode);
            _tooltipPortalNode = null;
            _tooltipPortal = null;
        }
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
        // Only trigger auto-complete on '.' in C# files when the LSP is running
        if (_lsp == null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        var editor = _editorManager?.CurrentEditor;
        if (editor == null) return;

        int col = editor.CurrentColumn - 1;   // 0-based
        var lines = content.Split('\n');
        int lineIdx = editor.CurrentLine - 1;
        if (lineIdx < 0 || lineIdx >= lines.Length) return;

        string currentLine = lines[lineIdx];
        if (col <= 0 || col > currentLine.Length) return;

        if (currentLine[col - 1] == '.')
        {
            // Flush the pending DidChange immediately so the LSP has the dot before
            // we request completions.  Then wait ~300 ms for the server to process it.
            _ = _lsp.FlushPendingChangeAsync();

            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = new Timer(
                _ => _ = ShowCompletionAsync(),
                null, 350, Timeout.Infinite);
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
                "[dim]           Enables: IntelliSense · Go to Definition[/]",
                "[dim]                    Signature Help · Hover · Formatting[/]",
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
            "[dim]  F12  Definition  Alt+←  Back[/]",
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
