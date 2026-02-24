using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class JsonFileMiddleware : IFileMiddleware
{
    private static readonly JsonSyntaxHighlighter Highlighter = new();

    public bool Handles(string filePath) =>
        FileService.GetExtension(filePath) == ".json";

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
