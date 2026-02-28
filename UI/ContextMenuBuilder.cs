using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;

namespace DotNetIDE;

internal class ContextMenuBuilder
{
    private readonly GitService _gitService;
    private readonly ProjectService _projectService;
    private readonly EditorManager _editorManager;
    private readonly ExplorerPanel _explorer;
    private readonly SidePanel _sidePanel;
    private readonly GitCoordinator _gitOps;
    private readonly LspCoordinator _lspCoord;

    private ContextMenuPortal? _contextMenuPortal;
    private LayoutNode? _contextMenuPortalNode;
    private IWindowControl? _contextMenuOwner;

    private Window? _mainWindow;
    private ConsoleWindowSystem? _ws;

    public FileOperationDelegates? FileOps { get; set; }

    public ContextMenuBuilder(
        GitService gitService,
        ProjectService projectService,
        EditorManager editorManager,
        ExplorerPanel explorer,
        SidePanel sidePanel,
        GitCoordinator gitOps,
        LspCoordinator lspCoord)
    {
        _gitService = gitService;
        _projectService = projectService;
        _editorManager = editorManager;
        _explorer = explorer;
        _sidePanel = sidePanel;
        _gitOps = gitOps;
        _lspCoord = lspCoord;
    }

    public void SetMainWindow(Window mainWindow, ConsoleWindowSystem ws)
    {
        _mainWindow = mainWindow;
        _ws = ws;
    }

    public bool ProcessPreviewKey(KeyPressedEventArgs e)
    {
        if (_contextMenuPortal != null)
        {
            _contextMenuPortal.ProcessKey(e.KeyInfo);
            e.Handled = true;
            return true;
        }
        return false;
    }

    public void DismissContextMenu()
    {
        if (_contextMenuPortalNode != null && _mainWindow != null && _contextMenuOwner != null)
        {
            _mainWindow.RemovePortal(_contextMenuOwner, _contextMenuPortalNode);
            _contextMenuPortalNode = null;
            _contextMenuPortal = null;
            _contextMenuOwner = null;
        }
    }

    public void ShowContextMenu(List<ContextMenuItem> items, int anchorX, int anchorY, IWindowControl? owner = null)
    {
        DismissContextMenu();
        if (_mainWindow == null || items.Count == 0) return;

        // Use provided owner or fall back to current editor or explorer
        var portalOwner = owner ?? _editorManager.CurrentEditor ?? _explorer.Control;
        if (portalOwner == null) return;

        var portal = new ContextMenuPortal(items, anchorX, anchorY,
            _mainWindow.Width, _mainWindow.Height);
        portal.Container = _mainWindow;
        _contextMenuPortal = portal;
        _contextMenuOwner = portalOwner;
        _contextMenuPortalNode = _mainWindow.CreatePortal(portalOwner, portal);

        portal.ItemSelected += (_, item) =>
        {
            DismissContextMenu();
            item.Action?.Invoke();
        };

        portal.Dismissed += (_, _) =>
        {
            DismissContextMenu();
        };

        // Library auto-dismisses portal on outside click; clean up local state
        portal.DismissRequested += (_, _) =>
        {
            _contextMenuPortalNode = null;
            _contextMenuPortal = null;
            _contextMenuOwner = null;
        };
    }

