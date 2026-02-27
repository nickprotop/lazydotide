using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using Spectre.Console;
using Color = Spectre.Console.Color;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;
using TreeNode = SharpConsoleUI.Controls.TreeNode;

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

    public void Refresh()
    {
        // Capture expanded paths and selection before rebuilding
        var expandedTags = new HashSet<string>();
        CollectExpandedTags(_tree.RootNodes, expandedTags);
        var selectedTag = _tree.SelectedNode?.Tag as string;

        _tree.Clear();
        BuildTree();

        // Restore expanded state
        RestoreExpandedTags(_tree.RootNodes, expandedTags);

        // Restore selection
        if (selectedTag != null)
        {
            var node = _tree.FindNodeByTag(selectedTag);
            if (node != null) _tree.SelectNode(node);
        }
    }

    private static void CollectExpandedTags(IEnumerable<SharpConsoleUI.Controls.TreeNode> nodes, HashSet<string> tags)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Tag is string path)
                tags.Add(path);
            if (node.Children.Count > 0)
                CollectExpandedTags(node.Children, tags);
        }
    }

    private static void RestoreExpandedTags(IEnumerable<SharpConsoleUI.Controls.TreeNode> nodes, HashSet<string> tags)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is string path && tags.Contains(path))
                node.IsExpanded = true;
            if (node.Children.Count > 0)
                RestoreExpandedTags(node.Children, tags);
        }
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

    private void BuildTree()
    {
        var root = _projectService.BuildTree();
        AddNode(root, null);
    }

    private void AddNode(FileNode fileNode, TreeNode? parent)
    {
        string label = fileNode.Name;

        // Append git status badge
        string badge = GetGitBadge(fileNode);
        if (!string.IsNullOrEmpty(badge))
            label += badge;

        TreeNode treeNode;
        if (parent == null)
        {
            treeNode = _tree.AddRootNode(label);
            treeNode.IsExpanded = true;
        }
        else
        {
            treeNode = parent.AddChild(label);
            treeNode.IsExpanded = false;
        }

        treeNode.Tag = fileNode.FullPath;

        if (IsIgnored(fileNode))
        {
            treeNode.TextColor = Color.Grey35;
        }
        else if (fileNode.IsDirectory)
        {
            treeNode.TextColor = Color.Cyan1;
        }
        else
        {
            treeNode.TextColor = GetFileColor(fileNode.Name);
        }

        if (fileNode.IsDirectory)
        {
            foreach (var child in fileNode.Children)
                AddNode(child, treeNode);
        }
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
