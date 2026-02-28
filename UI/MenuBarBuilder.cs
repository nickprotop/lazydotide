using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace DotNetIDE;

internal class MenuBarBuilder
{
    private readonly ConsoleWindowSystem _ws;
    private readonly EditorManager _editorManager;
    private readonly ExplorerPanel _explorer;
    private readonly SidePanel _sidePanel;
    private readonly BuildService _buildService;
    private readonly IdeConfig _config;
    private readonly FileMiddlewarePipeline _pipeline;
    private readonly GitCoordinator _gitOps;
    private readonly BuildCoordinator _buildOps;
    private readonly LspCoordinator _lspCoord;
    private readonly LayoutController _layout;

    // Delegates for IdeApp-owned operations
    public FileOperationDelegates? FileOps { get; set; }
    public Func<Task>? OpenFolderAsync { get; set; }
    public Action? CloseCurrentTab { get; set; }
    public Action? ReloadCurrentFromDisk { get; set; }
    public Action? ShowCommandPalette { get; set; }

    private Window? _mainWindow;
    private IWindowControl? _menuControl;

    public MenuBarBuilder(
        ConsoleWindowSystem ws,
        EditorManager editorManager,
        ExplorerPanel explorer,
        SidePanel sidePanel,
        BuildService buildService,
        IdeConfig config,
        FileMiddlewarePipeline pipeline,
        GitCoordinator gitOps,
        BuildCoordinator buildOps,
        LspCoordinator lspCoord,
        LayoutController layout)
    {
        _ws = ws;
        _editorManager = editorManager;
        _explorer = explorer;
        _sidePanel = sidePanel;
        _buildService = buildService;
        _config = config;
        _pipeline = pipeline;
        _gitOps = gitOps;
        _buildOps = buildOps;
        _lspCoord = lspCoord;
        _layout = layout;
    }

    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void AddMenuBar()
    {
        if (_menuControl != null)
            _mainWindow!.RemoveContent(_menuControl);

        var menu = Controls.Menu()
            .Horizontal()
            .Sticky()
            .AddItem("File", m => m
                .AddItem("Open Folder...", () => _ = OpenFolderAsync?.Invoke() ?? Task.CompletedTask)
                .AddSeparator()
                .AddItem("New File", "Ctrl+N", () => { var dir = _explorer.GetSelectedPath(); if (dir != null) { if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir); if (dir != null) _ = FileOps?.HandleNewFileAsync?.Invoke(dir) ?? Task.CompletedTask; } })
                .AddItem("New Folder", "Ctrl+Shift+N", () => { var dir = _explorer.GetSelectedPath(); if (dir != null) { if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir); if (dir != null) _ = FileOps?.HandleNewFolderAsync?.Invoke(dir) ?? Task.CompletedTask; } })
                .AddItem("Rename", "F2", () => { var p = _explorer.GetSelectedPath(); if (p != null) _ = FileOps?.HandleRenameAsync?.Invoke(p) ?? Task.CompletedTask; })
                .AddItem("Delete", "Del", () => { var p = _explorer.GetSelectedPath(); if (p != null) _ = FileOps?.HandleDeleteAsync?.Invoke(p) ?? Task.CompletedTask; })
                .AddSeparator()
                .AddItem("Save", "Ctrl+S", () => _editorManager.SaveCurrent())
                .AddItem("Close Tab", "Ctrl+W", () => CloseCurrentTab?.Invoke())
                .AddSeparator()
                .AddItem("Refresh Explorer", "F5", () => _ = FileOps?.RefreshExplorerAndGitAsync?.Invoke() ?? Task.CompletedTask)
                .AddSeparator()
                .AddItem("Exit", "Alt+F4", () => _ws.Shutdown(0)))
            .AddItem("Edit", m =>
            {
                m.AddItem("Word Wrap", () => _layout.SetWrapMode(WrapMode.WrapWords))
                 .AddItem("Wrap (character)", () => _layout.SetWrapMode(WrapMode.Wrap))
                 .AddItem("No Wrap", () => _layout.SetWrapMode(WrapMode.NoWrap))
                 .AddSeparator()
                 .AddItem("Find...",    "Ctrl+F", () => _layout.ShowFindReplace())
                 .AddItem("Replace...", "Ctrl+H", () => _layout.ShowFindReplace())
                 .AddSeparator()
                 .AddItem("Syntax Highlighter", sub =>
                 {
                     foreach (var (name, highlighter) in _pipeline.GetAvailableHighlighters())
                     {
                         var n = name;
                         var h = highlighter;
                         sub.AddItem(n, () => _editorManager.SetSyntaxHighlighter(n, h));
                     }
                 });
            })
            .AddItem("Build", m => m
                .AddItem("Build", "F6", () => _ = _buildOps.BuildProjectAsync())
                .AddItem("Test", "F7", () => _ = _buildOps.TestProjectAsync())
                .AddSeparator()
                .AddItem("Clean", () => _ = _buildOps.CleanProjectAsync())
                .AddItem("Stop", "F4", () => _buildService.Cancel()))
            .AddItem("Run", m => m
                .AddItem("Run", "F5", () => _buildOps.RunProject())
                .AddItem("Stop", "F4", () => _buildService.Cancel()))
            .AddItem("View", m =>
            {
                m.AddItem("Toggle Explorer", "Ctrl+B", () => _layout.ToggleExplorer())
                 .AddItem("Toggle Output Panel", "Ctrl+J", () => _layout.ToggleOutput())
                 .AddItem("Toggle Side Panel", "Alt+;", () => _layout.ToggleSidePanel())
                 .AddSeparator();

                // Editor Tabs submenu (dynamic)
                m.AddItem("Editor Tabs", sub =>
                {
                    if (_editorManager.TabControl.TabCount > 0)
                    {
                        var tabs = _editorManager.TabControl.TabPages;
                        for (int i = 0; i < tabs.Count; i++)
                        {
                            int idx = i;
                            sub.AddItem(tabs[i].Title, () => _editorManager.TabControl.ActiveTabIndex = idx);
                        }
                    }
                    else
                    {
                        sub.AddItem("(no tabs open)", () => { });
                    }
                });

                // Side Panel submenu
                m.AddItem("Side Panel", sub =>
                {
                    sub.AddItem("Symbols", () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); _sidePanel.SwitchToSymbolsTab(); });
                    sub.AddItem("Git", () => _layout.ShowSourceControl());
                    if (_sidePanel.TabControl.HasTab("Shell"))
                        sub.AddItem("Shell", () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); _sidePanel.SwitchToShellTab(); });
                });
            })
            .AddItem("Git", m => m
                .AddItem("Source Control", "Alt+G", () => _layout.ShowSourceControl())
                .AddSeparator()
                .AddItem("Refresh Status", () => _ = _gitOps.RefreshGitStatusAsync())
                .AddSeparator()
                .AddItem("Stage All", () => _ = _gitOps.GitStageAllAsync())
                .AddItem("Unstage All", () => _ = _gitOps.GitUnstageAllAsync())
                .AddSeparator()
                .AddItem("Commit\u2026", "Ctrl+Enter", () => _ = _gitOps.GitCommitAsync())
                .AddSeparator()
                .AddItem("Pull", () => _ = _gitOps.GitCommandAsync("pull"))
                .AddItem("Push", () => _ = _gitOps.GitCommandAsync("push"))
                .AddSeparator()
                .AddItem("Stash\u2026", () => _ = _gitOps.GitStashAsync())
                .AddItem("Stash Pop", () => _ = _gitOps.GitStashPopAsync())
                .AddSeparator()
                .AddItem("Switch Branch\u2026", () => _ = _gitOps.GitSwitchBranchAsync())
                .AddItem("New Branch\u2026", () => _ = _gitOps.GitNewBranchAsync())
                .AddSeparator()
                .AddItem("Log", () => _ = _gitOps.GitShowLogAsync())
                .AddItem("Diff All", () => _ = _gitOps.GitShowDiffAllAsync())
                .AddSeparator()
                .AddItem("Discard All Changes\u2026", () => _ = _gitOps.GitDiscardAllAsync()))
            .AddItem("Tools", m =>
            {
                m.AddItem("Command Palette",  "Ctrl+P",         () => ShowCommandPalette?.Invoke())
                 .AddSeparator();

                m.AddItem("Navigate", sub => sub
                    .AddItem("Go to Definition",     "F12",       () => _ = _lspCoord.ShowGoToDefinitionAsync())
                    .AddItem("Go to Implementation", "Ctrl+F12",  () => _ = _lspCoord.ShowGoToImplementationAsync())
                    .AddItem("Find All References",  "Shift+F12", () => _ = _lspCoord.ShowFindReferencesAsync())
                    .AddItem("Navigate Back",        "Alt+Left",  () => _lspCoord.NavigateBack()));

                m.AddItem("Refactor", sub => sub
                    .AddItem("Rename Symbol", "Ctrl+F2", () => _ = _lspCoord.ShowRenameAsync(_ws))
                    .AddItem("Code Actions",  "Ctrl+.",  () => _ = _lspCoord.ShowCodeActionsAsync(_ws))
                    .AddItem("Focus Symbols", "Alt+O",   () => _layout.FocusSymbolsTab()));

                m.AddItem("Code", sub => sub
                    .AddItem("Signature Help",   "F2",          () => _ = _lspCoord.ShowSignatureHelpAsync())
                    .AddItem("Format Document",  "Alt+Shift+F", () => _ = _lspCoord.FormatDocumentAsync())
                    .AddItem("Reload from Disk", "Alt+Shift+R", () => ReloadCurrentFromDisk?.Invoke()));

                m.AddSeparator();

                m.AddItem("NuGet", sub =>
                {
                    if (_buildOps.HasLazyNuGet)
                        sub.AddItem("LazyNuGet", "F9", () => { if (IdeConstants.IsDesktopOs) _buildOps.OpenLazyNuGetTab(); });
                    else
                        sub.AddItem("Add NuGet Package", () => _buildOps.ShowNuGetDialog());
                });

                m.AddItem("Terminal", sub => sub
                    .AddItem("Bottom Shell",     "F8",       () => _buildOps.OpenShell())
                    .AddItem("Editor Shell Tab", "",         () => { if (IdeConstants.IsDesktopOs) _buildOps.OpenShellTab(); })
                    .AddItem("Side Panel Shell", "Shift+F8", () => { if (IdeConstants.IsDesktopOs) _layout.OpenSidePanelShell(); }));

                if (_config.Tools.Count > 0)
                {
                    m.AddSeparator();
                    for (int i = 0; i < _config.Tools.Count; i++)
                    {
                        int idx = i;
                        var toolName = _config.Tools[i].Name;
                        m.AddItem(toolName, sub =>
                        {
                            sub.AddItem("Open in Tab",          () => { if (IdeConstants.IsDesktopOs) _buildOps.OpenConfigToolTab(idx); });
                            sub.AddItem("Open in Bottom Shell", () => { if (IdeConstants.IsDesktopOs) _buildOps.OpenConfigToolInOutputPanel(idx); });
                            sub.AddItem("Open in Side Panel",   () => { if (IdeConstants.IsDesktopOs) _buildOps.OpenConfigToolInSidePanel(idx, () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); }, () => _layout.InvalidateSidePanel()); });
                        });
                    }
                }

                m.AddSeparator();
                m.AddItem("Edit Config", "", () => _buildOps.OpenConfigFile());
            })
            .AddItem("Help", m => m
                .AddItem("About lazydotide\u2026", () => _layout.ShowAbout()))
            .Build();

        menu.StickyPosition = StickyPosition.Top;
        _menuControl = menu;
        _mainWindow!.InsertControl(0, menu);
    }

    public void AddToolbar()
    {
        var toolbar = Controls.Toolbar()
            .AddButton("Run F5", (_, _) => _buildOps.RunProject())
            .AddButton("Build F6", (_, _) => _ = _buildOps.BuildProjectAsync())
            .AddButton("Test F7", (_, _) => _ = _buildOps.TestProjectAsync())
            .AddButton("Stop F4", (_, _) => _buildService.Cancel())
            .AddButton("Shell F8", (_, _) => _buildOps.OpenShell())
            .AddButton("Shell Tab", (_, _) => { if (IdeConstants.IsDesktopOs) _buildOps.OpenShellTab(); })
            .AddButton("LazyNuGet F9", (_, _) => { if (IdeConstants.IsDesktopOs) _buildOps.OpenLazyNuGetTab(); })
            .AddButton("Explorer", (_, _) => _layout.ToggleExplorer())
            .AddButton("Output", (_, _) => _layout.ToggleOutput())
            .AddButton("Side Panel", (_, _) => _layout.ToggleSidePanel())
            .StickyTop()
            .Build();

        _mainWindow!.AddControl(toolbar);
    }
}
