using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class JsFileMiddleware : IFileMiddleware
{
    private static readonly JsSyntaxHighlighter Highlighter = new();

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"
    };

    public bool Handles(string filePath) =>
        Extensions.Contains(FileService.GetExtension(filePath));

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
