using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

/// <summary>
/// Modal dialog for entering a git commit message.
/// Returns the message string, or null if cancelled.
/// </summary>
public static class GitCommitDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string statusSummary)
    {
        var tcs = new TaskCompletionSource<string?>();

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(72, Math.Max(50, desktop.Width - 4));
        int dialogHeight = Math.Min(18, Math.Max(12, desktop.Height - 4));
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var modal = new WindowBuilder(ws)
            .WithTitle("Git Commit")
            .WithSize(dialogWidth, dialogHeight)
            .AtPosition(px, py)
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithColors(Color.Grey93, Color.Grey15)
            .Build();

        if (!string.IsNullOrEmpty(statusSummary))
        {
            modal.AddControl(Controls.Markup()
                .AddLine($"[grey50]{Markup.Escape(statusSummary)}[/]")
                .WithAlignment(HorizontalAlignment.Left)
                .Build());
        }

        var editor = Controls.MultilineEdit()
            .WithPlaceholder("Enter commit message...")
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithWrapMode(WrapMode.Wrap)
            .Build();
        editor.IsEditing = true;

        modal.AddControl(editor);

        string? result = null;

        void DoCommit()
        {
            var msg = editor.Content?.Trim();
            if (!string.IsNullOrEmpty(msg))
            {
                result = msg;
                modal.Close();
            }
        }

        var commitBtn = new ButtonControl { Text = "Commit", Width = 10 };
        var cancelBtn = new ButtonControl { Text = "Cancel", Width = 10 };
        commitBtn.Click += (_, _) => DoCommit();
        cancelBtn.Click += (_, _) => modal.Close();

        var buttonRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left, StickyPosition = StickyPosition.Bottom };
        var commitCol = new ColumnContainer(buttonRow); commitCol.AddContent(commitBtn); buttonRow.AddColumn(commitCol);
        var cancelCol = new ColumnContainer(buttonRow); cancelCol.AddContent(cancelBtn); buttonRow.AddColumn(cancelCol);

        modal.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        modal.AddControl(buttonRow);
        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Ctrl+Enter: Commit  \u2022  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        modal.OnClosed += (_, _) => tcs.TrySetResult(result);

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Enter &&
                e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                DoCommit();
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
        editor.SetFocus(true, FocusReason.Programmatic);

        return tcs.Task;
    }
}

/// <summary>
/// Modal dialog for selecting a branch from a list.
/// Returns the selected branch name, or null if cancelled.
/// </summary>
public static class GitBranchPickerDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, List<string> branches, string currentBranch)
    {
        var tcs = new TaskCompletionSource<string?>();

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(50, Math.Max(30, desktop.Width - 4));
        int dialogHeight = Math.Min(branches.Count + 5, Math.Min(20, desktop.Height - 2));
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var modal = new WindowBuilder(ws)
            .WithTitle("Switch Branch")
            .WithSize(dialogWidth, dialogHeight)
            .AtPosition(px, py)
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithColors(Color.Grey93, Color.Grey15)
            .Build();

        var list = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        foreach (var branch in branches)
        {
            var label = branch == currentBranch
                ? $"[cyan1]\u2713 {Markup.Escape(branch)}[/]"
                : $"  {Markup.Escape(branch)}";
            var item = new ListItem(label) { Tag = branch };
            list.AddItem(item);
        }

        list.DoubleClickActivates = true;

        modal.AddControl(list);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Enter: Switch  \u2022  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        string? result = null;

        list.ItemActivated += (_, item) =>
        {
            if (item?.Tag is string branchName && branchName != currentBranch)
            {
                result = branchName;
                modal.Close();
            }
        };

        modal.OnClosed += (_, _) => tcs.TrySetResult(result);

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Enter)
            {
                var selected = list.SelectedIndex >= 0 ? list.Items[list.SelectedIndex] : null;
                if (selected?.Tag is string branchName && branchName != currentBranch)
                {
                    result = branchName;
                    modal.Close();
                }
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
        list.SetFocus(true, FocusReason.Programmatic);

        return tcs.Task;
    }
}

