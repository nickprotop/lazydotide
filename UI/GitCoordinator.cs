using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Parsing;
using SharpConsoleUI.Highlighting;

namespace DotNetIDE;

internal class GitCoordinator
{
    private readonly AppContext _ctx;

    private string _gitMarkup = IdeConstants.GitStatusDefault;

    public event EventHandler<string>? GitStatusMarkupChanged;

    public string GitMarkup => _gitMarkup;

    public GitCoordinator(AppContext ctx)
    {
        _ctx = ctx;
    }

    private void EnqueueGitOutput(string line, bool clear = false)
    {
        _ctx.PendingUiActions.Enqueue(() =>
        {
            if (clear) _ctx.OutputPanel.ClearGitOutput();
            _ctx.OutputPanel.AppendGitLine(line);
            _ctx.OutputPanel.SwitchToGitTab();
        });
    }

    public async Task RefreshGitStatusAsync()
    {
        var branch = await _ctx.GitService.GetBranchAsync(_ctx.ProjectService.RootPath);
        var status = await _ctx.GitService.GetStatusSummaryAsync(_ctx.ProjectService.RootPath);

        var bar = new IdeStatusBar();

        if (string.IsNullOrEmpty(branch))
        {
            bar.AddSegment("[dim] git: none[/]", " git: none");
        }
        else
        {
            var displayBranch = branch.Length > 22
                ? branch[..19] + "..."
                : branch;

            if (string.IsNullOrEmpty(status))
            {
                bar.AddSegment($"[green] git:{MarkupParser.Escape(displayBranch)}[/]",
                               $" git:{displayBranch}");
            }
            else
            {
                bar.AddSegment($"[yellow] git:{MarkupParser.Escape(displayBranch)}[/]",
                               $" git:{displayBranch}")
                   .AddSegment($"[dim]  {MarkupParser.Escape(status)}[/]",
                               $"  {status}");
            }
        }

        _gitMarkup = bar.Render();
        GitStatusMarkupChanged?.Invoke(this, _gitMarkup);

        await RefreshGitFileStatusesAsync();
    }

    public async Task RefreshGitFileStatusesAsync()
    {
        var (detailedFiles, workingDir) = await _ctx.GitService.GetDetailedFileStatusesAsync(_ctx.ProjectService.RootPath);
        var isPathIgnored = await _ctx.GitService.CreateIgnoreCheckerAsync(_ctx.ProjectService.RootPath);

        var fileStatuses = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in detailedFiles)
        {
            if (!fileStatuses.ContainsKey(f.RelativePath))
                fileStatuses[f.RelativePath] = f.Status;
        }

        _ctx.PendingUiActions.Enqueue(() => _ctx.Explorer.UpdateGitStatuses(fileStatuses, workingDir, isPathIgnored));

        var root = _ctx.ProjectService.RootPath;
        var diffUpdates = await _ctx.EditorManager.CollectGitDiffMarkersAsync(
            path => _ctx.GitService.GetLineDiffMarkersAsync(root, path)!);
        _ctx.PendingUiActions.Enqueue(() => _ctx.EditorManager.ApplyGitDiffMarkers(diffUpdates));

        var branch = await _ctx.GitService.GetBranchAsync(_ctx.ProjectService.RootPath);
        var log = await _ctx.GitService.GetLogAsync(_ctx.ProjectService.RootPath, 15);
        var (ahead, behind) = _ctx.GitService.GetAheadBehind(_ctx.ProjectService.RootPath);
        var sidePanelFiles = detailedFiles
            .Select(f => (f.RelativePath, f.AbsolutePath, f.Status, f.IsStaged))
            .ToList();
        _ctx.PendingUiActions.Enqueue(() => _ctx.SidePanel.UpdateGitPanel(branch, sidePanelFiles, log, ahead, behind));

