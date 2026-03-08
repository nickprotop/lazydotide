using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public enum DialogResult { Save, DontSave, Cancel }

public class ConfirmSaveDialog : DialogBase<DialogResult>
{
    private const int DialogWidth = 50;
    private const int DialogHeight = 10;

    private readonly string _fileName;

    private ConfirmSaveDialog(string fileName) { _fileName = fileName; }

    public static Task<DialogResult> ShowAsync(ConsoleWindowSystem ws, string fileName)
        => new ConfirmSaveDialog(fileName).ShowAsync(ws);

    protected override string GetTitle() => "Unsaved Changes";
    protected override (int width, int height) GetSize() => (DialogWidth, DialogHeight);
    protected override DialogResult GetDefaultResult() => DialogResult.Cancel;

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Unsaved Changes[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.SecondaryMarkup}]Save changes to [{ColorScheme.WarningMarkup}]{MarkupParser.Escape(_fileName)}[/][{ColorScheme.SecondaryMarkup}]?[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 1, 1)
            .Build());

        var saveBtn = Controls.Button("[grey93]Save (S)[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkGreen)
            .WithFocusedForegroundColor(Color.White)
            .WithMargin(0, 1, 0, 0)
            .Build();

        var dontSaveBtn = Controls.Button("[grey93]Don't Save (D)[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.DarkOrange)
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

        saveBtn.Click     += (_, _) => CloseWithResult(DialogResult.Save);
        dontSaveBtn.Click += (_, _) => CloseWithResult(DialogResult.DontSave);
        cancelBtn.Click   += (_, _) => CloseWithResult(DialogResult.Cancel);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var buttonRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .WithMargin(0, 0, 0, 0)
            .Column(col => col.Add(saveBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(dontSaveBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(cancelBtn))
            .Build();
        Modal.AddControl(buttonRow);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]S:Save  D:Don't Save  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.S)
        {
            CloseWithResult(DialogResult.Save);
            e.Handled = true;
        }
        else if (e.KeyInfo.Key == ConsoleKey.D)
        {
            CloseWithResult(DialogResult.DontSave);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}
