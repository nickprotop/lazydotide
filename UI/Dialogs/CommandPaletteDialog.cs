using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public static class CommandPaletteDialog
{
    public static void Show(
        ConsoleWindowSystem windowSystem,
        CommandRegistry registry,
        Action<IdeCommand?> onCommandSelected)
    {
        const int paletteWidth  = 85;
        const int paletteHeight = 22;
        var desktop = windowSystem.DesktopDimensions;
        int px = Math.Max(0, (desktop.Width - paletteWidth) / 2);
        int py = 0;

        var modal = new WindowBuilder(windowSystem)
            .WithTitle("Command Palette")
            .WithSize(paletteWidth, paletteHeight)
            .AtPosition(px, py)
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(true)
            .Movable(true)
            .Minimizable(false)
            .WithColors(Color.Grey93, Color.Grey15)
            .Build();

        var searchInput = Controls.Prompt()
            .WithPrompt("> ")
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithMargin(1, 0, 1, 0)
            .Build();

        modal.AddControl(searchInput);

        modal.AddControl(Controls.RuleBuilder()
            .WithColor(Color.Grey23)
            .Build());

        var commandList = Controls.List()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithColors(Color.Grey93, Color.Grey15)
            .WithFocusedColors(Color.Grey93, Color.Grey15)
            .WithHighlightColors(Color.White, Color.Grey35)
            .WithDoubleClickActivation(true)
            .WithTitle(string.Empty)
            .Build();

        modal.AddControl(commandList);

        var statusText = Controls.Markup()
            .AddLine($"[grey50]{registry.All.Count} commands[/]")
            .WithAlignment(HorizontalAlignment.Left)
            .WithMargin(1, 0, 1, 0)
            .StickyBottom()
            .Build();

        modal.AddControl(Controls.RuleBuilder()
            .WithColor(Color.Grey23)
            .StickyBottom()
            .Build());

        modal.AddControl(statusText);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey70]Enter/Double-click: Execute  •  Escape: Cancel  •  ↑↓: Navigate[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(0, 0, 0, 0)
            .StickyBottom()
            .Build());

        UpdateCommandList(commandList, statusText, registry, "");

        // Track which command (if any) was chosen before the window closes.
        // onCommandSelected is called exactly once from OnClosed, so every
        // close path (title-bar X, Escape, Enter, double-click) resets the flag.
        IdeCommand? selectedCommand = null;

        modal.OnClosed += (_, _) => onCommandSelected(selectedCommand);

        searchInput.InputChanged += (_, newText) =>
        {
            UpdateCommandList(commandList, statusText, registry, newText);
        };

        commandList.ItemActivated += (_, item) =>
        {
            if (item?.Tag is IdeCommand command)
            {
                selectedCommand = command;
                modal.Close();
            }
        };

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Enter)
            {
                if (searchInput.HasFocus && commandList.Items.Count > 0)
                {
                    commandList.SetFocus(true, FocusReason.Keyboard);
                    e.Handled = true;
                }
                else if (commandList.HasFocus)
                {
                    var selectedItem = commandList.SelectedItem;
                    if (selectedItem?.Tag is IdeCommand command)
                    {
                        selectedCommand = command;
                        modal.Close();
                    }
                    e.Handled = true;
                }
            }
            else if (e.KeyInfo.Key == ConsoleKey.Escape)
            {
                modal.Close();
                e.Handled = true;
            }
            else if (e.KeyInfo.Key == ConsoleKey.DownArrow)
            {
                if (searchInput.HasFocus && commandList.Items.Count > 0)
                {
                    commandList.SetFocus(true, FocusReason.Keyboard);
                    e.Handled = true;
                }
            }
            else if (e.KeyInfo.Key == ConsoleKey.UpArrow)
            {
                if (commandList.HasFocus && commandList.SelectedIndex <= 0)
                {
                    searchInput.SetFocus(true, FocusReason.Keyboard);
                    e.Handled = true;
                }
            }
            else if (commandList.HasFocus)
            {
                // While the list is focused, redirect printable characters and Backspace
                // to the search prompt so typing keeps filtering without switching focus.
                char ch = e.KeyInfo.KeyChar;
                if (ch != '\0' && !char.IsControl(ch))
                {
                    searchInput.SetInput(searchInput.Input + ch);
                    e.Handled = true;
                }
                else if (e.KeyInfo.Key == ConsoleKey.Backspace && searchInput.Input.Length > 0)
                {
                    searchInput.SetInput(searchInput.Input[..^1]);
                    e.Handled = true;
                }
            }
        };

        windowSystem.AddWindow(modal);
        windowSystem.SetActiveWindow(modal);
        searchInput.SetFocus(true, FocusReason.Programmatic);
    }

    private static void UpdateCommandList(
        ListControl list,
        MarkupControl status,
        CommandRegistry registry,
        string searchQuery)
    {
        list.ClearItems();

        var filtered = registry.Search(searchQuery);

        foreach (var cmd in filtered)
        {
            var label = $"{cmd.Icon} [dim]{cmd.Category,-8}[/] {cmd.Label,-34} [grey50]{cmd.Keybinding ?? ""}[/]";
            list.AddItem(new ListItem(label) { Tag = cmd });
        }

        var statusLine = string.IsNullOrWhiteSpace(searchQuery)
            ? $"[grey50]{filtered.Count} commands[/]"
            : $"[grey50]{filtered.Count} of {registry.All.Count} commands[/]";

        status.SetContent(new List<string> { statusLine });
    }
}
