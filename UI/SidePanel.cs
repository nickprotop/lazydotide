using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;
using TreeNode = SharpConsoleUI.Controls.TreeNode;

namespace DotNetIDE;

internal enum GitContextMenuTarget { StagedFile, UnstagedFile, CommitEntry }

internal class GitContextMenuEventArgs : EventArgs
{
    public string? FilePath { get; init; }
    public bool IsStaged { get; init; }
    public int ScreenX { get; init; }
    public int ScreenY { get; init; }
    public GitLogEntry? LogEntry { get; init; }
    public GitContextMenuTarget Target { get; init; }
}

internal class SidePanel
{
    private readonly TabControl _tabControl;
    private readonly TreeControl _symbolsTree;
    private readonly ScrollablePanelControl _symbolsPanel;
    private TerminalControl? _shellTerminal;
    private int _shellTabIndex = -1;

    // Git tab
    private readonly ScrollablePanelControl _gitPanel;
    private readonly ListControl _stagedList;
    private readonly ListControl _unstagedList;
    private readonly ListControl _logList;
    private readonly ToolbarControl _gitToolbar;
    private int _gitTabIndex;

    public TabControl TabControl => _tabControl;
    public TerminalControl? ShellTerminal => _shellTerminal;
    public event EventHandler<LspLocationEntry>? SymbolActivated;

    // File interactions
    public event EventHandler<string>? GitDiffRequested;

    // Toolbar actions
    public event EventHandler? GitCommitRequested;
    public event EventHandler? GitRefreshRequested;
    public event EventHandler? GitStageAllRequested;
    public event EventHandler? GitUnstageAllRequested;
    public event EventHandler? GitMoreMenuRequested;

    // Context menu
    public event EventHandler<GitContextMenuEventArgs>? GitContextMenuRequested;

    // Log entry interaction
    public event EventHandler<GitLogEntry>? GitLogEntryActivated;

    public SidePanel()
    {
        _symbolsTree = new TreeControl
        {
            Guide = TreeGuide.Line,
            HighlightBackgroundColor = Color.SteelBlue,
            HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _symbolsTree.NodeActivated += OnSymbolActivated;

        _symbolsPanel = new ScrollablePanelControl
        {
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _symbolsPanel.AddControl(_symbolsTree);

        // Git toolbar
        _gitToolbar = ToolbarControl.Create()
            .AddButton("✓ Commit", (_, _) => GitCommitRequested?.Invoke(this, EventArgs.Empty))
            .AddButton("↻", (_, _) => GitRefreshRequested?.Invoke(this, EventArgs.Empty))
            .AddButton("±", (_, _) => GitStageAllRequested?.Invoke(this, EventArgs.Empty))
            .AddButton("∓", (_, _) => GitUnstageAllRequested?.Invoke(this, EventArgs.Empty))
            .AddSeparator(1)
            .AddButton("⋯", (_, _) => GitMoreMenuRequested?.Invoke(this, EventArgs.Empty))
            .WithSpacing(1)
            .WithWrap(true)
            .Build();

        // Git panel — staged files
        // Enter = show diff, double-click = open file for editing
        _stagedList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HoverHighlightsItems = true,
            SelectOnRightClick = true
        };
        _stagedList.ItemActivated += (_, item) =>
        {
            if (item?.Tag is string path)
                GitDiffRequested?.Invoke(this, path);
        };
        _stagedList.MouseRightClick += (_, args) =>
        {
            var item = _stagedList.SelectedItem;
            if (item?.Tag is string path)
                GitContextMenuRequested?.Invoke(this, new GitContextMenuEventArgs
                {
                    FilePath = path,
                    IsStaged = true,
                    ScreenX = args.AbsolutePosition.X,
                    ScreenY = args.AbsolutePosition.Y,
                    Target = GitContextMenuTarget.StagedFile
                });
        };

        // Git panel — unstaged files
        // Enter = show diff, double-click = open file for editing
        _unstagedList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HoverHighlightsItems = true,
            SelectOnRightClick = true
        };
        _unstagedList.ItemActivated += (_, item) =>
        {
            if (item?.Tag is string path)
                GitDiffRequested?.Invoke(this, path);
        };
        _unstagedList.MouseRightClick += (_, args) =>
        {
            var item = _unstagedList.SelectedItem;
            if (item?.Tag is string path)
                GitContextMenuRequested?.Invoke(this, new GitContextMenuEventArgs
                {
                    FilePath = path,
                    IsStaged = false,
                    ScreenX = args.AbsolutePosition.X,
                    ScreenY = args.AbsolutePosition.Y,
                    Target = GitContextMenuTarget.UnstagedFile
                });
        };

        // Git panel — recent commits
        _logList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HoverHighlightsItems = true,
            DoubleClickActivates = true,
            SelectOnRightClick = true
        };
        _logList.ItemActivated += (_, item) =>
        {
            if (item?.Tag is GitLogEntry entry)
                GitLogEntryActivated?.Invoke(this, entry);
        };
        _logList.MouseRightClick += (_, args) =>
        {
            var item = _logList.SelectedItem;
            if (item?.Tag is GitLogEntry entry)
                GitContextMenuRequested?.Invoke(this, new GitContextMenuEventArgs
                {
                    LogEntry = entry,
                    ScreenX = args.AbsolutePosition.X,
                    ScreenY = args.AbsolutePosition.Y,
                    Target = GitContextMenuTarget.CommitEntry
                });
        };

