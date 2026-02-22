using SharpConsoleUI;
using SharpConsoleUI.Builders;

namespace DotNetIDE;

public abstract class DialogBase<TResult>
{
    private readonly TaskCompletionSource<TResult> _tcs = new();
    protected TResult? Result { get; set; }
    protected Window Dialog { get; private set; } = null!;
    protected ConsoleWindowSystem WindowSystem { get; private set; } = null!;

    public Task<TResult> ShowAsync(ConsoleWindowSystem windowSystem)
    {
        WindowSystem = windowSystem;
        Dialog = CreateDialog();
        BuildContent();
        AttachEventHandlers();
        WindowSystem.AddWindow(Dialog);
        SetInitialFocus();
        return _tcs.Task;
    }

    protected virtual Window CreateDialog()
    {
        var (w, h) = GetSize();
        var builder = new WindowBuilder(WindowSystem)
            .WithTitle(GetTitle())
            .WithSize(w, h)
            .Centered()
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false);
        if (IsModal) builder.AsModal();
        if (AlwaysOnTop) builder.WithAlwaysOnTop();
        return builder.Build();
    }

    protected abstract void BuildContent();
    protected abstract string GetTitle();
    protected virtual (int width, int height) GetSize() => (52, 10);
    protected virtual bool IsModal => true;
    protected virtual bool AlwaysOnTop => false;
    protected virtual void SetInitialFocus() { }
    protected virtual void OnCleanup() { }
    protected virtual TResult GetDefaultResult() => default(TResult)!;

    protected virtual void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Escape)
        {
            CloseWithResult(GetDefaultResult());
            e.Handled = true;
        }
    }

    protected void CloseWithResult(TResult result)
    {
        Result = result;
        Dialog.Close();
    }

    private void AttachEventHandlers()
    {
        Dialog.KeyPressed += OnKeyPressed;
        Dialog.OnClosed += OnDialogClosed;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        OnCleanup();
        _tcs.TrySetResult(Result ?? GetDefaultResult());
    }
}
