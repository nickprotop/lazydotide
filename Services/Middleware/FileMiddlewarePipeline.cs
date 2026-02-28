using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class FileMiddlewarePipeline
{
    private readonly List<IFileMiddleware> _handlers = new();

    public void Register(IFileMiddleware handler) => _handlers.Add(handler);

    private IFileMiddleware Pick(string filePath)
    {
        foreach (var h in _handlers)
            if (h.Handles(filePath)) return h;
        return new DefaultFileMiddleware();
    }

    public string ProcessLoad(string rawContent, string filePath) =>
        Pick(filePath).OnLoad(rawContent, filePath);

    public string ProcessSave(string editorContent, string filePath) =>
        Pick(filePath).OnSave(editorContent, filePath);

    public ISyntaxHighlighter? GetHighlighter(string filePath) =>
        Pick(filePath).GetSyntaxHighlighter(filePath);

    public IReadOnlyList<string>? Validate(string editorContent, string filePath) =>
        Pick(filePath).Validate(editorContent, filePath);

    /// <summary>Returns display-name / highlighter pairs for all registered syntaxes.</summary>
    public IReadOnlyList<(string Name, ISyntaxHighlighter? Highlighter)> GetAvailableHighlighters()
    {
        var result = new List<(string, ISyntaxHighlighter?)>();
        var seen = new HashSet<string>();
        foreach (var h in _handlers)
        {
            if (seen.Add(h.SyntaxName))
                result.Add((h.SyntaxName, h.GetSyntaxHighlighter("")));
        }
        return result;
    }

    /// <summary>Returns the display name for the auto-detected syntax of a file.</summary>
    public string GetSyntaxName(string filePath) => Pick(filePath).SyntaxName;
}
