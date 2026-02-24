using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Spectre.Console;
using Color = Spectre.Console.Color;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

// ──────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ──────────────────────────────────────────────────────────────────────────────

internal static class LspPortalLayout
{
    // Clamp a portal rectangle so it stays fully inside the window's inner area.
    // windowWidth/Height include the 1-cell border on each side.
    public static Rectangle Clamp(int x, int y, int w, int h, int windowWidth, int windowHeight)
    {
        int maxX = windowWidth  - 2;   // rightmost valid column (inside right border)
        int maxY = windowHeight - 2;   // bottom-most valid row (inside bottom border)
        int minX = 1;
        int minY = 1;

        // Clamp width / height first so they fit at all
        w = Math.Max(4, Math.Min(w, maxX - minX + 1));
        h = Math.Max(3, Math.Min(h, maxY - minY + 1));

        // Then clamp origin
        x = Math.Max(minX, Math.Min(x, maxX - w + 1));
        y = Math.Max(minY, Math.Min(y, maxY - h + 1));

        return new Rectangle(x, y, w, h);
    }

    // Pick Y position: prefer 'below' cursor; flip above if it would overflow.
    // Returns the chosen Y, and may shrink 'h' if even after flipping there's still no room.
    public static int PickY(int cursorY, int h, int windowHeight, bool preferAbove, out int finalH)
    {
        int maxY = windowHeight - 2;
        int minY = 1;
        finalH = h;

        if (!preferAbove)
        {
            // Try below first
            int below = cursorY + 1;
            if (below + h - 1 <= maxY) return below;

            // Try above
            int above = cursorY - h;
            if (above >= minY) return above;

            // Neither fits exactly — go below and truncate
            finalH = Math.Max(3, maxY - below + 1);
            return Math.Max(minY, below);
        }
        else
        {
            // Try above first
            int above = cursorY - h;
            if (above >= minY) return above;

            // Try below
            int below = cursorY + 1;
            if (below + h - 1 <= maxY) return below;

            // Neither fits — go above and clamp
            int y = Math.Max(minY, cursorY - h);
            finalH = Math.Max(3, cursorY - y);
            return y;
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Tooltip portal — used for hover info and signature help (read-only text)
// Uses MarkupControl internally for all text rendering (Spectre markup support,
// word-wrap, alignment) — only the rounded border is drawn directly.
// ──────────────────────────────────────────────────────────────────────────────

internal class LspTooltipPortalContent : IWindowControl, IDOMPaintable, IHasPortalBounds
{
    private readonly MarkupControl _markup;
    private Rectangle _bounds;

    private static readonly Color Bg       = Color.Grey11;
    private static readonly Color Fg       = Color.Grey93;
    private static readonly Color BorderFg = Color.Grey50;

    public LspTooltipPortalContent(
        List<string> markupLines, int cursorX, int cursorY,
        int windowWidth, int windowHeight,
        bool preferAbove = true)
    {
        _markup = new MarkupControl(markupLines)
        {
            Margin = new Margin(1, 0, 1, 0)  // 1-column left/right padding inside border
        };

        // Measure using StripSpectreLength — lines are already Spectre markup
        int contentW = markupLines.Count > 0
            ? markupLines.Max(l => AnsiConsoleHelper.StripSpectreLength(l)) + 4  // +2 border + 2 padding
            : 20;
        int popupW = Math.Min(80, contentW);
        int popupH = markupLines.Count + 2;  // +2 for top/bottom border

        int y = LspPortalLayout.PickY(cursorY, popupH, windowHeight, preferAbove, out popupH);
        _bounds = LspPortalLayout.Clamp(cursorX, y, popupW, popupH, windowWidth, windowHeight);
    }

    public Rectangle GetPortalBounds() => _bounds;

    // ── IDOMPaintable ──────────────────────────────────────────────────────────

    public LayoutSize MeasureDOM(LayoutConstraints c) => new(_bounds.Width, _bounds.Height);

    public void PaintDOM(CharacterBuffer buffer, LayoutRect bounds, LayoutRect clip,
                         Color defaultFg, Color defaultBg)
    {
        _actualX = bounds.X; _actualY = bounds.Y;
        _actualWidth = bounds.Width; _actualHeight = bounds.Height;

        // Draw border
        buffer.DrawBox(bounds, BoxChars.Rounded, BorderFg, Bg);

        // Delegate all text rendering to MarkupControl — it handles markup parsing,
        // color resolution, and buffer writing.
        var inner = new LayoutRect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        ((IDOMPaintable)_markup).PaintDOM(buffer, inner, clip, Fg, Bg);
    }

    // ── IWindowControl boilerplate ─────────────────────────────────────────────

    private int _actualX, _actualY, _actualWidth, _actualHeight;
    public int ActualX => _actualX;
    public int ActualY => _actualY;
    public int ActualWidth => _actualWidth;
    public int ActualHeight => _actualHeight;
    public int? ContentWidth  => _bounds.Width;
    public int? ContentHeight => _bounds.Height;
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;
    public VerticalAlignment   VerticalAlignment   { get; set; } = VerticalAlignment.Top;
    public IContainer? Container { get; set; }
    public Margin Margin { get; set; } = new(0, 0, 0, 0);
    public StickyPosition StickyPosition { get; set; } = StickyPosition.None;
    public string? Name { get; set; }
    public object? Tag { get; set; }
    public bool Visible { get; set; } = true;
    public int? Width { get; set; }
    public Size GetLogicalContentSize() => new(_bounds.Width, _bounds.Height);
    public void Invalidate() => Container?.Invalidate(true);
    public void Dispose() { }
}

// ──────────────────────────────────────────────────────────────────────────────
// Completion portal — interactive list with Up/Down/Enter/Tab/Escape support
// Uses ListControl internally for item rendering (markup support, scroll indicator,
// correct highlight colours) — only the rounded border is drawn directly.
// ──────────────────────────────────────────────────────────────────────────────

internal class LspCompletionPortalContent : IWindowControl, IDOMPaintable, IHasPortalBounds, IMouseAwareControl
{
    private readonly List<CompletionItem> _allItems;
    private List<CompletionItem> _filteredItems;
    private string _filterText = string.Empty;
    private readonly ListControl _list;
    private Rectangle _bounds;

    private static readonly Color Bg       = Color.Grey11;
    private static readonly Color Fg       = Color.Grey93;
    private static readonly Color BorderFg = Color.Grey50;
    private static readonly Color SelBg    = Color.SteelBlue;
    private static readonly Color SelFg    = Color.White;

    public int SelectedIndex => _list.SelectedIndex;
    public string FilterText => _filterText;
    public bool HasVisibleItems => _filteredItems.Count > 0;

    public CompletionItem? GetSelected() => _list.SelectedItem?.Tag as CompletionItem;

    /// <summary>Fires when the user clicks an item — signals IdeApp to accept and insert.</summary>
    public event EventHandler<CompletionItem>? ItemAccepted;

    public void SelectNext()
    {
        if (_list.SelectedIndex < _list.Items.Count - 1)
            _list.SelectedIndex++;
        Invalidate();
    }

    public void SelectPrev()
    {
        if (_list.SelectedIndex > 0)
            _list.SelectedIndex--;
        Invalidate();
    }

    /// <summary>Updates the visible item list based on a prefix filter (case-insensitive).</summary>
    public void SetFilter(string prefix)
    {
        _filterText = prefix;
        _filteredItems = string.IsNullOrEmpty(prefix)
            ? _allItems
            : _allItems
                .Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        _list.Items = _filteredItems.Select(BuildListItem).ToList();
        if (_filteredItems.Count > 0) _list.SelectedIndex = 0;
        Invalidate();
    }

    public static string GetKindIcon(int kind) => kind switch
    {
        2 or 3 => "f()",
        4 => "new",
        5 or 6 => "var",
        7 => "cls",
        8 => "ifc",
        10 => "prp",
        _ => "   "
    };

    private static ListItem BuildListItem(CompletionItem item)
    {
        string icon = GetKindIcon(item.Kind);
        string text = icon + " " + Markup.Escape(item.Label);
        if (item.Detail != null)
            text += "  [dim]" + Markup.Escape(item.Detail) + "[/]";
        return new ListItem(text) { Tag = item };
    }

    public LspCompletionPortalContent(
        List<CompletionItem> items, int cursorX, int cursorY,
        int windowWidth, int windowHeight)
    {
        _allItems      = items;
        _filteredItems = items;

        _list = new ListControl
        {
            BackgroundColor          = Bg,
            ForegroundColor          = Fg,
            FocusedBackgroundColor   = Bg,   // keep same bg in "focused" state
            FocusedForegroundColor   = Fg,
            HighlightBackgroundColor = SelBg,
            HighlightForegroundColor = SelFg,
            HoverHighlightsItems     = false, // no mouse-hover highlight
            AutoAdjustWidth          = false,
        };

        // Mark the list as focused so the selected row uses HighlightBackground.
        // (Without focus the selected row falls back to unfocused/grey theme colours.)
        _list.HasFocus = true;

        foreach (var item in items)
            _list.AddItem(BuildListItem(item));

        if (items.Count > 0) _list.SelectedIndex = 0;

        // Width: icon + space + label + "  " + detail — cap at 60
        int maxLabel = items.Take(20).Max(i =>
        {
            string icon  = GetKindIcon(i.Kind);
            string label = icon + " " + i.Label;
            if (i.Detail != null) label += "  " + i.Detail;
            return AnsiConsoleHelper.StripSpectreLength(label);
        });
        int popupW = Math.Min(60, maxLabel + 4);  // +2 border + 2 padding

        int visibleItems = Math.Min(12, items.Count);
        int popupH = visibleItems + 2;  // +2 border

        int y = LspPortalLayout.PickY(cursorY, popupH, windowHeight, preferAbove: false, out popupH);
        _bounds = LspPortalLayout.Clamp(cursorX, y, popupW, popupH, windowWidth, windowHeight);
    }

    public Rectangle GetPortalBounds() => _bounds;

    // ── IDOMPaintable ──────────────────────────────────────────────────────────

    public LayoutSize MeasureDOM(LayoutConstraints c) => new(_bounds.Width, _bounds.Height);

    public void PaintDOM(CharacterBuffer buffer, LayoutRect bounds, LayoutRect clip,
                         Color defaultFg, Color defaultBg)
    {
        _actualX = bounds.X; _actualY = bounds.Y;
        _actualWidth = bounds.Width; _actualHeight = bounds.Height;

        // Draw border
        buffer.DrawBox(bounds, BoxChars.Rounded, BorderFg, Bg);

        // Delegate all item rendering to ListControl — it handles markup, scroll
        // indicator, and selection highlighting.
        var inner = new LayoutRect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        ((IDOMPaintable)_list).PaintDOM(buffer, inner, clip, Fg, Bg);
    }

    // ── IMouseAwareControl — scroll wheel only ─────────────────────────────────

    public bool WantsMouseEvents => true;
    public bool CanFocusWithMouse => false;

    public bool ProcessMouseEvent(MouseEventArgs args)
    {
        if (args.HasFlag(MouseFlags.WheeledUp))   { SelectPrev(); return true; }
        if (args.HasFlag(MouseFlags.WheeledDown)) { SelectNext(); return true; }

        // Delegate clicks to inner ListControl, adjusting for the 1-cell border offset.
        // After selection updates, fire ItemAccepted so IdeApp can insert the text.
        if (args.HasFlag(MouseFlags.Button1Clicked))
        {
            var innerArgs = args.WithPosition(
                new System.Drawing.Point(args.Position.X - 1, args.Position.Y - 1));
            ((IMouseAwareControl)_list).ProcessMouseEvent(innerArgs);

            var clicked = GetSelected();
            if (clicked != null)
                ItemAccepted?.Invoke(this, clicked);
            else
                Invalidate();  // no item → just redraw the new selection

            return true;
        }

        return false;
    }

#pragma warning disable CS0067 // required by IMouseAwareControl; portal never fires these
    public event EventHandler<MouseEventArgs>? MouseClick;
    public event EventHandler<MouseEventArgs>? MouseDoubleClick;
    public event EventHandler<MouseEventArgs>? MouseEnter;
    public event EventHandler<MouseEventArgs>? MouseLeave;
    public event EventHandler<MouseEventArgs>? MouseMove;
#pragma warning restore CS0067

    // ── IWindowControl boilerplate ─────────────────────────────────────────────

    private int _actualX, _actualY, _actualWidth, _actualHeight;
    public int ActualX => _actualX;
    public int ActualY => _actualY;
    public int ActualWidth => _actualWidth;
    public int ActualHeight => _actualHeight;
    public int? ContentWidth  => _bounds.Width;
    public int? ContentHeight => _bounds.Height;
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;
    public VerticalAlignment   VerticalAlignment   { get; set; } = VerticalAlignment.Top;
    public IContainer? Container { get; set; }
    public Margin Margin { get; set; } = new(0, 0, 0, 0);
    public StickyPosition StickyPosition { get; set; } = StickyPosition.None;
    public string? Name { get; set; }
    public object? Tag { get; set; }
    public bool Visible { get; set; } = true;
    public int? Width { get; set; }
    public Size GetLogicalContentSize() => new(_bounds.Width, _bounds.Height);
    public void Invalidate() => Container?.Invalidate(true);
    public void Dispose() { }
}

// ──────────────────────────────────────────────────────────────────────────────
// Location list portal — used for Find References, Go-to-Implementation (multiple)
// Interactive list with Up/Down/Enter/Escape support, shows file:line + context
// ──────────────────────────────────────────────────────────────────────────────

internal record LspLocationEntry(string FilePath, int Line, int Column, string DisplayText);

internal class LspLocationListPortalContent : IWindowControl, IDOMPaintable, IHasPortalBounds, IMouseAwareControl
{
    private readonly List<LspLocationEntry> _entries;
    private readonly ListControl _list;
    private Rectangle _bounds;

    private static readonly Color Bg       = Color.Grey11;
    private static readonly Color Fg       = Color.Grey93;
    private static readonly Color BorderFg = Color.Grey50;
    private static readonly Color SelBg    = Color.SteelBlue;
    private static readonly Color SelFg    = Color.White;

    public int SelectedIndex => _list.SelectedIndex;

    public LspLocationEntry? GetSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _entries.Count)
            return null;
        return _entries[_list.SelectedIndex];
    }

    public event EventHandler<LspLocationEntry>? ItemAccepted;

    public void SelectNext()
    {
        if (_list.SelectedIndex < _list.Items.Count - 1)
            _list.SelectedIndex++;
        Invalidate();
    }

    public void SelectPrev()
    {
        if (_list.SelectedIndex > 0)
            _list.SelectedIndex--;
        Invalidate();
    }

    private static ListItem BuildListItem(LspLocationEntry entry)
    {
        var fileName = Path.GetFileName(entry.FilePath);
        var text = $"[cyan]{Markup.Escape(fileName)}[/]:[dim]{entry.Line}[/] {Markup.Escape(entry.DisplayText)}";
        return new ListItem(text) { Tag = entry };
    }

    public LspLocationListPortalContent(
        List<LspLocationEntry> entries,
        int cursorX, int cursorY,
        int windowWidth, int windowHeight)
    {
        _entries = entries;

        _list = new ListControl
        {
            BackgroundColor          = Bg,
            ForegroundColor          = Fg,
            FocusedBackgroundColor   = Bg,
            FocusedForegroundColor   = Fg,
            HighlightBackgroundColor = SelBg,
            HighlightForegroundColor = SelFg,
            HoverHighlightsItems     = false,
            AutoAdjustWidth          = false,
        };
        _list.HasFocus = true;

        foreach (var entry in entries)
            _list.AddItem(BuildListItem(entry));

        if (entries.Count > 0) _list.SelectedIndex = 0;

        // Size: fit the widest entry, cap at 80
        int maxWidth = entries.Take(30).Max(e =>
        {
            var fileName = Path.GetFileName(e.FilePath);
            return fileName.Length + 1 + e.Line.ToString().Length + 1 + e.DisplayText.Length;
        });
        int popupW = Math.Min(80, Math.Max(30, maxWidth + 6));
        int visibleItems = Math.Min(15, entries.Count);
        int popupH = visibleItems + 2;

        int y = LspPortalLayout.PickY(cursorY, popupH, windowHeight, preferAbove: false, out popupH);
        _bounds = LspPortalLayout.Clamp(cursorX, y, popupW, popupH, windowWidth, windowHeight);
    }

    public Rectangle GetPortalBounds() => _bounds;

    public LayoutSize MeasureDOM(LayoutConstraints c) => new(_bounds.Width, _bounds.Height);

    public void PaintDOM(CharacterBuffer buffer, LayoutRect bounds, LayoutRect clip,
                         Color defaultFg, Color defaultBg)
    {
        _actualX = bounds.X; _actualY = bounds.Y;
        _actualWidth = bounds.Width; _actualHeight = bounds.Height;

        buffer.DrawBox(bounds, BoxChars.Rounded, BorderFg, Bg);

        var inner = new LayoutRect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        ((IDOMPaintable)_list).PaintDOM(buffer, inner, clip, Fg, Bg);
    }

    public bool WantsMouseEvents => true;
    public bool CanFocusWithMouse => false;

    public bool ProcessMouseEvent(MouseEventArgs args)
    {
        if (args.HasFlag(MouseFlags.WheeledUp))   { SelectPrev(); return true; }
        if (args.HasFlag(MouseFlags.WheeledDown)) { SelectNext(); return true; }

        if (args.HasFlag(MouseFlags.Button1Clicked))
        {
            var innerArgs = args.WithPosition(
                new System.Drawing.Point(args.Position.X - 1, args.Position.Y - 1));
            ((IMouseAwareControl)_list).ProcessMouseEvent(innerArgs);

            var clicked = GetSelected();
            if (clicked != null)
                ItemAccepted?.Invoke(this, clicked);
            else
                Invalidate();
            return true;
        }

        return false;
    }

#pragma warning disable CS0067
    public event EventHandler<MouseEventArgs>? MouseClick;
    public event EventHandler<MouseEventArgs>? MouseDoubleClick;
    public event EventHandler<MouseEventArgs>? MouseEnter;
    public event EventHandler<MouseEventArgs>? MouseLeave;
    public event EventHandler<MouseEventArgs>? MouseMove;
#pragma warning restore CS0067

    private int _actualX, _actualY, _actualWidth, _actualHeight;
    public int ActualX => _actualX;
    public int ActualY => _actualY;
    public int ActualWidth => _actualWidth;
    public int ActualHeight => _actualHeight;
    public int? ContentWidth  => _bounds.Width;
    public int? ContentHeight => _bounds.Height;
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;
    public VerticalAlignment   VerticalAlignment   { get; set; } = VerticalAlignment.Top;
    public IContainer? Container { get; set; }
    public Margin Margin { get; set; } = new(0, 0, 0, 0);
    public StickyPosition StickyPosition { get; set; } = StickyPosition.None;
    public string? Name { get; set; }
    public object? Tag { get; set; }
    public bool Visible { get; set; } = true;
    public int? Width { get; set; }
    public Size GetLogicalContentSize() => new(_bounds.Width, _bounds.Height);
    public void Invalidate() => Container?.Invalidate(true);
    public void Dispose() { }
}
