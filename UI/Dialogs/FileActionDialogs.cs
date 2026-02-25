using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace DotNetIDE;

public static class NewFileDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string parentDir, bool isFolder = false)
    {
        var tcs = new TaskCompletionSource<string?>();

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(50, Math.Max(30, desktop.Width - 4));
        const int dialogHeight = 7;
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var title = isFolder ? "New Folder" : "New File";
        var promptText = isFolder ? "Folder name: " : "File name: ";

        var modal = new WindowBuilder(ws)
            .WithTitle(title)
            .WithSize(dialogWidth, dialogHeight)
            .AtPosition(px, py)
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithColors(Color.Grey93, Color.Grey15)
            .Build();

        var input = Controls.Prompt()
            .WithPrompt(promptText)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        modal.AddControl(input);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Enter: Create  \u2022  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        string? result = null;

        void Accept()
        {
            var name = input.Input?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            // Validate no invalid path characters
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;

            var fullPath = Path.Combine(parentDir, name);

            // Don't overwrite existing
            if (File.Exists(fullPath) || Directory.Exists(fullPath)) return;

            result = fullPath;
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

public static class FileRenameDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string currentPath)
    {
        var tcs = new TaskCompletionSource<string?>();
        var currentName = Path.GetFileName(currentPath);
        var parentDir = Path.GetDirectoryName(currentPath) ?? "";

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(50, Math.Max(30, desktop.Width - 4));
        const int dialogHeight = 7;
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var modal = new WindowBuilder(ws)
            .WithTitle("Rename")
            .WithSize(dialogWidth, dialogHeight)
            .AtPosition(px, py)
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithColors(Color.Grey93, Color.Grey15)
            .Build();

        var input = Controls.Prompt()
            .WithPrompt("New name: ")
            .WithInput(currentName)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        modal.AddControl(input);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Enter: Rename  \u2022  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        string? result = null;

        void Accept()
        {
            var newName = input.Input?.Trim();
            if (string.IsNullOrEmpty(newName) || newName == currentName) return;

            // Validate no invalid path characters
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;

            var newPath = Path.Combine(parentDir, newName);

            // Don't overwrite existing
            if (File.Exists(newPath) || Directory.Exists(newPath)) return;

            result = newPath;
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

public class DeleteConfirmDialog : DialogBase<bool>
{
    private readonly string _path;

    private DeleteConfirmDialog(string path) { _path = path; }

    public static Task<bool> ShowAsync(ConsoleWindowSystem ws, string path)
        => new DeleteConfirmDialog(path).ShowAsync(ws);

    protected override string GetTitle() => "Confirm Delete";
    protected override (int width, int height) GetSize() => (50, 8);
    protected override bool GetDefaultResult() => false;

    protected override void BuildContent()
    {
        var name = Path.GetFileName(_path);
        var isDir = Directory.Exists(_path);
        var message = isDir
            ? $"  Delete folder [yellow]{Markup.Escape(name)}[/] and all contents?"
            : $"  Delete [yellow]{Markup.Escape(name)}[/]?";

        var deleteBtn = new ButtonControl { Text = "Delete", Width = 10 };
        var cancelBtn = new ButtonControl { Text = "Cancel", Width = 10 };

        deleteBtn.Click += (_, _) => CloseWithResult(true);
        cancelBtn.Click += (_, _) => CloseWithResult(false);

        var buttonRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var deleteCol = new ColumnContainer(buttonRow); deleteCol.AddContent(deleteBtn); buttonRow.AddColumn(deleteCol);
        var cancelCol = new ColumnContainer(buttonRow); cancelCol.AddContent(cancelBtn); buttonRow.AddColumn(cancelCol);
        buttonRow.StickyPosition = StickyPosition.Bottom;

        Dialog.AddControl(new MarkupControl(new List<string> { "", message }));
        Dialog.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        Dialog.AddControl(buttonRow);
    }
}
