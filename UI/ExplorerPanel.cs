using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using TreeNode = SharpConsoleUI.Controls.TreeNode;
using Color = SharpConsoleUI.Color;

namespace DotNetIDE;

public class ExplorerPanel
{
    private readonly ConsoleWindowSystem _ws;
    private readonly ProjectService _projectService;
    private readonly TreeControl _tree;
    private readonly ScrollablePanelControl _panel;
    private Dictionary<string, GitFileStatus> _gitStatuses = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, bool>? _isPathIgnored;
    private string? _repoWorkingDir;

    // Directory nodes whose children have been read from disk. Unloaded directories
    // carry a single placeholder child so the tree still shows an expander.
    private readonly HashSet<TreeNode> _loadedDirs = new();
    private const string PlaceholderLabel = "[grey35]…[/]";

    public event EventHandler<string>? FileOpenRequested;
    public event EventHandler<string>? NewFileRequested;
    public event EventHandler<string>? NewFolderRequested;
    public event EventHandler<string>? RenameRequested;
    public event EventHandler<string>? DeleteRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<(string Path, Point ScreenPosition, bool IsDirectory)>? ContextMenuRequested;

    public ExplorerPanel(ConsoleWindowSystem ws, ProjectService projectService)
    {
        _ws = ws;
        _projectService = projectService;

        _tree = new TreeControl
        {
            Guide = TreeGuide.Line,
            HighlightBackgroundColor = Color.SteelBlue,
            HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            SelectOnRightClick = true
        };

        _panel = new ScrollablePanelControl
        {
            ShowScrollbar = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        _panel.AddControl(_tree);

        _tree.NodeActivated += OnNodeActivated;
        _tree.NodeExpandCollapse += OnNodeExpandCollapse;
        _tree.MouseRightClick += OnTreeRightClick;
        BuildTree();
    }

    public IWindowControl Control => _panel;
    public TreeControl Tree => _tree;
    public bool HasFocus => _tree.HasFocus;

    public string? GetSelectedPath()
    {
        return _tree.SelectedNode?.Tag as string;
    }

    public bool IsSelectedDirectory()
    {
        var path = GetSelectedPath();
        return path != null && Directory.Exists(path);
    }

    public void UpdateGitStatuses(Dictionary<string, GitFileStatus> statuses, string? repoWorkingDir = null, Func<string, bool>? isPathIgnored = null)
    {
        _gitStatuses = statuses;
        _isPathIgnored = isPathIgnored;
        _repoWorkingDir = repoWorkingDir;
        Refresh();
    }

    /// <summary>
    /// Brings the tree back in line with the filesystem and the current git statuses, in place.
    /// </summary>
    /// <remarks>
    /// This runs far more often than a user-initiated refresh: the file watcher calls it on every
    /// structure change AND every git change, so an ordinary save used to tear the whole tree
    /// down and rebuild it. That lost the scroll offset, briefly emptied the control, and made
    /// every open directory reload from disk.
    ///
    /// Now only what actually differs is touched. Nodes keep their identity, so expansion,
    /// selection and scroll position survive by construction rather than by being captured and
    /// restored around a rebuild. A directory nobody has opened is not read at all.
    /// </remarks>
    public void Refresh()
    {
        var roots = _tree.RootNodes.ToList();
        if (roots.Count == 0 || roots[0].Tag as string != _projectService.RootPath)
        {
            // First build, or the workspace root changed: nothing to reconcile against.
            Rebuild();
            return;
        }

        SyncNode(roots[0]);
    }

    /// <summary>Full teardown and rebuild. Only for a genuinely different tree.</summary>
    private void Rebuild()
    {
        _tree.Clear();
        _loadedDirs.Clear();
        BuildTree();
    }

    /// <summary>
    /// Reconciles one directory node against the filesystem, then recurses into the children
    /// that are loaded.
    /// </summary>
    /// <remarks>
    /// An unloaded directory is left alone: its contents are unknown to the user, so reading
    /// them here would be work nobody asked for. Its placeholder is kept in step with whether
    /// the directory still has entries, so the expander does not lie.
    /// </remarks>
    private void SyncNode(TreeNode node)
    {
        if (node.Tag is not string dirPath) return;

        // A node whose children were never read keeps its placeholder; just refresh the label
        // in case its own git badge changed.
        if (!_loadedDirs.Contains(node))
        {
            SyncPlaceholder(node, dirPath);
            return;
        }

        if (!Directory.Exists(dirPath))
        {
            node.ClearChildren();
            _loadedDirs.Remove(node);
            return;
        }

        var wanted = _projectService.ListDirectory(dirPath);

        // Snapshot before mutating: TreeNode.Children is AsReadOnly(), a LIVE view of the
        // backing list, so enumerating it while adding or removing corrupts the walk.
        var existing = node.Children.ToList();
        var byPath = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in existing)
        {
            if (child.Tag is string p && !byPath.ContainsKey(p))
                byPath[p] = child;
        }

        var wantedPaths = new HashSet<string>(wanted.Select(w => w.FullPath), StringComparer.OrdinalIgnoreCase);

        // Gone from disk.
        foreach (var child in existing)
        {
            if (child.Tag is not string p)
            {
                // The placeholder of a directory that has since been loaded.
                node.RemoveChild(child);
                continue;
            }
            if (!wantedPaths.Contains(p))
            {
                Forget(child);
                node.RemoveChild(child);
            }
        }

        // Added, or still present and possibly restyled. Rebuilt in the order ListDirectory
        // returns (directories then files, each sorted) so insertion order stays correct
        // without needing to compute a minimal edit script.
        var ordered = new List<TreeNode>(wanted.Count);
        foreach (var fileNode in wanted)
        {
            if (byPath.TryGetValue(fileNode.FullPath, out var child))
            {
                ApplyNodeStyle(child, fileNode);
                ordered.Add(child);
            }
            else
            {
                ordered.Add(CreateNode(fileNode));
            }
        }

        // Re-seat the children only when the order actually changed, because ClearChildren plus
        // AddChild is what loses expansion state on the nodes being re-added.
        if (!SameOrder(node.Children.ToList(), ordered))
        {
            node.ClearChildren();
            foreach (var child in ordered)
                node.AddChild(child);
        }

        foreach (var child in ordered)
        {
            if (child.Tag is string p && Directory.Exists(p))
                SyncNode(child);
        }
    }

