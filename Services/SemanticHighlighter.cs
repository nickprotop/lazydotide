using SharpConsoleUI.Controls;
using Spectre.Console;

namespace DotNetIDE;

internal class SemanticHighlighter : ISyntaxHighlighter
{
    private static void Log(string msg) => DiagnosticLog.Write("semantic", msg);

    private readonly ISyntaxHighlighter _fallback;
    private Dictionary<int, List<SemanticToken>>? _tokensByLine;
    private SemanticTokensLegend? _legend;

    // Token types to skip — regex handles these correctly and we don't want
    // to lose block-comment state tracking
    private static readonly HashSet<string> SkipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword", "comment", "string", "number", "regexp", "operator"
    };

    // Color mapping (VS Code Dark+ style)
    private static readonly Dictionary<string, Color> TokenColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["namespace"] = Color.Grey,
        ["type"] = Color.MediumTurquoise,
        ["class"] = Color.MediumTurquoise,
        ["struct"] = Color.MediumTurquoise,
        ["enum"] = Color.MediumTurquoise,
        ["interface"] = Color.MediumTurquoise,
        ["typeParameter"] = Color.MediumTurquoise,
        ["method"] = Color.DarkKhaki,
        ["function"] = Color.DarkKhaki,
        ["property"] = Color.LightSkyBlue1,
        ["variable"] = Color.CornflowerBlue,
        ["parameter"] = Color.LightSteelBlue,
        ["enumMember"] = Color.SteelBlue,
        ["event"] = Color.DarkKhaki
    };

    public SemanticHighlighter(ISyntaxHighlighter fallback)
    {
        _fallback = fallback;
    }

    public void UpdateTokens(List<SemanticToken> tokens, SemanticTokensLegend legend)
    {
        _legend = legend;
        _tokensByLine = tokens
            .GroupBy(t => t.Line)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public (IReadOnlyList<SyntaxToken> Tokens, SyntaxLineState EndState)
        Tokenize(string line, int lineIndex, SyntaxLineState startState)
    {
        var (regexTokens, endState) = _fallback.Tokenize(line, lineIndex, startState);

        if (_tokensByLine == null || _legend == null)
            return (regexTokens, endState);

        if (!_tokensByLine.TryGetValue(lineIndex, out var semTokens) || semTokens.Count == 0)
            return (regexTokens, endState);

        return (MergeTokens(regexTokens, semTokens, line.Length), endState);
    }

    private IReadOnlyList<SyntaxToken> MergeTokens(
        IReadOnlyList<SyntaxToken> regexTokens,
        List<SemanticToken> semTokens,
        int lineLength)
    {
        if (lineLength == 0) return regexTokens;

        // Build color array from regex tokens (null = default/no color)
        var colors = new Color?[lineLength];
        foreach (var rt in regexTokens)
        {
            int end = Math.Min(rt.StartIndex + rt.Length, lineLength);
            for (int i = rt.StartIndex; i < end; i++)
                colors[i] = rt.ForegroundColor;
        }

        // Overlay semantic token colors
        foreach (var st in semTokens)
        {
            if (st.TokenType < 0 || st.TokenType >= _legend!.TokenTypes.Length)
                continue;

            var typeName = _legend.TokenTypes[st.TokenType];

            if (SkipTypes.Contains(typeName))
                continue;

            if (!TokenColors.TryGetValue(typeName, out var color))
                continue;

            int end = Math.Min(st.StartChar + st.Length, lineLength);
            for (int i = st.StartChar; i < end; i++)
                colors[i] = color;
        }

        // Collapse adjacent same-color chars into SyntaxToken spans
        var result = new List<SyntaxToken>();
        int spanStart = -1;
        Color? spanColor = null;

        for (int i = 0; i <= lineLength; i++)
        {
            var c = i < lineLength ? colors[i] : null;

            if (c != spanColor || i == lineLength)
            {
                if (spanStart >= 0 && spanColor != null)
                    result.Add(new SyntaxToken(spanStart, i - spanStart, spanColor.Value));

                spanStart = i;
                spanColor = c;
            }
        }

        return result;
    }
}
