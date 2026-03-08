using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class LaunchProfileDialog : DialogBase<LaunchProfileEntry?>
{
    private const int DialogWidth = 56;
    private const int DialogHeight = 16;

    private readonly List<LaunchProfileEntry> _profiles;
    private ListControl _profileList = null!;
    private PromptControl _argsPrompt = null!;
    private PromptControl _envPrompt = null!;

    private LaunchProfileDialog(List<LaunchProfileEntry> profiles)
    {
        _profiles = profiles;
        // Ensure there's always at least one profile
        if (_profiles.Count == 0)
            _profiles.Add(new LaunchProfileEntry { Name = "Default" });
    }

    public static Task<LaunchProfileEntry?> ShowAsync(ConsoleWindowSystem ws, string projectDir, List<LaunchProfileEntry>? workspaceProfiles = null)
    {
        var profiles = LaunchSettingsReader.Read(projectDir);

        // Merge workspace profiles that aren't already in launchSettings
        if (workspaceProfiles != null)
        {
            var existingNames = new HashSet<string>(profiles.Select(p => p.Name));
            foreach (var wp in workspaceProfiles)
            {
                if (!existingNames.Contains(wp.Name))
                    profiles.Add(wp);
            }
        }

        if (profiles.Count == 0)
            profiles.Add(new LaunchProfileEntry { Name = "Default" });

        return new LaunchProfileDialog(profiles).ShowAsync(ws);
    }

    protected override string GetTitle() => "Launch Profile";
    protected override (int width, int height) GetSize() => (DialogWidth, DialogHeight);
    protected override LaunchProfileEntry? GetDefaultResult() => null;

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Launch Profile[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        // Profile list
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.SecondaryMarkup}]Profile:[/]")
            .WithMargin(2, 0, 0, 0)
            .Build());

        _profileList = new ListControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            HoverHighlightsItems = true
        };
        foreach (var profile in _profiles)
        {
            _profileList.AddItem(new ListItem(MarkupParser.Escape(profile.Name)) { Tag = profile });
        }
        _profileList.SelectedItemChanged += (_, _) => PopulateFromSelected();
        _profileList.Margin = new Margin(2, 2, 0, 0);
        Modal.AddControl(_profileList);

        // Args prompt
        _argsPrompt = new PromptControl { Prompt = "Args: ", InputWidth = 38 };
        _argsPrompt.Margin = new Margin(2, 2, 1, 0);
        Modal.AddControl(_argsPrompt);

        // Env prompt (semicolon-separated KEY=VAL pairs)
        _envPrompt = new PromptControl { Prompt = "Env:  ", InputWidth = 38 };
        _envPrompt.Margin = new Margin(2, 2, 0, 0);
        Modal.AddControl(_envPrompt);

        // Buttons
        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var launchBtn = Controls.Button("[grey93]Launch[/]")
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

        launchBtn.Click += (_, _) => AcceptProfile();
        cancelBtn.Click += (_, _) => CloseWithResult(null);

        var buttonRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Column(col => col.Add(launchBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(cancelBtn))
            .Build();
        Modal.AddControl(buttonRow);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Launch  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        // Populate from first profile
        PopulateFromSelected();
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_profileList);
    }

    private void PopulateFromSelected()
    {
        var selected = _profileList.SelectedItem?.Tag as LaunchProfileEntry;
        if (selected == null) return;

        _argsPrompt.Input = selected.Args != null ? string.Join(" ", selected.Args) : "";
        _envPrompt.Input = selected.Env != null
            ? string.Join(";", selected.Env.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
    }

    private void AcceptProfile()
    {
        var selected = _profileList.SelectedItem?.Tag as LaunchProfileEntry;
        var entry = new LaunchProfileEntry
        {
            Name = selected?.Name ?? "Default",
            Project = selected?.Project ?? "",
            WorkingDirectory = selected?.WorkingDirectory
        };

        // Parse args from prompt
        var argsText = _argsPrompt.Input?.Trim();
        if (!string.IsNullOrEmpty(argsText))
            entry.Args = argsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Parse env from prompt (semicolon-separated KEY=VAL)
        var envText = _envPrompt.Input?.Trim();
        if (!string.IsNullOrEmpty(envText))
        {
            entry.Env = new Dictionary<string, string>();
            foreach (var pair in envText.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx > 0)
                    entry.Env[pair[..eqIdx].Trim()] = pair[(eqIdx + 1)..].Trim();
            }
        }

        CloseWithResult(entry);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            AcceptProfile();
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}
