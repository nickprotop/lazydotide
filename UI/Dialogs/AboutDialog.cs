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
    bool DapDetected,
    bool DapDetectionDone,
    string? DetectedDapExe,
    IReadOnlyList<ToolEntry> Tools,
    string ProjectPath,
    Action? OnInstallDebugger = null);

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
            .WithBorderStyle(BorderStyle.DoubleLine)
            .Resizable(true)
            .Movable(true)
            .Minimizable(false)
            .Maximizable(true)
            .WithColors(Color.Grey93, ColorScheme.WindowBackground)
            .WithBorderColor(ColorScheme.BorderColor)
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
            .AddLine($"[{ColorScheme.MutedMarkup}]A modern .NET IDE for the terminal[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .Build());

        // Version + meta line
        string version = System.Reflection.Assembly
            .GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.0";
        modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]v{version}  \u00b7  MIT License  \u00b7  github.com/nickprotop/lazydotide[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(0, 0, 0, 1)
            .Build());

        modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).WithMargin(1,0,1,0).Build());

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
            .WithBackgroundColor(ColorScheme.WindowBackground)
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
        modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).WithMargin(1,0,1,0).StickyBottom().Build());

        var footerGrid = new HorizontalGridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            StickyPosition      = StickyPosition.Bottom
        };

        var copyrightCol = new ColumnContainer(footerGrid);
        copyrightCol.AddContent(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]  \u00a9 Nikolaos Protopapas \u00b7 MIT License[/]")
            .Build());
        footerGrid.AddColumn(copyrightCol);

        var closeColContainer = new ColumnContainer(footerGrid) { Width = 12 };
        var closeBtn = Controls.Button("[grey93]Close[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Grey50)
            .WithFocusedForegroundColor(Color.White)
            .Build();
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
            .WithBackgroundColor(ColorScheme.WindowBackground)
            .WithForegroundColor(Color.Grey93)
            .Build();

    private static MarkupControl BuildInfoTab()
    {
        var lines = new List<string>
        {
            "",
            $"  [{ColorScheme.MutedMarkup}]Project[/]",
            $"  [{ColorScheme.InfoMarkup}]lazydotide[/]  \u2014  A modern .NET IDE for the terminal",
            "  [dim]https://github.com/nickprotop/lazydotide[/]",
            "",
            $"  [{ColorScheme.MutedMarkup}]Built on[/]",
            $"  [{ColorScheme.InfoMarkup}]SharpConsoleUI[/] (ConsoleEx)  \u2014  A .NET 9 console windowing framework",
            "  [dim]https://github.com/nickprotop/ConsoleEx[/]",
            "",
            $"  [{ColorScheme.MutedMarkup}]Useful resources[/]",
            "  [dim].NET docs       \u00b7  https://learn.microsoft.com/dotnet[/]",
            "  [dim]NuGet           \u00b7  https://www.nuget.org[/]",
            "  [dim]Spectre.Console \u00b7  https://spectreconsole.net[/]",
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
                lspLines.Add($"  [{ColorScheme.MutedMarkup}]LSP          [/][dim]\u25cb detecting\u2026[/]");
                break;
            case { LspStarted: true, DetectedLspExe: var exe }:
                lspLines.Add($"  [{ColorScheme.MutedMarkup}]LSP          [/][{ColorScheme.SuccessMarkup}]\u25cf {Markup.Escape(exe!)} (running)[/]");
                break;
            case { DetectedLspExe: var exe } when exe != null:
                lspLines.Add($"  [{ColorScheme.MutedMarkup}]LSP          [/][dim]\u25cb {Markup.Escape(exe)} (failed to start)[/]");
                break;
            default:
                lspLines.Add($"  [{ColorScheme.MutedMarkup}]LSP          [/][dim]\u25cb not detected[/]");
                lspLines.Add($"[{ColorScheme.WarningMarkup}]               Install:  [/][italic]dotnet tool install -g csharp-ls[/]");
                lspLines.Add($"[dim]               Config:   {Markup.Escape(ConfigService.GetConfigPath())}[/]");
                break;
        }

        var dapLines = new List<string>();
        switch (info)
        {
            case { DapDetectionDone: false }:
                dapLines.Add($"  [{ColorScheme.MutedMarkup}]Debugger      [/][dim]\u25cb detecting\u2026[/]");
                break;
            case { DapDetected: true, DetectedDapExe: var dapExe }:
                dapLines.Add($"  [{ColorScheme.MutedMarkup}]Debugger      [/][{ColorScheme.SuccessMarkup}]\u25cf {Markup.Escape(dapExe!)}[/]");
                break;
            default:
                dapLines.Add($"  [{ColorScheme.MutedMarkup}]Debugger      [/][dim]\u25cb not detected[/]");
                if (info.OnInstallDebugger != null)
                    dapLines.Add($"[{ColorScheme.WarningMarkup}]               Use the Install button below or install manually[/]");
                else
                {
                    if (OperatingSystem.IsLinux())
                        dapLines.Add($"[{ColorScheme.WarningMarkup}]               Install:  [/][italic]scoop install netcoredbg  (or AUR/Nix)[/]");
                    else if (OperatingSystem.IsMacOS())
                        dapLines.Add($"[{ColorScheme.WarningMarkup}]               Install:  [/][italic]brew install netcoredbg  (or from GitHub releases)[/]");
                    else
                        dapLines.Add($"[{ColorScheme.WarningMarkup}]               Install:  [/][italic]scoop install netcoredbg[/]");
                    dapLines.Add($"[dim]               Releases: [/][dim italic]github.com/Samsung/netcoredbg[/]");
                }
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
        result.AddRange(dapLines);
        result.AddRange(new[]
        {
            $"  [{ColorScheme.MutedMarkup}].NET Runtime [/]{Markup.Escape(Environment.Version.ToString())}",
            $"  [{ColorScheme.MutedMarkup}]OS           [/]{Markup.Escape(Environment.OSVersion.VersionString)}",
            $"  [{ColorScheme.MutedMarkup}]Architecture [/]{Markup.Escape(arch)}",
            $"  [{ColorScheme.MutedMarkup}]Clipboard    [/]{Markup.Escape(clipBackend)}",
        });
        if (ClipboardHelper.Backend == ClipboardBackend.InternalFallback)
        {
            string installHint = OperatingSystem.IsLinux()
                ? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null
                    ? "wl-clipboard"
                    : "xclip  or  xsel"
                : "a system clipboard tool";
            result.Add($"[{ColorScheme.WarningMarkup}]               Install:  [/][italic]{installHint}[/]");
        }
        result.AddRange(new[]
        {
            $"  [{ColorScheme.MutedMarkup}]Project      [/][dim]{Markup.Escape(Path.GetFileName(info.ProjectPath.TrimEnd(Path.DirectorySeparatorChar)))}[/]",
            $"  [{ColorScheme.MutedMarkup}]Path         [/][dim]{Markup.Escape(info.ProjectPath)}[/]",
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
            lines.Add($"  [{ColorScheme.MutedMarkup}]Add tools to your [dim].lazydotide.json[/][{ColorScheme.MutedMarkup}] config file:[/]");
            lines.Add("");
            lines.Add("  [grey35]{[/]");
            lines.Add("  [grey35]  \"Tools\": [[/]");
            lines.Add("  [grey35]    { \"Name\": \"My Tool\", \"Command\": \"mytool\", \"Args\": [] }[/]");
            lines.Add("  [grey35]  ][/]");
            lines.Add("  [grey35]}[/]");
        }
        else
        {
            lines.Add($"  [{ColorScheme.MutedMarkup}]{info.Tools.Count} tool{(info.Tools.Count == 1 ? "" : "s")} configured:[/]");
            lines.Add("");
            foreach (var tool in info.Tools)
            {
                lines.Add($"  [{ColorScheme.InfoMarkup}]\u00b7[/] [bold]{Markup.Escape(tool.Name)}[/]");
                lines.Add($"    [{ColorScheme.MutedMarkup}]cmd [/]{Markup.Escape(tool.Command)}" +
                          (tool.Args is { Length: > 0 }
                              ? $" [{ColorScheme.MutedMarkup}]{Markup.Escape(string.Join(" ", tool.Args))}[/]"
                              : ""));
                if (tool.WorkingDir != null)
                    lines.Add($"    [{ColorScheme.MutedMarkup}]dir [/][dim]{Markup.Escape(tool.WorkingDir)}[/]");
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
