using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace DotNetIDE;

public class FindReplaceDialog : DialogBase<bool>
{
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
    protected override (int width, int height) GetSize() => (60, 13);
    protected override bool IsModal => false;
    protected override bool AlwaysOnTop => true;
    protected override bool GetDefaultResult() => false;

    protected override void BuildContent()
    {
        _findPrompt    = new PromptControl { Prompt = "Find:    ", InputWidth = 36 };
        _replacePrompt = new PromptControl { Prompt = "Replace: ", InputWidth = 36 };
        _caseBox       = new CheckboxControl { Label = "Case-sensitive", Checked = false };

        var findNextBtn    = new ButtonControl { Text = "Find Next",   Width = 12 };
        var findPrevBtn    = new ButtonControl { Text = "Find Prev",   Width = 12 };
        var replaceBtn     = new ButtonControl { Text = "Replace",     Width = 10 };
        var replaceAllBtn  = new ButtonControl { Text = "Replace All", Width = 13 };

        findNextBtn.Click   += (_, _) => DoFindNext();
        findPrevBtn.Click   += (_, _) => DoFindPrev();
        replaceBtn.Click    += (_, _) => DoReplace();
        replaceAllBtn.Click += (_, _) => DoReplaceAll();

        var actionRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var c1 = new ColumnContainer(actionRow); c1.AddContent(findNextBtn);   actionRow.AddColumn(c1);
        var c2 = new ColumnContainer(actionRow); c2.AddContent(findPrevBtn);   actionRow.AddColumn(c2);
        var c3 = new ColumnContainer(actionRow); c3.AddContent(replaceBtn);    actionRow.AddColumn(c3);
        var c4 = new ColumnContainer(actionRow); c4.AddContent(replaceAllBtn); actionRow.AddColumn(c4);
        actionRow.StickyPosition = StickyPosition.Bottom;

        _statusLabel = new MarkupControl(new List<string> { "" });

        var closeBtn = new ButtonControl { Text = "Close", Width = 8 };
        closeBtn.Click += (_, _) => CloseWithResult(false);

        var bottomRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var statusCol = new ColumnContainer(bottomRow);
        statusCol.AddContent(_statusLabel);
        bottomRow.AddColumn(statusCol);
        var closeCol = new ColumnContainer(bottomRow) { Width = 10 };
        closeCol.AddContent(closeBtn);
        bottomRow.AddColumn(closeCol);
        bottomRow.StickyPosition = StickyPosition.Bottom;

        Dialog.AddControl(new MarkupControl(new List<string> { "" }));
        Dialog.AddControl(_findPrompt);
        Dialog.AddControl(_replacePrompt);
        Dialog.AddControl(_caseBox);
        Dialog.AddControl(new MarkupControl(new List<string> { "" }));
        Dialog.AddControl(actionRow);
        Dialog.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        Dialog.AddControl(bottomRow);
    }

    private void SetStatus(string message)
    {
        _statusLabel.SetContent(new List<string> { message });
    }

    private void DoFindNext()
    {
        bool found = _editorManager.FindNext(_findPrompt.Input, _caseBox.Checked);
        SetStatus(found ? "" : "[yellow]Not found[/]");
    }

    private void DoFindPrev()
    {
        bool found = _editorManager.FindPrevious(_findPrompt.Input, _caseBox.Checked);
        SetStatus(found ? "" : "[yellow]Not found[/]");
    }

    private void DoReplace()
    {
        bool found = _editorManager.ReplaceNext(_findPrompt.Input, _replacePrompt.Input, _caseBox.Checked);
        SetStatus(found ? "" : "[yellow]Not found[/]");
    }

    private void DoReplaceAll()
    {
        int count = _editorManager.ReplaceAll(_findPrompt.Input, _replacePrompt.Input, _caseBox.Checked);
        SetStatus(count > 0
            ? $"[green]{count} replacement{(count != 1 ? "s" : "")} made[/]"
            : "[yellow]Not found[/]");
    }
}
