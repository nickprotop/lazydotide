using System.Collections.Concurrent;
using System.Drawing;
using SharpConsoleUI.Controls;

namespace DotNetIDE;

/// <summary>
/// Manages LSP navigation: go-to-definition, go-to-implementation, find-references,
/// and navigation history (navigate-back).
/// </summary>
internal class LspNavigationManager
{
    private static void LogError(string context, Exception ex) =>
        DiagnosticLog.Error("lsp-coord", context, ex);

    private readonly EditorManager _editorManager;
    private readonly ConcurrentQueue<Action> _pendingUiActions;
    private readonly LspPortalManager _portalManager;

    private readonly Stack<(string FilePath, int Line, int Col)> _navHistory = new();

    public LspNavigationManager(
        EditorManager editorManager,
        ConcurrentQueue<Action> pendingUiActions,
        LspPortalManager portalManager)
    {
        _editorManager = editorManager;
        _pendingUiActions = pendingUiActions;
        _portalManager = portalManager;
    }

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

    public async Task ShowGoToDefinitionAsync(LspClient lsp)
    {
        if (_editorManager.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var locations = await lsp.DefinitionAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        _pendingUiActions.Enqueue(() =>
        {
            if (locations.Count == 0)
            {
                _portalManager.ShowTransientTooltip("No definition found at current position.");
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
                _portalManager.ShowLocationPortal(LocationsToEntries(locations), NavigateToLocation);
            }
        });
    }

    public async Task ShowFindReferencesAsync(LspClient lsp)
    {
        if (_editorManager.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var locations = await lsp.ReferencesAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        _pendingUiActions.Enqueue(() =>
        {
            if (locations.Count == 0)
            {
                _portalManager.ShowTransientTooltip("No references found at cursor.");
                return;
            }

            _portalManager.ShowLocationPortal(LocationsToEntries(locations), NavigateToLocation);
        });
    }

    public async Task ShowGoToImplementationAsync(LspClient lsp)
    {
        if (_editorManager.CurrentEditor == null) return;
        var editor = _editorManager.CurrentEditor;
        var path = _editorManager.CurrentFilePath;
        if (path == null) return;

        var locations = await lsp.ImplementationAsync(path, editor.CurrentLine - 1, editor.CurrentColumn - 1);
        _pendingUiActions.Enqueue(() =>
        {
            if (locations.Count == 0)
            {
                _portalManager.ShowTransientTooltip("No implementation found at cursor.");
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
                _portalManager.ShowLocationPortal(LocationsToEntries(locations), NavigateToLocation);
            }
        });
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

    private static string? TryReadLineFromFile(string filePath, int lineIndex)
    {
        try
        {
            var content = FileService.ReadFile(filePath);
            var lines = content.Split('\n');
            if (lineIndex >= 0 && lineIndex < lines.Length)
                return lines[lineIndex].Trim();
        }
        catch (Exception ex) { LogError("TryReadLineFromFile", ex); }
        return null;
    }
}
