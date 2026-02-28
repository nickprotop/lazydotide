using SharpConsoleUI.Controls;

namespace DotNetIDE;

public interface IFileMiddleware
{
    string SyntaxName { get; }
    bool Handles(string filePath);
    string OnLoad(string rawContent, string filePath);
    string OnSave(string editorContent, string filePath);
    ISyntaxHighlighter? GetSyntaxHighlighter(string filePath);

    /// <summary>
    /// Validate content before save. Returns warning strings, or null if clean.
    /// </summary>
    IReadOnlyList<string>? Validate(string editorContent, string filePath);
}
