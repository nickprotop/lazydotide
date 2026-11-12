using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;

namespace DotNetIDE;

internal class MenuBarBuilder
{
    private readonly AppContext _ctx;
    private readonly GitCoordinator _gitOps;
    private readonly BuildCoordinator _buildOps;
    private readonly LspCoordinator _lspCoord;
    private readonly DebugCoordinator _debugCoord;
    private readonly LayoutController _layout;

    // Delegates for IdeApp-owned operations
    public FileOperationDelegates? FileOps { get; set; }
    public Func<Task>? OpenFolderAsync { get; set; }
    public Action? CloseCurrentTab { get; set; }
    public Action? ReloadCurrentFromDisk { get; set; }
    public Action? ShowCommandPalette { get; set; }
    public Action? HandleF5 { get; set; }
    public Action? HandleF5WithProfile { get; set; }

    private IWindowControl? _menuControl;
    private IWindowControl? _toolbarControl;

    public MenuBarBuilder(
        AppContext ctx,
        GitCoordinator gitOps,
        BuildCoordinator buildOps,
        LspCoordinator lspCoord,
        DebugCoordinator debugCoord,
        LayoutController layout)
    {
        _ctx = ctx;
        _gitOps = gitOps;
        _buildOps = buildOps;
        _lspCoord = lspCoord;
        _debugCoord = debugCoord;
        _layout = layout;
    }

