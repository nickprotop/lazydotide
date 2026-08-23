using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Logging;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class OutputPanel
{
    private readonly ConsoleWindowSystem _ws;
    private readonly TabControl _tabControl;
    private readonly ListControl _problemsList;
    private readonly ListControl _outputList;
    private readonly LogService _appLog;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action>? _uiActions;
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

    public OutputPanel(ConsoleWindowSystem ws, System.Collections.Concurrent.ConcurrentQueue<Action>? uiActions = null)
    {
        _ws = ws;
        _uiActions = uiActions;




        _problemsList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _problemsList.ItemActivated += OnProblemActivated;

        // A dedicated log service, NOT ws.LogService: the window system's logger
        // carries SharpConsoleUI's own framework diagnostics (renderer, layout and
        // input chatter), which would bury this application's few status messages.
        // LogService defaults to MinimumLevel = Warning, which would silently drop
        // every status message this panel exists to show.
        _appLog = new LogService { MinimumLevel = LogLevel.Information };

        _outputList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        _appLog.LogAdded += OnAppLogAdded;
        _appLog.LogsCleared += OnAppLogsCleared;

        // Deliberately NOT wired to DiagnosticLog: that is a verbose protocol trace
        // (~550 lines a session for LSP alone) and belongs in its files. This tab is
        // for the handful of status messages a user should actually see.

        _tabControl = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            HeaderStyle = TabHeaderStyle.Separator
        };
        _tabControl.AddTab("Output", _outputList);
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

    // ──────────────────────────────────────────────────────────────
    // Unified output stream
    //
    // Build, test and git all append here rather than owning a tab each: the
    // bottom panel is only a dozen rows, and one chronological feed reads better
    // than five near-empty views. Nothing clears on a new run — a run appends a
    // header and its lines below what came before, so earlier context survives.
    // ──────────────────────────────────────────────────────────────

    /// <summary>Appends a pre-formatted markup line to the output stream.</summary>
    private void AppendMarkup(string markup) =>
        RunOnUi(() =>
        {
            _outputList.AddItem(markup);
            _outputList.SelectedIndex = _outputList.Items.Count - 1;
        });

    /// <summary>Writes a section header, so runs stay visually separated.</summary>
    public void AppendHeader(string title) =>
        AppendMarkup($"[bold cyan1]── {MarkupParser.Escape(title)} ──[/]");

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

        AppendMarkup(markup);
    }

    public void AppendTestLine(string line)
    {
        string markup;
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            markup = $"[red]{MarkupParser.Escape(line)}[/]";
        else if (line.Contains("passed", StringComparison.OrdinalIgnoreCase))
            markup = $"[green]{MarkupParser.Escape(line)}[/]";
        else
            markup = $"[grey]{MarkupParser.Escape(line)}[/]";

        AppendMarkup(markup);
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

        AppendMarkup(markup);
    }

    // The stream is append-only: a new run must not erase earlier context, so
    // these mark the start of a run instead of wiping the panel.
    public void ClearBuildOutput() => AppendHeader("Build");
    public void ClearTestOutput() => AppendHeader("Test");
    public void ClearGitOutput() { }

    /// <summary>The application's log service; messages written here appear in the Output tab.</summary>
    public ILogService AppLog => _appLog;

    /// <summary>Writes a line to the Output tab.</summary>
    public void AppendOutputLine(string line) => _appLog.LogInfo(line);

    public void ClearOutput() => _appLog.ClearLogs();

    private void OnAppLogAdded(object? sender, LogEntry entry)
    {
        // Log calls arrive from background work (LSP, DAP, git), so touch the
        // control on the UI loop rather than the calling thread.
        RunOnUi(() =>
        {
            // ToMarkup() already colours by level; ListControl parses markup.
            _outputList.AddItem(entry.ToMarkup());

            // Keep the newest entry in view. LogService bounds its own buffer, so
            // the list length follows it rather than being trimmed here.
            _outputList.SelectedIndex = _outputList.Items.Count - 1;
        });
    }

    private void OnAppLogsCleared(object? sender, EventArgs e) => RunOnUi(_outputList.ClearItems);

    private void RunOnUi(Action action)
    {
        if (_uiActions != null) _uiActions.Enqueue(action);
        else action();
    }

    // Build/test/git all stream into the single Output tab now.
    public void SwitchToOutputTab() => _tabControl.ActiveTabIndex = 0;
    public void SwitchToBuildTab() => SwitchToOutputTab();
    public void SwitchToTestTab() => SwitchToOutputTab();
    public void SwitchToGitTab() => SwitchToOutputTab();
    public void SwitchToProblemsTab() => _tabControl.ActiveTabIndex = 1;

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
        AppendHeader("Markdown Warnings");
        foreach (var w in warnings)
            AppendMarkup($"[yellow]▲ {MarkupParser.Escape(w)}[/]");
        SwitchToOutputTab();
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
