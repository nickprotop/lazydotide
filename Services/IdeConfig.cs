namespace DotNetIDE;

public class IdeConfig
{
    public List<ToolEntry> Tools { get; set; } = new();
    public LspConfig? Lsp { get; set; }
}

public class LspConfig
{
    public string Command { get; set; } = "";
    public string[]? Args { get; set; }
}

public class ToolEntry
{
    public string Name       { get; set; } = "";
    public string Command    { get; set; } = "";
    public string[]? Args    { get; set; }
    public string? WorkingDir { get; set; }  // null = project root
}
