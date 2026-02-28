using SharpConsoleUI.Controls;

namespace DotNetIDE;

internal class WorkspaceStateManager
{
    private readonly WorkspaceService _workspaceService;
    private readonly EditorManager _editorManager;
    private readonly ExplorerPanel _explorer;
    private readonly OutputPanel _outputPanel;

    public WorkspaceStateManager(
        WorkspaceService workspaceService,
        EditorManager editorManager,
        ExplorerPanel explorer,
        OutputPanel outputPanel)
    {
        _workspaceService = workspaceService;
        _editorManager = editorManager;
        _explorer = explorer;
        _outputPanel = outputPanel;
    }

    /// <summary>
    /// Captures current layout and editor state into the workspace state.
    /// The caller must provide current layout visibility state via the snapshot.
    /// </summary>
    public void CaptureWorkspaceState(LayoutSnapshot layout)
    {
        var state = _workspaceService.State;

        // Layout visibility
        state.ExplorerVisible = layout.ExplorerVisible;
        state.OutputVisible = layout.OutputVisible;
        state.SidePanelVisible = layout.SidePanelVisible;
        state.SplitRatio = layout.SplitRatio;
        state.ExplorerColumnWidth = layout.ExplorerColumnWidth;
        state.SidePanelColumnWidth = layout.SidePanelColumnWidth;

        // Wrap mode
        state.WrapMode = _editorManager.WrapMode.ToString();

        // Open files
        state.OpenFiles.Clear();
        int tabCount = _editorManager.TabControl.TabCount;
        int activeIdx = _editorManager.TabControl.ActiveTabIndex;
        for (int i = 0; i < tabCount; i++)
        {
            var filePath = _editorManager.GetTabFilePath(i);
            if (filePath == null) continue;
            var cursor = _editorManager.GetTabCursor(i);
            state.OpenFiles.Add(new WorkspaceFile
            {
                Path = _workspaceService.ToRelativePath(filePath),
                CursorLine = cursor?.Line ?? 1,
                CursorColumn = cursor?.Column ?? 1,
                IsActive = i == activeIdx
            });
        }

        // Explorer expanded paths
        state.ExpandedPaths.Clear();
        CollectExpandedPaths(_explorer.Tree.RootNodes, state.ExpandedPaths);

        // Explorer selected path
        state.SelectedExplorerPath = null;
        var selectedPath = _explorer.GetSelectedPath();
        if (selectedPath != null)
            state.SelectedExplorerPath = _workspaceService.ToRelativePath(selectedPath);

        // Output tab
        state.ActiveOutputTab = _outputPanel.TabControl.ActiveTabIndex;
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
        var state = _workspaceService.State;
        var snapshot = new LayoutSnapshot();

        // Determine which toggles are needed
        snapshot.NeedToggleExplorer = !state.ExplorerVisible && currentExplorerVisible;
        snapshot.NeedToggleOutput = !state.OutputVisible && currentOutputVisible;
        snapshot.NeedToggleSidePanel = state.SidePanelVisible && !currentSidePanelVisible;

        snapshot.ExplorerColumnWidth = state.ExplorerColumnWidth;
        snapshot.SidePanelColumnWidth = state.SidePanelColumnWidth;
        snapshot.SplitRatio = state.SplitRatio;
        snapshot.WrapMode = state.WrapMode;

        // Open files
        int activeTabIndex = -1;
        for (int i = 0; i < state.OpenFiles.Count; i++)
        {
            var entry = state.OpenFiles[i];
            var absolutePath = _workspaceService.ToAbsolutePath(entry.Path);
            if (!File.Exists(absolutePath)) continue;

            _editorManager.OpenFile(absolutePath);

            var editor = _editorManager.CurrentEditor;
            if (editor != null && (entry.CursorLine > 1 || entry.CursorColumn > 1))
            {
                editor.SetLogicalCursorPosition(new System.Drawing.Point(
                    entry.CursorColumn - 1, entry.CursorLine - 1));
            }

            if (entry.IsActive)
                activeTabIndex = _editorManager.TabControl.ActiveTabIndex;
        }

        if (activeTabIndex >= 0)
            _editorManager.TabControl.ActiveTabIndex = activeTabIndex;

        // Explorer expanded paths
        foreach (var relativePath in state.ExpandedPaths)
        {
            var absolutePath = _workspaceService.ToAbsolutePath(relativePath);
            var node = _explorer.Tree.FindNodeByTag(absolutePath);
            if (node != null) node.IsExpanded = true;
        }

        // Explorer selected path
        if (state.SelectedExplorerPath != null)
        {
            var absolutePath = _workspaceService.ToAbsolutePath(state.SelectedExplorerPath);
            var node = _explorer.Tree.FindNodeByTag(absolutePath);
            if (node != null) _explorer.Tree.SelectNode(node);
        }

        // Output tab
        if (state.ActiveOutputTab >= 0
            && state.ActiveOutputTab < _outputPanel.TabControl.TabCount)
        {
            _outputPanel.TabControl.ActiveTabIndex = state.ActiveOutputTab;
        }

        return snapshot;
    }

    public void Load() => _workspaceService.Load();
    public void Save() => _workspaceService.Save();

    private void CollectExpandedPaths(IEnumerable<SharpConsoleUI.Controls.TreeNode> nodes, List<string> paths)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Tag is string fullPath)
            {
                paths.Add(_workspaceService.ToRelativePath(fullPath));
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
