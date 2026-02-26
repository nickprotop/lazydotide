using SharpConsoleUI.Controls;
using Spectre.Console;

namespace DotNetIDE;

/// <summary>
/// Gutter renderer that displays colored change markers for lines with
/// uncommitted git changes: green (added), blue (modified), red (deleted).
/// </summary>
public class GitDiffGutterRenderer : IGutterRenderer
{
    private Dictionary<int, GitLineChangeType>? _markers;

    /// <summary>
    /// Updates the marker dictionary. Pass null or empty to hide the gutter column.
    /// </summary>
    public void UpdateMarkers(Dictionary<int, GitLineChangeType>? markers)
    {
        _markers = markers is { Count: > 0 } ? markers : null;
    }

    /// <inheritdoc/>
    public int GetWidth(int totalLineCount) => _markers != null ? 1 : 0;

    /// <inheritdoc/>
    public void Render(in GutterRenderContext ctx, int width)
    {
        if (width == 0) return;

        char ch = ' ';
        Color fg = ctx.BackgroundColor;

        if (ctx.SourceLineIndex >= 0 && ctx.IsFirstWrappedSegment &&
            _markers != null && _markers.TryGetValue(ctx.SourceLineIndex, out var changeType))
        {
            switch (changeType)
            {
                case GitLineChangeType.Added:
                    ch = '▌';
                    fg = Color.Green;
                    break;
                case GitLineChangeType.Modified:
                    ch = '▌';
                    fg = Color.DodgerBlue1;
                    break;
                case GitLineChangeType.Deleted:
                    ch = '▶';
                    fg = Color.Red;
                    break;
            }
        }

        ctx.Buffer.SetCell(ctx.X, ctx.Y, ch, fg, ctx.BackgroundColor);
    }
}
