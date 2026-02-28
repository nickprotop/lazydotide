using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class DockerfileMiddleware : IFileMiddleware
{
    public string SyntaxName => "Dockerfile";
    private static readonly DockerfileSyntaxHighlighter Highlighter = new();

    public bool Handles(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
               FileService.GetExtension(filePath).Equals(".dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