    public void HandleGitPanelContextMenu(object? sender, GitContextMenuEventArgs e)
    {
        var items = new List<ContextMenuItem>();
        switch (e.Target)
        {
            case GitContextMenuTarget.StagedFile:
                items.Add(new("Unstage", null, () => _ = _gitOps.GitUnstageFileAsync(e.FilePath!)));
                items.Add(new("Open File", null, () => _editorManager.OpenFile(e.FilePath!)));
                items.Add(new("Diff", null, () => _ = _gitOps.GitShowDiffAsync(e.FilePath!)));
                items.Add(new("-"));
                items.Add(new("File Log", null, () => _ = _gitOps.GitShowFileLogAsync(e.FilePath!)));
                items.Add(new("Blame", null, () => _ = _gitOps.GitShowBlameAsync(e.FilePath!)));
                break;
            case GitContextMenuTarget.UnstagedFile:
                items.Add(new("Stage", null, () => _ = _gitOps.GitStageFileAsync(e.FilePath!)));
                items.Add(new("Open File", null, () => _editorManager.OpenFile(e.FilePath!)));
                items.Add(new("Diff", null, () => _ = _gitOps.GitShowDiffAsync(e.FilePath!)));
                items.Add(new("-"));
                items.Add(new("Discard Changes", null, () => _ = _gitOps.GitDiscardFileAsync(e.FilePath!)));
                items.Add(new("-"));
                items.Add(new("File Log", null, () => _ = _gitOps.GitShowFileLogAsync(e.FilePath!)));
                items.Add(new("Blame", null, () => _ = _gitOps.GitShowBlameAsync(e.FilePath!)));
                break;
            case GitContextMenuTarget.CommitEntry:
                items.Add(new("Copy SHA", null, () => ClipboardHelper.SetText(e.LogEntry!.Sha)));
                items.Add(new("Show Full Log", null, () => _ = _gitOps.GitShowLogAsync()));
                break;
        }
        ShowContextMenu(items, e.ScreenX, e.ScreenY, _sidePanel.TabControl);
    }

    public void ShowGitMoreMenu()
    {
        var items = new List<ContextMenuItem>
        {
            new("Stash…", null, () => _ = _gitOps.GitStashAsync()),
            new("Stash Pop", null, () => _ = _gitOps.GitStashPopAsync()),
            new("-"),
            new("Switch Branch…", null, () => _ = _gitOps.GitSwitchBranchAsync()),
            new("New Branch…", null, () => _ = _gitOps.GitNewBranchAsync()),
            new("-"),
            new("Discard All Changes", null, () => _ = _gitOps.GitDiscardAllAsync()),
            new("-"),
            new("Diff All", null, () => _ = _gitOps.GitShowDiffAllAsync()),
            new("Full Log", null, () => _ = _gitOps.GitShowLogAsync()),
        };
        var sp = _sidePanel.TabControl;
        ShowContextMenu(items, sp.ActualX + 2, sp.ActualY + 3, sp);
    }

    public void HandleExplorerContextMenu(object? sender, (string Path, System.Drawing.Point ScreenPosition, bool IsDirectory) e)
    {
        _ = BuildExplorerContextMenuAsync(e);
    }

