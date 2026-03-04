using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;

namespace DotNetIDE;

public class InstallDebuggerModal : DialogBase<bool>
{
    private MarkupControl _statusLabel = null!;
    private ProgressBarControl _progressBar = null!;
    private MarkupControl _logContent = null!;
    private ButtonControl _cancelButton = null!;
    private readonly List<string> _logLines = new();
    private bool? _finalResult;
    private readonly CancellationTokenSource _cts = new();
    private EventHandler<ButtonControl>? _cancelClickHandler;

    private InstallDebuggerModal() { }

    public static Task<bool> ShowAsync(ConsoleWindowSystem ws)
    {
        DialogBase<bool> dlg = new InstallDebuggerModal();
        return dlg.ShowAsync(ws);
    }

    protected override string GetTitle() => "Install netcoredbg";
    protected override (int width, int height) GetSize() => (80, 22);
    protected override bool GetResizable() => true;
    protected override bool GetDefaultResult() => false;

    protected override Window CreateDialog()
    {
        var modal = base.CreateDialog();
        modal.IsClosable = false;
        return modal;
    }

    protected override void BuildContent()
    {
        // Header
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Installing netcoredbg[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        // Subheader
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.SecondaryMarkup}]Debug adapter for .NET[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        // Progress bar (indeterminate)
        _progressBar = Controls.ProgressBar()
            .Indeterminate()
            .WithMargin(2, 1, 2, 0)
            .Stretch()
            .Build();
        Modal.AddControl(_progressBar);

        // Status label
        _statusLabel = Controls.Markup()
            .AddLine($"[{ColorScheme.SecondaryMarkup}]Preparing...[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 0, 1, 0)
            .Build();
        Modal.AddControl(_statusLabel);

        // Separator
        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).WithMargin(2, 1, 2, 0).Build());

        // Log viewer
        var logPanel = Controls.ScrollablePanel()
            .WithVerticalScroll(ScrollMode.Scroll)
            .WithScrollbar(true)
            .WithMouseWheel(true)
            .WithAutoScroll(true)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithColors(Color.Grey93, ColorScheme.StatusBarBackground)
            .WithMargin(2, 0, 2, 1)
            .Build();

        _logContent = Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Waiting for output...[/]")
            .Build();
        logPanel.AddControl(_logContent);
        Modal.AddControl(logPanel);

        // Separator
        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().WithMargin(2, 1, 2, 0).Build());

        // Cancel button
        _cancelButton = Controls.Button("[grey93]Cancel[/] [grey78](Esc)[/]")
            .WithMargin(2, 0, 0, 0)
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Grey50)
            .WithFocusedForegroundColor(Color.White)
            .Build();
        _cancelClickHandler = (_, _) => OnCancelButtonClick();
        _cancelButton.Click += _cancelClickHandler;
        Modal.AddControl(_cancelButton);

        // Footer rule + hint
        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().WithMargin(2, 1, 2, 0).Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());

        // Start the operation
        _ = RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        var progress = new Progress<string>(msg =>
        {
            lock (_logLines)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                _logLines.Add($"[{ColorScheme.MutedMarkup}][{timestamp}][/] {Markup.Escape(msg)}");
                if (_logLines.Count > 500) _logLines.RemoveRange(0, _logLines.Count - 500);
                _logContent.SetContent(_logLines);
                _statusLabel.SetContent(new List<string> { $"[{ColorScheme.SecondaryMarkup}]{Markup.Escape(msg)}[/]" });
            }
        });

        try
        {
            await NetcoredbgInstaller.InstallAsync(progress, _cts.Token);
            _finalResult = true;
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 100;
            _statusLabel.SetContent(new List<string> { $"[{ColorScheme.SuccessMarkup}]\u2713 Installed successfully[/]" });
        }
        catch (OperationCanceledException)
        {
            _finalResult = false;
            _statusLabel.SetContent(new List<string> { $"[{ColorScheme.WarningMarkup}]Installation cancelled[/]" });
        }
        catch (Exception ex)
        {
            _finalResult = false;
            _statusLabel.SetContent(new List<string> { $"[{ColorScheme.ErrorMarkup}]\u2717 Installation failed[/]" });
            lock (_logLines)
            {
                _logLines.Add($"[{ColorScheme.ErrorMarkup}]{Markup.Escape(ex.Message)}[/]");
                _logContent.SetContent(_logLines);
            }
        }

        // Update button to "Close" and allow closing
        _cancelButton.Text = "[grey93]Close[/] [grey78](Esc)[/]";
        Modal.IsClosable = true;
    }

    private void OnCancelButtonClick()
    {
        if (_finalResult != null)
        {
            CloseWithResult(_finalResult.Value);
        }
        else
        {
            _cts.Cancel();
            _statusLabel.SetContent(new List<string> { $"[{ColorScheme.WarningMarkup}]Cancelling...[/]" });
        }
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Escape)
        {
            OnCancelButtonClick();
            e.Handled = true;
        }
    }

    protected override void OnCleanup()
    {
        if (_cancelClickHandler != null)
            _cancelButton.Click -= _cancelClickHandler;
        _cts.Dispose();
    }
}
