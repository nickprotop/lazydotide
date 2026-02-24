using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public record AboutInfo(
    bool LspStarted,
    bool LspDetectionDone,
    string? DetectedLspExe,
    IReadOnlyList<ToolEntry> Tools,
    string ProjectPath);

public static class AboutDialog
{
    private const int PreferredWidth  = 90;
    private const int PreferredHeight = 34;

    public static Action Show(
        ConsoleWindowSystem windowSystem,
        Func<AboutInfo> infoProvider,
        Action onClosed)
    {
        var info = infoProvider();
        var desktop = windowSystem.DesktopDimensions;
        int width  = Math.Min(PreferredWidth, desktop.Width - 4);
        int height = Math.Min(PreferredHeight, desktop.Height - 2);

        var modal = new WindowBuilder(windowSystem)
            .WithTitle("About lazydotide")
            .WithSize(width, height)
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(true)
            .Movable(true)
            .Minimizable(false)
            .Maximizable(true)
            .WithColors(Color.Grey93, Color.Grey11)
            .Build();

        // FigleControl header
        modal.AddControl(Controls.Figlet("LazyDotIde")
            .Small()
            .WithColor(Color.Cyan1)
            .Centered()
            .WithMargin(2, 1, 2, 0)
            .Build());

        // Tagline
        modal.AddControl(Controls.Markup()
            .AddLine("[dim]A modern .NET IDE for the terminal[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .Build());

        // Version + meta line
        string version = System.Reflection.Assembly
            .GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.0";
        modal.AddControl(Controls.Markup()
            .AddLine($"[grey50]v{version}  ·  MIT License  ·  github.com/nickprotop/lazydotide[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(0, 0, 0, 1)
            .Build());

        modal.AddControl(Controls.RuleBuilder().WithColor(Color.Grey35).WithMargin(1,0,1,0).Build());

        // Build environment tab with live-updatable markup
        var envMarkup = BuildEnvironmentTab(info);

        // Tab control — Info first, then Environment, then Tools
        var tabControl = Controls.TabControl()
            .AddTab("  Info  ",        ScrollWrap(BuildInfoTab()))
            .AddTab("  Environment  ", ScrollWrap(envMarkup))
            .AddTab("  Tools  ",       ScrollWrap(BuildToolsTab(info)))
            .WithHeaderStyle(TabHeaderStyle.Separator)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithMargin(1,0,1,0)
            .Fill()
            .WithBackgroundColor(Color.Grey11)
            .WithForegroundColor(Color.Grey93)
            .Build();

        // Refresh environment content when switching to that tab
        tabControl.TabChanged += (_, e) =>
        {
            if (e.NewIndex == 1)
                envMarkup.SetContent(BuildEnvironmentLines(infoProvider()));
        };

        modal.AddControl(tabControl);

        // Footer rule + bar (sticky bottom)
        modal.AddControl(Controls.RuleBuilder().WithColor(Color.Grey35).WithMargin(1,0,1,0).StickyBottom().Build());

        var footerGrid = new HorizontalGridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            StickyPosition      = StickyPosition.Bottom
        };

        var copyrightCol = new ColumnContainer(footerGrid);
        copyrightCol.AddContent(Controls.Markup()
            .AddLine("[grey50]  © Nikolaos Protopapas · MIT License[/]")
            .Build());
        footerGrid.AddColumn(copyrightCol);

        var closeColContainer = new ColumnContainer(footerGrid) { Width = 10 };
        var closeBtn = new ButtonControl { Text = "Close", Width = 8 };
        closeBtn.Click += (_, _) => modal.Close();
        closeColContainer.AddContent(closeBtn);
        footerGrid.AddColumn(closeColContainer);

        modal.AddControl(footerGrid);

        modal.OnClosed += (_, _) => onClosed();

        modal.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Escape)
            {
                modal.Close();
                e.Handled = true;
            }
        };

        windowSystem.AddWindow(modal);
        windowSystem.SetActiveWindow(modal);

        return () => envMarkup.SetContent(BuildEnvironmentLines(infoProvider()));
    }

    private static ScrollablePanelControl ScrollWrap(MarkupControl content) =>
        Controls.ScrollablePanel()
            .AddControl(content)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithBackgroundColor(Color.Grey11)
            .WithForegroundColor(Color.Grey93)
            .Build();

