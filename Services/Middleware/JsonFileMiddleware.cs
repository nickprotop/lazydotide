using SharpConsoleUI.Controls;
using SharpConsoleUI.Highlighting;

namespace DotNetIDE;

public class JsonFileMiddleware : IFileMiddleware
{
    public string SyntaxName => "JSON";
    private static readonly ISyntaxHighlighter? Highlighter = SyntaxHighlighters.For("json");

    public bool Handles(string filePath) =>
        FileService.GetExtension(filePath) == ".json";

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
