using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Events;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Spectre.Console;
using Color = Spectre.Console.Color;
using Rectangle = System.Drawing.Rectangle;

namespace DotNetIDE;

internal record ContextMenuItem(string Label, string? Shortcut = null, Action? Action = null, bool Enabled = true)
{
    public bool IsSeparator => Label == "-";
}

/// <summary>
/// Right-click context menu portal built on PortalContentContainer + vertical MenuControl.
/// MenuControl handles all rendering (separators, shortcuts, disabled items, highlight)
/// and keyboard/mouse navigation natively.
/// </summary>
internal class ContextMenuPortal : PortalContentContainer
{
    private readonly MenuControl _menu;
    private readonly List<ContextMenuItem> _items;
    private readonly Dictionary<MenuItem, ContextMenuItem> _menuItemMap = new();

    private static readonly Color MenuBg   = Color.Grey11;
    private static readonly Color MenuFg   = Color.Grey93;
    private static readonly Color SelBg    = Color.SteelBlue;
    private static readonly Color SelFg    = Color.White;

    public event EventHandler<ContextMenuItem>? ItemSelected;
    public event EventHandler? Dismissed;

    public ContextMenuPortal(List<ContextMenuItem> items, int anchorX, int anchorY,
        int windowWidth, int windowHeight)
    {
        _items = items;

        _menu = new MenuControl
        {
            Orientation = MenuOrientation.Vertical,
            DropdownBackgroundColor = MenuBg,
            DropdownForegroundColor = MenuFg,
            DropdownHighlightBackgroundColor = SelBg,
            DropdownHighlightForegroundColor = SelFg,
            MenuBarBackgroundColor = MenuBg,
            MenuBarForegroundColor = MenuFg,
            MenuBarHighlightBackgroundColor = SelBg,
            MenuBarHighlightForegroundColor = SelFg,
        };

        BackgroundColor = MenuBg;
        ForegroundColor = MenuFg;
        DismissOnOutsideClick = true;

        // Build menu items from ContextMenuItems
        foreach (var item in items)
        {
            var mi = new MenuItem
            {
                Text = item.Label,
                Shortcut = item.Shortcut,
                IsSeparator = item.IsSeparator,
                IsEnabled = item.Enabled && !item.IsSeparator,
                Action = item.Action,
            };
            _menu.AddItem(mi);
            if (!item.IsSeparator)
                _menuItemMap[mi] = item;
        }

        _menu.HasFocus = true;

        _menu.ItemSelected += (_, mi) =>
        {
            if (_menuItemMap.TryGetValue(mi, out var contextItem))
                ItemSelected?.Invoke(this, contextItem);
        };

        AddChild(_menu);
        SetFocusOnFirstChild();

        // Calculate bounds
        int maxLabelW = 0;
        int maxShortcutW = 0;
        foreach (var item in items)
        {
            if (item.IsSeparator) continue;
            maxLabelW = Math.Max(maxLabelW, item.Label.Length);
            if (item.Shortcut != null)
                maxShortcutW = Math.Max(maxShortcutW, item.Shortcut.Length);
        }

        // width = padding(2) + label + gap(2) + shortcut + padding(2) + border(2)
        int contentW = maxLabelW + (maxShortcutW > 0 ? maxShortcutW + 2 : 0) + 4;
        int popupW = Math.Clamp(contentW + 2, 16, 50); // +2 for border
        int popupH = items.Count + 2; // +2 for border

        int y = LspPortalLayout.PickY(anchorY, popupH, windowHeight, preferAbove: false, out popupH);
        PortalBounds = LspPortalLayout.Clamp(anchorX, y, popupW, popupH, windowWidth, windowHeight);
    }

    /// <summary>
    /// Adjusts mouse args to account for the 1-cell border, then delegates to the
    /// PortalContentContainer base which routes to the MenuControl child.
    /// </summary>
    public override bool ProcessMouseEvent(MouseEventArgs args)
    {
        // Offset by (-1,-1) to account for the border we draw around the menu content.
        // The portal receives coordinates relative to PortalBounds (0,0 = top-left of border),
        // but PortalContentContainer.HitTestChild expects (0,0) = top-left of child content.
        var adjusted = args.WithPosition(
            new Point(args.Position.X - 1, args.Position.Y - 1));

        // Forward hover/mouse-move to the MenuControl for highlight tracking
        if (args.HasAnyFlag(SharpConsoleUI.Drivers.MouseFlags.ReportMousePosition))
        {
            if (_menu is IMouseAwareControl mac && mac.WantsMouseEvents)
                mac.ProcessMouseEvent(adjusted);
            return true;
        }

        return base.ProcessMouseEvent(adjusted);
    }

    public new bool ProcessKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // Delegate to the PortalContentContainer which forwards to MenuControl
        if (base.ProcessKey(key))
            return true;

        // Consume all keys while context menu is open
        return true;
    }

    protected override void PaintPortalContent(CharacterBuffer buffer, LayoutRect bounds,
        LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        // Draw rounded border
        buffer.DrawBox(bounds, BoxChars.Rounded, Color.Grey50, MenuBg);

        // Paint menu content inside border
        var inner = new LayoutRect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        base.PaintPortalContent(buffer, inner, clipRect, MenuFg, MenuBg);
    }
}
