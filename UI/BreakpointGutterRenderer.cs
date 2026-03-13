using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class BreakpointGutterRenderer : IGutterRenderer
{
    private readonly HashSet<int> _breakpoints = new(); // 0-based source line indices
    private int _stoppedLine = -1; // 0-based, -1 = not stopped

    public IReadOnlySet<int> Breakpoints => _breakpoints;

    public void ToggleBreakpoint(int sourceLineIndex)
    {
        if (!_breakpoints.Remove(sourceLineIndex))
            _breakpoints.Add(sourceLineIndex);
    }

    public bool HasBreakpoint(int sourceLineIndex) => _breakpoints.Contains(sourceLineIndex);

    public void SetBreakpoint(int sourceLineIndex) => _breakpoints.Add(sourceLineIndex);

    public void ClearBreakpoint(int sourceLineIndex) => _breakpoints.Remove(sourceLineIndex);

    public void SetStoppedLine(int sourceLineIndex) => _stoppedLine = sourceLineIndex;

    public void ClearStoppedLine() => _stoppedLine = -1;

    public int GetWidth(int totalLineCount) => 2;

    public void Render(in GutterRenderContext ctx, int width)
    {
        if (width == 0) return;

        // Column 0: breakpoint marker
        char bpChar = ' ';
        Color bpFg = ctx.BackgroundColor;
        if (ctx.SourceLineIndex >= 0 && ctx.IsFirstWrappedSegment && _breakpoints.Contains(ctx.SourceLineIndex))
        {
            bpChar = '●';
            bpFg = Color.Red;
        }
        ctx.Buffer.SetNarrowCell(ctx.X, ctx.Y, bpChar, bpFg, ctx.BackgroundColor);

        // Column 1: stopped indicator
        char stChar = ' ';
        Color stFg = ctx.BackgroundColor;
        if (ctx.SourceLineIndex >= 0 && ctx.IsFirstWrappedSegment && _stoppedLine == ctx.SourceLineIndex)
        {
            stChar = '▶';
            stFg = Color.Yellow;
        }
        ctx.Buffer.SetNarrowCell(ctx.X + 1, ctx.Y, stChar, stFg, ctx.BackgroundColor);
    }
}
