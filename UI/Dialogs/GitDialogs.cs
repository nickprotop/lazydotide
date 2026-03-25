using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class GitCommitDialog : DialogBase<string?>
{
    private const int CommitDialogMaxWidth = 72;
    private const int CommitDialogMinWidth = 50;
    private const int CommitDialogMaxHeight = 18;
    private const int CommitDialogMinHeight = 12;
    private const int DialogPadding = 4;

    private readonly string _statusSummary;
    private MultilineEditControl _editor = null!;

    private GitCommitDialog(string statusSummary) { _statusSummary = statusSummary; }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string statusSummary)
        => new GitCommitDialog(statusSummary).ShowAsync(ws);

    protected override string GetTitle() => "Git Commit";
    protected override bool GetResizable() => true;
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (
            Math.Min(CommitDialogMaxWidth, Math.Max(CommitDialogMinWidth, desktop.Width - DialogPadding)),
            Math.Min(CommitDialogMaxHeight, Math.Max(CommitDialogMinHeight, desktop.Height - DialogPadding)));
    }

    protected override void BuildContent()
    {
        if (!string.IsNullOrEmpty(_statusSummary))
        {
            Modal.AddControl(Controls.Markup()
                .AddLine($"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(_statusSummary)}[/]")
                .WithAlignment(HorizontalAlignment.Left)
                .Build());
        }

        _editor = Controls.MultilineEdit()
            .WithPlaceholder("Enter commit message...")
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithWrapMode(WrapMode.Wrap)
            .Build();
        _editor.IsEditing = true;
        Modal.AddControl(_editor);

        var commitBtn = Controls.Button("[grey93]Commit[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkGreen)
            .WithFocusedForegroundColor(Color.White)
            .WithMargin(0, 1, 0, 0)
            .Build();

        var cancelBtn = Controls.Button("[grey93]Cancel[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Grey50)
            .WithFocusedForegroundColor(Color.White)
            .WithMargin(0, 1, 0, 0)
            .Build();

        commitBtn.Click += (_, _) => DoCommit();
        cancelBtn.Click += (_, _) => CloseWithResult(null);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var buttonRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Column(col => col.Add(commitBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(cancelBtn))
            .Build();
        Modal.AddControl(buttonRow);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Ctrl+Enter:Commit  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_editor);
    }

    private void DoCommit()
    {
        var msg = _editor.Content?.Trim();
        if (!string.IsNullOrEmpty(msg))
            CloseWithResult(msg);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter &&
            e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            DoCommit();
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}

public class GitBranchPickerDialog : DialogBase<string?>
{
    private const int StandardDialogMaxWidth = 50;
    private const int StandardDialogMinWidth = 30;
    private const int DialogPadding = 4;

    private readonly List<string> _branches;
    private readonly string _currentBranch;
    private ListControl _list = null!;

    private GitBranchPickerDialog(List<string> branches, string currentBranch)
    {
        _branches = branches;
        _currentBranch = currentBranch;
    }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, List<string> branches, string currentBranch)
        => new GitBranchPickerDialog(branches, currentBranch).ShowAsync(ws);

    protected override string GetTitle() => "Switch Branch";
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (
            Math.Min(StandardDialogMaxWidth, Math.Max(StandardDialogMinWidth, desktop.Width - DialogPadding)),
            Math.Min(_branches.Count + 5, Math.Min(20, desktop.Height - 2)));
    }

    protected override void BuildContent()
    {
        _list = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };

        foreach (var branch in _branches)
        {
            var label = branch == _currentBranch
                ? $"[cyan1]\u2713 {MarkupParser.Escape(branch)}[/]"
                : $"  {MarkupParser.Escape(branch)}";
            var item = new ListItem(label) { Tag = branch };
            _list.AddItem(item);
        }

        _list.DoubleClickActivates = true;
        _list.ItemActivated += (_, item) =>
        {
            if (item?.Tag is string branchName && branchName != _currentBranch)
                CloseWithResult(branchName);
        };

        Modal.AddControl(_list);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Switch  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_list);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            var selected = _list.SelectedIndex >= 0 ? _list.Items[_list.SelectedIndex] : null;
            if (selected?.Tag is string branchName && branchName != _currentBranch)
                CloseWithResult(branchName);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}

public class GitNewBranchDialog : DialogBase<string?>
{
    private PromptControl _input = null!;

    private GitNewBranchDialog() { }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws)
        => ((DialogBase<string?>)new GitNewBranchDialog()).ShowAsync(ws);

    protected override string GetTitle() => "New Branch";
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (Math.Min(50, Math.Max(30, desktop.Width - 4)), 9);
    }

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]New Branch[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        _input = Controls.Prompt()
            .WithPrompt("Branch name: ")
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        Modal.AddControl(_input);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Create  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_input);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            var name = _input.Input?.Trim();
            if (!string.IsNullOrEmpty(name))
                CloseWithResult(name);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}

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
    protected override (int width, int height) GetSize() => (55, 10);
    protected override bool GetDefaultResult() => false;
    protected override Color GetBorderColor() => Color.Red;

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Discard Changes[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        string message;
        if (_isAll)
            message = $"Discard [{ColorScheme.WarningMarkup}]ALL[/] working directory changes?";
        else
        {
            var name = Path.GetFileName(_path);
            message = $"Discard changes in [{ColorScheme.WarningMarkup}]{MarkupParser.Escape(name)}[/]?";
        }

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.SecondaryMarkup}]{message}[/]")
            .AddLine($"[{ColorScheme.ErrorMarkup}]This cannot be undone.[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 1, 0)
            .Build());

        var discardBtn = Controls.Button("[grey93]Discard (Y)[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Red)
            .WithFocusedForegroundColor(Color.White)
            .WithMargin(0, 1, 0, 0)
            .Build();

        var cancelBtn = Controls.Button("[grey93]Cancel (Esc)[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Grey50)
            .WithFocusedForegroundColor(Color.White)
            .WithMargin(0, 1, 0, 0)
            .Build();

        discardBtn.Click += (_, _) => CloseWithResult(true);
        cancelBtn.Click += (_, _) => CloseWithResult(false);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var buttonRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Column(col => col.Add(discardBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(cancelBtn))
            .Build();
        Modal.AddControl(buttonRow);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Y:Discard  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Y)
        {
            CloseWithResult(true);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}

public class GitStashDialog : DialogBase<string?>
{
    private PromptControl _input = null!;

    private GitStashDialog() { }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws)
        => ((DialogBase<string?>)new GitStashDialog()).ShowAsync(ws);

    protected override string GetTitle() => "Git Stash";
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (Math.Min(50, Math.Max(30, desktop.Width - 4)), 9);
    }

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Stash Changes[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        _input = Controls.Prompt()
            .WithPrompt("Message: ")
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        Modal.AddControl(_input);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Stash  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_input);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            var msg = _input.Input?.Trim();
            CloseWithResult(string.IsNullOrEmpty(msg) ? "LazyDotIDE stash" : msg);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}
