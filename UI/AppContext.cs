using System.Collections.Concurrent;
using SharpConsoleUI;

namespace DotNetIDE;

/// <summary>
/// Groups frequently shared dependencies into a single object, reducing constructor parameter counts.
/// </summary>
internal class AppContext
{
    public required ConsoleWindowSystem WindowSystem { get; init; }
    public required ProjectService ProjectService { get; init; }
    public required BuildService BuildService { get; init; }
    public required EditorManager EditorManager { get; init; }
    public required ExplorerPanel Explorer { get; init; }
    public required OutputPanel OutputPanel { get; init; }
    public required SidePanel SidePanel { get; init; }
    public required IdeConfig Config { get; init; }
    public required Window MainWindow { get; init; }
    /// <summary>The main window's layout grid: 4 rows x 3 columns (menu, toolbar, the three
    /// resizable columns, bottom panel). Panel sizes are its track definitions.</summary>
    public required SharpConsoleUI.Controls.GridControl MainGrid { get; init; }
    public required GitService GitService { get; init; }
    public required FileWatcher FileWatcher { get; init; }
    public required FileMiddlewarePipeline Pipeline { get; init; }
    public required WorkspaceService WorkspaceService { get; init; }
    public required ConcurrentQueue<Action> PendingUiActions { get; init; }
    public required ConcurrentQueue<string> BuildLines { get; init; }
    public required ConcurrentQueue<string> TestLines { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}
