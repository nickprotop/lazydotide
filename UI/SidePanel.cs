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

internal class SidePanel
{
    private readonly TabControl _tabControl;
    private readonly TreeControl _symbolsTree;
    private readonly ScrollablePanelControl _symbolsPanel;
    private TerminalControl? _shellTerminal;
    private int _shellTabIndex = -1;

    public TabControl TabControl => _tabControl;
    public TerminalControl? ShellTerminal => _shellTerminal;
    public event EventHandler<LspLocationEntry>? SymbolActivated;

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

        _tabControl = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            HeaderStyle = TabHeaderStyle.Separator
        };
        _tabControl.AddTab("Symbols", _symbolsPanel);
    }

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
        _tabControl.AddTab("Shell", _shellTerminal);
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
