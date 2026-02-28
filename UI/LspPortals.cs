using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Spectre.Console;
using Color = Spectre.Console.Color;
using Rectangle = System.Drawing.Rectangle;

namespace DotNetIDE;

// ──────────────────────────────────────────────────────────────────────────────
// Tooltip portal — used for hover info and signature help (read-only text)
// Uses MarkupControl internally for all text rendering (Spectre markup support,
// word-wrap, alignment) — only the rounded border is drawn directly.
// Click anywhere on the tooltip to dismiss it.
// ──────────────────────────────────────────────────────────────────────────────

internal class LspTooltipPortalContent : PortalContentBase
{
    private const int TooltipMaxWidth = 80;

    private readonly MarkupControl _markup;
    private Rectangle _bounds;

    private static readonly Color Bg       = Color.Grey11;
    private static readonly Color Fg       = Color.Grey93;
    private static readonly Color BorderFg = Color.Grey50;

    /// <summary>Fires when the user clicks the tooltip — signals IdeApp to dismiss.</summary>
    public event EventHandler? Clicked;

    public LspTooltipPortalContent(
        List<string> markupLines, int cursorX, int cursorY,
        int windowWidth, int windowHeight,
        bool preferAbove = true)
    {
        DismissOnOutsideClick = true;
        BorderStyle = BoxChars.Rounded;
        BorderColor = BorderFg;
        BorderBackgroundColor = Bg;

        _markup = new MarkupControl(markupLines)
        {
            Margin = new Margin(1, 0, 1, 0)  // 1-column left/right padding inside border
        };

        // Measure using StripSpectreLength — lines are already Spectre markup
        int contentW = markupLines.Count > 0
            ? markupLines.Max(l => AnsiConsoleHelper.StripSpectreLength(l)) + 4  // +2 border + 2 padding
            : 20;
        int popupW = Math.Min(TooltipMaxWidth, contentW);

        // Account for word wrapping: MarkupControl wraps by default, so long lines
        // produce more rendered lines than input lines.  The inner width available
        // for text is popupW minus border (2) minus left+right padding (2).
        int innerW = popupW - 4;
        int wrappedLines = 0;
        foreach (var line in markupLines)
        {
            int lineW = AnsiConsoleHelper.StripSpectreLength(line);
            wrappedLines += (innerW > 0 && lineW > innerW)
                ? (int)Math.Ceiling((double)lineW / innerW)
                : 1;
        }
        int popupH = wrappedLines + 2;  // +2 for top/bottom border

        var pos = PortalPositioner.CalculateFromPoint(
            new System.Drawing.Point(cursorX, cursorY),
            new System.Drawing.Size(popupW, popupH),
            new Rectangle(1, 1, windowWidth - 2, windowHeight - 2),
            preferAbove ? PortalPlacement.AboveOrBelow : PortalPlacement.BelowOrAbove,
            new System.Drawing.Size(4, 3));
        _bounds = pos.Bounds;
    }

    public override Rectangle GetPortalBounds() => _bounds;

