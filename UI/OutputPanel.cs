using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class OutputPanel
{
    private readonly ConsoleWindowSystem _ws;
    private readonly TabControl _tabControl;
    private readonly ScrollablePanelControl _buildPanel;
    private readonly ScrollablePanelControl _testPanel;
    private readonly ScrollablePanelControl _gitPanel;
    private readonly ListControl _problemsList;
    private readonly LogViewerControl _logViewer;
    private TerminalControl? _shellTerminal;
    private int _shellTabIndex = -1;

    // Search tab
    private readonly PromptControl _searchInput;
    private readonly CheckboxControl _caseSensitiveBox;
    private readonly MarkupControl _searchStatus;
    private readonly ListControl _searchResults;
    private int _searchTabIndex;
    private System.Threading.Timer? _searchDebounceTimer;

    public event EventHandler<BuildDiagnostic>? DiagnosticNavigateRequested;
    public event EventHandler<SearchResult>? SearchNavigateRequested;
    public event EventHandler<(string Term, bool CaseSensitive)>? SearchRequested;

    public TabControl TabControl => _tabControl;

    public OutputPanel(ConsoleWindowSystem ws)
    {
        _ws = ws;

        _buildPanel = new ScrollablePanelControl
        {
            AutoScroll = true,
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        _testPanel = new ScrollablePanelControl
        {
            AutoScroll = true,
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        _gitPanel = new ScrollablePanelControl
        {
            AutoScroll = true,
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        _problemsList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _problemsList.ItemActivated += OnProblemActivated;

        _logViewer = new LogViewerControl(ws.LogService)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        _tabControl = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            HeaderStyle = TabHeaderStyle.Separator
        };
        _tabControl.AddTab("Output", _logViewer);
        _tabControl.AddTab("Build", _buildPanel);
        _tabControl.AddTab("Test", _testPanel);
        _tabControl.AddTab("Git Output", _gitPanel);
        _tabControl.AddTab("Problems", _problemsList);

        // Search tab — toolbar + results list inside a scrollable panel
        _searchInput = new PromptControl { Prompt = "Search: ", InputWidth = 30 };
        _caseSensitiveBox = new CheckboxControl { Label = "Aa", Checked = false };
        _searchStatus = new MarkupControl(new List<string> { "[dim]Ready[/]" });
        _searchResults = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _searchResults.ItemActivated += OnSearchResultActivated;
        _searchInput.InputChanged += OnSearchInputChanged;

        var searchToolbar = ToolbarControl.Create()
            .Add(_searchInput)
            .Add(_caseSensitiveBox)
            .Add(_searchStatus)
            .WithSpacing(1)
            .Build();

        var searchGrid = new HorizontalGridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        var searchCol = new ColumnContainer(searchGrid)
        {
            VerticalAlignment = VerticalAlignment.Fill
        };
        searchCol.AddContent(searchToolbar);
        searchCol.AddContent(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).Build());
        searchCol.AddContent(_searchResults);
        searchGrid.AddColumn(searchCol);

        _tabControl.AddTab("Search", searchGrid);
        _searchTabIndex = _tabControl.TabCount - 1;
    }

    public void AppendBuildLine(string line)
    {
        string markup;
        if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            markup = $"[red]{MarkupParser.Escape(line)}[/]";
        else if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
            markup = $"[yellow]{MarkupParser.Escape(line)}[/]";
        else if (line.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase))
            markup = $"[green]{MarkupParser.Escape(line)}[/]";
        else if (line.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase))
            markup = $"[bold red]{MarkupParser.Escape(line)}[/]";
        else
            markup = $"[grey]{MarkupParser.Escape(line)}[/]";

        _buildPanel.AddControl(new MarkupControl(new List<string> { markup }));
        _buildPanel.ScrollToBottom();
    }

    public void AppendTestLine(string line)
    {
        string markup;
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            markup = $"[red]{MarkupParser.Escape(line)}[/]";
        else if (line.Contains("passed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("PASSED", StringComparison.OrdinalIgnoreCase))
            markup = $"[green]{MarkupParser.Escape(line)}[/]";
        else
            markup = $"[grey]{MarkupParser.Escape(line)}[/]";

        _testPanel.AddControl(new MarkupControl(new List<string> { markup }));
        _testPanel.ScrollToBottom();
    }

    public void ClearBuildOutput()
    {
        _buildPanel.ClearContents();
    }

    public void ClearTestOutput()
    {
        _testPanel.ClearContents();
    }

    public void AppendGitLine(string line)
    {
        string markup;
        if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("fatal", StringComparison.OrdinalIgnoreCase))
            markup = $"[red]{MarkupParser.Escape(line)}[/]";
        else if (line.StartsWith("warning", StringComparison.OrdinalIgnoreCase))
            markup = $"[yellow]{MarkupParser.Escape(line)}[/]";
        else
            markup = $"[grey]{MarkupParser.Escape(line)}[/]";

        _gitPanel.AddControl(new MarkupControl(new List<string> { markup }));
        _gitPanel.ScrollToBottom();
    }

    public void ClearGitOutput()
    {
        _gitPanel.ClearContents();
    }

    public void SwitchToBuildTab() => _tabControl.ActiveTabIndex = 1;
    public void SwitchToTestTab() => _tabControl.ActiveTabIndex = 2;
    public void SwitchToGitTab() => _tabControl.ActiveTabIndex = 3;
    public void SwitchToProblemsTab() => _tabControl.ActiveTabIndex = 4;

    public TerminalControl? ShellTerminal => _shellTerminal;
    public bool IsShellTabActive => _shellTabIndex >= 0 && _tabControl.ActiveTabIndex == _shellTabIndex;

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

    public void PopulateProblems(List<BuildDiagnostic> diagnostics)
    {
        _problemsList.ClearItems();
        foreach (var diag in diagnostics)
        {
            var fileName = Path.GetFileName(diag.FilePath);
            var icon = diag.Severity == "error" ? "[red]E[/]" : "[yellow]W[/]";
            var text = $"{icon} {MarkupParser.Escape(fileName)}({diag.Line},{diag.Column}): {MarkupParser.Escape(diag.Message)}";
            var item = new ListItem(text) { Tag = diag };
            _problemsList.AddItem(item);
        }
    }

    public void PopulateLspDiagnostics(List<BuildDiagnostic> diagnostics)
    {
        PopulateProblems(diagnostics);
    }

    public void ShowWarnings(IReadOnlyList<string> warnings)
    {
        _buildPanel.AddControl(new MarkupControl(new List<string>
            { "[yellow]── Markdown Warnings ──[/]" }));
        foreach (var w in warnings)
            _buildPanel.AddControl(new MarkupControl(new List<string>
                { $"[yellow]▲ {MarkupParser.Escape(w)}[/]" }));
        _buildPanel.ScrollToBottom();
        SwitchToBuildTab();
    }

    private void OnProblemActivated(object? sender, ListItem item)
    {
        if (item.Tag is BuildDiagnostic diag)
            DiagnosticNavigateRequested?.Invoke(this, diag);
    }

    // ── Search tab ──────────────────────────────────────────────

    public void SwitchToSearchTab()
    {
        _tabControl.ActiveTabIndex = _searchTabIndex;
        _searchInput.GetParentWindow()?.FocusManager.SetFocus(_searchInput, SharpConsoleUI.Controls.FocusReason.Keyboard);
    }

    public void ClearSearchResults() => _searchResults.ClearItems();

    public void AddSearchResult(SearchResult result, string rootPath)
    {
        var relPath = Path.GetRelativePath(rootPath, result.FilePath).Replace('\\', '/');
        var lineText = result.LineText.Length > 120
            ? result.LineText[..120] + "…"
            : result.LineText;
        var text = $"[dim]{MarkupParser.Escape(relPath)}:{result.Line}[/]  {MarkupParser.Escape(lineText.TrimStart())}";
        _searchResults.AddItem(new ListItem(text) { Tag = result });
    }

    public void SetSearchStatus(string markup)
    {
        _searchStatus.SetContent(new List<string> { markup });
    }

    private void OnSearchInputChanged(object? sender, string newText)
    {
        // Debounce: reset timer on each keystroke, fire after 400ms
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = new System.Threading.Timer(_ =>
        {
            var term = newText;
            var caseSensitive = _caseSensitiveBox.Checked;
            SearchRequested?.Invoke(this, (term, caseSensitive));
        }, null, 400, Timeout.Infinite);
    }

    private void OnSearchResultActivated(object? sender, ListItem item)
    {
        if (item.Tag is SearchResult result)
            SearchNavigateRequested?.Invoke(this, result);
    }
}