    public void AddMenuBar()
    {
        if (_menuControl != null)
            _ctx.MainGrid.RemoveControl(_menuControl);

        var menu = Controls.Menu()
            .Horizontal()
            .AddItem("File", m => m
                .AddItem("Open Folder...", () => _ = OpenFolderAsync?.Invoke() ?? Task.CompletedTask)
                .AddSeparator()
                .AddItem("New File", "Ctrl+N", () => { var dir = _ctx.Explorer.GetSelectedPath(); if (dir != null) { if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir); if (dir != null) _ = FileOps?.HandleNewFileAsync?.Invoke(dir) ?? Task.CompletedTask; } })
                .AddItem("New Folder", "Ctrl+Shift+N", () => { var dir = _ctx.Explorer.GetSelectedPath(); if (dir != null) { if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir); if (dir != null) _ = FileOps?.HandleNewFolderAsync?.Invoke(dir) ?? Task.CompletedTask; } })
                .AddItem("Rename", "F2", () => { var p = _ctx.Explorer.GetSelectedPath(); if (p != null) _ = FileOps?.HandleRenameAsync?.Invoke(p) ?? Task.CompletedTask; })
                .AddItem("Delete", "Del", () => { var p = _ctx.Explorer.GetSelectedPath(); if (p != null) _ = FileOps?.HandleDeleteAsync?.Invoke(p) ?? Task.CompletedTask; })
                .AddSeparator()
                .AddItem("Save", "Ctrl+S", () => _ctx.EditorManager.SaveCurrent())
                .AddItem("Close Tab", "Ctrl+W", () => CloseCurrentTab?.Invoke())
                .AddSeparator()
                .AddItem("Refresh Explorer", "F5", () => _ = FileOps?.RefreshExplorerAndGitAsync?.Invoke() ?? Task.CompletedTask)
                .AddSeparator()
                .AddItem("Exit", "Alt+F4", () => _ctx.WindowSystem.Shutdown(0)))
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
                     foreach (var (name, highlighter) in _ctx.Pipeline.GetAvailableHighlighters())
                     {
                         var n = name;
                         var h = highlighter;
                         sub.AddItem(n, () => _ctx.EditorManager.SetSyntaxHighlighter(n, h));
                     }
                 });
            })
            .AddItem("Build", m => m
                .AddItem("Build", "F6", () => _ = _buildOps.BuildProjectAsync())
                .AddItem("Test", "F7", () => _ = _buildOps.TestProjectAsync())
                .AddSeparator()
                .AddItem("Clean", () => _ = _buildOps.CleanProjectAsync())
                .AddItem("Stop", "F4", () => _ctx.BuildService.Cancel()))
            .AddItem("Run", m => m
                .AddItem("Start Debugging", "F5", () => HandleF5?.Invoke())
                .AddItem("Start with Profile…", "", () => HandleF5WithProfile?.Invoke())
                .AddItem("Run Without Debugging", "Ctrl+F5", () => _buildOps.RunProject())
                .AddSeparator()
                .AddItem("Toggle Breakpoint", "F9", () => { })
                .AddSeparator()
                .AddItem("Stop Debugging", "Shift+F5", () => { })
                .AddItem("Stop", "F4", () => _ctx.BuildService.Cancel()))
            .AddItem("View", m =>
            {
                m.AddItem("Toggle Explorer", "Ctrl+B", () => _layout.ToggleExplorer())
                 .AddItem("Toggle Output Panel", "Ctrl+J", () => _layout.ToggleOutput())
                 .AddItem("Toggle Side Panel", "Alt+;", () => _layout.ToggleSidePanel())
                 .AddItem("Explorer Full Height", () => _layout.ToggleExplorerFullHeight())
                 .AddSeparator();

                // Editor Tabs submenu (dynamic)
                m.AddItem("Editor Tabs", sub =>
                {
                    if (_ctx.EditorManager.TabControl.TabCount > 0)
                    {
                        var tabs = _ctx.EditorManager.TabControl.TabPages;
                        for (int i = 0; i < tabs.Count; i++)
                        {
                            int idx = i;
                            sub.AddItem(tabs[i].Title, () => _ctx.EditorManager.TabControl.ActiveTabIndex = idx);
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
                    sub.AddItem("Symbols", () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); _ctx.SidePanel.SwitchToSymbolsTab(); });
                    sub.AddItem("Git", () => _layout.ShowSourceControl());
                    if (_ctx.SidePanel.TabControl.HasTab("Shell"))
                        sub.AddItem("Shell", () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); _ctx.SidePanel.SwitchToShellTab(); });
                    sub.AddSeparator();
                    sub.AddItem("Variables", () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); _debugCoord.ShowVariablesTab(); });
                    sub.AddItem("Call Stack", () => { if (!_layout.SidePanelVisible) _layout.ToggleSidePanel(); _debugCoord.ShowCallStackTab(); });
                });

                m.AddItem("Debug Console", () => { if (!_layout.OutputVisible) _layout.ToggleOutput(); _debugCoord.ShowDebugConsoleTab(); });
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
                    .AddItem("Rename Symbol", "Ctrl+F2", () => _ = _lspCoord.ShowRenameAsync(_ctx.WindowSystem))
                    .AddItem("Code Actions",  "Ctrl+.",  () => _ = _lspCoord.ShowCodeActionsAsync(_ctx.WindowSystem))
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

                if (_ctx.Config.Tools.Count > 0)
                {
                    m.AddSeparator();
                    for (int i = 0; i < _ctx.Config.Tools.Count; i++)
                    {
                        int idx = i;
                        var toolName = _ctx.Config.Tools[i].Name;
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
                .AddItem("Install csharp-ls (LSP)\u2026", () => _layout.InstallLsp())
                .AddItem("Install netcoredbg\u2026", () => _layout.InstallDebugger())
                .AddSeparator()
                .AddItem("About lazydotide\u2026", () => _layout.ShowAbout()))
            .Build();

        _menuControl = menu;
        _ctx.MainGrid.Place(menu, 0, 0, colSpan: 3);
    }

    public void AddToolbar()
    {
        if (_toolbarControl != null)
            _ctx.MainGrid.RemoveControl(_toolbarControl);

        // Ten identically-weighted labels in a row read as one undifferentiated strip, and the
        // shortcut sat inside the label at the same brightness as the action. Three things fix
        // that without adding chrome: space between buttons, separators between groups, and the
        // shortcut dimmed so the eye lands on the verb.
        //
        // Grouped run/build, then tools, then panel toggles - the panel ones only change what is
        // on screen, so they do not belong beside Build and Test.
        var toolbar = Controls.Toolbar()
            .WithSpacing(1)
            .WithWrap()
            .WithMargin(1, 0, 1, 0)
            .WithBackgroundColor(Color.Transparent)
            .WithBelowLineColor(ColorScheme.RuleColor)
            .Build();

        // Raise each button a little off the bar instead of leaving it transparent: a surface
        // one step lighter than the window reads as a key cap, which is what separates a row of
        // buttons from a row of words. Derived with PaletteColors.Tint rather than hardcoded, so
        // it tracks the theme - on a light theme Tint would wash out, hence Shade there.
        var surface = _ctx.WindowSystem.Theme.WindowBackgroundColor;
        var face = surface.IsDark() ? surface.Tint(0.10) : surface.Shade(0.06);
        var faceFocused = surface.IsDark() ? surface.Tint(0.22) : surface.Shade(0.14);
        var faceText = PaletteColors.ReadableOn(face);

        void Btn(string glyph, string label, string? key, Action action) =>
            toolbar.AddItem(Controls.Button()
                .WithText(key == null
                    ? $"{glyph} {label}"
                    : $"{glyph} {label} [{ColorScheme.MutedMarkup}]{key}[/]")
                .WithBorder(ButtonBorderStyle.None)
                .WithBackgroundColor(face)
                .WithForegroundColor(faceText)
                .WithBorderBackgroundColor(face)
                .WithFocusedBackgroundColor(faceFocused)
                .WithFocusedForegroundColor(PaletteColors.ReadableOn(faceFocused))
                .OnClick((_, _) => action())
                .Build());

        // Run and build.
        Btn("\u25b8", "Debug", "F5", () => HandleF5?.Invoke());
        Btn("\u2692", "Build", "F6", () => _ = _buildOps.BuildProjectAsync());
        Btn("\u2697", "Test", "F7", () => _ = _buildOps.TestProjectAsync());
        Btn("\u25a0", "Stop", "F4", () => _ctx.BuildService.Cancel());

        toolbar.AddItem(new SeparatorControl());

        // Tools.
        Btn("\u2328", "Shell", "F8", () => _buildOps.OpenShell());
        if (IdeConstants.IsDesktopOs)
        {
            Btn("\u2750", "Shell Tab", null, () => _buildOps.OpenShellTab());
            Btn("\u229e", "LazyNuGet", "F9", () => _buildOps.OpenLazyNuGetTab());
        }

        toolbar.AddItem(new SeparatorControl());

        // Panel toggles. A filled glyph means the panel is showing, so the toolbar reports state
        // rather than only offering an action; AddToolbar is re-run on every toggle to refresh it.
        Btn(_layout.ExplorerVisible ? "\u25e7" : "\u25a1", "Explorer", "^B", () => _layout.ToggleExplorer());
        Btn(_layout.OutputVisible ? "\u25e9" : "\u25a1", "Output", "^J", () => _layout.ToggleOutput());
        Btn(_layout.SidePanelVisible ? "\u25e8" : "\u25a1", "Side Panel", null, () => _layout.ToggleSidePanel());

        _toolbarControl = toolbar;
        _ctx.MainGrid.Place(toolbar, 1, 0, colSpan: 3);
    }
}
