using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace DotNetIDE;

public enum DialogResult { Save, DontSave, Cancel }

public class ConfirmSaveDialog : DialogBase<DialogResult>
{
    private const int DialogWidth = 50;
    private const int DialogHeight = 8;

    private readonly string _fileName;

    private ConfirmSaveDialog(string fileName) { _fileName = fileName; }

    public static Task<DialogResult> ShowAsync(ConsoleWindowSystem ws, string fileName)
        => new ConfirmSaveDialog(fileName).ShowAsync(ws);

    protected override string GetTitle() => "Unsaved Changes";
    protected override (int width, int height) GetSize() => (DialogWidth, DialogHeight);
    protected override DialogResult GetDefaultResult() => DialogResult.Cancel;

    protected override void BuildContent()
    {
        var saveBtn     = new ButtonControl { Text = "Save",       Width = 10 };
        var dontSaveBtn = new ButtonControl { Text = "Don't Save", Width = 14 };
        var cancelBtn   = new ButtonControl { Text = "Cancel",     Width = 10 };

        saveBtn.Click     += (_, _) => CloseWithResult(DialogResult.Save);
        dontSaveBtn.Click += (_, _) => CloseWithResult(DialogResult.DontSave);
        cancelBtn.Click   += (_, _) => CloseWithResult(DialogResult.Cancel);

        var buttonRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var saveCol   = new ColumnContainer(buttonRow); saveCol.AddContent(saveBtn);     buttonRow.AddColumn(saveCol);
        var dontCol   = new ColumnContainer(buttonRow); dontCol.AddContent(dontSaveBtn); buttonRow.AddColumn(dontCol);
        var cancelCol = new ColumnContainer(buttonRow); cancelCol.AddContent(cancelBtn); buttonRow.AddColumn(cancelCol);
        buttonRow.StickyPosition = StickyPosition.Bottom;

        Dialog.AddControl(new MarkupControl(new List<string>
        {
            "",
            $"  [yellow]{Spectre.Console.Markup.Escape(_fileName)}[/] has unsaved changes.",
            "",
            "  Do you want to save before closing?"
        }));
        Dialog.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        Dialog.AddControl(buttonRow);
    }
}
