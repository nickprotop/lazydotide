using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetIDE;

public class WorkspaceFile
{
    public string Path { get; set; } = "";
    public int CursorLine { get; set; } = 1;
    public int CursorColumn { get; set; } = 1;
    public bool IsActive { get; set; }
}

public class WorkspaceState
{
    public bool ExplorerVisible { get; set; } = true;
    public bool OutputVisible { get; set; } = true;
    public bool SidePanelVisible { get; set; }
    public double SplitRatio { get; set; } = 0.68;
    public int ExplorerColumnWidth { get; set; } = 26;
    public int SidePanelColumnWidth { get; set; } = 30;
    public string WrapMode { get; set; } = "NoWrap";
    public int ActiveOutputTab { get; set; }
    public List<WorkspaceFile> OpenFiles { get; set; } = new();
    public List<string> ExpandedPaths { get; set; } = new();
    public string? SelectedExplorerPath { get; set; }
}

public class WorkspaceService
{
    private readonly string _projectRoot;
    private readonly string _filePath;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WorkspaceState State { get; private set; } = new();

    public WorkspaceService(string projectRoot)
    {
        _projectRoot = projectRoot;
        _filePath = System.IO.Path.Combine(projectRoot, ".lazydotide.workspace");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<WorkspaceState>(json, s_readOptions);
            if (state != null)
                State = state;
        }
        catch
        {
            // Corrupt or unreadable — keep defaults
            State = new WorkspaceState();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(State, s_jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Silently ignore write failures
        }
    }

    public string ToRelativePath(string absolutePath)
        => Path.GetRelativePath(_projectRoot, absolutePath).Replace('\\', '/');

    public string ToAbsolutePath(string relativePath)
        => Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
}
