using System.Collections.Concurrent;
using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Spectre.Console;

namespace DotNetIDE;

internal class LspCoordinator : IAsyncDisposable
{
    private readonly EditorManager _editorManager;
    private readonly SidePanel _sidePanel;
    private readonly ConcurrentQueue<Action> _pendingUiActions;

    private LspClient? _lsp;
    private Window? _mainWindow;

    // Portal overlays
    private LspCompletionPortalContent? _completionPortal;
    private LayoutNode? _completionPortalNode;
    private LspTooltipPortalContent? _tooltipPortal;
    private LayoutNode? _tooltipPortalNode;
    private LspLocationListPortalContent? _locationPortal;
    private LayoutNode? _locationPortalNode;

    // Command palette portal
    private CommandPalettePortal? _commandPalettePortal;
    private LayoutNode? _commandPalettePortalNode;

    // Completion filter tracking
    private int _completionTriggerColumn;
    private int _completionTriggerLine;

    // Debounce timers
    private Timer? _dotTriggerDebounce;
    private Timer? _tooltipAutoDismiss;
    private int _tooltipAutoDismissGeneration;
    private Timer? _symbolRefreshDebounce;

    // Navigation history
    private readonly Stack<(string FilePath, int Line, int Col)> _navHistory = new();

    // Dashboard LSP state
    private string? _detectedLspExe;
    private bool _lspStarted;
    private bool _lspDetectionDone;

    // Events
    public event EventHandler<List<BuildDiagnostic>>? DiagnosticsUpdated;
    public Action? LspInitCompleted;

    // Public accessors
    public bool HasLsp => _lsp != null;
    public bool LspStarted => _lspStarted;
    public bool LspDetectionDone => _lspDetectionDone;
    public string? DetectedLspExe => _detectedLspExe;

    public LspCoordinator(
        EditorManager editorManager,
        SidePanel sidePanel,
        ConcurrentQueue<Action> pendingUiActions)
    {
        _editorManager = editorManager;
        _sidePanel = sidePanel;
        _pendingUiActions = pendingUiActions;
    }

    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public async Task InitLspAsync(string projectPath, LspConfig? lspConfig, ConsoleWindowSystem ws)
    {
        var lspServer = LspDetector.Find(projectPath, lspConfig);
        if (lspServer != null)
        {
            _detectedLspExe = lspServer.Exe;
            _lsp = new LspClient();
            bool started = await _lsp.StartAsync(lspServer, projectPath);
            if (started)
            {
                _lspStarted = true;
                _lsp.DiagnosticsReceived += OnLspDiagnostics;
                ws.LogService.LogInfo("LSP server started: " + lspServer.Exe);

                foreach (var (filePath, content) in _editorManager.GetOpenDocuments())
                    await _lsp.DidOpenAsync(filePath, content);

                RefreshSymbolsForFile(_editorManager.CurrentFilePath);
            }
            else
            {
                await _lsp.DisposeAsync();
                _lsp = null;
                ws.LogService.LogInfo("LSP server unavailable — running without IntelliSense");
            }
        }

        _lspDetectionDone = true;
        LspInitCompleted?.Invoke();
    }

    public async Task ReinitLspAsync(string projectPath, LspConfig? lspConfig)
    {
        if (_lsp != null)
        {
            await _lsp.ShutdownAsync();
            _lsp = null;
        }
        var lspServer = LspDetector.Find(projectPath, lspConfig);
        if (lspServer != null)
        {
            _lsp = new LspClient();
            _lsp.DiagnosticsReceived += OnLspDiagnostics;
            await _lsp.StartAsync(lspServer, projectPath);
        }
    }

    // LSP document lifecycle
    public Task DidOpenAsync(string filePath, string content) =>
        _lsp?.DidOpenAsync(filePath, content) ?? Task.CompletedTask;
    public Task DidChangeAsync(string filePath, string content) =>
        _lsp?.DidChangeAsync(filePath, content) ?? Task.CompletedTask;
    public Task DidSaveAsync(string filePath) =>
        _lsp?.DidSaveAsync(filePath) ?? Task.CompletedTask;
    public Task DidCloseAsync(string filePath) =>
        _lsp?.DidCloseAsync(filePath) ?? Task.CompletedTask;

