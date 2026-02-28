using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class CSharpFileMiddleware : IFileMiddleware
{
    public string SyntaxName => "C#";
    private static readonly CSharpSyntaxHighlighter Highlighter = new();

    public bool Handles(string filePath) =>
        FileService.GetExtension(filePath) == ".cs";

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