    private async Task BuildExplorerContextMenuAsync((string Path, System.Drawing.Point ScreenPosition, bool IsDirectory) e)
    {
        var items = new List<ContextMenuItem>();
        var path = e.Path;
        var parentDir = e.IsDirectory ? path : Path.GetDirectoryName(path);

        items.Add(new ContextMenuItem("New File", "Ctrl+N",
            () => { if (parentDir != null) _ = FileOps?.HandleNewFileAsync?.Invoke(parentDir) ?? Task.CompletedTask; }));
        items.Add(new ContextMenuItem("New Folder", "Ctrl+Shift+N",
            () => { if (parentDir != null) _ = FileOps?.HandleNewFolderAsync?.Invoke(parentDir) ?? Task.CompletedTask; }));
        items.Add(new ContextMenuItem("-"));
        items.Add(new ContextMenuItem("Rename", "F2",
            () => _ = FileOps?.HandleRenameAsync?.Invoke(path) ?? Task.CompletedTask));
        items.Add(new ContextMenuItem("Delete", "Del",
            () => _ = FileOps?.HandleDeleteAsync?.Invoke(path) ?? Task.CompletedTask));

        // Git section — query file status
        if (!e.IsDirectory)
        {
            var isStaged = await _gitService.IsStagedAsync(_projectService.RootPath, path);
            var hasChanges = await _gitService.HasWorkingChangesAsync(_projectService.RootPath, path);
            var gitStatus = await _gitService.GetFileStatusAsync(_projectService.RootPath, path);

            if (isStaged || hasChanges || gitStatus != null)
            {
                items.Add(new ContextMenuItem("-"));
                if (hasChanges || gitStatus == GitFileStatus.Untracked)
                    items.Add(new ContextMenuItem("Git: Stage", null, () => _ = _gitOps.GitStageFileAsync(path)));
                if (isStaged)
                    items.Add(new ContextMenuItem("Git: Unstage", null, () => _ = _gitOps.GitUnstageFileAsync(path)));
                if (hasChanges || isStaged)
                    items.Add(new ContextMenuItem("Git: Diff", null, () => _ = _gitOps.GitShowDiffAsync(path)));
                if (hasChanges)
                    items.Add(new ContextMenuItem("Git: Discard Changes", null, () => _ = _gitOps.GitDiscardFileAsync(path)));
            }

            // Always show log/blame for tracked files
            if (gitStatus != GitFileStatus.Untracked || gitStatus == null)
            {
                if (!items.Any(i => i.Label.StartsWith("Git:")))
                    items.Add(new ContextMenuItem("-"));
                items.Add(new ContextMenuItem("Git: Log", null, () => _ = _gitOps.GitShowFileLogAsync(path)));
                items.Add(new ContextMenuItem("Git: Blame", null, () => _ = _gitOps.GitShowBlameAsync(path)));
            }

            // Gitignore
            var inGitignore = await _gitService.IsInGitignoreAsync(_projectService.RootPath, path);
            if (inGitignore)
                items.Add(new ContextMenuItem("Git: Remove from .gitignore", null, () => _ = _gitOps.GitRemoveFromGitignoreAsync(path)));
            else
                items.Add(new ContextMenuItem("Git: Add to .gitignore", null, () => _ = _gitOps.GitAddToGitignoreAsync(path, false)));
        }
        else
        {
            // Directory-level git
            items.Add(new ContextMenuItem("-"));
            items.Add(new ContextMenuItem("Git: Stage Folder", null, () => _ = _gitOps.GitStageFileAsync(path)));
            items.Add(new ContextMenuItem("Git: Unstage Folder", null, () => _ = _gitOps.GitUnstageFileAsync(path)));

            // Gitignore
            var inGitignore = await _gitService.IsInGitignoreAsync(_projectService.RootPath, path);
            if (inGitignore)
                items.Add(new ContextMenuItem("Git: Remove from .gitignore", null, () => _ = _gitOps.GitRemoveFromGitignoreAsync(path)));
            else
                items.Add(new ContextMenuItem("Git: Add to .gitignore", null, () => _ = _gitOps.GitAddToGitignoreAsync(path, true)));
        }

        items.Add(new ContextMenuItem("-"));
        items.Add(new ContextMenuItem("Copy Path", null,
            () => ClipboardHelper.SetText(path)));
        items.Add(new ContextMenuItem("Copy Relative Path", null,
            () => CopyRelativePath(path)));

        if (e.IsDirectory)
        {
            items.Add(new ContextMenuItem("-"));
            items.Add(new ContextMenuItem("Refresh", "F5",
                () => _ = FileOps?.RefreshExplorerAndGitAsync?.Invoke() ?? Task.CompletedTask));
        }

        ShowContextMenu(items, e.ScreenPosition.X, e.ScreenPosition.Y, _explorer.Control);
    }

    public void HandleTabContextMenu(object? sender, (string FilePath, System.Drawing.Point ScreenPosition) e)
    {
        _ = BuildTabContextMenuAsync(e);
    }

