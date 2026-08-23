using SharpConsoleUI.Controls;
using SharpConsoleUI.Highlighting;

namespace DotNetIDE;

/// <summary>
/// Catch-all passthrough — registered last in the pipeline. Files with no dedicated middleware
/// still get syntax highlighting when a TextMate grammar covers their extension, so shell
/// scripts, Python, Rust, SQL and the rest highlight without a middleware each.
/// </summary>
public class DefaultFileMiddleware : IFileMiddleware
{
    public string SyntaxName => "Plain Text";

    public bool Handles(string filePath) => true;

    public string OnLoad(string rawContent, string filePath) => rawContent;

    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath)
        => SyntaxHighlighters.For(LanguageHintFor(filePath));

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) => null;

    /// <summary>
    /// Maps a path to a language hint the registry understands: the file extension, or the
    /// whole file name for extensionless files that name their own type (Dockerfile, Makefile).
    /// </summary>
    internal static string LanguageHintFor(string filePath)
    {
        var ext = FileService.GetExtension(filePath);
        if (!string.IsNullOrEmpty(ext))
            return ext.TrimStart('.');

        return Path.GetFileName(filePath);
    }
}
