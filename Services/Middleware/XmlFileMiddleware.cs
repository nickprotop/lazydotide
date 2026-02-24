using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class XmlFileMiddleware : IFileMiddleware
{
    private static readonly XmlSyntaxHighlighter Highlighter = new();

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets",
        ".nuspec", ".config", ".resx", ".xaml"
    };

    public bool Handles(string filePath) =>
        Extensions.Contains(FileService.GetExtension(filePath));

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
