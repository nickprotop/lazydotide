using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class YamlFileMiddleware : IFileMiddleware
{
    public string SyntaxName => "YAML";
    private static readonly YamlSyntaxHighlighter Highlighter = new();

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yml", ".yaml"
    };

    public bool Handles(string filePath) =>
        Extensions.Contains(FileService.GetExtension(filePath));

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
