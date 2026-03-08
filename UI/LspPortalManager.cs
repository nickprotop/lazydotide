using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

/// <summary>
/// Manages LSP portal overlays: completion, tooltip, location list, command palette.
/// Handles portal creation, dismissal, and preview key processing for portal navigation.
/// </summary>
internal class LspPortalManager
{
    private readonly EditorManager _editorManager;
    private readonly ConcurrentQueue<Action> _pendingUiActions;

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

    // Tooltip auto-dismiss
    private Timer? _tooltipAutoDismiss;
    private int _tooltipAutoDismissGeneration;

    public bool HasCompletionPortal => _completionPortal != null;

    // Callback for dot trigger debounce disposal on completion accept
    public Action? OnCompletionAccepted;

    // Callback for navigation (set by LspNavigationManager)
    public Action<LspLocationEntry>? NavigateToLocation;

    public LspPortalManager(
        EditorManager editorManager,
        ConcurrentQueue<Action> pendingUiActions)
    {
        _editorManager = editorManager;
        _pendingUiActions = pendingUiActions;
    }

    public void SetMainWindow(Window mainWindow) => _mainWindow = mainWindow;

    // ── Completion Portal ──────────────────────────────────────────────

    public async Task ShowCompletionAsync(LspClient lsp, bool silent = false)
    {
        if (_editorManager.CurrentEditor == null || _mainWindow == null) return;

        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        int requestLine = editor.CurrentLine;
        int requestCol = editor.CurrentColumn;

        var items = await lsp.CompletionAsync(path, requestLine - 1, requestCol - 1);
        if (items.Count == 0)
        {
            if (!silent) _pendingUiActions.Enqueue(() => ShowTransientTooltip("No completions at cursor."));
            return;
        }

        if (editor.CurrentLine != requestLine) return;

        _pendingUiActions.Enqueue(() =>
        {
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
                _mainWindow!.Width, _mainWindow.Height);

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
                OnCompletionAccepted?.Invoke();
            };

            portal.DismissRequested += (_, _) => DismissCompletionPortal();

            editor.ContentChanged += OnEditorContentChangedForCompletion;
        });
    }

    public void ShowCodeActionsPortal(
        List<CodeAction> actions, ConsoleWindowSystem ws,
        Action<WorkspaceEdit> applyWorkspaceEdit)
    {
        var editor = _editorManager.CurrentEditor;
        if (editor == null || _mainWindow == null) return;

        var items = actions.Select(a => new CompletionItem(a.Title, a.Kind, null, 1)).ToList();

        DismissCompletionPortal();
        var cursor = _editorManager.GetCursorBounds();
        var portal = new LspCompletionPortalContent(
            items, cursor.X, cursor.Y,
            _mainWindow!.Width, _mainWindow.Height);

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
                applyWorkspaceEdit(action.Edit);
                ws.NotificationStateService.ShowNotification(
                    "Code Action", $"Applied: {action.Title}",
                    SharpConsoleUI.Core.NotificationSeverity.Info);
            }
        };
    }

    // ── Dismiss helpers ──────────────────────────────────────────────

    public void DismissCompletionPortal()
    {
        var editor = _editorManager.CurrentEditor;
        if (editor != null)
            editor.ContentChanged -= OnEditorContentChangedForCompletion;

        if (_completionPortalNode != null && _mainWindow != null)
        {
            _mainWindow.RemovePortal(editor ?? (IWindowControl)_mainWindow, _completionPortalNode);
            _completionPortalNode = null;
        }
        _completionPortal = null;
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

    public void DismissAll()
    {
        DismissCommandPalette();
        DismissCompletionPortal();
        DismissTooltipPortal();
        DismissLocationPortal();
    }

    // ── Show helpers ──────────────────────────────────────────────────

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

    public void ShowTooltipPortal(List<string> lines, bool preferAbove = true)
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

        ShowTooltipPortal(new List<string> { MarkupParser.Escape(message) });

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

    public void ShowLocationPortal(List<LspLocationEntry> entries, Action<LspLocationEntry> onAccepted)
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

    // ── Preview key processing (portal navigation) ──────────────────

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

        // Escape: dismiss portals (only consume ESC if a portal was actually open)
        if (key == ConsoleKey.Escape && mods == 0)
        {
            if (_locationPortal != null)
            {
                DismissLocationPortal();
                e.Handled = true;
                return true;
            }
            if (_completionPortal != null)
            {
                DismissCompletionPortal();
                e.Handled = true;
                return true;
            }
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
                        NavigateToLocation?.Invoke(selected);
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
                        OnCompletionAccepted?.Invoke();
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

    // ── Internal helpers ──────────────────────────────────────────────

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

    internal static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

    public void DisposeTimers()
    {
        _tooltipAutoDismiss?.Dispose();
    }
}
