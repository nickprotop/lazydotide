using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace DotNetIDE;

public class BreakpointGutterRenderer : IGutterRenderer
{
    private readonly HashSet<int> _breakpoints = new(); // 0-based source line indices
    private int _stoppedLine = -1; // 0-based, -1 = not stopped

    /// <inheritdoc/>
    public event EventHandler? Invalidated;

    // Signal a state change; the host editor derives the invalidation level by re-querying GetWidth.
    private void NotifyChanged() => Invalidated?.Invoke(this, EventArgs.Empty);

    public IReadOnlySet<int> Breakpoints => _breakpoints;

    public void ToggleBreakpoint(int sourceLineIndex)
    {
        if (!_breakpoints.Remove(sourceLineIndex))
            _breakpoints.Add(sourceLineIndex);
        NotifyChanged();
    }

    public bool HasBreakpoint(int sourceLineIndex) => _breakpoints.Contains(sourceLineIndex);

    public void SetBreakpoint(int sourceLineIndex)
    {
        if (_breakpoints.Add(sourceLineIndex)) NotifyChanged();
    }

    public void ClearBreakpoint(int sourceLineIndex)
    {
        if (_breakpoints.Remove(sourceLineIndex)) NotifyChanged();
    }

    public void SetStoppedLine(int sourceLineIndex)
    {
        if (_stoppedLine == sourceLineIndex) return;
        _stoppedLine = sourceLineIndex;
        NotifyChanged();
    }

    public void ClearStoppedLine()
    {
        if (_stoppedLine == -1) return;
        _stoppedLine = -1;
        NotifyChanged();
    }

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
