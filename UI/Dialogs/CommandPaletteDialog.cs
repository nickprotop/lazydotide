using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Layout;
using Spectre.Console;
using Rectangle = System.Drawing.Rectangle;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public class CommandPalettePortal : PortalContentContainer
{
    private readonly CommandRegistry _registry;
    private readonly PromptControl _searchInput;
    private readonly ListControl _commandList;
    private readonly MarkupControl _statusText;

    public event EventHandler<IdeCommand?>? CommandSelected;

    public CommandPalettePortal(CommandRegistry registry, int windowWidth, int windowHeight)
    {
        _registry = registry;

        // Portal style
        DismissOnOutsideClick = true;
        BorderStyle = BoxChars.Rounded;
        BorderColor = Color.Grey50;
        BorderBackgroundColor = Color.Grey15;
        BackgroundColor = Color.Grey15;
        ForegroundColor = Color.Grey93;

        // Search input
        _searchInput = Controls.Prompt()
            .WithPrompt("> ")
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithMargin(1, 0, 1, 0)
            .Build();
        AddChild(_searchInput);

        // Top separator
        AddChild(Controls.RuleBuilder()
            .WithColor(Color.Grey23)
            .Build());

        // Command list
        _commandList = Controls.List()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithColors(Color.Grey93, Color.Grey15)
            .WithFocusedColors(Color.Grey93, Color.Grey15)
            .WithHighlightColors(Color.White, Color.Grey35)
            .WithDoubleClickActivation(true)
            .WithTitle(string.Empty)
            .Build();
        AddChild(_commandList);

        // Bottom separator
        AddChild(Controls.RuleBuilder()
            .WithColor(Color.Grey23)
            .StickyBottom()
            .Build());

        // Status line
        _statusText = Controls.Markup()
            .AddLine($"[grey50]{registry.All.Count} commands[/]")
            .WithAlignment(HorizontalAlignment.Left)
            .WithMargin(1, 0, 1, 0)
            .StickyBottom()
            .Build();
        AddChild(_statusText);

        // Hint line
        AddChild(Controls.Markup()
            .AddLine("[grey70]Enter/Double-click: Execute  •  Escape: Cancel  •  ↑↓: Navigate[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(0, 0, 0, 0)
            .StickyBottom()
            .Build());

        // Calculate bounds
        int w = Math.Min(85, windowWidth - 4);
        int h = Math.Min(22, windowHeight - 2);
        int x = (windowWidth - w) / 2;
        int y = 1;
        PortalBounds = new Rectangle(x, y, w, h);

        // The list's scroll logic queries GetVisibleHeightForControl which returns the
        // full portal viewport height, not just the list's portion.  Cap MaxVisibleItems
        // to the actual rows available for the list (total height minus border minus the
        // other fixed-height children: prompt 1, rule 1, rule 1, status 1, hint 1 = 5).
        int listRows = h - 2 - 5; // 2 for border, 5 for other children
        _commandList.MaxVisibleItems = Math.Max(1, listRows);

        // Populate initial list
        UpdateCommandList("");

        // Wire events
        _searchInput.InputChanged += (_, newText) => UpdateCommandList(newText);

        _commandList.ItemActivated += (_, item) =>
        {
            if (item?.Tag is IdeCommand command)
                CommandSelected?.Invoke(this, command);
        };

        SetFocusOnFirstChild();
    }

    public new bool ProcessKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            CommandSelected?.Invoke(this, null);
            return true;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (_searchInput.HasFocus && _commandList.Items.Count > 0)
            {
                _commandList.SetFocus(true, FocusReason.Keyboard);
                return true;
            }
            if (_commandList.HasFocus)
            {
                var item = _commandList.SelectedItem;
                if (item?.Tag is IdeCommand cmd)
                    CommandSelected?.Invoke(this, cmd);
                return true;
            }
        }

        if (key.Key == ConsoleKey.DownArrow && _searchInput.HasFocus && _commandList.Items.Count > 0)
        {
            _commandList.SetFocus(true, FocusReason.Keyboard);
            return true;
        }

        if (key.Key == ConsoleKey.UpArrow && _commandList.HasFocus && _commandList.SelectedIndex <= 0)
        {
            _searchInput.SetFocus(true, FocusReason.Keyboard);
            return true;
        }

        // Redirect typing to search when list is focused
        if (_commandList.HasFocus)
        {
            char ch = key.KeyChar;
            if (ch != '\0' && !char.IsControl(ch))
            {
                _searchInput.SetInput(_searchInput.Input + ch);
                return true;
            }
            if (key.Key == ConsoleKey.Backspace && _searchInput.Input.Length > 0)
            {
                _searchInput.SetInput(_searchInput.Input[..^1]);
                return true;
            }
        }

        base.ProcessKey(key);
        return true; // swallow all keys while palette is open
    }

    private void UpdateCommandList(string searchQuery)
    {
        _commandList.ClearItems();

        var filtered = _registry.Search(searchQuery);

        foreach (var cmd in filtered)
        {
            var label = $"{cmd.Icon} [dim]{cmd.Category,-8}[/] {cmd.Label,-34} [grey50]{cmd.Keybinding ?? ""}[/]";
            _commandList.AddItem(new ListItem(label) { Tag = cmd });
        }

        var statusLine = string.IsNullOrWhiteSpace(searchQuery)
            ? $"[grey50]{filtered.Count} commands[/]"
            : $"[grey50]{filtered.Count} of {_registry.All.Count} commands[/]";

        _statusText.SetContent(new List<string> { statusLine });
    }
}
