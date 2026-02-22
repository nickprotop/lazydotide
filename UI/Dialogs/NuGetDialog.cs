using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace DotNetIDE;

public class NuGetDialog : DialogBase<(string? PackageName, string? Version)>
{
    private PromptControl _namePrompt    = null!;
    private PromptControl _versionPrompt = null!;

    private NuGetDialog() { }

    public static new Task<(string? PackageName, string? Version)> ShowAsync(ConsoleWindowSystem ws)
    {
        DialogBase<(string? PackageName, string? Version)> dlg = new NuGetDialog();
        return dlg.ShowAsync(ws);
    }

    protected override string GetTitle() => "Add NuGet Package";
    protected override (int width, int height) GetSize() => (52, 10);
    protected override (string? PackageName, string? Version) GetDefaultResult() => (null, null);

    protected override void BuildContent()
    {
        _namePrompt    = new PromptControl { Prompt = "Package name: ",       InputWidth = 30 };
        _versionPrompt = new PromptControl { Prompt = "Version (optional): ", InputWidth = 20 };

        var addBtn    = new ButtonControl { Text = "Add",    Width = 8 };
        var cancelBtn = new ButtonControl { Text = "Cancel", Width = 8 };

        addBtn.Click += (_, _) =>
        {
            var pkg = _namePrompt.Input.Trim();
            var ver = _versionPrompt.Input.Trim();
            CloseWithResult((
                string.IsNullOrWhiteSpace(pkg) ? null : pkg,
                string.IsNullOrWhiteSpace(ver) ? null : ver));
        };
        cancelBtn.Click += (_, _) => CloseWithResult((null, null));

        var buttonRow = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Left };
        var addCol    = new ColumnContainer(buttonRow); addCol.AddContent(addBtn);    buttonRow.AddColumn(addCol);
        var cancelCol = new ColumnContainer(buttonRow); cancelCol.AddContent(cancelBtn); buttonRow.AddColumn(cancelCol);
        buttonRow.StickyPosition = StickyPosition.Bottom;

        Dialog.AddControl(new MarkupControl(new List<string> { "" }));
        Dialog.AddControl(_namePrompt);
        Dialog.AddControl(_versionPrompt);
        Dialog.AddControl(new RuleControl { StickyPosition = StickyPosition.Bottom });
        Dialog.AddControl(buttonRow);
    }
}