    private static bool SameOrder(List<TreeNode> a, List<TreeNode> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>Drops a subtree from the loaded set, so a directory that comes back is re-read.</summary>
    private void Forget(TreeNode node)
    {
        _loadedDirs.Remove(node);
        foreach (var child in node.Children.ToList())
            Forget(child);
    }

    /// <summary>
    /// Keeps an unloaded directory's placeholder in step with whether it still has entries.
    /// </summary>
    private void SyncPlaceholder(TreeNode node, string dirPath)
    {
        bool hasEntries = Directory.Exists(dirPath) && ProjectService.HasEntries(dirPath);
        var children = node.Children.ToList();

        if (hasEntries && children.Count == 0)
            node.AddChild(PlaceholderLabel).TextColor = Color.Grey35;
        else if (!hasEntries && children.Count > 0)
            node.ClearChildren();
    }

    public bool HandleKey(ConsoleKey key, ConsoleModifiers mods)
    {
        if (!_tree.HasFocus) return false;

        var selectedPath = GetSelectedPath();
        if (selectedPath == null) return false;

        if (key == ConsoleKey.F2 && mods == 0)
        {
            RenameRequested?.Invoke(this, selectedPath);
            return true;
        }
        if (key == ConsoleKey.Delete && mods == 0)
        {
            DeleteRequested?.Invoke(this, selectedPath);
            return true;
        }
        if (key == ConsoleKey.N && mods == ConsoleModifiers.Control)
        {
            var dir = Directory.Exists(selectedPath) ? selectedPath : Path.GetDirectoryName(selectedPath);
            if (dir != null) NewFileRequested?.Invoke(this, dir);
            return true;
        }
        if (key == ConsoleKey.N && mods == (ConsoleModifiers.Control | ConsoleModifiers.Shift))
        {
            var dir = Directory.Exists(selectedPath) ? selectedPath : Path.GetDirectoryName(selectedPath);
            if (dir != null) NewFolderRequested?.Invoke(this, dir);
            return true;
        }
        if (key == ConsoleKey.F5 && mods == 0)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Expands the directory node for <paramref name="path"/>, loading its children if needed.
    /// The parent must already be loaded, so callers expand ancestors first.
    /// </summary>
    public void ExpandPath(string path)
    {
        var node = _tree.FindNodeByTag(path);
        if (node != null) ExpandNode(node);
    }

    private void ExpandNode(TreeNode node)
    {
        EnsureChildrenLoaded(node);
        node.IsExpanded = true;
    }

    private void BuildTree()
    {
        var rootPath = _projectService.RootPath;
        var name = Path.GetFileName(rootPath) is { Length: > 0 } n ? n : rootPath;
        var root = AddNode(new FileNode(name, rootPath, true, new List<FileNode>()), null);
        EnsureChildrenLoaded(root);
    }

    private void OnNodeExpandCollapse(object? sender, TreeNodeEventArgs args)
    {
        if (args.Node is { IsExpanded: true } node)
            EnsureChildrenLoaded(node);
    }

    private void EnsureChildrenLoaded(TreeNode node)
    {
        if (node.Tag is not string dirPath || !_loadedDirs.Add(node)) return;
        if (!Directory.Exists(dirPath)) return;

        node.ClearChildren();
        foreach (var child in _projectService.ListDirectory(dirPath))
            AddNode(child, node);
    }

    private TreeNode AddNode(FileNode fileNode, TreeNode? parent)
    {
        TreeNode treeNode;
        if (parent == null)
        {
            treeNode = _tree.AddRootNode(fileNode.Name);
            treeNode.IsExpanded = true;
        }
        else
        {
            treeNode = parent.AddChild(fileNode.Name);
            treeNode.IsExpanded = false;
        }

        ApplyNodeStyle(treeNode, fileNode);

        if (fileNode.IsDirectory && parent != null && ProjectService.HasEntries(fileNode.FullPath))
            treeNode.AddChild(PlaceholderLabel).TextColor = Color.Grey35;

        return treeNode;
    }

    /// <summary>
    /// Builds a detached node for <paramref name="fileNode"/>, for the caller to seat.
    /// </summary>
    private TreeNode CreateNode(FileNode fileNode)
    {
        var treeNode = new TreeNode(fileNode.Name) { IsExpanded = false };
        ApplyNodeStyle(treeNode, fileNode);

        if (fileNode.IsDirectory && ProjectService.HasEntries(fileNode.FullPath))
            treeNode.AddChild(PlaceholderLabel).TextColor = Color.Grey35;

        return treeNode;
    }

    /// <summary>
    /// Writes the label (name plus git badge), tag and colour onto an existing node.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="AddNode"/> so a reconcile can restyle a node it is keeping. The
    /// badge is part of the label, so a file whose git status changed needs its text rewritten
    /// even though nothing about the file itself moved.
    /// </remarks>
    private void ApplyNodeStyle(TreeNode treeNode, FileNode fileNode)
    {
        string label = fileNode.Name;
        string badge = GetGitBadge(fileNode);
        if (!string.IsNullOrEmpty(badge))
            label += badge;

        if (treeNode.Text != label)
            treeNode.Text = label;

        treeNode.Tag = fileNode.FullPath;

        treeNode.TextColor = IsIgnored(fileNode)
            ? Color.Grey35
            : fileNode.IsDirectory
                ? Color.Cyan1
                : GetFileColor(fileNode.Name);
    }

    private bool IsIgnored(FileNode fileNode)
    {
        if (_isPathIgnored == null) return false;
        var relativePath = GetRelativePath(fileNode.FullPath);
        if (relativePath == null) return false;
        return _isPathIgnored(relativePath);
    }

    private string GetGitBadge(FileNode fileNode)
    {
        var relativePath = GetRelativePath(fileNode.FullPath);
        if (relativePath == null) return "";

        // Show ignored badge
        if (IsIgnored(fileNode))
            return "[grey35] I[/]";

        if (_gitStatuses.Count == 0) return "";

        if (!fileNode.IsDirectory)
        {
            if (_gitStatuses.TryGetValue(relativePath, out var status))
            {
                return status switch
                {
                    GitFileStatus.Modified   => "[yellow] M[/]",
                    GitFileStatus.Added      => "[green] A[/]",
                    GitFileStatus.Deleted    => "[red] D[/]",
                    GitFileStatus.Untracked  => "[grey] U[/]",
                    GitFileStatus.Renamed    => "[blue] R[/]",
                    GitFileStatus.Conflicted => "[red bold] ![/]",
                    _ => ""
                };
            }
            return "";
        }

        // For directories: check if any descendant has a status
        var dirPrefix = relativePath.EndsWith('/') ? relativePath : relativePath + "/";
        foreach (var key in _gitStatuses.Keys)
        {
            if (key.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                return "[yellow] \u25cf[/]";
        }
        return "";
    }

    private string? GetRelativePath(string fullPath)
    {
        // Use the repo working directory if available, otherwise fall back to project root
        var basePath = _repoWorkingDir ?? _projectService.RootPath;
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = fullPath[basePath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // LibGit2Sharp uses forward slashes
        return relative.Replace('\\', '/');
    }

    private static Color GetFileColor(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" => Color.LightGreen,
            ".csproj" => Color.Gold1,
            ".sln" => Color.Orange1,
            ".json" => Color.Yellow,
            ".md" => Color.Magenta1,
            _ => Color.White
        };

    private void OnTreeRightClick(object? sender, MouseEventArgs args)
    {
        var node = _tree.SelectedNode;
        var path = node?.Tag as string;
        if (path == null) return;
        bool isDir = Directory.Exists(path);
        // Use the tree control's actual screen position + mouse position for anchor
        int screenX = _tree.ActualX + args.Position.X;
        int screenY = _tree.ActualY + args.Position.Y;
        ContextMenuRequested?.Invoke(this, (path, new Point(screenX, screenY), isDir));
    }

    private void OnNodeActivated(object? sender, TreeNodeEventArgs args)
    {
        if (args.Node?.Tag is string path && File.Exists(path))
            FileOpenRequested?.Invoke(this, path);
    }
}