        // Update ahead/behind with fresh remote data in the background
        _ = Task.Run(async () =>
        {
            _ctx.FileWatcher?.SuppressGitChanges();
            try
            {
                var (freshAhead, freshBehind) = await _ctx.GitService.GetAheadBehindWithFetchAsync(_ctx.ProjectService.RootPath);
                if (freshAhead != ahead || freshBehind != behind)
                    _ctx.PendingUiActions.Enqueue(() => _ctx.SidePanel.UpdateGitPanel(branch, sidePanelFiles, log, freshAhead, freshBehind));
            }
            finally
            {
                _ctx.FileWatcher?.ResumeGitChanges();
            }
        });
    }

    public async Task RefreshGitDiffMarkersForFileAsync(string filePath)
    {
        var markers = await _ctx.GitService.GetLineDiffMarkersAsync(_ctx.ProjectService.RootPath, filePath);
        _ctx.PendingUiActions.Enqueue(() => _ctx.EditorManager.UpdateGitDiffMarkers(filePath, markers));
    }

    public async Task RefreshExplorerAndGitAsync()
    {
        _ctx.PendingUiActions.Enqueue(() => _ctx.Explorer.Refresh());
        await RefreshGitStatusAsync();
    }

    public async Task GitCommandAsync(string command)
    {
        _ctx.PendingUiActions.Enqueue(() =>
        {
            _ctx.OutputPanel.ClearGitOutput();
            _ctx.OutputPanel.SwitchToGitTab();
            _ctx.OutputPanel.AppendGitLine($"$ git {command}");
        });

        await _ctx.BuildService.RunAsync(
            "git", ["-C", _ctx.ProjectService.RootPath, .. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            line => _ctx.PendingUiActions.Enqueue(() => _ctx.OutputPanel.AppendGitLine(line)),
            _ctx.CancellationToken, workingDirectory: _ctx.ProjectService.RootPath);

        await RefreshGitStatusAsync();
    }

    public async Task GitStageFileAsync(string absolutePath)
    {
        await _ctx.GitService.StageAsync(_ctx.ProjectService.RootPath, absolutePath);
        await RefreshGitStatusAsync();
    }

    public async Task GitUnstageFileAsync(string absolutePath)
    {
        await _ctx.GitService.UnstageAsync(_ctx.ProjectService.RootPath, absolutePath);
        await RefreshGitStatusAsync();
    }

    public async Task GitAddToGitignoreAsync(string absolutePath, bool isDirectory)
    {
        await _ctx.GitService.AddToGitignoreAsync(_ctx.ProjectService.RootPath, absolutePath, isDirectory);
        var gitignorePath = Path.Combine(_ctx.ProjectService.RootPath, ".gitignore");
        _ctx.PendingUiActions.Enqueue(() => ReloadIfOpen(gitignorePath));
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitRemoveFromGitignoreAsync(string absolutePath)
    {
        await _ctx.GitService.RemoveFromGitignoreAsync(_ctx.ProjectService.RootPath, absolutePath);
        var gitignorePath = Path.Combine(_ctx.ProjectService.RootPath, ".gitignore");
        _ctx.PendingUiActions.Enqueue(() => ReloadIfOpen(gitignorePath));
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitStageAllAsync()
    {
        await _ctx.GitService.StageAllAsync(_ctx.ProjectService.RootPath);
        await RefreshGitStatusAsync();
    }

    public async Task GitUnstageAllAsync()
    {
        await _ctx.GitService.UnstageAllAsync(_ctx.ProjectService.RootPath);
        await RefreshGitStatusAsync();
    }

    public async Task GitDiscardFileAsync(string absolutePath)
    {
        var confirmed = await GitDiscardConfirmDialog.ShowAsync(_ctx.WindowSystem, absolutePath);
        if (!confirmed) return;
        await _ctx.GitService.DiscardChangesAsync(_ctx.ProjectService.RootPath, absolutePath);
        _ctx.PendingUiActions.Enqueue(() => ReloadIfOpen(absolutePath));
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitDiscardAllAsync()
    {
        var confirmed = await GitDiscardConfirmDialog.ShowAllAsync(_ctx.WindowSystem);
        if (!confirmed) return;
        await _ctx.GitService.DiscardAllAsync(_ctx.ProjectService.RootPath);
        _ctx.PendingUiActions.Enqueue(() => ReloadAllOpenFiles());
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitShowDiffAsync(string absolutePath)
    {
        var diff = await _ctx.GitService.GetDiffAsync(_ctx.ProjectService.RootPath, absolutePath);
        if (string.IsNullOrEmpty(diff))
        {
            diff = await _ctx.GitService.GetStagedDiffAsync(_ctx.ProjectService.RootPath, absolutePath);
        }
        if (string.IsNullOrEmpty(diff)) return;

        var fileName = Path.GetFileName(absolutePath);
        _ctx.PendingUiActions.Enqueue(() => OpenReadOnlyTab($"Diff: {fileName}", diff, new DiffSyntaxHighlighter()));
    }

    public async Task GitShowDiffAllAsync()
    {
        var diff = await _ctx.GitService.GetDiffAllAsync(_ctx.ProjectService.RootPath);
        if (string.IsNullOrEmpty(diff)) return;
        _ctx.PendingUiActions.Enqueue(() => OpenReadOnlyTab("Diff: All Changes", diff, new DiffSyntaxHighlighter()));
    }

    public async Task GitCommitAsync()
    {
        var status = await _ctx.GitService.GetStatusSummaryAsync(_ctx.ProjectService.RootPath);
        var message = await GitCommitDialog.ShowAsync(_ctx.WindowSystem, status);
        if (message == null) return;

        await _ctx.GitService.StageAllAsync(_ctx.ProjectService.RootPath);
        var result = await _ctx.GitService.CommitAsync(_ctx.ProjectService.RootPath, message);
        EnqueueGitOutput(result.StartsWith("Error")
            ? result
            : $"Committed: {result}", clear: true);
        await RefreshGitStatusAsync();
    }

    public async Task GitStashAsync()
    {
        var message = await GitStashDialog.ShowAsync(_ctx.WindowSystem);
        if (message == null) return;

        var result = await _ctx.GitService.StashAsync(_ctx.ProjectService.RootPath, message);
        EnqueueGitOutput(result, clear: true);
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitStashPopAsync()
    {
        var result = await _ctx.GitService.StashPopAsync(_ctx.ProjectService.RootPath);
        EnqueueGitOutput(result, clear: true);
        _ctx.PendingUiActions.Enqueue(() => ReloadAllOpenFiles());
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitSwitchBranchAsync()
    {
        var branches = await _ctx.GitService.GetBranchesAsync(_ctx.ProjectService.RootPath);
        if (branches.Count == 0) return;
        var current = branches.Count > 0 ? branches[0] : "";
        var selected = await GitBranchPickerDialog.ShowAsync(_ctx.WindowSystem, branches, current);
        if (selected == null) return;

        var result = await _ctx.GitService.CheckoutAsync(_ctx.ProjectService.RootPath, selected);
        EnqueueGitOutput(result.StartsWith("Error")
            ? result
            : $"Switched to branch: {result}", clear: true);
        _ctx.PendingUiActions.Enqueue(() => ReloadAllOpenFiles());
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitNewBranchAsync()
    {
        var name = await GitNewBranchDialog.ShowAsync(_ctx.WindowSystem);
        if (name == null) return;

        var result = await _ctx.GitService.CreateBranchAsync(_ctx.ProjectService.RootPath, name);
        EnqueueGitOutput(result.StartsWith("Error")
            ? result
            : $"Created branch: {result}", clear: true);
        await RefreshGitStatusAsync();
    }

    public async Task ShowCommitDetailAsync(GitLogEntry entry)
    {
        var detail = await _ctx.GitService.GetCommitDetailAsync(_ctx.ProjectService.RootPath, entry.Sha);
        _ctx.PendingUiActions.Enqueue(() => OpenReadOnlyTab($"Commit: {entry.ShortSha}", detail, new CommitDetailSyntaxHighlighter()));
    }

    public async Task GitShowLogAsync()
    {
        var entries = await _ctx.GitService.GetLogAsync(_ctx.ProjectService.RootPath);
        if (entries.Count == 0) return;
        var lines = entries.Select(e => $"{e.ShortSha}  {e.Author,-16}  {e.When:yyyy-MM-dd HH:mm}  {e.MessageShort}");
        var content = string.Join('\n', lines);
        _ctx.PendingUiActions.Enqueue(() => OpenReadOnlyTab("Git Log", content));
    }

    public async Task GitShowFileLogAsync(string absolutePath)
    {
        var entries = await _ctx.GitService.GetFileLogAsync(_ctx.ProjectService.RootPath, absolutePath);
        if (entries.Count == 0) return;
        var fileName = Path.GetFileName(absolutePath);
        var lines = entries.Select(e => $"{e.ShortSha}  {e.Author,-16}  {e.When:yyyy-MM-dd HH:mm}  {e.MessageShort}");
        var content = string.Join('\n', lines);
        _ctx.PendingUiActions.Enqueue(() => OpenReadOnlyTab($"Log: {fileName}", content));
    }

    public async Task GitShowBlameAsync(string absolutePath)
    {
        var blameLines = await _ctx.GitService.GetBlameAsync(_ctx.ProjectService.RootPath, absolutePath);
        if (blameLines.Count == 0) return;

        string[] sourceLines;
        try { sourceLines = await File.ReadAllLinesAsync(absolutePath); }
        catch { return; } // Cannot show blame if source file is unreadable

        var output = new List<string>();
        for (int i = 0; i < sourceLines.Length; i++)
        {
            var blame = i < blameLines.Count ? blameLines[i] : null;
            var prefix = blame != null
                ? $"{blame.ShortSha} {blame.Author,-12} {blame.When:yy-MM-dd}"
                : new string(' ', 27);
            output.Add($"{prefix} | {sourceLines[i]}");
        }

        var fileName = Path.GetFileName(absolutePath);
        var content = string.Join('\n', output);
        _ctx.PendingUiActions.Enqueue(() => OpenReadOnlyTab($"Blame: {fileName}", content));
    }

    public void OpenReadOnlyTab(string title, string content, ISyntaxHighlighter? highlighter = null)
    {
        _ctx.EditorManager.OpenReadOnlyTab(title, content, highlighter);
    }

    public void ReloadIfOpen(string absolutePath)
    {
        var idx = _ctx.EditorManager.GetTabIndexForPath(absolutePath);
        if (idx >= 0)
            _ctx.EditorManager.ReloadTabFromDisk(idx);
    }

    public void ReloadAllOpenFiles()
    {
        for (int i = 0; i < _ctx.EditorManager.TabCount; i++)
            _ctx.EditorManager.ReloadTabFromDisk(i);
    }

}
