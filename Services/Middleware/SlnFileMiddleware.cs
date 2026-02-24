using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class SlnFileMiddleware : IFileMiddleware
{
    private static readonly SlnSyntaxHighlighter Highlighter = new();

    public bool Handles(string filePath) =>
        FileService.GetExtension(filePath).Equals(".sln", StringComparison.OrdinalIgnoreCase);

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
