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
            var name = SyntaxName(h);
            if (seen.Add(name))
                result.Add((name, h.GetSyntaxHighlighter("")));
        }
        return result;
    }

    /// <summary>Returns a display name for the syntax a highlighter provides.</summary>
    public static string SyntaxName(IFileMiddleware middleware) => middleware switch
    {
        CSharpFileMiddleware     => "C#",
        MarkdownFileMiddleware   => "Markdown",
        JsonFileMiddleware       => "JSON",
        XmlFileMiddleware        => "XML",
        YamlFileMiddleware       => "YAML",
        DockerfileMiddleware     => "Dockerfile",
        SlnFileMiddleware        => "Solution",
        DefaultFileMiddleware    => "Plain Text",
        _                        => middleware.GetType().Name
    };

    /// <summary>Returns the display name for the auto-detected syntax of a file.</summary>
    public string GetSyntaxName(string filePath) => SyntaxName(Pick(filePath));
}
