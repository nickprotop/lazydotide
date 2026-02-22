using SharpConsoleUI.Controls;

namespace DotNetIDE;

/// <summary>Catch-all passthrough — registered last in the pipeline.</summary>
public class DefaultFileMiddleware : IFileMiddleware
{
    public bool Handles(string filePath) => true;
    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;
    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => null;
    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;
}
