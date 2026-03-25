using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;

namespace DotNetIDE;

public class NuGetDialog : DialogBase<(string? PackageName, string? Version)>
{
    private PromptControl _namePrompt    = null!;
    private PromptControl _versionPrompt = null!;

    private NuGetDialog() { }

    public static Task<(string? PackageName, string? Version)> ShowAsync(ConsoleWindowSystem ws)
    {
        DialogBase<(string? PackageName, string? Version)> dlg = new NuGetDialog();
        return dlg.ShowAsync(ws);
    }

    protected override string GetTitle() => "Add NuGet Package";
    protected override (int width, int height) GetSize() => (52, 12);
    protected override (string? PackageName, string? Version) GetDefaultResult() => (null, null);

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Add NuGet Package[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        _namePrompt    = new PromptControl { Prompt = "Package name: ",       InputWidth = 30 };
        _versionPrompt = new PromptControl { Prompt = "Version (optional): ", InputWidth = 20 };

        var addBtn = Controls.Button("[grey93]Add[/]")
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

        addBtn.Click += (_, _) =>
        {
            var pkg = _namePrompt.Input.Trim();
            var ver = _versionPrompt.Input.Trim();
            CloseWithResult((
                string.IsNullOrWhiteSpace(pkg) ? null : pkg,
                string.IsNullOrWhiteSpace(ver) ? null : ver));
        };
        cancelBtn.Click += (_, _) => CloseWithResult((null, null));

        Modal.AddControl(new MarkupControl(new List<string> { "" }));
        Modal.AddControl(_namePrompt);
        Modal.AddControl(_versionPrompt);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var buttonRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Column(col => col.Add(addBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(cancelBtn))
            .Build();
        Modal.AddControl(buttonRow);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Add  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_namePrompt);
    }
}