    private async Task BuildTabContextMenuAsync((string FilePath, System.Drawing.Point ScreenPosition) e)
    {
        var filePath = e.FilePath;
        var tabIndex = _editorManager.GetTabIndexForPath(filePath);

        var items = new List<ContextMenuItem>
        {
            new("Close", "Ctrl+W", () =>
            {
                if (tabIndex >= 0) _editorManager.CloseTabAt(tabIndex);
            }),
            new("Close Others", null, () => _editorManager.CloseOthers(filePath)),
            new("Close All", null, () => _editorManager.CloseAll()),
            new("-"),
            new("Save", "Ctrl+S", () =>
            {
                if (tabIndex >= 0) _editorManager.SaveTabAt(tabIndex);
            }),
        };

        // Git section for the tab's file
        if (!filePath.StartsWith(IdeConstants.ReadOnlyTabPrefix))
        {
            var isStaged = await _gitService.IsStagedAsync(_projectService.RootPath, filePath);
            var hasChanges = await _gitService.HasWorkingChangesAsync(_projectService.RootPath, filePath);

            if (isStaged || hasChanges)
            {
                items.Add(new ContextMenuItem("-"));
                if (hasChanges)
                    items.Add(new ContextMenuItem("Git: Stage", null, () => _ = _gitOps.GitStageFileAsync(filePath)));
                if (isStaged)
                    items.Add(new ContextMenuItem("Git: Unstage", null, () => _ = _gitOps.GitUnstageFileAsync(filePath)));
                items.Add(new ContextMenuItem("Git: Diff", null, () => _ = _gitOps.GitShowDiffAsync(filePath)));
                if (hasChanges)
                    items.Add(new ContextMenuItem("Git: Discard Changes", null, () => _ = _gitOps.GitDiscardFileAsync(filePath)));
            }
        }

        items.Add(new ContextMenuItem("-"));
        items.Add(new ContextMenuItem("Copy Path", null, () => ClipboardHelper.SetText(filePath)));
        items.Add(new ContextMenuItem("Copy Relative Path", null, () => CopyRelativePath(filePath)));

        ShowContextMenu(items, e.ScreenPosition.X, e.ScreenPosition.Y, _editorManager.TabControl);
    }

    public void HandleEditorContextMenu(object? sender, (string? FilePath, System.Drawing.Point ScreenPosition) e)
    {
        var editor = _editorManager.CurrentEditor;
        if (editor == null) return;

        bool hasLsp = _lspCoord.HasLsp;

        var items = new List<ContextMenuItem>
        {
            new("Cut", "Ctrl+X", () => editor.ProcessKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, true))),
            new("Copy", "Ctrl+C", () => editor.ProcessKey(new ConsoleKeyInfo('c', ConsoleKey.C, false, false, true))),
            new("Paste", "Ctrl+V", () => editor.ProcessKey(new ConsoleKeyInfo('v', ConsoleKey.V, false, false, true))),
            new("-"),
            new("Select All", "Ctrl+A", () => editor.ProcessKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, true))),
            new("-"),
            new("Go to Definition", "F12", () => _ = _lspCoord.ShowGoToDefinitionAsync(), Enabled: hasLsp),
            new("Find References", "Shift+F12", () => _ = _lspCoord.ShowFindReferencesAsync(), Enabled: hasLsp),
            new("Rename Symbol", "Ctrl+F2", () => _ = _lspCoord.ShowRenameAsync(_ws!), Enabled: hasLsp),
            new("Hover Info", "Ctrl+K", () => _ = _lspCoord.ShowHoverAsync(), Enabled: hasLsp),
        };

        // Git items for the current file
        if (e.FilePath != null && !e.FilePath.StartsWith(IdeConstants.ReadOnlyTabPrefix))
        {
            var filePath = e.FilePath;
            items.Add(new ContextMenuItem("-"));
            items.Add(new ContextMenuItem("Git: Diff", null, () => _ = _gitOps.GitShowDiffAsync(filePath)));
            items.Add(new ContextMenuItem("Git: Blame", null, () => _ = _gitOps.GitShowBlameAsync(filePath)));
        }

        ShowContextMenu(items, e.ScreenPosition.X, e.ScreenPosition.Y, editor);
    }

    private void CopyRelativePath(string fullPath)
    {
        var root = _projectService.RootPath;
        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ClipboardHelper.SetText(relative);
        }
        else
        {
            ClipboardHelper.SetText(fullPath);
        }
    }
}
