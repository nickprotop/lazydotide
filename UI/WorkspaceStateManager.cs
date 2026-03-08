using SharpConsoleUI.Controls;

namespace DotNetIDE;

internal class WorkspaceStateManager
{
    private readonly AppContext _ctx;

    public DebugCoordinator? DebugCoordinator { get; set; }

    public WorkspaceStateManager(AppContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Captures current layout and editor state into the workspace state.
    /// The caller must provide current layout visibility state via the snapshot.
    /// </summary>
    public void CaptureWorkspaceState(LayoutSnapshot layout)
    {
        var state = _ctx.WorkspaceService.State;

        // Layout visibility
        state.ExplorerVisible = layout.ExplorerVisible;
        state.OutputVisible = layout.OutputVisible;
        state.SidePanelVisible = layout.SidePanelVisible;
        state.SplitRatio = layout.SplitRatio;
        state.ExplorerColumnWidth = layout.ExplorerColumnWidth;
        state.SidePanelColumnWidth = layout.SidePanelColumnWidth;

        // Wrap mode
        state.WrapMode = _ctx.EditorManager.WrapMode.ToString();

        // Open files
        state.OpenFiles.Clear();
        int tabCount = _ctx.EditorManager.TabControl.TabCount;
        int activeIdx = _ctx.EditorManager.TabControl.ActiveTabIndex;
        for (int i = 0; i < tabCount; i++)
        {
            var filePath = _ctx.EditorManager.GetTabFilePath(i);
            if (filePath == null) continue;
            var cursor = _ctx.EditorManager.GetTabCursor(i);
            state.OpenFiles.Add(new WorkspaceFile
            {
                Path = _ctx.WorkspaceService.ToRelativePath(filePath),
                CursorLine = cursor?.Line ?? 1,
                CursorColumn = cursor?.Column ?? 1,
                IsActive = i == activeIdx
            });
        }

        // Explorer expanded paths
        state.ExpandedPaths.Clear();
        CollectExpandedPaths(_ctx.Explorer.Tree.RootNodes, state.ExpandedPaths);

        // Explorer selected path
        state.SelectedExplorerPath = null;
        var selectedPath = _ctx.Explorer.GetSelectedPath();
        if (selectedPath != null)
            state.SelectedExplorerPath = _ctx.WorkspaceService.ToRelativePath(selectedPath);

        // Output tab
        state.ActiveOutputTab = _ctx.OutputPanel.TabControl.ActiveTabIndex;

        // Breakpoints
        DebugCoordinator?.SaveBreakpoints(state, _ctx.WorkspaceService);
    }

    /// <summary>
    /// Restores workspace state. Returns the layout adjustments needed.
    /// The caller is responsible for applying the layout toggles and window sizes.
    /// </summary>
    public LayoutSnapshot RestoreWorkspaceState(
        bool currentExplorerVisible,
        bool currentOutputVisible,
        bool currentSidePanelVisible)
    {
        var state = _ctx.WorkspaceService.State;
        var snapshot = new LayoutSnapshot();

        // Determine which toggles are needed
        snapshot.NeedToggleExplorer = !state.ExplorerVisible && currentExplorerVisible;
        snapshot.NeedToggleOutput = !state.OutputVisible && currentOutputVisible;
        snapshot.NeedToggleSidePanel = state.SidePanelVisible && !currentSidePanelVisible;

        snapshot.ExplorerColumnWidth = state.ExplorerColumnWidth;
        snapshot.SidePanelColumnWidth = state.SidePanelColumnWidth;
        snapshot.SplitRatio = state.SplitRatio;
        snapshot.WrapMode = state.WrapMode;

        // Load breakpoints BEFORE opening files so RegisterGutter can apply them
        DebugCoordinator?.LoadBreakpoints(state, _ctx.WorkspaceService);

        // Open files
        int activeTabIndex = -1;
        for (int i = 0; i < state.OpenFiles.Count; i++)
        {
            var entry = state.OpenFiles[i];
            var absolutePath = _ctx.WorkspaceService.ToAbsolutePath(entry.Path);
            if (!File.Exists(absolutePath)) continue;

            _ctx.EditorManager.OpenFile(absolutePath);

            var editor = _ctx.EditorManager.CurrentEditor;
            if (editor != null && (entry.CursorLine > 1 || entry.CursorColumn > 1))
            {
                editor.SetLogicalCursorPosition(new System.Drawing.Point(
                    entry.CursorColumn - 1, entry.CursorLine - 1));
            }

            if (entry.IsActive)
                activeTabIndex = _ctx.EditorManager.TabControl.ActiveTabIndex;
        }

        if (activeTabIndex >= 0)
            _ctx.EditorManager.TabControl.ActiveTabIndex = activeTabIndex;

        // Explorer expanded paths
        foreach (var relativePath in state.ExpandedPaths)
        {
            var absolutePath = _ctx.WorkspaceService.ToAbsolutePath(relativePath);
            var node = _ctx.Explorer.Tree.FindNodeByTag(absolutePath);
            if (node != null) node.IsExpanded = true;
        }

        // Explorer selected path
        if (state.SelectedExplorerPath != null)
        {
            var absolutePath = _ctx.WorkspaceService.ToAbsolutePath(state.SelectedExplorerPath);
            var node = _ctx.Explorer.Tree.FindNodeByTag(absolutePath);
            if (node != null) _ctx.Explorer.Tree.SelectNode(node);
        }

        // Output tab
        if (state.ActiveOutputTab >= 0
            && state.ActiveOutputTab < _ctx.OutputPanel.TabControl.TabCount)
        {
            _ctx.OutputPanel.TabControl.ActiveTabIndex = state.ActiveOutputTab;
        }

        return snapshot;
    }

    public void Load() => _ctx.WorkspaceService.Load();
    public void Save() => _ctx.WorkspaceService.Save();

    private void CollectExpandedPaths(IEnumerable<SharpConsoleUI.Controls.TreeNode> nodes, List<string> paths)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Tag is string fullPath)
            {
                paths.Add(_ctx.WorkspaceService.ToRelativePath(fullPath));
            }
            if (node.Children.Count > 0)
                CollectExpandedPaths(node.Children, paths);
        }
    }
}

/// <summary>
/// Snapshot of layout state for capture/restore.
/// </summary>
internal class LayoutSnapshot
{
    // For capture
    public bool ExplorerVisible { get; set; }
    public bool OutputVisible { get; set; }
    public bool SidePanelVisible { get; set; }
    public double SplitRatio { get; set; }
    public int ExplorerColumnWidth { get; set; }
    public int SidePanelColumnWidth { get; set; }

    // For restore - which toggles are needed
    public bool NeedToggleExplorer { get; set; }
    public bool NeedToggleOutput { get; set; }
    public bool NeedToggleSidePanel { get; set; }

    // Wrap mode
    public string? WrapMode { get; set; }
}
