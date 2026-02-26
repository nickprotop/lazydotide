using SharpConsoleUI.Controls;
using Spectre.Console;

namespace DotNetIDE;

/// <summary>
/// Syntax highlighter for unified diff output.
/// Colors +lines green, -lines red, @@hunks cyan, and diff headers bold.
/// </summary>
public class DiffSyntaxHighlighter : ISyntaxHighlighter
{
    private static readonly Color AddedColor = Color.Green;
    private static readonly Color DeletedColor = Color.Red;
    private static readonly Color HunkColor = Color.Cyan1;
    private static readonly Color HeaderColor = Color.Yellow;
    private static readonly Color IndexColor = Color.Grey;

    public (IReadOnlyList<SyntaxToken> Tokens, SyntaxLineState EndState)
        Tokenize(string line, int lineIndex, SyntaxLineState startState)
    {
        var tokens = new List<SyntaxToken>();

        if (line.Length == 0)
            return (tokens, SyntaxLineState.Initial);

        if (line.StartsWith("+++") || line.StartsWith("---"))
        {
            tokens.Add(new SyntaxToken(0, line.Length, HeaderColor));
        }
        else if (line.StartsWith("@@"))
        {
            tokens.Add(new SyntaxToken(0, line.Length, HunkColor));
        }
        else if (line.StartsWith("+"))
        {
            tokens.Add(new SyntaxToken(0, line.Length, AddedColor));
        }
        else if (line.StartsWith("-"))
        {
            tokens.Add(new SyntaxToken(0, line.Length, DeletedColor));
        }
        else if (line.StartsWith("diff ") || line.StartsWith("index ") ||
                 line.StartsWith("new file") || line.StartsWith("deleted file") ||
                 line.StartsWith("similarity") || line.StartsWith("rename"))
        {
            tokens.Add(new SyntaxToken(0, line.Length, IndexColor));
        }

        return (tokens, SyntaxLineState.Initial);
    }
}

/// <summary>
/// Syntax highlighter for git commit detail output (git show style).
/// </summary>
public class CommitDetailSyntaxHighlighter : ISyntaxHighlighter
{
    private static readonly Color ShaColor = Color.Yellow;
    private static readonly Color LabelColor = Color.Cyan1;
    private static readonly Color MessageColor = Color.White;
    private static readonly Color AddedColor = Color.Green;
    private static readonly Color DeletedColor = Color.Red;
    private static readonly Color StatsColor = Color.Grey;

    public (IReadOnlyList<SyntaxToken> Tokens, SyntaxLineState EndState)
        Tokenize(string line, int lineIndex, SyntaxLineState startState)
    {
        var tokens = new List<SyntaxToken>();

        if (line.Length == 0)
            return (tokens, SyntaxLineState.Initial);

        if (line.StartsWith("commit "))
        {
            tokens.Add(new SyntaxToken(0, 7, LabelColor));
            tokens.Add(new SyntaxToken(7, line.Length - 7, ShaColor));
        }
        else if (line.StartsWith("Author:") || line.StartsWith("Date:"))
        {
            var colonIdx = line.IndexOf(':');
            tokens.Add(new SyntaxToken(0, colonIdx + 1, LabelColor));
        }
        else if (line.StartsWith("    "))
        {
            tokens.Add(new SyntaxToken(0, line.Length, MessageColor));
        }
        else if (line.Length > 2 && line[1] == ' ' &&
                 line[0] is 'M' or 'A' or 'D' or 'R' or 'C' or '?')
        {
            // File stat line: "M  +12  -3   path/to/file"
            var statusColor = line[0] switch
            {
                'A' => AddedColor,
                'D' => DeletedColor,
                'M' => Color.DodgerBlue1,
                'R' => Color.Blue,
                _ => StatsColor
            };
            tokens.Add(new SyntaxToken(0, 1, statusColor));
            // Highlight +N and -N
            for (int i = 1; i < line.Length; i++)
            {
                if (line[i] == '+' && i + 1 < line.Length && char.IsDigit(line[i + 1]))
                {
                    int end = i + 1;
                    while (end < line.Length && char.IsDigit(line[end])) end++;
                    tokens.Add(new SyntaxToken(i, end - i, AddedColor));
                    i = end - 1;
                }
                else if (line[i] == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1]))
                {
                    int end = i + 1;
                    while (end < line.Length && char.IsDigit(line[end])) end++;
                    tokens.Add(new SyntaxToken(i, end - i, DeletedColor));
                    i = end - 1;
                }
            }
        }
        else if (line.Contains("file(s) changed"))
        {
            tokens.Add(new SyntaxToken(0, line.Length, StatsColor));
        }

        return (tokens, SyntaxLineState.Initial);
    }
}