        _gitPanel = new ScrollablePanelControl
        {
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        // Build git panel layout
        RebuildGitPanel();

        _tabControl = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            HeaderStyle = TabHeaderStyle.Separator
        };
        _tabControl.AddTab("Symbols", _symbolsPanel);
        _tabControl.AddTab("Git", _gitPanel);
        _gitTabIndex = 1;
    }

    // ──────────────────────────────────────────────────────────────
    // Git Tab
    // ──────────────────────────────────────────────────────────────

    public void SwitchToGitTab()
    {
        _tabControl.ActiveTabIndex = _gitTabIndex;
    }

    /// <summary>
    /// Updates the Git panel with current branch, staged/unstaged files, and recent log.
    /// </summary>
    public void UpdateGitPanel(
        string branch,
        List<(string RelativePath, string AbsolutePath, GitFileStatus Status, bool IsStaged)> files,
        List<GitLogEntry>? recentLog = null)
    {
        _stagedList.ClearItems();
        _unstagedList.ClearItems();
        _logList.ClearItems();

        foreach (var f in files)
        {
            var item = new ListItem($"  {Markup.Escape(f.RelativePath)}")
            {
                Icon = GetStatusChar(f.Status),
                IconColor = GetStatusColor(f.Status),
                Tag = f.AbsolutePath
            };

            if (f.IsStaged)
                _stagedList.AddItem(item);
            else
                _unstagedList.AddItem(item);
        }

        if (recentLog != null)
        {
            foreach (var entry in recentLog.Take(20))
            {
                var label = $"  [grey50]{Markup.Escape(entry.ShortSha)}[/] {Markup.Escape(entry.MessageShort)}";
                _logList.AddItem(new ListItem(label) { Tag = entry });
            }
        }

        RebuildGitPanel(branch);
    }

    private void RebuildGitPanel(string? branch = null)
    {
        // Remove all children without disposing persistent controls
        while (_gitPanel.Children.Count > 0)
            _gitPanel.RemoveControl(_gitPanel.Children[^1]);

        var branchDisplay = string.IsNullOrEmpty(branch) ? "none" : branch;
        _gitPanel.AddControl(new MarkupControl(new List<string>
        {
            $"[cyan1]\u2387 {Markup.Escape(branchDisplay)}[/]"
        }));

        _gitPanel.AddControl(_gitToolbar);

        _gitPanel.AddControl(new RuleControl
        {
            Color = Color.Grey,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

        if (_stagedList.Items.Count > 0)
        {
            _gitPanel.AddControl(new MarkupControl(new List<string>
            {
                "",
                $"[green]\u25B6 Staged Changes ({_stagedList.Items.Count})[/]"
            }));
            _gitPanel.AddControl(_stagedList);
        }

        _gitPanel.AddControl(new MarkupControl(new List<string>
        {
            "",
            $"[yellow]\u25CB Changes ({_unstagedList.Items.Count})[/]"
        }));
        if (_unstagedList.Items.Count > 0)
        {
            _gitPanel.AddControl(_unstagedList);
        }
        else
        {
            _gitPanel.AddControl(new MarkupControl(new List<string>
            {
                "  [dim]No changes[/]"
            }));
        }

        if (_logList.Items.Count > 0)
        {
            _gitPanel.AddControl(new MarkupControl(new List<string>
            {
                "",
                "[grey50]\u2502 Recent Commits[/]"
            }));
            _gitPanel.AddControl(_logList);
        }
    }

    private static string GetStatusChar(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified   => "M",
        GitFileStatus.Added      => "A",
        GitFileStatus.Deleted    => "D",
        GitFileStatus.Renamed    => "R",
        GitFileStatus.Untracked  => "?",
        GitFileStatus.Conflicted => "!",
        _ => " "
    };

    private static Color GetStatusColor(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified   => Color.Yellow,
        GitFileStatus.Added      => Color.Green,
        GitFileStatus.Deleted    => Color.Red,
        GitFileStatus.Renamed    => Color.Blue,
        GitFileStatus.Untracked  => Color.Grey,
        GitFileStatus.Conflicted => Color.Red,
        _ => Color.White
    };

    public void UpdateSymbols(string filePath, List<DocumentSymbol> symbols)
    {
        _symbolsTree.Clear();
        foreach (var sym in symbols)
            AddSymbolNode(filePath, sym, null);
    }

    public void ClearSymbols()
    {
        _symbolsTree.Clear();
    }

    public void SwitchToSymbolsTab()
    {
        _tabControl.ActiveTabIndex = 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public TerminalControl LaunchShell(string? workingDirectory = null)
    {
        // If tab exists but the process exited, replace it
        if (_shellTabIndex >= 0 && (_shellTerminal == null || _shellTerminal.IsDisposed))
        {
            _tabControl.RemoveTab(_shellTabIndex);
            _shellTabIndex = -1;
            _shellTerminal = null;
        }

        if (_shellTabIndex >= 0)
        {
            _tabControl.ActiveTabIndex = _shellTabIndex;
            return _shellTerminal!;
        }

        _shellTerminal = Controls.Terminal()
            .WithWorkingDirectory(workingDirectory)
            .Build();
        _shellTerminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        _shellTerminal.VerticalAlignment = VerticalAlignment.Fill;
        _tabControl.AddTab("Shell", _shellTerminal, isClosable: true);
        _shellTabIndex = _tabControl.TabCount - 1;
        _tabControl.ActiveTabIndex = _shellTabIndex;
        return _shellTerminal;
    }

    public void SwitchToShellTab()
    {
        if (_shellTabIndex >= 0)
            _tabControl.ActiveTabIndex = _shellTabIndex;
    }

    private void AddSymbolNode(string filePath, DocumentSymbol symbol, TreeNode? parent)
    {
        TreeNode node;
        if (parent == null)
            node = _symbolsTree.AddRootNode(symbol.Name);
        else
            node = parent.AddChild(symbol.Name);

        node.Tag = new LspLocationEntry(
            filePath,
            symbol.SelectionRange.Start.Line + 1,
            symbol.SelectionRange.Start.Character + 1,
            symbol.Name);

        node.TextColor = GetSymbolColor(symbol.Kind);
        node.IsExpanded = true;

        if (symbol.Children != null)
        {
            foreach (var child in symbol.Children)
                AddSymbolNode(filePath, child, node);
        }
    }

    private static Color GetSymbolColor(int kind) => kind switch
    {
        5 => Color.LightGreen,   // Class
        11 => Color.LightGreen,  // Interface
        22 => Color.LightGreen,  // Struct
        10 => Color.LightGreen,  // Enum
        6 => Color.Yellow,       // Method
        9 => Color.Yellow,       // Constructor
        12 => Color.Yellow,      // Function
        7 => Color.Cyan1,        // Property
        8 => Color.SteelBlue,    // Field
        13 => Color.SteelBlue,   // Variable
        14 => Color.SteelBlue,   // Constant
        23 => Color.Magenta1,    // Event
        3 => Color.Grey,         // Namespace
        _ => Color.White
    };

    public static string GetSymbolKindName(int kind) => kind switch
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

    private void OnSymbolActivated(object? sender, TreeNodeEventArgs args)
    {
        if (args.Node?.Tag is LspLocationEntry entry)
            SymbolActivated?.Invoke(this, entry);
    }
}
