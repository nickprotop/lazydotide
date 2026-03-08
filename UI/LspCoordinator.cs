using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

/// <summary>
/// Orchestrates LSP integration: manages LspClient lifecycle, wires events between
/// EditorManager and LSP, handles debounce timers, and coordinates didOpen/didChange/didSave.
/// Delegates portal UI to LspPortalManager and navigation to LspNavigationManager.
/// </summary>
internal class LspCoordinator : IAsyncDisposable
{
    private const int SymbolRefreshMs = 500;
    private const int DotTriggerMs = 350;
    private const int SignatureTriggerMs = 250;
    private const int WordCompletionMs = 300;
    private const int SemanticTokenRefreshMs = 800;

    private static void LogError(string context, Exception ex) =>
        DiagnosticLog.Error("lsp-coord", context, ex);


    private readonly AppContext _ctx;

    private LspClient? _lsp;

    // Sub-managers
    private readonly LspPortalManager _portalManager;
    private readonly LspNavigationManager _navManager;

    // Debounce timers
    private Timer? _dotTriggerDebounce;
    private Timer? _symbolRefreshDebounce;
    private Timer? _semanticTokenDebounce;
    private int _busyCount;

    // Semantic token highlighters cache
    private readonly ConcurrentDictionary<string, SemanticHighlighter> _semanticHighlighters = new();

    // Dashboard LSP state
    private string? _detectedLspExe;
    private bool _lspStarted;
    private bool _lspDetectionDone;

    // Events
    public event EventHandler<List<BuildDiagnostic>>? DiagnosticsUpdated;
    public Action? LspInitCompleted;
    public Action<bool>? LspBusyChanged; // true = busy, false = idle

    // Public accessors
    public bool HasLsp => _lsp != null;
    public bool LspStarted => _lspStarted;
    public bool LspDetectionDone => _lspDetectionDone;
    public string? DetectedLspExe => _detectedLspExe;

    public LspCoordinator(AppContext ctx)
    {
        _ctx = ctx;

        _portalManager = new LspPortalManager(ctx.EditorManager, ctx.PendingUiActions);
        _portalManager.SetMainWindow(ctx.MainWindow);
        _navManager = new LspNavigationManager(ctx.EditorManager, ctx.PendingUiActions, _portalManager);

        // Wire cross-manager callbacks
        _portalManager.NavigateToLocation = _navManager.NavigateToLocation;
        _portalManager.OnCompletionAccepted = () =>
        {
            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = null;
        };
    }

    // ── LSP Lifecycle ──────────────────────────────────────────────────

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

                foreach (var (filePath, content) in _ctx.EditorManager.GetOpenDocuments())
                    await _lsp.DidOpenAsync(filePath, content);

                RefreshSymbolsForFile(_ctx.EditorManager.CurrentFilePath);
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
        // Clear stale semantic highlighters from previous LSP session
        _semanticHighlighters.Clear();
        _semanticTokenDebounce?.Dispose();
        _semanticTokenDebounce = null;

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

    // ── LSP Document Lifecycle ──────────────────────────────────────────

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

