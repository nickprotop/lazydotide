using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace DotNetIDE;

public class FindReplaceDialog : DialogBase<bool>
{
    private const int DialogWidth = 60;
    private const int DialogHeight = 13;

    private readonly EditorManager _editorManager;
    private PromptControl _findPrompt    = null!;
    private PromptControl _replacePrompt = null!;
    private CheckboxControl _caseBox     = null!;
    private MarkupControl _statusLabel   = null!;

    private FindReplaceDialog(EditorManager editorManager)
    {
        _editorManager = editorManager;
    }

    public static Task<bool> ShowAsync(ConsoleWindowSystem ws, EditorManager editorManager)
        => new FindReplaceDialog(editorManager).ShowAsync(ws);

    protected override string GetTitle() => "Find & Replace";
    protected override (int width, int height) GetSize() => (DialogWidth, DialogHeight);
    protected override bool IsModal => false;
    protected override bool AlwaysOnTop => true;
    protected override bool GetDefaultResult() => false;

    protected override void BuildContent()
    {
        _findPrompt    = new PromptControl { Prompt = "Find:    ", InputWidth = 36 };
        _replacePrompt = new PromptControl { Prompt = "Replace: ", InputWidth = 36 };
        _caseBox       = new CheckboxControl { Label = "Case-sensitive", Checked = false };

        var findNextBtn = Controls.Button("[grey93]Find Next[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkGreen)
            .WithFocusedForegroundColor(Color.White)
            .Build();

        var findPrevBtn = Controls.Button("[grey93]Find Prev[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkGreen)
            .WithFocusedForegroundColor(Color.White)
            .Build();

        var replaceBtn = Controls.Button("[grey93]Replace[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkOrange)
            .WithFocusedForegroundColor(Color.White)
            .Build();

        var replaceAllBtn = Controls.Button("[grey93]Replace All[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkOrange)
            .WithFocusedForegroundColor(Color.White)
            .Build();

        findNextBtn.Click   += (_, _) => DoFindNext();
        findPrevBtn.Click   += (_, _) => DoFindPrev();
        replaceBtn.Click    += (_, _) => DoReplace();
        replaceAllBtn.Click += (_, _) => DoReplaceAll();

        var actionRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Column(col => col.Add(findNextBtn))
            .Column(col => col.Width(1))
            .Column(col => col.Add(findPrevBtn))
            .Column(col => col.Width(1))
            .Column(col => col.Add(replaceBtn))
            .Column(col => col.Width(1))
            .Column(col => col.Add(replaceAllBtn))
            .Build();

        _statusLabel = new MarkupControl(new List<string> { "" });

        var closeBtn = Controls.Button("[grey93]Close[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Grey50)
            .WithFocusedForegroundColor(Color.White)
            .Build();
        closeBtn.Click += (_, _) => CloseWithResult(false);

        var bottomRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var statusCol = new ColumnContainer(bottomRow);
        statusCol.AddContent(_statusLabel);
        bottomRow.AddColumn(statusCol);
        var closeCol = new ColumnContainer(bottomRow) { Width = 10 };
        closeCol.AddContent(closeBtn);
        bottomRow.AddColumn(closeCol);
        bottomRow.StickyPosition = StickyPosition.Bottom;

        Modal.AddControl(new MarkupControl(new List<string> { "" }));
        Modal.AddControl(_findPrompt);
        Modal.AddControl(_replacePrompt);
        Modal.AddControl(_caseBox);
        Modal.AddControl(new MarkupControl(new List<string> { "" }));
        Modal.AddControl(actionRow);
        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());
        Modal.AddControl(bottomRow);
    }

    private void SetStatus(string message)
    {
        _statusLabel.SetContent(new List<string> { message });
    }

    private string GetMatchStatus()
    {
        var editor = _editorManager.CurrentEditor;
        if (editor == null || !editor.HasActiveSearch) return "[yellow]Not found[/]";
        return $"Match {editor.CurrentMatchIndex + 1} of {editor.MatchCount}";
    }

    private void DoFindNext()
    {
        bool found = _editorManager.FindNext(_findPrompt.Input, _caseBox.Checked);
        SetStatus(found ? GetMatchStatus() : "[yellow]Not found[/]");
    }

    private void DoFindPrev()
    {
        bool found = _editorManager.FindPrevious(_findPrompt.Input, _caseBox.Checked);
        SetStatus(found ? GetMatchStatus() : "[yellow]Not found[/]");
    }

    private void DoReplace()
    {
        bool found = _editorManager.ReplaceNext(_findPrompt.Input, _replacePrompt.Input, _caseBox.Checked);
        SetStatus(found ? GetMatchStatus() : "[yellow]No more matches[/]");
    }

    private void DoReplaceAll()
    {
        int count = _editorManager.ReplaceAll(_findPrompt.Input, _replacePrompt.Input, _caseBox.Checked);
        SetStatus(count > 0
            ? $"[green]{count} replacement{(count != 1 ? "s" : "")} made[/]"
            : "[yellow]Not found[/]");
    }

    protected override void OnCleanup()
    {
        _editorManager.ClearSearch();
        base.OnCleanup();
    }
}
