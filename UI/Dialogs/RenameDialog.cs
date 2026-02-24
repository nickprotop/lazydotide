using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public static class RenameDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string currentName)
    {
        var tcs = new TaskCompletionSource<string?>();

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(50, Math.Max(30, desktop.Width - 4));
        const int dialogHeight = 7;
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var modal = new WindowBuilder(ws)
            .WithTitle("Rename Symbol")
            .WithSize(dialogWidth, dialogHeight)
            .AtPosition(px, py)
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithColors(Color.Grey93, Color.Grey15)
            .Build();

        // Use builder's WithInput to set initial text before the control is attached.
        // This avoids size-dependent rendering issues that SetInput can trigger.
        var input = Controls.Prompt()
            .WithPrompt("New name: ")
            .WithInput(currentName)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        modal.AddControl(input);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Enter: Rename  •  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        string? result = null;

        void Accept()
        {
            var newName = input.Input?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != currentName)
                result = newName;
            modal.Close();
        }

        modal.OnClosed += (_, _) => tcs.TrySetResult(result);

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Enter)
            {
                Accept();
                e.Handled = true;
            }
            else if (e.KeyInfo.Key == ConsoleKey.Escape)
            {
                modal.Close();
                e.Handled = true;
            }
        };

        ws.AddWindow(modal);
        ws.SetActiveWindow(modal);
        input.SetFocus(true, FocusReason.Programmatic);

        return tcs.Task;
    }
}
