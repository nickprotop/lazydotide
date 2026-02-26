using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace DotNetIDE;

/// <summary>
/// Converts LSP markdown hover/documentation content into Spectre markup lines
/// suitable for display in tooltip portals.
/// </summary>
internal static class LspMarkdownHelper
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "break", "case", "catch", "checked", "class",
        "const", "continue", "default", "delegate", "do", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "for", "foreach", "goto",
        "if", "implicit", "in", "interface", "internal", "is", "lock", "namespace",
        "new", "null", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sealed", "sizeof", "stackalloc",
        "static", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "unchecked", "unsafe", "using", "virtual", "volatile", "while", "async",
        "await", "yield", "where", "get", "set", "init", "record", "required"
    };

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long",
        "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "void",
        "var", "dynamic", "nint", "nuint"
    };

    private static readonly Regex TokenPattern = new(
        @"\b[a-zA-Z_]\w*\b|""[^""]*""|'[^']*'",
        RegexOptions.Compiled);

    /// <summary>
    /// Converts LSP markdown content (hover, documentation) to a list of Spectre markup lines.
    /// Handles code fences with C# syntax highlighting and basic inline markdown.
    /// </summary>
    public static List<string> ConvertToSpectreMarkup(string markdown, int maxLines = 12)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new List<string>();

        var lines = new List<string>();
        var rawLines = markdown.Split('\n');
        bool inCodeFence = false;

        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.TrimEnd();

            // Code fence start/end
            if (trimmed.StartsWith("```"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (lines.Count >= maxLines)
                break;

            if (inCodeFence)
            {
                // Syntax-highlight code lines
                lines.Add(HighlightCSharpLine(trimmed));
            }
            else
            {
                // Documentation text — convert inline markdown
                var converted = ConvertInlineMarkdown(trimmed);
                if (!string.IsNullOrEmpty(converted))
                    lines.Add(converted);
            }
        }

        return lines;
    }

    /// <summary>
    /// Escapes a string for Spectre markup — doubles both '[' and ']'.
    /// Spectre's Markup.Escape only handles '[', but lone ']' characters
    /// (e.g. from Roslyn's parameter-bracket notation in hover labels)
    /// break Spectre's markup parser.
    /// </summary>
    private static string EscapeBrackets(string text)
        => Markup.Escape(text).Replace("]", "]]");

    /// <summary>
    /// Applies basic C# syntax highlighting to a single line, returning Spectre markup.
    /// </summary>
    private static string HighlightCSharpLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return EscapeBrackets(line);

        var sb = new StringBuilder();
        int lastEnd = 0;

        foreach (Match m in TokenPattern.Matches(line))
        {
            // Append any gap (whitespace/punctuation between tokens)
            if (m.Index > lastEnd)
                sb.Append(EscapeBrackets(line[lastEnd..m.Index]));

            string token = m.Value;

            if (Keywords.Contains(token))
                sb.Append($"[dodgerblue2]{EscapeBrackets(token)}[/]");
            else if (TypeKeywords.Contains(token))
                sb.Append($"[mediumturquoise]{EscapeBrackets(token)}[/]");
            else if (token.StartsWith('"') || token.StartsWith('\''))
                sb.Append($"[orange3]{EscapeBrackets(token)}[/]");
            else
                sb.Append(EscapeBrackets(token));

            lastEnd = m.Index + m.Length;
        }

        // Trailing content
        if (lastEnd < line.Length)
            sb.Append(EscapeBrackets(line[lastEnd..]));

        return sb.ToString();
    }

    /// <summary>
    /// Converts basic inline markdown (bold, italic, inline code, horizontal rules) to Spectre markup.
    /// </summary>
    private static string ConvertInlineMarkdown(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        // Horizontal rule
        if (Regex.IsMatch(line.Trim(), @"^[-_*]{3,}$"))
            return "[dim]───[/]";

        // Strip markdown backslash escapes (e.g. \. \* \[ \] \\ etc.)
        var result = Regex.Replace(line, @"\\([\\`*_{}\[\]()#+\-.!])", "$1");

        // Inline code: `code` → highlighted
        result = Regex.Replace(result, @"`([^`]+)`", m =>
            $"§CODESTART§{m.Groups[1].Value}§CODEEND§");

        // Bold: **text** or __text__
        result = Regex.Replace(result, @"\*\*([^*]+)\*\*|__([^_]+)__", m =>
        {
            var text = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return $"§BOLDSTART§{text}§BOLDEND§";
        });

        // Italic: *text* or _text_ (but not inside words with underscores)
        result = Regex.Replace(result, @"(?<!\w)\*([^*]+)\*(?!\w)|(?<!\w)_([^_]+)_(?!\w)", m =>
        {
            var text = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return $"§ITALSTART§{text}§ITALEND§";
        });

        // Now escape the whole thing for Spectre (both [ and ])
        result = EscapeBrackets(result);

        // Restore formatting markers with Spectre tags
        result = result.Replace("§CODESTART§", "[cyan]").Replace("§CODEEND§", "[/]");
        result = result.Replace("§BOLDSTART§", "[bold]").Replace("§BOLDEND§", "[/]");
        result = result.Replace("§ITALSTART§", "[dim italic]").Replace("§ITALEND§", "[/]");

        return result;
    }
}
