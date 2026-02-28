using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class MarkdownFileMiddleware : IFileMiddleware
{
    public string SyntaxName => "Markdown";
    private static readonly MarkdownSyntaxHighlighter Highlighter = new();
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public bool Handles(string filePath) =>
        FileService.GetExtension(filePath) == ".md";

    public string OnLoad(string rawContent, string filePath) => rawContent;
    public string OnSave(string editorContent, string filePath) => editorContent;

    public ISyntaxHighlighter? GetSyntaxHighlighter(string filePath) => Highlighter;

    public IReadOnlyList<string>? Validate(string editorContent, string filePath)
    {
        var warnings = new List<string>();
        var document = Markdown.Parse(editorContent, Pipeline);
        var baseDir = Path.GetDirectoryName(filePath) ?? ".";
        var lines = editorContent.Split('\n');

        foreach (var block in document.Descendants())
        {
            switch (block)
            {
                case HeadingBlock heading:
                    CheckEmptyHeading(heading, lines, warnings);
                    break;

                case LinkInline link:
                    CheckLink(link, lines, baseDir, warnings);
                    break;

                case MdTable table:
                    CheckTable(table, warnings);
                    break;
            }
        }

        return warnings.Count > 0 ? warnings : null;
    }

    private static void CheckEmptyHeading(HeadingBlock heading, string[] lines, List<string> warnings)
    {
        int lineNum = (heading.Line) + 1;
        var rawLine = lineNum > 0 && lineNum <= lines.Length
            ? lines[lineNum - 1].TrimEnd()
            : null;

        bool isEmpty = heading.Inline == null || !heading.Inline.Any();
        if (isEmpty && rawLine != null)
        {
            // Confirm it really is just # characters with nothing after
            var stripped = rawLine.TrimStart('#').Trim();
            if (string.IsNullOrEmpty(stripped))
                warnings.Add($"Line {lineNum}: empty heading content");
        }
    }

    private static void CheckLink(LinkInline link, string[] lines, string baseDir, List<string> warnings)
    {
        int lineNum = link.Line + 1;
        var url = link.Url ?? "";

        if (string.IsNullOrEmpty(url))
        {
            warnings.Add($"Line {lineNum}: empty link target in [{GetLinkText(link)}]()");
            return;
        }

        // Check local file links
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("#"))
        {
            // Strip fragment
            var urlWithoutFragment = url.Split('#')[0];
            if (string.IsNullOrEmpty(urlWithoutFragment)) return;

            var fullPath = Path.GetFullPath(Path.Combine(baseDir, urlWithoutFragment));
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                warnings.Add($"Line {lineNum}: broken local link [{GetLinkText(link)}]({url})");
            }
        }
    }

    private static void CheckTable(MdTable table, List<string> warnings)
    {
        int headerCols = -1;
        int rowIndex = 0;
        foreach (var row in table.Descendants<MdTableRow>())
        {
            int cols = row.Count;
            if (rowIndex == 0)
            {
                headerCols = cols;
            }
            else if (headerCols >= 0 && cols != headerCols)
            {
                int lineNum = row.Line + 1;
                warnings.Add(
                    $"Line {lineNum}: table row has {cols} column(s) but header has {headerCols}");
            }
            rowIndex++;
        }
    }

    private static string GetLinkText(LinkInline link)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in link)
        {
            if (child is LiteralInline literal)
                sb.Append(literal.Content);
        }
        return sb.ToString();
    }
}
