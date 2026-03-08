using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;

namespace DotNetIDE;

public class RenameDialog : DialogBase<string?>
{
    private readonly string _currentName;
    private PromptControl _input = null!;

    private RenameDialog(string currentName) { _currentName = currentName; }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string currentName)
        => new RenameDialog(currentName).ShowAsync(ws);

    protected override string GetTitle() => "Rename Symbol";
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (Math.Min(50, Math.Max(30, desktop.Width - 4)), 9);
    }

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Rename Symbol[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        _input = Controls.Prompt()
            .WithPrompt("New name: ")
            .WithInput(_currentName)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        Modal.AddControl(_input);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Rename  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        _input.SetFocus(true, FocusReason.Programmatic);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            var newName = _input.Input?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != _currentName)
                CloseWithResult(newName);
            else
                CloseWithResult(null);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}