    private void OnLspDiagnostics(object? sender, (string Uri, List<LspDiagnostic> Diags) args)
    {
        var mapped = args.Diags.Select(d => new BuildDiagnostic(
            FilePath: LspClient.UriToPath(args.Uri),
            Line: d.Range.Start.Line + 1,
            Column: d.Range.Start.Character + 1,
            Code: d.Code ?? "",
            Severity: d.Severity == 1 ? "error" : "warning",
            Message: d.Message)).ToList();

        DiagnosticsUpdated?.Invoke(this, mapped);
    }

    // ── Hover ──────────────────────────────────────────────────────────

    public async Task ShowHoverAsync()
    {
        if (_lsp == null || _editorManager.CurrentEditor == null)
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

    // ── Completion ──────────────────────────────────────────────────────

    public async Task ShowCompletionAsync(bool silent = false)
    {
        if (_lsp == null || _editorManager.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        int requestLine = editor.CurrentLine;
        int requestCol = editor.CurrentColumn;

        var items = await _lsp.CompletionAsync(path, requestLine - 1, requestCol - 1);
        if (items.Count == 0)
        {
            if (!silent) ShowTransientTooltip("No completions at cursor.");
            return;
        }

        if (editor.CurrentLine != requestLine) return;

        DismissCompletionPortal();

        var lineContent = editor.Content.Split('\n');
        int lineIdx = requestLine - 1;
        int cursorCol0 = editor.CurrentColumn - 1;
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

        _completionTriggerColumn = wordStart0 + 1;
        _completionTriggerLine = editor.CurrentLine;

        var screenCol = Math.Max(0, editor.ActualX + editor.GutterWidth + (wordStart0 - editor.HorizontalScrollOffset));
        var screenRow = editor.ActualY + Math.Max(0, editor.CurrentLine - 1 - editor.VerticalScrollOffset);

        var portal = new LspCompletionPortalContent(
            items, screenCol, screenRow,
            _mainWindow.Width, _mainWindow.Height);

        if (initialFilter.Length > 0)
            portal.SetFilter(initialFilter);

        portal.Container = _mainWindow;
        _completionPortal = portal;
        _completionPortalNode = _mainWindow.CreatePortal(editor, portal);

        portal.ItemAccepted += (_, item) =>
        {
            int filterLen = _completionPortal?.FilterText.Length ?? 0;
            DismissCompletionPortal();
            if (filterLen > 0) editor.DeleteCharsBefore(filterLen);
            editor.InsertText(item.InsertText ?? item.Label);
            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = null;
        };

        portal.DismissRequested += (_, _) => DismissCompletionPortal();

        editor.ContentChanged += OnEditorContentChangedForCompletion;
    }

    public Task FlushPendingChangeAsync() =>
        _lsp?.FlushPendingChangeAsync() ?? Task.CompletedTask;

    // ── Navigation ──────────────────────────────────────────────────────

    public async Task ShowGoToDefinitionAsync()
    {
        if (_lsp == null || _editorManager.CurrentEditor == null) return;
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

    public void NavigateBack()
    {
        if (_navHistory.Count == 0) return;
        var (prevPath, prevLine, prevCol) = _navHistory.Pop();
        _editorManager.OpenFile(prevPath);
        var editor = _editorManager.CurrentEditor;
        if (editor != null)
        {
            editor.GoToLine(prevLine);
            editor.SetLogicalCursorPosition(new Point(prevCol - 1, prevLine - 1));
        }
    }

    public async Task ShowFindReferencesAsync()
    {
        if (_lsp == null || _editorManager.CurrentEditor == null) return;
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

    public async Task ShowGoToImplementationAsync()
    {
        if (_lsp == null || _editorManager.CurrentEditor == null) return;
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

    // ── Signature Help ──────────────────────────────────────────────────

    public async Task ShowSignatureHelpAsync(bool silent = false)
    {
        if (_lsp == null || _editorManager.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var sig = await _lsp.SignatureHelpAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        if (sig == null || sig.Signatures.Count == 0)
        {
            if (!silent) ShowTransientTooltip("No signature at cursor. Position inside function arguments.");
            return;
        }

        var activeSig = sig.Signatures[Math.Min(sig.ActiveSignature, sig.Signatures.Count - 1)];
        string sigLabel = activeSig.Label;

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

    // ── Rename ──────────────────────────────────────────────────────────

    public async Task ShowRenameAsync(ConsoleWindowSystem ws)
    {
        try
        {
            if (_lsp == null || _editorManager.CurrentEditor == null)
            {
                ShowTransientTooltip("LSP not running.");
                return;
            }
            var editor = _editorManager.CurrentEditor;
            var path = _editorManager.CurrentFilePath;
            if (path == null) return;

            string currentName = ExtractWordAtCursor(editor);
            if (string.IsNullOrEmpty(currentName))
            {
                ShowTransientTooltip("No symbol at cursor.");
                return;
            }

            var newName = await RenameDialog.ShowAsync(ws, currentName);
            if (newName == null) return;

            var workspaceEdit = await _lsp.RenameAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1, newName);
            if (workspaceEdit?.Changes == null || workspaceEdit.Changes.Count == 0)
            {
                ShowTransientTooltip("LSP returned no edits.");
                return;
            }

            ApplyWorkspaceEdit(workspaceEdit);
            ws.NotificationStateService.ShowNotification(
                "Rename", $"Renamed '{currentName}' to '{newName}' in {workspaceEdit.Changes.Count} file(s).",
                SharpConsoleUI.Core.NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            ws.NotificationStateService.ShowNotification(
                "Rename Error", ex.Message, SharpConsoleUI.Core.NotificationSeverity.Danger);
        }
    }

    // ── Code Actions ──────────────────────────────────────────────────

    public async Task ShowCodeActionsAsync(ConsoleWindowSystem ws)
    {
        if (_lsp == null || _editorManager.CurrentEditor == null || _mainWindow == null) return;
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
            var action = actions.FirstOrDefault(a => a.Title == item.Label);
            if (action?.Edit != null)
            {
                ApplyWorkspaceEdit(action.Edit);
                ws.NotificationStateService.ShowNotification(
                    "Code Action", $"Applied: {action.Title}",
                    SharpConsoleUI.Core.NotificationSeverity.Info);
            }
        };
    }

    // ── Format ──────────────────────────────────────────────────────────

    public async Task FormatDocumentAsync()
    {
        if (_lsp == null || _editorManager.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var edits = await _lsp.FormattingAsync(path);
        if (edits.Count == 0) return;

        var lines = editor.Content.Split('\n').ToList();

        var sortedEdits = edits
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character)
            .ToList();

        foreach (var edit in sortedEdits)
        {
            int startLine = Math.Min(edit.Range.Start.Line, lines.Count - 1);
            int startChar = edit.Range.Start.Character;
            int endLine = Math.Min(edit.Range.End.Line, lines.Count - 1);
            int endChar = edit.Range.End.Character;

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

        editor.Content = string.Join('\n', lines);
    }

    // ── Symbols ──────────────────────────────────────────────────────────

    public void RefreshSymbolsForFile(string? filePath)
    {
        if (filePath == null)
        {
            _sidePanel.ClearSymbols();
            return;
        }
        if (_lsp == null || !_sidePanel.TabControl.Visible)
            return;
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _sidePanel.ClearSymbols();
            return;
        }
        _ = RefreshSymbolsAsync(filePath);
    }

    public void ScheduleSymbolRefresh(string filePath, bool sidePanelVisible)
    {
        if (!sidePanelVisible) return;
        _symbolRefreshDebounce?.Dispose();
        _symbolRefreshDebounce = new Timer(_ =>
        {
            _pendingUiActions.Enqueue(() => RefreshSymbolsForFile(filePath));
        }, null, 500, Timeout.Infinite);
    }

    private async Task RefreshSymbolsAsync(string filePath)
    {
        if (_lsp == null) return;
        try
        {
            var symbols = await _lsp.DocumentSymbolAsync(filePath);
            _sidePanel.UpdateSymbols(filePath, symbols);
        }
        catch
        {
            _sidePanel.ClearSymbols();
        }
    }

    public async Task ShowDocumentSymbolsAsync(string? currentFilePath)
    {
        if (_lsp == null || _editorManager.CurrentEditor == null) return;
        var path = currentFilePath ?? _editorManager.CurrentFilePath;
        if (path == null) return;

        var symbols = await _lsp.DocumentSymbolAsync(path);
        if (symbols.Count == 0)
        {
            ShowTransientTooltip("No symbols found in document.");
            return;
        }

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

        var tempRegistry = new CommandRegistry();
        foreach (var (display, sym, depth) in flat)
        {
            var indent = new string(' ', depth * 2);
            var kindName = GetSymbolKindName(sym.Kind);
            var s = sym;
            tempRegistry.Register(new IdeCommand
            {
                Id = $"sym.{sym.SelectionRange.Start.Line}.{sym.Name}",
                Category = kindName,
                Label = $"{indent}{sym.Name}",
                Keybinding = $"Ln {sym.SelectionRange.Start.Line + 1}",
                Execute = () => NavigateToLocation(new LspLocationEntry(
                    path!, s.SelectionRange.Start.Line + 1,
                    s.SelectionRange.Start.Character + 1, s.Name)),
                Priority = 100 - sym.SelectionRange.Start.Line
            });
        }

        ShowCommandPalettePortal(tempRegistry);
    }

    // ── Dot trigger / auto-completion ──────────────────────────────────

    public void TryScheduleDotCompletion(string filePath, string content)
    {
        if (_lsp == null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        var editor = _editorManager.CurrentEditor;
        if (editor == null) return;

        int col = editor.CurrentColumn - 1;
        var lines = content.Split('\n');
        int lineIdx = editor.CurrentLine - 1;
        if (lineIdx < 0 || lineIdx >= lines.Length) return;

        string currentLine = lines[lineIdx];
        if (col <= 0 || col > currentLine.Length) return;

        char lastChar = currentLine[col - 1];

        if (lastChar == '.')
        {
            _ = _lsp.FlushPendingChangeAsync();
            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = new Timer(
                _ => _ = ShowCompletionAsync(silent: true),
                null, 350, Timeout.Infinite);
        }
        else if (lastChar is '(' or ',')
        {
            _ = _lsp.FlushPendingChangeAsync();
            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = new Timer(
                _ => _ = ShowSignatureHelpAsync(silent: true),
                null, 250, Timeout.Infinite);
        }
        else if (IsIdentifierChar(lastChar) && _completionPortal == null)
        {
            int wordLen = 0;
            int i = col - 1;
            while (i >= 0 && IsIdentifierChar(currentLine[i])) { wordLen++; i--; }

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

    // ── Portal dismiss/show helpers ──────────────────────────────────────

    public void DismissCompletionPortal()
    {
        var editor = _editorManager.CurrentEditor;
        if (editor != null)
            editor.ContentChanged -= OnEditorContentChangedForCompletion;

        if (_completionPortalNode != null && _mainWindow != null)
        {
            _mainWindow.RemovePortal(editor ?? (IWindowControl)_mainWindow, _completionPortalNode);
            _completionPortalNode = null;
            _completionPortal = null;
        }
    }

    public void DismissTooltipPortal()
    {
        _tooltipAutoDismiss?.Dispose();
        _tooltipAutoDismiss = null;

        if (_tooltipPortalNode != null && _mainWindow != null)
        {
            _mainWindow.RemovePortal(_editorManager.CurrentEditor ?? (IWindowControl)_mainWindow, _tooltipPortalNode);
            _tooltipPortalNode = null;
            _tooltipPortal = null;
        }
    }

    public void DismissLocationPortal()
    {
        if (_locationPortalNode != null && _mainWindow != null)
        {
            var editor = _editorManager.CurrentEditor;
            _mainWindow.RemovePortal(editor ?? (IWindowControl)_mainWindow, _locationPortalNode);
            _locationPortalNode = null;
            _locationPortal = null;
        }
    }

    public void DismissCommandPalette()
    {
        if (_commandPalettePortalNode == null || _mainWindow == null) return;

        _mainWindow.RemovePortal(_editorManager.TabControl, _commandPalettePortalNode);
        _commandPalettePortalNode = null;
        _commandPalettePortal = null;
    }

    public void ShowCommandPalettePortal(CommandRegistry registry)
    {
        if (_mainWindow == null) return;

        if (_commandPalettePortal != null)
        {
            DismissCommandPalette();
            return;
        }

        var portal = new CommandPalettePortal(registry,
            _mainWindow.Width, _mainWindow.Height);
        portal.Container = _mainWindow;
        _commandPalettePortal = portal;
        _commandPalettePortalNode = _mainWindow.CreatePortal(_editorManager.TabControl, portal);

        portal.CommandSelected += (_, cmd) =>
        {
            DismissCommandPalette();
            if (cmd != null) cmd.Execute();
            var editor = _editorManager.CurrentEditor;
            if (editor != null) _mainWindow?.FocusControl(editor);
        };

        portal.DismissRequested += (_, _) => DismissCommandPalette();
    }

    private void ShowTooltipPortal(List<string> lines, bool preferAbove = true)
    {
        DismissTooltipPortal();
        ++_tooltipAutoDismissGeneration;
        var editor = _editorManager.CurrentEditor;
        if (editor == null || _mainWindow == null) return;
        var cursor = _editorManager.GetCursorBounds();
        var portal = new LspTooltipPortalContent(lines, cursor.X, cursor.Y,
            _mainWindow.Width, _mainWindow.Height, preferAbove);
        portal.Container = _mainWindow;
        portal.Clicked += (_, _) => DismissTooltipPortal();
        portal.DismissRequested += (_, _) => DismissTooltipPortal();
        _tooltipPortal = portal;
        _tooltipPortalNode = _mainWindow.CreatePortal(editor, portal);
    }

    public void ShowTransientTooltip(string message, int dismissMs = 2000)
    {
        _tooltipAutoDismiss?.Dispose();
        _tooltipAutoDismiss = null;

        ShowTooltipPortal(new List<string> { Markup.Escape(message) });

        int gen = ++_tooltipAutoDismissGeneration;
        _tooltipAutoDismiss = new Timer(_ =>
        {
            _pendingUiActions.Enqueue(() =>
            {
                if (_tooltipAutoDismissGeneration == gen)
                    DismissTooltipPortal();
            });
        }, null, dismissMs, Timeout.Infinite);
    }

    private void ShowLocationPortal(List<LspLocationEntry> entries, Action<LspLocationEntry> onAccepted)
    {
        DismissLocationPortal();
        var editor = _editorManager.CurrentEditor;
        if (editor == null || _mainWindow == null) return;
        var cursor = _editorManager.GetCursorBounds();
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

    // ── Preview key processing (portal navigation) ──────────────────────

    /// <summary>
    /// Process a preview key for LSP portals. Returns true if the key was handled.
    /// </summary>
    public bool ProcessPreviewKey(KeyPressedEventArgs e)
    {
        var key = e.KeyInfo.Key;
        var mods = e.KeyInfo.Modifiers;

        // Dismiss tooltip on typing keys
        if (_tooltipPortal != null)
        {
            bool isModifierOnly = key is ConsoleKey.LeftWindows or ConsoleKey.RightWindows;
            bool isArrowKey = key is ConsoleKey.UpArrow or ConsoleKey.DownArrow
                                  or ConsoleKey.LeftArrow or ConsoleKey.RightArrow;
            bool isCtrlCombo = (mods & ConsoleModifiers.Control) != 0 && key != ConsoleKey.Escape;
            if (!isModifierOnly && !isArrowKey && !isCtrlCombo)
                DismissTooltipPortal();
        }

        // Command palette portal
        if (_commandPalettePortal != null)
        {
            if (_commandPalettePortal.ProcessKey(e.KeyInfo))
            {
                e.Handled = true;
                return true;
            }
        }

        // Escape: dismiss portals
        if (key == ConsoleKey.Escape && mods == 0)
        {
            if (_locationPortal != null)
            {
                DismissLocationPortal();
                e.Handled = true;
                return true;
            }
            if (_completionPortal != null)
                DismissCompletionPortal();
            e.Handled = true;
            return true;
        }

        // Location list portal navigation
        if (_locationPortal != null)
        {
            if (mods == 0)
            {
                if (key == ConsoleKey.UpArrow)
                {
                    _locationPortal.SelectPrev();
                    _mainWindow?.Invalidate(false);
                    e.Handled = true;
                    return true;
                }
                if (key == ConsoleKey.DownArrow)
                {
                    _locationPortal.SelectNext();
                    _mainWindow?.Invalidate(false);
                    e.Handled = true;
                    return true;
                }
                if (key == ConsoleKey.Enter)
                {
                    var selected = _locationPortal.GetSelected();
                    DismissLocationPortal();
                    if (selected != null)
                        NavigateToLocation(selected);
                    e.Handled = true;
                    return true;
                }
            }
            char lch = e.KeyInfo.KeyChar;
            if (lch != '\0' && !char.IsControl(lch))
            {
                DismissLocationPortal();
            }
        }

        // Completion portal navigation
        if (_completionPortal == null) return false;

        if (mods == 0)
        {
            if (key == ConsoleKey.UpArrow)
            {
                _completionPortal.SelectPrev();
                _mainWindow?.Invalidate(false);
                e.Handled = true;
                return true;
            }
            if (key == ConsoleKey.DownArrow)
            {
                _completionPortal.SelectNext();
                _mainWindow?.Invalidate(false);
                e.Handled = true;
                return true;
            }
            if (key == ConsoleKey.Enter || key == ConsoleKey.Tab)
            {
                var accepted = _completionPortal.GetSelected();
                int filterLen = _completionPortal.FilterText.Length;
                DismissCompletionPortal();
                if (accepted != null)
                {
                    var editor = _editorManager.CurrentEditor;
                    if (editor != null)
                    {
                        if (filterLen > 0)
                            editor.DeleteCharsBefore(filterLen);
                        editor.InsertText(accepted.InsertText ?? accepted.Label);
                        _dotTriggerDebounce?.Dispose();
                        _dotTriggerDebounce = null;
                    }
                }
                e.Handled = true;
                return true;
            }
            if (key == ConsoleKey.Escape)
            {
                DismissCompletionPortal();
                e.Handled = true;
                return true;
            }

            char ch = e.KeyInfo.KeyChar;
            bool isTypingKey = (ch != '\0' && !char.IsControl(ch)) || key == ConsoleKey.Backspace;
            if (isTypingKey)
                return false; // let editor handle, filter updates via ContentChanged
        }

        bool isCompletionShortcut =
            (key == ConsoleKey.Spacebar && mods == ConsoleModifiers.Control) ||
            key == ConsoleKey.F12;
        if (!isCompletionShortcut)
            DismissCompletionPortal();

        return false;
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    public void NavigateToLocation(LspLocationEntry entry)
    {
        var editor = _editorManager.CurrentEditor;
        var currentPath = _editorManager.CurrentFilePath;
        if (currentPath != null && editor != null)
            _navHistory.Push((currentPath, editor.CurrentLine, editor.CurrentColumn));

        _editorManager.OpenFile(entry.FilePath);
        var targetEditor = _editorManager.CurrentEditor;
        if (targetEditor != null)
        {
            targetEditor.GoToLine(entry.Line);
            targetEditor.SetLogicalCursorPosition(
                new Point(entry.Column - 1, entry.Line - 1));
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

    private void OnEditorContentChangedForCompletion(object? sender, string content)
    {
        var editor = _editorManager.CurrentEditor;
        if (editor == null || _completionPortal == null) return;

        if (editor.CurrentLine != _completionTriggerLine)
        {
            DismissCompletionPortal();
            return;
        }

        int filterLen = editor.CurrentColumn - _completionTriggerColumn;
        if (filterLen < 0)
        {
            DismissCompletionPortal();
            return;
        }

        string filterText = string.Empty;
        if (filterLen > 0)
        {
            var lines = content.Split('\n');
            int lineIdx = editor.CurrentLine - 1;
            if (lineIdx >= 0 && lineIdx < lines.Length)
            {
                var line = lines[lineIdx];
                int start = _completionTriggerColumn - 1;
                int len = Math.Min(filterLen, line.Length - start);
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

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

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

    private void ApplyWorkspaceEdit(WorkspaceEdit edit)
    {
        if (edit.Changes == null) return;

        foreach (var (uri, textEdits) in edit.Changes)
        {
            var filePath = LspClient.UriToPath(uri);

            var openEditor = GetEditorForFile(filePath);
            if (openEditor != null)
            {
                ApplyTextEdits(openEditor, textEdits);
            }
            else
            {
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
        foreach (var (fp, content) in _editorManager.GetOpenDocuments())
        {
            if (string.Equals(fp, filePath, StringComparison.OrdinalIgnoreCase))
            {
                _editorManager.OpenFile(filePath);
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

    public static string GetSymbolKindName(int kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package",
        5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field",
        9 => "Constructor", 10 => "Enum", 11 => "Interface", 12 => "Function",
        13 => "Variable", 14 => "Constant", 15 => "String", 16 => "Number",
        17 => "Boolean", 18 => "Array", 19 => "Object", 22 => "Struct",
        23 => "Event", 24 => "Operator", 25 => "TypeParam",
        _ => "Symbol"
    };

    public async ValueTask DisposeAsync()
    {
        _dotTriggerDebounce?.Dispose();
        _symbolRefreshDebounce?.Dispose();
        _tooltipAutoDismiss?.Dispose();
        DismissCommandPalette();
        DismissCompletionPortal();
        DismissTooltipPortal();
        DismissLocationPortal();
        if (_lsp != null)
            await _lsp.DisposeAsync();
    }
}
