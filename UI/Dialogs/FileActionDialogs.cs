using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace DotNetIDE;

public class NewFileDialog : DialogBase<string?>
{
    private readonly string _parentDir;
    private readonly bool _isFolder;
    private PromptControl _input = null!;

    private NewFileDialog(string parentDir, bool isFolder) { _parentDir = parentDir; _isFolder = isFolder; }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string parentDir, bool isFolder = false)
        => new NewFileDialog(parentDir, isFolder).ShowAsync(ws);

    protected override string GetTitle() => _isFolder ? "New Folder" : "New File";
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (Math.Min(50, Math.Max(30, desktop.Width - 4)), 9);
    }

    protected override void BuildContent()
    {
        var promptText = _isFolder ? "Folder name: " : "File name: ";

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]{(_isFolder ? "New Folder" : "New File")}[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        _input = Controls.Prompt()
            .WithPrompt(promptText)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        Modal.AddControl(_input);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Enter:Create  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void SetInitialFocus()
    {
        Modal.FocusControl(_input);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            var name = _input.Input?.Trim();
            if (!string.IsNullOrEmpty(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                var fullPath = Path.Combine(_parentDir, name);
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    CloseWithResult(fullPath);
                }
            }
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}

public class FileRenameDialog : DialogBase<string?>
{
    private readonly string _currentPath;
    private readonly string _currentName;
    private readonly string _parentDir;
    private PromptControl _input = null!;

    private FileRenameDialog(string currentPath)
    {
        _currentPath = currentPath;
        _currentName = Path.GetFileName(currentPath);
        _parentDir = Path.GetDirectoryName(currentPath) ?? "";
    }

    public static Task<string?> ShowAsync(ConsoleWindowSystem ws, string currentPath)
        => new FileRenameDialog(currentPath).ShowAsync(ws);

    protected override string GetTitle() => "Rename";
    protected override (int width, int height) GetSize()
    {
        var desktop = WindowSystem.DesktopDimensions;
        return (Math.Min(50, Math.Max(30, desktop.Width - 4)), 9);
    }

    protected override void BuildContent()
    {
        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Rename[/]")
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
        Modal.FocusControl(_input);
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Enter)
        {
            var newName = _input.Input?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != _currentName &&
                newName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                var newPath = Path.Combine(_parentDir, newName);
                if (!File.Exists(newPath) && !Directory.Exists(newPath))
                {
                    CloseWithResult(newPath);
                }
            }
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}

public class DeleteConfirmDialog : DialogBase<bool>
{
    private readonly string _path;

    private DeleteConfirmDialog(string path) { _path = path; }

    public static Task<bool> ShowAsync(ConsoleWindowSystem ws, string path)
        => new DeleteConfirmDialog(path).ShowAsync(ws);

    protected override string GetTitle() => "Confirm Delete";
    protected override (int width, int height) GetSize() => (50, 10);
    protected override bool GetDefaultResult() => false;
    protected override Color GetBorderColor() => Color.Red;

    protected override void BuildContent()
    {
        var name = Path.GetFileName(_path);
        var isDir = Directory.Exists(_path);

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.PrimaryMarkup}]Delete {(isDir ? "Folder" : "File")}[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 0, 0)
            .Build());

        var message = isDir
            ? $"Delete folder [{ColorScheme.WarningMarkup}]{MarkupParser.Escape(name)}[/] and all contents?"
            : $"Delete [{ColorScheme.WarningMarkup}]{MarkupParser.Escape(name)}[/]?";

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.SecondaryMarkup}]{message}[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 1, 1, 1)
            .Build());

        var deleteBtn = Controls.Button("[grey93]Delete (Y)[/]")
            .WithBackgroundColor(Color.Grey30)
            .WithForegroundColor(Color.Grey93)
            .WithFocusedBackgroundColor(Color.Red)
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

        deleteBtn.Click += (_, _) => CloseWithResult(true);
        cancelBtn.Click += (_, _) => CloseWithResult(false);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        var buttonRow = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Column(col => col.Add(deleteBtn))
            .Column(col => col.Width(2))
            .Column(col => col.Add(cancelBtn))
            .Build();
        Modal.AddControl(buttonRow);

        Modal.AddControl(Controls.RuleBuilder().WithColor(ColorScheme.RuleColor).StickyBottom().Build());

        Modal.AddControl(Controls.Markup()
            .AddLine($"[{ColorScheme.MutedMarkup}]Y:Delete  Esc:Cancel[/]")
            .WithAlignment(HorizontalAlignment.Center)
            .StickyBottom()
            .Build());
    }

    protected override void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Y)
        {
            CloseWithResult(true);
            e.Handled = true;
        }
        else
        {
            base.OnKeyPressed(sender, e);
        }
    }
}