        _ctx.PendingUiActions.Enqueue(() => DiagnosticsUpdated?.Invoke(this, mapped));
    }

    // ── Hover ──────────────────────────────────────────────────────────

    public async Task ShowHoverAsync()
    {
        if (_lsp == null || _ctx.EditorManager.CurrentEditor == null)
        {
            _portalManager.ShowTransientTooltip("Language server not running.");
            return;
        }
        var editor = _ctx.EditorManager.CurrentEditor;
        var path = _ctx.EditorManager.CurrentFilePath;
        if (path == null) return;

        var result = await _lsp.HoverAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        _ctx.PendingUiActions.Enqueue(() =>
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Contents))
            {
                _portalManager.ShowTransientTooltip("No type info at cursor.");
                return;
            }

            var lines = LspMarkdownHelper.ConvertToSpectreMarkup(result.Contents);
            if (lines.Count == 0) return;

            _portalManager.ShowTooltipPortal(lines);
        });
    }

    // ── Completion ──────────────────────────────────────────────────────

    public async Task ShowCompletionAsync(bool silent = false)
    {
        if (_lsp == null) return;
        await _portalManager.ShowCompletionAsync(_lsp, silent);
    }

    public Task FlushPendingChangeAsync() =>
        _lsp?.FlushPendingChangeAsync() ?? Task.CompletedTask;

    // ── Navigation (delegated) ──────────────────────────────────────────

    public async Task ShowGoToDefinitionAsync()
    {
        if (_lsp == null) return;
        await _navManager.ShowGoToDefinitionAsync(_lsp);
    }

    public void NavigateBack() => _navManager.NavigateBack();

    public async Task ShowFindReferencesAsync()
    {
        if (_lsp == null) return;
        await _navManager.ShowFindReferencesAsync(_lsp);
    }

    public async Task ShowGoToImplementationAsync()
    {
        if (_lsp == null) return;
        await _navManager.ShowGoToImplementationAsync(_lsp);
    }

    // ── Signature Help ──────────────────────────────────────────────────

    public async Task ShowSignatureHelpAsync(bool silent = false)
    {
        if (_lsp == null || _ctx.EditorManager.CurrentEditor == null) return;

        var editor = _ctx.EditorManager.CurrentEditor;
        var path = _ctx.EditorManager.CurrentFilePath;
        if (path == null) return;

        var sig = await _lsp.SignatureHelpAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        _ctx.PendingUiActions.Enqueue(() =>
        {
            if (sig == null || sig.Signatures.Count == 0)
            {
                if (!silent) _portalManager.ShowTransientTooltip("No signature at cursor. Position inside function arguments.");
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
                    ? MarkupParser.Escape(sigLabel[..idx]) + $"[bold yellow]{MarkupParser.Escape(paramLabel)}[/]" + MarkupParser.Escape(sigLabel[(idx + paramLabel.Length)..])
                    : MarkupParser.Escape(sigLabel);
            }
            else
            {
                line1 = MarkupParser.Escape(sigLabel);
            }

            var lines = new List<string> { line1 };
            if (!string.IsNullOrWhiteSpace(activeSig.Documentation))
                lines.AddRange(LspMarkdownHelper.ConvertToSpectreMarkup(activeSig.Documentation!));

            _portalManager.ShowTooltipPortal(lines);
        });
    }

    // ── Rename ──────────────────────────────────────────────────────────

    public async Task ShowRenameAsync(ConsoleWindowSystem ws)
    {
        try
        {
            if (_lsp == null || _ctx.EditorManager.CurrentEditor == null)
            {
                _portalManager.ShowTransientTooltip("LSP not running.");
                return;
            }
            var editor = _ctx.EditorManager.CurrentEditor;
            var path = _ctx.EditorManager.CurrentFilePath;
            if (path == null) return;

            string currentName = ExtractWordAtCursor(editor);
            if (string.IsNullOrEmpty(currentName))
            {
                _portalManager.ShowTransientTooltip("No symbol at cursor.");
                return;
            }

            var newName = await RenameDialog.ShowAsync(ws, currentName);
            if (newName == null) return;

            var workspaceEdit = await _lsp.RenameAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1, newName);
            _ctx.PendingUiActions.Enqueue(() =>
            {
                if (workspaceEdit?.Changes == null || workspaceEdit.Changes.Count == 0)
                {
                    _portalManager.ShowTransientTooltip("LSP returned no edits.");
                    return;
                }

                ApplyWorkspaceEdit(workspaceEdit);
                ws.NotificationStateService.ShowNotification(
                    "Rename", $"Renamed '{currentName}' to '{newName}' in {workspaceEdit.Changes.Count} file(s).",
                    SharpConsoleUI.Core.NotificationSeverity.Info);
            });
        }
        catch (Exception ex)
        {
            _ctx.PendingUiActions.Enqueue(() => ws.NotificationStateService.ShowNotification(
                "Rename Error", ex.Message, SharpConsoleUI.Core.NotificationSeverity.Danger));
        }
    }

    // ── Code Actions ──────────────────────────────────────────────────

    public async Task ShowCodeActionsAsync(ConsoleWindowSystem ws)
    {
        if (_lsp == null || _ctx.EditorManager.CurrentEditor == null) return;
        var editor = _ctx.EditorManager.CurrentEditor;
        var path = _ctx.EditorManager.CurrentFilePath;
        if (path == null) return;

        int line = editor.CurrentLine - 1;
        int col = editor.CurrentColumn - 1;

        var actions = await _lsp.CodeActionAsync(path, line, col, line, col);
        _ctx.PendingUiActions.Enqueue(() =>
        {
            if (actions.Count == 0)
            {
                _portalManager.ShowTransientTooltip("No code actions available at cursor.");
                return;
            }

            _portalManager.ShowCodeActionsPortal(actions, ws, ApplyWorkspaceEdit);
        });
    }

    // ── Format ──────────────────────────────────────────────────────────

    public async Task FormatDocumentAsync()
    {
        if (_lsp == null || _ctx.EditorManager.CurrentEditor == null) return;
        var editor = _ctx.EditorManager.CurrentEditor;
        var path = _ctx.EditorManager.CurrentFilePath;
        if (path == null) return;

        var edits = await _lsp.FormattingAsync(path);
        if (edits.Count == 0) return;

        var lines = editor.Content.Split('\n').ToList();
        ApplyTextEditsToLines(lines, edits);
        var formatted = string.Join('\n', lines);
        _ctx.PendingUiActions.Enqueue(() => editor.Content = formatted);
    }

    // ── Symbols ──────────────────────────────────────────────────────────

    public void RefreshSymbolsForFile(string? filePath)
    {
        if (filePath == null)
        {
            _ctx.SidePanel.ClearSymbols();
            return;
        }
        if (_lsp == null || !_ctx.SidePanel.TabControl.Visible)
            return;
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _ctx.SidePanel.ClearSymbols();
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
            _ctx.PendingUiActions.Enqueue(() => RefreshSymbolsForFile(filePath));
        }, null, SymbolRefreshMs, Timeout.Infinite);
    }

    private async Task RefreshSymbolsAsync(string filePath)
    {
        if (_lsp == null) return;
        try
        {
            var symbols = await _lsp.DocumentSymbolAsync(filePath);
            _ctx.PendingUiActions.Enqueue(() => _ctx.SidePanel.UpdateSymbols(filePath, symbols));
        }
        catch (Exception ex)
        {
            LogError("RefreshSymbolsAsync", ex);
            _ctx.PendingUiActions.Enqueue(() => _ctx.SidePanel.ClearSymbols());
        }
    }

    public async Task ShowDocumentSymbolsAsync(string? currentFilePath)
    {
        if (_lsp == null || _ctx.EditorManager.CurrentEditor == null) return;
        var path = currentFilePath ?? _ctx.EditorManager.CurrentFilePath;
        if (path == null) return;

        var symbols = await _lsp.DocumentSymbolAsync(path);
        if (symbols.Count == 0)
        {
            _portalManager.ShowTransientTooltip("No symbols found in document.");
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
            var kindName = LspSymbolHelper.GetSymbolKindName(sym.Kind);
            var s = sym;
            tempRegistry.Register(new IdeCommand
            {
                Id = $"sym.{sym.SelectionRange.Start.Line}.{sym.Name}",
                Category = kindName,
                Label = $"{indent}{sym.Name}",
                Keybinding = $"Ln {sym.SelectionRange.Start.Line + 1}",
                Execute = () => _navManager.NavigateToLocation(new LspLocationEntry(
                    path!, s.SelectionRange.Start.Line + 1,
                    s.SelectionRange.Start.Character + 1, s.Name)),
                Priority = 100 - sym.SelectionRange.Start.Line
            });
        }

        _portalManager.ShowCommandPalettePortal(tempRegistry);
    }

    // ── Semantic Tokens ──────────────────────────────────────────────────

    public void SetupSemanticHighlighter(string filePath)
    {
        if (_lsp == null || _lsp.TokenLegend == null) return;
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        if (_semanticHighlighters.ContainsKey(filePath)) return;

        var editor = _ctx.EditorManager.GetEditorByPath(filePath);
        if (editor == null) return;

        var currentHighlighter = editor.SyntaxHighlighter;
        if (currentHighlighter is SemanticHighlighter) return; // already wrapped

        var semantic = new SemanticHighlighter(currentHighlighter ?? new CSharpSyntaxHighlighter());
        editor.SyntaxHighlighter = semantic;
        _semanticHighlighters[filePath] = semantic;

        // Schedule immediate token fetch
        ScheduleSemanticTokenRefresh(filePath, immediate: true);
    }

    public void ScheduleSemanticTokenRefresh(string filePath, bool immediate = false)
    {
        if (_lsp == null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        _semanticTokenDebounce?.Dispose();
        _semanticTokenDebounce = new Timer(_ =>
            _ctx.PendingUiActions.Enqueue(() => _ = RefreshSemanticTokensAsync(filePath)),
            null, immediate ? 0 : SemanticTokenRefreshMs, Timeout.Infinite);
    }

    private static readonly int[] RetryDelaysMs = [3000, 8000, 15000];

    private async Task RefreshSemanticTokensAsync(string filePath, int attempt = 0)
    {
        if (_lsp == null || _lsp.TokenLegend == null) return;

        var isFirst = attempt == 0;
        if (isFirst && Interlocked.Increment(ref _busyCount) == 1)
            _ctx.PendingUiActions.Enqueue(() => LspBusyChanged?.Invoke(true));

        try
        {
            await _lsp.FlushPendingChangeAsync();
            var tokens = await _lsp.SemanticTokensFullAsync(filePath);

            if (tokens.Count > 0 && _semanticHighlighters.TryGetValue(filePath, out var highlighter))
            {
                var legend = _lsp.TokenLegend;
                _ctx.PendingUiActions.Enqueue(() =>
                {
                    highlighter.UpdateTokens(tokens, legend);
                    var editor = _ctx.EditorManager.GetEditorByPath(filePath);
                    if (editor != null)
                        editor.SyntaxHighlighter = editor.SyntaxHighlighter;
                });
            }
            else if (attempt < RetryDelaysMs.Length && _semanticHighlighters.ContainsKey(filePath))
            {
                // Server may still be indexing — retry with increasing delay
                await Task.Delay(RetryDelaysMs[attempt]);
                await RefreshSemanticTokensAsync(filePath, attempt + 1);
            }
        }
        catch (Exception ex)
        {
            LogError("RefreshSemanticTokensAsync", ex);
        }

        if (isFirst && Interlocked.Decrement(ref _busyCount) == 0)
            _ctx.PendingUiActions.Enqueue(() => LspBusyChanged?.Invoke(false));
    }

    public void RemoveSemanticHighlighter(string filePath)
    {
        _semanticHighlighters.TryRemove(filePath, out _);
    }

    public void SetupSemanticHighlightersForOpenFiles()
    {
        if (_lsp == null || _lsp.TokenLegend == null) return;

        var files = _ctx.EditorManager.GetOpenDocuments()
            .Select(d => d.FilePath)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var filePath in files)
        {
            // Set up the highlighter wrapper (no immediate refresh — we'll batch below)
            if (_semanticHighlighters.ContainsKey(filePath)) continue;
            var editor = _ctx.EditorManager.GetEditorByPath(filePath);
            if (editor == null) continue;
            var currentHighlighter = editor.SyntaxHighlighter;
            if (currentHighlighter is SemanticHighlighter) continue;
            var semantic = new SemanticHighlighter(currentHighlighter ?? new CSharpSyntaxHighlighter());
            editor.SyntaxHighlighter = semantic;
            _semanticHighlighters[filePath] = semantic;
        }

        // Fire all initial token fetches concurrently (each has its own retry logic)
        if (files.Count > 0)
            _ = Task.Run(async () =>
            {
                foreach (var filePath in files)
                    if (_semanticHighlighters.ContainsKey(filePath))
                        await RefreshSemanticTokensAsync(filePath);
            });
    }

    // ── Dot trigger / auto-completion ──────────────────────────────────

    public void TryScheduleDotCompletion(string filePath, string content)
    {
        if (_lsp == null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        var editor = _ctx.EditorManager.CurrentEditor;
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
                _ => _ctx.PendingUiActions.Enqueue(() => _ = ShowCompletionAsync(silent: true)),
                null, DotTriggerMs, Timeout.Infinite);
        }
        else if (lastChar is '(' or ',')
        {
            _ = _lsp.FlushPendingChangeAsync();
            _dotTriggerDebounce?.Dispose();
            _dotTriggerDebounce = new Timer(
                _ => _ctx.PendingUiActions.Enqueue(() => _ = ShowSignatureHelpAsync(silent: true)),
                null, SignatureTriggerMs, Timeout.Infinite);
        }
        else if (LspPortalManager.IsIdentifierChar(lastChar) && !HasCompletionPortal)
        {
            int wordLen = 0;
            int i = col - 1;
            while (i >= 0 && LspPortalManager.IsIdentifierChar(currentLine[i])) { wordLen++; i--; }

            bool afterDot = i >= 0 && currentLine[i] == '.';

            if (wordLen >= 3 && !afterDot)
            {
                _dotTriggerDebounce?.Dispose();
                _dotTriggerDebounce = new Timer(
                    _ => _ctx.PendingUiActions.Enqueue(() => _ = ShowCompletionAsync(silent: true)),
                    null, WordCompletionMs, Timeout.Infinite);
            }
        }
    }

    private bool HasCompletionPortal => _portalManager.HasCompletionPortal;

    // ── Portal delegation ──────────────────────────────────────────────

    public void ShowTransientTooltip(string message) =>
        _portalManager.ShowTransientTooltip(message);

    public void DismissCompletionPortal() => _portalManager.DismissCompletionPortal();
    public void DismissTooltipPortal() => _portalManager.DismissTooltipPortal();
    public void DismissLocationPortal() => _portalManager.DismissLocationPortal();

    public void ShowCommandPalettePortal(CommandRegistry registry) =>
        _portalManager.ShowCommandPalettePortal(registry);

    public bool ProcessPreviewKey(KeyPressedEventArgs e) =>
        _portalManager.ProcessPreviewKey(e);

    public void NavigateToLocation(LspLocationEntry entry) =>
        _navManager.NavigateToLocation(entry);

    // ── Workspace edit helpers ──────────────────────────────────────────

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
                catch (Exception ex) { LogError("ApplyWorkspaceEdit", ex); }
            }
        }
    }

    private MultilineEditControl? GetEditorForFile(string filePath)
    {
        foreach (var (fp, content) in _ctx.EditorManager.GetOpenDocuments())
        {
            if (string.Equals(fp, filePath, StringComparison.OrdinalIgnoreCase))
            {
                _ctx.EditorManager.OpenFile(filePath);
                return _ctx.EditorManager.CurrentEditor;
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

    private static string ExtractWordAtCursor(MultilineEditControl editor)
    {
        var lines = editor.Content.Split('\n');
        int lineIdx = editor.CurrentLine - 1;
        if (lineIdx < 0 || lineIdx >= lines.Length) return "";
        var line = lines[lineIdx];
        int col = Math.Min(editor.CurrentColumn - 1, line.Length);
        int start = col, end = col;
        while (start > 0 && LspPortalManager.IsIdentifierChar(line[start - 1])) start--;
        while (end < line.Length && LspPortalManager.IsIdentifierChar(line[end])) end++;
        return start < end ? line[start..end] : "";
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _dotTriggerDebounce?.Dispose();
        _symbolRefreshDebounce?.Dispose();
        _semanticTokenDebounce?.Dispose();
        _portalManager.DisposeTimers();
        _portalManager.DismissAll();
        if (_lsp != null)
            await _lsp.DisposeAsync();
    }
}