/// <summary>
/// Modal dialog for entering a new branch name.
/// Returns the branch name, or null if cancelled.
/// </summary>
public static class GitNewBranchDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws)
    {
        var tcs = new TaskCompletionSource<string?>();

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(50, Math.Max(30, desktop.Width - 4));
        const int dialogHeight = 7;
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var modal = new WindowBuilder(ws)
            .WithTitle("New Branch")
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
            .WithPrompt("Branch name: ")
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        modal.AddControl(input);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Enter: Create  \u2022  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        string? result = null;

        modal.OnClosed += (_, _) => tcs.TrySetResult(result);

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Enter)
            {
                var name = input.Input?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    result = name;
                    modal.Close();
                }
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

/// <summary>
/// Confirmation dialog for destructive git operations (discard changes).
/// Returns true to proceed, false to cancel.
/// </summary>
public class GitDiscardConfirmDialog : DialogBase<bool>
{
    private readonly string _path;
    private readonly bool _isAll;

    private GitDiscardConfirmDialog(string path, bool isAll) { _path = path; _isAll = isAll; }

    public static Task<bool> ShowAsync(ConsoleWindowSystem ws, string path)
        => new GitDiscardConfirmDialog(path, false).ShowAsync(ws);

    public static Task<bool> ShowAllAsync(ConsoleWindowSystem ws)
        => new GitDiscardConfirmDialog("", true).ShowAsync(ws);

    protected override string GetTitle() => "Discard Changes";
    protected override (int width, int height) GetSize() => (55, 8);
    protected override bool GetDefaultResult() => false;

    protected override void BuildContent()
    {
        string message;
        if (_isAll)
        {
            message = "  Discard [yellow]ALL[/] working directory changes?";
        }
        else
        {
            var name = Path.GetFileName(_path);
            message = $"  Discard changes in [yellow]{Markup.Escape(name)}[/]?";
        }

        var discardBtn = new ButtonControl { Text = "Discard", Width = 10 };
        var cancelBtn = new ButtonControl { Text = "Cancel", Width = 10 };

        discardBtn.Click += (_, _) => CloseWithResult(true);
        cancelBtn.Click += (_, _) => CloseWithResult(false);

        var buttonRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var discardCol = new ColumnContainer(buttonRow); discardCol.AddContent(discardBtn); buttonRow.AddColumn(discardCol);
        var cancelCol = new ColumnContainer(buttonRow); cancelCol.AddContent(cancelBtn); buttonRow.AddColumn(cancelCol);
        buttonRow.StickyPosition = StickyPosition.Bottom;

        Dialog.AddControl(new MarkupControl(new List<string> { "", message, "", "  [red]This cannot be undone.[/]" }));
        Dialog.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        Dialog.AddControl(buttonRow);
    }
}

/// <summary>
/// Modal dialog for entering a stash message.
/// Returns the message string, or null if cancelled.
/// </summary>
public static class GitStashDialog
{
    public static Task<string?> ShowAsync(ConsoleWindowSystem ws)
    {
        var tcs = new TaskCompletionSource<string?>();

        var desktop = ws.DesktopDimensions;
        int dialogWidth = Math.Min(50, Math.Max(30, desktop.Width - 4));
        const int dialogHeight = 7;
        int px = Math.Max(0, (desktop.Width - dialogWidth) / 2);
        int py = Math.Max(0, (desktop.Height - dialogHeight) / 2);

        var modal = new WindowBuilder(ws)
            .WithTitle("Git Stash")
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
            .WithPrompt("Message: ")
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        modal.AddControl(input);

        modal.AddControl(Controls.Markup()
            .AddLine("[grey50]Enter: Stash  \u2022  Escape: Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        // Default to a non-null result so Enter with empty message still stashes
        string? result = null;

        modal.OnClosed += (_, _) => tcs.TrySetResult(result);

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Enter)
            {
                var msg = input.Input?.Trim();
                result = string.IsNullOrEmpty(msg) ? "LazyDotIDE stash" : msg;
                modal.Close();
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
