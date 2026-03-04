using SharpConsoleUI;
using SharpConsoleUI.Builders;
using Spectre.Console;

namespace DotNetIDE;

public abstract class DialogBase<TResult>
{
    private readonly TaskCompletionSource<TResult> _tcs = new();
    protected TResult? Result { get; set; }
    protected Window Modal { get; private set; } = null!;
    protected ConsoleWindowSystem WindowSystem { get; private set; } = null!;
    protected Window? ParentWindow { get; private set; }

    public Task<TResult> ShowAsync(ConsoleWindowSystem windowSystem, Window? parentWindow = null)
    {
        WindowSystem = windowSystem;
        ParentWindow = parentWindow;
        Modal = CreateDialog();
        BuildContent();
        AttachEventHandlers();
        WindowSystem.AddWindow(Modal);
        WindowSystem.SetActiveWindow(Modal);
        SetInitialFocus();
        return _tcs.Task;
    }

    protected virtual Window CreateDialog()
    {
        var builder = new WindowBuilder(WindowSystem)
            .AsModal()
            .Resizable(GetResizable())
            .Movable(GetMovable())
            .Minimizable(false)
            .Maximizable(false)
            .WithColors(Color.Grey93, ColorScheme.WindowBackground)
            .WithBorderStyle(GetBorderStyle())
            .WithBorderColor(GetBorderColor());

        if (AlwaysOnTop) builder.WithAlwaysOnTop();

        var title = GetTitle();
        if (!string.IsNullOrEmpty(title))
            builder.WithTitle(title);

        var (w, h) = GetSize();
        builder.WithSize(w, h).Centered();
        return builder.Build();
    }

    protected abstract void BuildContent();
    protected abstract string GetTitle();
    protected virtual (int width, int height) GetSize() => (52, 10);
    protected virtual bool GetResizable() => false;
    protected virtual bool GetMovable() => true;
    protected virtual BorderStyle GetBorderStyle() => BorderStyle.DoubleLine;
    protected virtual Color GetBorderColor() => ColorScheme.BorderColor;
    protected virtual bool IsModal => true;
    protected virtual bool AlwaysOnTop => false;
    protected virtual void SetInitialFocus() { }
    protected virtual void OnCleanup() { }
    protected virtual TResult GetDefaultResult() => default(TResult)!;

    protected virtual void OnEscapePressed()
    {
        CloseWithResult(GetDefaultResult());
    }

    protected virtual void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Escape)
        {
            OnEscapePressed();
            e.Handled = true;
        }
    }

    protected void CloseWithResult(TResult result)
    {
        Result = result;
        Modal.Close();
    }

    private void AttachEventHandlers()
    {
        Modal.KeyPressed += OnKeyPressed;
        Modal.OnClosed += OnModalClosed;
    }

    private void OnModalClosed(object? sender, EventArgs e)
    {
        OnCleanup();
        _tcs.TrySetResult(Result ?? GetDefaultResult());
    }
}
