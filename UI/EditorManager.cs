using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public class EditorManager
{
    private readonly ConsoleWindowSystem _ws;
    private readonly TabControl _tabControl;
    private record EditorTabData(string? FilePath, MultilineEditControl? Editor, bool IsDirty, string? SyntaxOverride = null, GitDiffGutterRenderer? DiffGutter = null);

    private readonly Dictionary<string, int> _openFiles = new();
    private readonly Dictionary<int, EditorTabData> _tabData = new();

    private readonly FileMiddlewarePipeline _pipeline;
    private WrapMode _wrapMode = WrapMode.NoWrap;

    public WrapMode WrapMode
    {
        get => _wrapMode;
        set
        {
            _wrapMode = value;
            foreach (var d in _tabData.Values)
                if (d.Editor != null) d.Editor.WrapMode = value;
        }
    }

    public event EventHandler<(int Line, int Column)>? CursorChanged;
    public event EventHandler<string?>? ActiveFileChanged;
    public event EventHandler<IReadOnlyList<string>>? ValidationWarnings;
    public event EventHandler<(string FilePath, string Content)>? DocumentOpened;
    public event EventHandler<(string FilePath, string Content)>? DocumentChanged;
    public event EventHandler<string>? DocumentSaved;
    public event EventHandler<string>? DocumentClosed;
    public event EventHandler<string>? SyntaxChanged;
    public event EventHandler<(string FilePath, Point ScreenPosition)>? TabContextMenuRequested;
    public event EventHandler<(string? FilePath, Point ScreenPosition)>? EditorContextMenuRequested;

    public string? CurrentFilePath =>
        _tabControl.ActiveTabIndex >= 0 && _tabData.TryGetValue(_tabControl.ActiveTabIndex, out var d) ? d.FilePath : null;

    public MultilineEditControl? CurrentEditor =>
        _tabControl.ActiveTabIndex >= 0 && _tabData.TryGetValue(_tabControl.ActiveTabIndex, out var d2) ? d2.Editor : null;

    public TabControl TabControl => _tabControl;

    public string CurrentSyntaxName
    {
        get
        {
            if (_tabControl.ActiveTabIndex >= 0 && _tabData.TryGetValue(_tabControl.ActiveTabIndex, out var d))
            {
                if (d.SyntaxOverride != null) return d.SyntaxOverride;
                if (d.FilePath != null) return _pipeline.GetSyntaxName(d.FilePath);
            }
            return "Plain Text";
        }
    }

    public void SetSyntaxHighlighter(string name, ISyntaxHighlighter? highlighter)
    {
        if (_tabControl.ActiveTabIndex < 0) return;
        if (!_tabData.TryGetValue(_tabControl.ActiveTabIndex, out var data)) return;
        if (data.Editor == null) return;

        data.Editor.SyntaxHighlighter = highlighter;
        _tabData[_tabControl.ActiveTabIndex] = data with { SyntaxOverride = name };
        SyntaxChanged?.Invoke(this, name);
    }

    public bool HasOpenFiles => _tabControl.TabCount > 0;
    public event EventHandler? OpenFilesStateChanged;

    public IEnumerable<(string FilePath, string Content)> GetOpenDocuments() =>
        _tabData.Values
            .Where(d => d.FilePath != null && d.Editor != null
                        && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(d => (d.FilePath!, d.Editor!.Content));

    public EditorManager(ConsoleWindowSystem ws, FileMiddlewarePipeline pipeline)
    {
        _ws = ws;
        _pipeline = pipeline;
        _tabControl = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            HeaderStyle = TabHeaderStyle.Separator,
            SelectOnRightClick = true
        };

        _tabControl.TabChanged += OnTabChanged;
        _tabControl.TabCloseRequested += OnTabCloseRequested;
        _tabControl.MouseRightClick += OnTabRightClick;
    }

    public void OpenFile(string path)
    {
        // Already open — just switch to it
        if (_openFiles.TryGetValue(path, out var existingIdx))
        {
            _tabControl.ActiveTabIndex = existingIdx;
            return;
        }

        IWindowControl content;
        bool isBinary = FileService.IsBinaryFile(path);

        if (isBinary)
        {
            var binaryPanel = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch };
            binaryPanel.AddControl(new MarkupControl(new List<string>
            {
                $"[yellow]Binary file: {Markup.Escape(Path.GetFileName(path))}[/]",
                "[dim]Cannot display binary content in text editor.[/]"
            }));
            content = binaryPanel;
            AddTab(path, content, editor: null, isDirty: false);
            return;
        }

        string fileContent;
        try
        {
            fileContent = _pipeline.ProcessLoad(FileService.ReadFile(path), path);
        }
        catch (Exception ex)
        {
            var errorPanel = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch };
            errorPanel.AddControl(new MarkupControl(new List<string>
                { $"[red]Error reading file: {Markup.Escape(ex.Message)}[/]" }));
            AddTab(path, errorPanel, editor: null, isDirty: false);
            return;
        }

        var (editor, diffGutter) = CreateEditor(path, fileContent);
        AddTab(path, editor, editor: editor, isDirty: false, diffGutter: diffGutter);
    }

    private void AddTab(string path, IWindowControl content, MultilineEditControl? editor, bool isDirty, GitDiffGutterRenderer? diffGutter = null)
    {
        bool wasEmpty = _tabControl.TabCount == 0;
        var tabTitle = Path.GetFileName(path);
        _tabControl.AddTab(tabTitle, content, isClosable: true);
        var tabIndex = _tabControl.TabCount - 1;
        _openFiles[path] = tabIndex;

        // Now that the editor has a Container, sync FocusedBackgroundColor to match view-mode bg
        if (editor != null)
            editor.FocusedBackgroundColor = editor.BackgroundColor;

        _tabData[tabIndex] = new EditorTabData(FilePath: path, Editor: editor, IsDirty: isDirty, DiffGutter: diffGutter);

        _tabControl.ActiveTabIndex = tabIndex;

        if (editor != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            DocumentOpened?.Invoke(this, (path, editor.Content));

        // Always notify changes (TabChanged may not fire for the first tab)
        ActiveFileChanged?.Invoke(this, CurrentFilePath);
        SyntaxChanged?.Invoke(this, CurrentSyntaxName);

        if (wasEmpty)
            OpenFilesStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private (MultilineEditControl Editor, GitDiffGutterRenderer DiffGutter) CreateEditor(string path, string content)
    {
        var editor = new MultilineEditControl
        {
            ShowLineNumbers = true,
            HighlightCurrentLine = true,
            AutoIndent = true,
            WrapMode = _wrapMode,
            IsEditing = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        editor.Content = content;
        editor.SyntaxHighlighter = _pipeline.GetHighlighter(path);

        // Add git diff gutter renderer (inserted at position 0 so it appears left of line numbers)
        var diffGutter = new GitDiffGutterRenderer();
        editor.InsertGutterRenderer(0, diffGutter);

        // Always re-enter editing mode on focus (code editor is always "typing mode")
        editor.GotFocus += (_, _) => editor.IsEditing = true;

        editor.CursorPositionChanged += (_, pos) =>
        {
            CursorChanged?.Invoke(this, pos);
        };

        editor.MouseRightClick += (_, args) =>
        {
            int screenX = editor.ActualX + args.Position.X;
            int screenY = editor.ActualY + args.Position.Y;
            var filePath = CurrentFilePath;
            EditorContextMenuRequested?.Invoke(this, (filePath, new Point(screenX, screenY)));
        };

        editor.ContentChanged += (_, _) =>
        {
            // Mark current tab as dirty
            if (_tabControl.ActiveTabIndex >= 0 && _tabData.TryGetValue(_tabControl.ActiveTabIndex, out var data))
            {
                if (!data.IsDirty)
                {
                    _tabData[_tabControl.ActiveTabIndex] = data with { IsDirty = true };
                    _tabControl.SetTabTitle(_tabControl.ActiveTabIndex, Path.GetFileName(data.FilePath!) + " *");
                }
                if (data.FilePath != null && data.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    DocumentChanged?.Invoke(this, (data.FilePath, editor.Content));
            }
        };

        return (editor, diffGutter);
    }

    public void SaveCurrent()
    {
        if (_tabControl.ActiveTabIndex < 0) return;
        if (!_tabData.TryGetValue(_tabControl.ActiveTabIndex, out var data)) return;
        if (data.Editor == null || data.FilePath == null) return;

        var dir = Path.GetDirectoryName(data.FilePath);
        if (dir != null && !Directory.Exists(dir)) return;

        try
        {
            var editorContent = data.Editor.Content;
            var warnings = _pipeline.Validate(editorContent, data.FilePath);
            if (warnings?.Count > 0)
                ValidationWarnings?.Invoke(this, warnings);

            FileService.WriteFile(data.FilePath, _pipeline.ProcessSave(editorContent, data.FilePath));
            _tabData[_tabControl.ActiveTabIndex] = data with { IsDirty = false };
            _tabControl.SetTabTitle(_tabControl.ActiveTabIndex, Path.GetFileName(data.FilePath));
            DocumentSaved?.Invoke(this, data.FilePath);
        }
        catch (Exception ex)
        {
            _ws.LogService.LogError($"Failed to save {data.FilePath}: {ex.Message}");
        }
    }

    public bool IsCurrentTabDirty()
    {
        if (_tabControl.ActiveTabIndex < 0) return false;
        return _tabData.TryGetValue(_tabControl.ActiveTabIndex, out var data) && data.IsDirty;
    }

    public bool IsTabDirty(int index) =>
        _tabData.TryGetValue(index, out var data) && data.IsDirty;

    public string? GetTabFilePath(int index) =>
        _tabData.TryGetValue(index, out var data) ? data.FilePath : null;

    public void CloseCurrentTab()
    {
        if (_tabControl.ActiveTabIndex < 0) return;
        var idx = _tabControl.ActiveTabIndex;

        if (_tabData.TryGetValue(idx, out var data) && data.FilePath != null)
        {
            _openFiles.Remove(data.FilePath);
            if (data.Editor != null && data.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                DocumentClosed?.Invoke(this, data.FilePath);
        }

        _tabData.Remove(idx);
        _tabControl.RemoveTab(idx);

        // Re-index remaining tabs after removal
        var newOpenFiles = new Dictionary<string, int>();
        var newTabData = new Dictionary<int, EditorTabData>();

        foreach (var (path, oldIdx) in _openFiles)
        {
            var newIdx = oldIdx > idx ? oldIdx - 1 : oldIdx;
            newOpenFiles[path] = newIdx;
        }
        foreach (var (oldIdx, tdata) in _tabData)
        {
            var newIdx = oldIdx > idx ? oldIdx - 1 : oldIdx;
            newTabData[newIdx] = tdata;
        }

        _openFiles.Clear();
        foreach (var kv in newOpenFiles) _openFiles[kv.Key] = kv.Value;
        _tabData.Clear();
        foreach (var kv in newTabData) _tabData[kv.Key] = kv.Value;

        if (_tabControl.TabCount == 0)
            OpenFilesStateChanged?.Invoke(this, EventArgs.Empty);

        ActiveFileChanged?.Invoke(this, CurrentFilePath);
        SyntaxChanged?.Invoke(this, CurrentSyntaxName);
    }

    public void CloseAll()
    {
        while (_tabControl.TabCount > 0)
            CloseCurrentTab();
    }

    public void CloseTabAt(int index)
    {
        if (index < 0 || index >= _tabControl.TabCount) return;
        _tabControl.ActiveTabIndex = index;
        CloseCurrentTab();
    }

    /// <summary>Opens any IWindowControl as a closable editor-area tab.</summary>
    public int OpenControlTab(string title, IWindowControl control, bool isClosable = true)
    {
        bool wasEmpty = _tabControl.TabCount == 0;
        _tabControl.AddTab(title, control, isClosable);
        int tabIndex = _tabControl.TabCount - 1;
        _tabData[tabIndex] = new EditorTabData(FilePath: null, Editor: null, IsDirty: false);
        _tabControl.ActiveTabIndex = tabIndex;

        if (control is SharpConsoleUI.Controls.Terminal.TerminalControl tc &&
            (IdeConstants.IsDesktopOs))
            tc.ProcessExited += (_, _) => RemoveTabWithControl(control);

        if (wasEmpty)
            OpenFilesStateChanged?.Invoke(this, EventArgs.Empty);

        return tabIndex;
    }

    private void RemoveTabWithControl(IWindowControl control)
    {
        for (int i = 0; i < _tabControl.TabCount; i++)
        {
            if (ReferenceEquals(_tabControl.TabPages[i].Content, control))
            {
                CloseTabAt(i);
                return;
            }
        }
    }

    public event EventHandler<int>? TabCloseRequested;

    private void OnTabCloseRequested(object? sender, SharpConsoleUI.Events.TabEventArgs args)
    {
        if (TabCloseRequested != null)
            TabCloseRequested.Invoke(this, args.Index);
        else
            CloseTabAt(args.Index);
    }

    public bool IsFileOpen(string path) => _openFiles.ContainsKey(path);

    public bool IsFileDirty(string path)
    {
        if (!_openFiles.TryGetValue(path, out var tabIndex)) return false;
        return _tabData.TryGetValue(tabIndex, out var data) && data.IsDirty;
    }

    public void ReloadFile(string path)
    {
        if (!_openFiles.TryGetValue(path, out var tabIndex)) return;
        if (!_tabData.TryGetValue(tabIndex, out var data)) return;
        if (data.Editor == null) return;
        try
        {
            var newContent = _pipeline.ProcessLoad(FileService.ReadFile(path), path);
            data.Editor.Content = newContent;
            _tabData[tabIndex] = data with { IsDirty = false };
            _tabControl.SetTabTitle(tabIndex, Path.GetFileName(path));
        }
        catch { } // Best effort reload — keep existing content if file read fails
    }

    public void MarkFileConflict(string path)
    {
        if (!_openFiles.TryGetValue(path, out var tabIndex)) return;
        if (!_tabData.TryGetValue(tabIndex, out var data)) return;
        _tabControl.SetTabTitle(tabIndex, "⚠ " + Path.GetFileName(path) + " *");
    }

    public (int Line, int Column)? GetTabCursor(int index)
    {
        if (!_tabData.TryGetValue(index, out var data) || data.Editor == null) return null;
        return (data.Editor.CurrentLine, data.Editor.CurrentColumn);
    }

    public void GoToLine(int line)
    {
        CurrentEditor?.GoToLine(line);
    }

    public LayoutRect GetCursorBounds()
    {
        var editor = CurrentEditor;
        if (editor == null) return new LayoutRect(30, 5, 1, 1);

        // ActualX/Y is the top-left of the editor widget in content-area coordinates.
        // Subtract scroll offsets so we get the screen row/col, not the document row/col.
        int gutterW = editor.GutterWidth;
        int col  = Math.Max(0, editor.CurrentColumn - 1 - editor.HorizontalScrollOffset);
        int line = Math.Max(0, editor.CurrentLine  - 1 - editor.VerticalScrollOffset);
        int x = editor.ActualX + gutterW + col;
        int y = editor.ActualY + line;
        return new LayoutRect(x, y, 1, 1);
    }

    private void OnTabChanged(object? sender, TabChangedEventArgs args)
    {
        ActiveFileChanged?.Invoke(this, CurrentFilePath);
        SyntaxChanged?.Invoke(this, CurrentSyntaxName);
    }

    // ──────────────────────────────────────────────────────────────
    // Search / Replace
    // ──────────────────────────────────────────────────────────────

    public bool FindNext(string query, bool caseSensitive)
    {
        var editor = CurrentEditor;
        if (editor == null || string.IsNullOrEmpty(query)) return false;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var lines = editor.Content.Split('\n');

        int startLine = editor.CurrentLine - 1;
        int startCol  = editor.CurrentColumn - 1;

        // Two-pass search: from cursor forward, then wrap from start
        for (int pass = 0; pass < 2; pass++)
        {
            int fromLine = pass == 0 ? startLine : 0;
            for (int i = fromLine; i < lines.Length; i++)
            {
                int fromCol = (pass == 0 && i == startLine) ? startCol : 0;
                int idx = lines[i].IndexOf(query, fromCol, comparison);
                if (idx >= 0)
                {
                    editor.SelectRange(i, idx, i, idx + query.Length);
                    return true;
                }
            }
        }
        return false;
    }

    public bool FindPrevious(string query, bool caseSensitive)
    {
        var editor = CurrentEditor;
        if (editor == null || string.IsNullOrEmpty(query)) return false;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var lines = editor.Content.Split('\n');

        int curLine = editor.CurrentLine - 1;
        int curCol  = editor.CurrentColumn - 1;

        // Collect all matches
        var matches = new List<(int Line, int Col)>();
        for (int i = 0; i < lines.Length; i++)
        {
            int from = 0;
            while (true)
            {
                int idx = lines[i].IndexOf(query, from, comparison);
                if (idx < 0) break;
                matches.Add((i, idx));
                from = idx + 1;
            }
        }
        if (matches.Count == 0) return false;

        // Find last match before cursor
        int target = -1;
        for (int m = matches.Count - 1; m >= 0; m--)
        {
            var (ml, mc) = matches[m];
            if (ml < curLine || (ml == curLine && mc < curCol - query.Length + 1))
            {
                target = m;
                break;
            }
        }
        if (target < 0) target = matches.Count - 1; // wrap

        var (line, col) = matches[target];
        editor.SelectRange(line, col, line, col + query.Length);
        return true;
    }

    public int ReplaceAll(string query, string replacement, bool caseSensitive)
    {
        var editor = CurrentEditor;
        if (editor == null || string.IsNullOrEmpty(query)) return 0;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string content = editor.Content;

        int count = 0;
        int idx = 0;
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            int found = content.IndexOf(query, idx, comparison);
            if (found < 0)
            {
                sb.Append(content, idx, content.Length - idx);
                break;
            }
            sb.Append(content, idx, found - idx);
            sb.Append(replacement);
            idx = found + query.Length;
            count++;
        }

        if (count > 0)
            editor.Content = sb.ToString();

        return count;
    }

    public bool ReplaceNext(string query, string replacement, bool caseSensitive)
    {
        var editor = CurrentEditor;
        if (editor == null || string.IsNullOrEmpty(query)) return false;

        // FindNext to select the match
        if (!FindNext(query, caseSensitive)) return false;

        // After FindNext cursor is at end of match; replace match at (currentLine-1, currentColumn-1-queryLength)
        var lines = editor.Content.Split('\n');
        int matchLine = editor.CurrentLine - 1;
        int matchCol  = editor.CurrentColumn - 1 - query.Length;

        if (matchLine < 0 || matchLine >= lines.Length || matchCol < 0) return false;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string line = lines[matchLine];
        if (matchCol + query.Length > line.Length) return false;

        // Verify the match still exists at expected position
        if (!line.Substring(matchCol, query.Length).Equals(query, comparison)) return false;

        lines[matchLine] = line[..matchCol] + replacement + line[(matchCol + query.Length)..];
        editor.Content = string.Join('\n', lines);

        // Advance to next match
        FindNext(query, caseSensitive);
        return true;
    }

    // ──────────────────────────────────────────────────────────────
    // Context Menu Support
    // ──────────────────────────────────────────────────────────────

    private void OnTabRightClick(object? sender, MouseEventArgs args)
    {
        // Use whichever tab is currently active (TabControl selects on click before right-click fires)
        var filePath = CurrentFilePath;
        if (filePath == null) return;
        int screenX = _tabControl.ActualX + args.Position.X;
        int screenY = _tabControl.ActualY + args.Position.Y;
        TabContextMenuRequested?.Invoke(this, (filePath, new Point(screenX, screenY)));
    }

    public void CloseOthers(string keepFilePath)
    {
        for (int i = _tabControl.TabCount - 1; i >= 0; i--)
        {
            var tabPath = GetTabFilePath(i);
            if (tabPath != null && tabPath != keepFilePath)
                CloseTabAt(i);
            else if (tabPath == null)
                CloseTabAt(i); // close non-file tabs too
        }
    }

    public void SaveTabAt(int index)
    {
        if (index < 0 || index >= _tabControl.TabCount) return;
        var prev = _tabControl.ActiveTabIndex;
        _tabControl.ActiveTabIndex = index;
        SaveCurrent();
        _tabControl.ActiveTabIndex = prev;
    }

    public int GetTabIndexForPath(string path)
    {
        return _openFiles.TryGetValue(path, out var idx) ? idx : -1;
    }

    public int TabCount => _tabControl.TabCount;

    /// <summary>
    /// Opens a read-only tab with the given title and text content.
    /// If a tab with the same title already exists, it is replaced.
    /// </summary>
    public void OpenReadOnlyTab(string title, string content, ISyntaxHighlighter? syntaxHighlighter = null)
    {
        var pseudoPath = $"__readonly:{title}";

        // Check if a tab with this title already exists
        if (_openFiles.TryGetValue(pseudoPath, out var existingIdx) &&
            _tabData.TryGetValue(existingIdx, out var existing) &&
            existing.Editor != null)
        {
            existing.Editor.Content = content;
            if (syntaxHighlighter != null)
                existing.Editor.SyntaxHighlighter = syntaxHighlighter;
            _tabControl.ActiveTabIndex = existingIdx;
            return;
        }

        var editor = new MultilineEditControl
        {
            ShowLineNumbers = true,
            HighlightCurrentLine = false,
            IsEditing = false,
            ReadOnly = true,
            WrapMode = WrapMode.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        editor.Content = content;
        if (syntaxHighlighter != null)
            editor.SyntaxHighlighter = syntaxHighlighter;

        bool wasEmpty = _tabControl.TabCount == 0;
        _tabControl.AddTab(title, editor, isClosable: true);
        var tabIndex = _tabControl.TabCount - 1;
        _openFiles[pseudoPath] = tabIndex;
        _tabData[tabIndex] = new EditorTabData(FilePath: pseudoPath, Editor: editor, IsDirty: false);

        // Sync focused bg to match view-mode bg (same as regular editor tabs)
        editor.FocusedBackgroundColor = editor.BackgroundColor;

        _tabControl.ActiveTabIndex = tabIndex;

        if (wasEmpty)
            OpenFilesStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reloads a tab's content from disk. No-op for read-only or binary tabs.
    /// </summary>
    public void ReloadTabFromDisk(int index)
    {
        if (!_tabData.TryGetValue(index, out var data)) return;
        if (data.FilePath == null || data.FilePath.StartsWith(IdeConstants.ReadOnlyTabPrefix)) return;
        ReloadFile(data.FilePath);
    }

    // ──────────────────────────────────────────────────────────────
    // Git diff gutter markers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates git diff markers for a specific file tab and invalidates the editor.
    /// </summary>
    public void UpdateGitDiffMarkers(string filePath, Dictionary<int, GitLineChangeType>? markers)
    {
        if (!_openFiles.TryGetValue(filePath, out var tabIndex)) return;
        if (!_tabData.TryGetValue(tabIndex, out var data)) return;
        if (data.DiffGutter == null) return;

        data.DiffGutter.UpdateMarkers(markers);
        data.Editor?.Container?.Invalidate(true);
    }

    /// <summary>
    /// Refreshes git diff markers for all open file tabs using the provided async factory.
    /// </summary>
    /// <summary>
    /// Collects git diff markers for all open file tabs (async, no UI calls).
    /// Returns a list of (tabIndex, markers) to be applied on the UI thread via ApplyGitDiffMarkers.
    /// </summary>
    public async Task<List<(int TabIndex, Dictionary<int, GitLineChangeType>? Markers)>> CollectGitDiffMarkersAsync(
        Func<string, Task<Dictionary<int, GitLineChangeType>?>> getMarkers)
    {
        var results = new List<(int, Dictionary<int, GitLineChangeType>?)>();
        foreach (var (filePath, tabIndex) in _openFiles)
        {
            if (!_tabData.TryGetValue(tabIndex, out var data)) continue;
            if (data.DiffGutter == null || data.FilePath == null) continue;
            if (data.FilePath.StartsWith(IdeConstants.ReadOnlyTabPrefix)) continue;

            var markers = await getMarkers(data.FilePath);
            results.Add((tabIndex, markers));
        }
        return results;
    }

    /// <summary>
    /// Applies previously collected diff markers to editor tabs. Must be called on the UI thread.
    /// </summary>
    public void ApplyGitDiffMarkers(List<(int TabIndex, Dictionary<int, GitLineChangeType>? Markers)> updates)
    {
        foreach (var (tabIndex, markers) in updates)
        {
            if (!_tabData.TryGetValue(tabIndex, out var data)) continue;
            if (data.DiffGutter == null) continue;
            data.DiffGutter.UpdateMarkers(markers);
            data.Editor?.Container?.Invalidate(true);
        }
    }
}