    public override bool ProcessMouseEvent(MouseEventArgs args)
    {
        if (args.HasFlag(MouseFlags.Button1Clicked))
        {
            Clicked?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }

    protected override void PaintPortalContent(CharacterBuffer buffer, LayoutRect bounds,
        LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        // Bounds are already the inner area (border drawn by base class)
        ((IDOMPaintable)_markup).PaintDOM(buffer, bounds, clipRect, Fg, Bg);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Completion portal — interactive list with Up/Down/Enter/Tab/Escape support
// Uses ListControl internally for item rendering (markup support, scroll indicator,
// correct highlight colours) — only the rounded border is drawn directly.
// ──────────────────────────────────────────────────────────────────────────────

internal class LspCompletionPortalContent : PortalContentBase
{
    private const int CompletionMaxItems = 20;
    private const int CompletionVisibleItems = 12;
    private const int CompletionMaxWidth = 60;

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
        DismissOnOutsideClick = true;
        BorderStyle = BoxChars.Rounded;
        BorderColor = BorderFg;
        BorderBackgroundColor = Bg;

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
        int maxLabel = items.Take(CompletionMaxItems).Max(i =>
        {
            string icon  = GetKindIcon(i.Kind);
            string label = icon + " " + i.Label;
            if (i.Detail != null) label += "  " + i.Detail;
            return AnsiConsoleHelper.StripSpectreLength(label);
        });
        int popupW = Math.Min(CompletionMaxWidth, maxLabel + 4);  // +2 border + 2 padding

        int visibleItems = Math.Min(CompletionVisibleItems, items.Count);
        int popupH = visibleItems + 2;  // +2 border

        var pos = PortalPositioner.CalculateFromPoint(
            new System.Drawing.Point(cursorX, cursorY),
            new System.Drawing.Size(popupW, popupH),
            new Rectangle(1, 1, windowWidth - 2, windowHeight - 2),
            PortalPlacement.BelowOrAbove,
            new System.Drawing.Size(4, 3));
        _bounds = pos.Bounds;
    }

    public override Rectangle GetPortalBounds() => _bounds;

    public override bool ProcessMouseEvent(MouseEventArgs args)
    {
        if (args.HasFlag(MouseFlags.WheeledUp))   { SelectPrev(); return true; }
        if (args.HasFlag(MouseFlags.WheeledDown)) { SelectNext(); return true; }

        // Coordinates are already adjusted for border offset by the base class.
        if (args.HasFlag(MouseFlags.Button1Clicked))
        {
            ((IMouseAwareControl)_list).ProcessMouseEvent(args);

            var clicked = GetSelected();
            if (clicked != null)
                ItemAccepted?.Invoke(this, clicked);
            else
                Invalidate();  // no item → just redraw the new selection

            return true;
        }

        return false;
    }

    protected override void PaintPortalContent(CharacterBuffer buffer, LayoutRect bounds,
        LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        // Bounds are already the inner area (border drawn by base class)
        ((IDOMPaintable)_list).PaintDOM(buffer, bounds, clipRect, Fg, Bg);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Location list portal — used for Find References, Go-to-Implementation (multiple)
// Interactive list with Up/Down/Enter/Escape support, shows file:line + context
// ──────────────────────────────────────────────────────────────────────────────

internal record LspLocationEntry(string FilePath, int Line, int Column, string DisplayText);

internal class LspLocationListPortalContent : PortalContentBase
{
    private const int LocationListMinWidth = 30;
    private const int LocationListVisibleItems = 15;
    private const int LocationListMaxWidth = 80;

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
        DismissOnOutsideClick = true;
        BorderStyle = BoxChars.Rounded;
        BorderColor = BorderFg;
        BorderBackgroundColor = Bg;

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
        int popupW = Math.Min(LocationListMaxWidth, Math.Max(LocationListMinWidth, maxWidth + 6));
        int visibleItems = Math.Min(LocationListVisibleItems, entries.Count);
        int popupH = visibleItems + 2;

        var pos = PortalPositioner.CalculateFromPoint(
            new System.Drawing.Point(cursorX, cursorY),
            new System.Drawing.Size(popupW, popupH),
            new Rectangle(1, 1, windowWidth - 2, windowHeight - 2),
            PortalPlacement.BelowOrAbove,
            new System.Drawing.Size(4, 3));
        _bounds = pos.Bounds;
    }

    public override Rectangle GetPortalBounds() => _bounds;

    public override bool ProcessMouseEvent(MouseEventArgs args)
    {
        if (args.HasFlag(MouseFlags.WheeledUp))   { SelectPrev(); return true; }
        if (args.HasFlag(MouseFlags.WheeledDown)) { SelectNext(); return true; }

        // Coordinates are already adjusted for border offset by the base class.
        if (args.HasFlag(MouseFlags.Button1Clicked))
        {
            ((IMouseAwareControl)_list).ProcessMouseEvent(args);

            var clicked = GetSelected();
            if (clicked != null)
                ItemAccepted?.Invoke(this, clicked);
            else
                Invalidate();
            return true;
        }

        return false;
    }

    protected override void PaintPortalContent(CharacterBuffer buffer, LayoutRect bounds,
        LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        // Bounds are already the inner area (border drawn by base class)
        ((IDOMPaintable)_list).PaintDOM(buffer, bounds, clipRect, Fg, Bg);
    }
}
