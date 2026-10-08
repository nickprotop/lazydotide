namespace DotNetIDE;

public record FileNode(string Name, string FullPath, bool IsDirectory, List<FileNode> Children);

public class ProjectService
{
    private string _rootPath;

    public ProjectService(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string RootPath => _rootPath;

    public void ChangeRootPath(string newPath) => _rootPath = newPath;

    // Directories never worth descending into when looking for project files.
    private static readonly HashSet<string> ProjectSearchSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "TestResults",
    };

    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"];

    private const int MaxProjectSearchDepth = 5;
    private const int MaxWorkspaceDetectDepth = 2;

    /// <summary>
    /// Lists the immediate children of a directory (subdirectories first, then files).
    /// Children of the returned directory nodes are not populated — the explorer loads them on expand.
    /// </summary>
    public List<FileNode> ListDirectory(string dirPath)
    {
        var children = new List<FileNode>();

        try
        {
            foreach (var subDir in Directory.GetDirectories(dirPath).OrderBy(d => d))
            {
                var dirName = Path.GetFileName(subDir);
                if (dirName is null or ".git") continue;
                children.Add(new FileNode(dirName, subDir, true, new List<FileNode>()));
            }

            foreach (var file in Directory.GetFiles(dirPath).OrderBy(f => f))
                children.Add(new FileNode(Path.GetFileName(file), file, false, new List<FileNode>()));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return children;
    }

    public static bool HasEntries(string dirPath)
    {
        try { return Directory.EnumerateFileSystemEntries(dirPath).Any(); }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// True when the root (or a shallow subdirectory) contains a solution or project file.
    /// Used to detect when the IDE was launched on an arbitrary folder such as $HOME.
    /// </summary>
    public bool ContainsDotNetProject()
    {
        // $HOME and the filesystem root routinely have projects a couple of levels down
        // (~/source/Foo/Foo.csproj) but are never themselves a workspace.
        if (IsSameDirectory(_rootPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            || IsSameDirectory(_rootPath, Path.GetPathRoot(_rootPath)))
            return false;

        return EnumerateFiles(_rootPath, MaxWorkspaceDetectDepth)
            .Any(f => ProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsSameDirectory(string a, string? b) =>
        !string.IsNullOrEmpty(b)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                         Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                         OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> FindProjectFiles(string pattern) =>
        EnumerateFiles(_rootPath, MaxProjectSearchDepth)
            .Where(f => Path.GetExtension(f).Equals(pattern, StringComparison.OrdinalIgnoreCase));

    // Breadth-first walk bounded by depth, skipping build output / VCS / hidden directories.
    private static IEnumerable<string> EnumerateFiles(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files.OrderBy(f => f))
                yield return file;

            if (depth >= maxDepth) continue;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var subDir in subDirs.OrderBy(d => d))
            {
                var name = Path.GetFileName(subDir);
                if (name.StartsWith('.') || ProjectSearchSkipDirs.Contains(name)) continue;
                queue.Enqueue((subDir, depth + 1));
            }
        }
    }

    public string? FindBuildTarget()
    {
        // Prefer .sln
        var sln = Directory.GetFiles(_rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sln != null) return sln;

        // Fallback to any .csproj
        return FindProjectFiles(".csproj").FirstOrDefault();
    }

    public string? FindRunTarget()
    {
        // Find a .csproj that has OutputType=Exe
        var projects = FindProjectFiles(".csproj").ToList();
        foreach (var csproj in projects)
        {
            try
            {
                var content = File.ReadAllText(csproj);
                if (content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
                    return csproj;
            }
            catch { } // Skip unparseable project files
        }
        return projects.FirstOrDefault();
    }
}