    private static MarkupControl BuildInfoTab()
    {
        var lines = new List<string>
        {
            "",
            "  [grey50]Project[/]",
            "  [cyan1]lazydotide[/]  —  A modern .NET IDE for the terminal",
            "  [dim]https://github.com/nickprotop/lazydotide[/]",
            "",
            "  [grey50]Built on[/]",
            "  [cyan1]SharpConsoleUI[/] (ConsoleEx)  —  A .NET 9 console windowing framework",
            "  [dim]https://github.com/nickprotop/ConsoleEx[/]",
            "",
            "  [grey50]Useful resources[/]",
            "  [dim].NET docs       ·  https://learn.microsoft.com/dotnet[/]",
            "  [dim]NuGet           ·  https://www.nuget.org[/]",
            "  [dim]Spectre.Console ·  https://spectreconsole.net[/]",
        };

        return new MarkupControl(lines)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Margin(1, 0, 1, 0)
        };
    }

    private static List<string> BuildEnvironmentLines(AboutInfo info)
    {
        var lspLines = new List<string>();
        switch (info)
        {
            case { LspDetectionDone: false }:
                lspLines.Add("  [grey50]LSP          [/][dim]○ detecting…[/]");
                break;
            case { LspStarted: true, DetectedLspExe: var exe }:
                lspLines.Add($"  [grey50]LSP          [/][green]● {Markup.Escape(exe!)} (running)[/]");
                break;
            case { DetectedLspExe: var exe } when exe != null:
                lspLines.Add($"  [grey50]LSP          [/][dim]○ {Markup.Escape(exe)} (failed to start)[/]");
                break;
            default:
                lspLines.Add("  [grey50]LSP          [/][dim]○ not detected[/]");
                lspLines.Add("[yellow]               Install:  [/][italic]dotnet tool install -g csharp-ls[/]");
                lspLines.Add($"[dim]               Config:   {Markup.Escape(ConfigService.GetConfigPath())}[/]");
                break;
        }

        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

        string clipBackend = ClipboardHelper.Backend switch
        {
            ClipboardBackend.WlClipboard     => "wl-clipboard (Wayland)",
            ClipboardBackend.Xclip           => "xclip (X11)",
            ClipboardBackend.Xsel            => "xsel (X11)",
            ClipboardBackend.Pbcopy          => "pbcopy / pbpaste (macOS)",
            ClipboardBackend.WindowsClip     => "clip.exe (Windows)",
            ClipboardBackend.InternalFallback => "internal (no system tool found)",
            _                                => "unknown"
        };

        var result = new List<string> { "" };
        result.AddRange(lspLines);
        result.AddRange(new[]
        {
            $"  [grey50].NET Runtime [/]{Markup.Escape(Environment.Version.ToString())}",
            $"  [grey50]OS           [/]{Markup.Escape(Environment.OSVersion.VersionString)}",
            $"  [grey50]Architecture [/]{Markup.Escape(arch)}",
            $"  [grey50]Clipboard    [/]{Markup.Escape(clipBackend)}",
        });
        if (ClipboardHelper.Backend == ClipboardBackend.InternalFallback)
        {
            string installHint = OperatingSystem.IsLinux()
                ? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null
                    ? "wl-clipboard"
                    : "xclip  or  xsel"
                : "a system clipboard tool";
            result.Add($"[yellow]               Install:  [/][italic]{installHint}[/]");
        }
        result.AddRange(new[]
        {
            $"  [grey50]Project      [/][dim]{Markup.Escape(Path.GetFileName(info.ProjectPath.TrimEnd(Path.DirectorySeparatorChar)))}[/]",
            $"  [grey50]Path         [/][dim]{Markup.Escape(info.ProjectPath)}[/]",
        });
        return result;
    }

    private static MarkupControl BuildEnvironmentTab(AboutInfo info)
    {
        return new MarkupControl(BuildEnvironmentLines(info))
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Margin(1, 0, 1, 0)
        };
    }

    private static MarkupControl BuildToolsTab(AboutInfo info)
    {
        var lines = new List<string> { "" };

        if (info.Tools.Count == 0)
        {
            lines.Add("  [dim]No custom tools configured.[/]");
            lines.Add("");
            lines.Add("  [grey50]Add tools to your [dim].lazydotide.json[/][grey50] config file:[/]");
            lines.Add("");
            lines.Add("  [grey35]{[/]");
            lines.Add("  [grey35]  \"Tools\": [[/]");
            lines.Add("  [grey35]    { \"Name\": \"My Tool\", \"Command\": \"mytool\", \"Args\": [] }[/]");
            lines.Add("  [grey35]  ][/]");
            lines.Add("  [grey35]}[/]");
        }
        else
        {
            lines.Add($"  [grey50]{info.Tools.Count} tool{(info.Tools.Count == 1 ? "" : "s")} configured:[/]");
            lines.Add("");
            foreach (var tool in info.Tools)
            {
                lines.Add($"  [cyan1]·[/] [bold]{Markup.Escape(tool.Name)}[/]");
                lines.Add($"    [grey50]cmd [/]{Markup.Escape(tool.Command)}" +
                          (tool.Args is { Length: > 0 }
                              ? $" [grey50]{Markup.Escape(string.Join(" ", tool.Args))}[/]"
                              : ""));
                if (tool.WorkingDir != null)
                    lines.Add($"    [grey50]dir [/][dim]{Markup.Escape(tool.WorkingDir)}[/]");
                lines.Add("");
            }
        }

        return new MarkupControl(lines)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Margin(1, 0, 1, 0)
        };
    }
}
