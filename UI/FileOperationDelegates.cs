namespace DotNetIDE;

internal record FileOperationDelegates(
    Func<string, Task>? HandleNewFileAsync,
    Func<string, Task>? HandleNewFolderAsync,
    Func<string, Task>? HandleRenameAsync,
    Func<string, Task>? HandleDeleteAsync,
    Func<Task>? RefreshExplorerAndGitAsync);
