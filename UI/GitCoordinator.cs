using System.Collections.Concurrent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using Spectre.Console;

namespace DotNetIDE;

internal class GitCoordinator
{
    private readonly GitService _gitService;
    private readonly ProjectService _projectService;
    private readonly BuildService _buildService;
    private readonly EditorManager _editorManager;
    private readonly OutputPanel _outputPanel;
    private readonly SidePanel _sidePanel;
    private readonly ExplorerPanel _explorer;
    private readonly ConcurrentQueue<string> _buildLines;
    private readonly ConcurrentQueue<Action> _pendingUiActions;
    private readonly CancellationToken _ct;

    private string _gitMarkup = IdeConstants.GitStatusDefault;

    public event EventHandler<string>? GitStatusMarkupChanged;

    public string GitMarkup => _gitMarkup;

    public GitCoordinator(
        GitService gitService,
        ProjectService projectService,
        BuildService buildService,
        EditorManager editorManager,
        OutputPanel outputPanel,
        SidePanel sidePanel,
        ExplorerPanel explorer,
        ConcurrentQueue<string> buildLines,
        ConcurrentQueue<Action> pendingUiActions,
        CancellationToken ct)
    {
        _gitService = gitService;
        _projectService = projectService;
        _buildService = buildService;
        _editorManager = editorManager;
        _outputPanel = outputPanel;
        _sidePanel = sidePanel;
        _explorer = explorer;
        _buildLines = buildLines;
        _pendingUiActions = pendingUiActions;
        _ct = ct;
    }

    public async Task RefreshGitStatusAsync()
    {
        var branch = await _gitService.GetBranchAsync(_projectService.RootPath);
        var status = await _gitService.GetStatusSummaryAsync(_projectService.RootPath);

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
                bar.AddSegment($"[green] git:{Markup.Escape(displayBranch)}[/]",
                               $" git:{displayBranch}");
            }
            else
            {
                bar.AddSegment($"[yellow] git:{Markup.Escape(displayBranch)}[/]",
                               $" git:{displayBranch}")
                   .AddSegment($"[dim]  {Markup.Escape(status)}[/]",
                               $"  {status}");
            }
        }

        _gitMarkup = bar.Render();
        GitStatusMarkupChanged?.Invoke(this, _gitMarkup);

        await RefreshGitFileStatusesAsync();
    }

    public async Task RefreshGitFileStatusesAsync()
    {
        var (detailedFiles, workingDir) = await _gitService.GetDetailedFileStatusesAsync(_projectService.RootPath);
        var isPathIgnored = await _gitService.CreateIgnoreCheckerAsync(_projectService.RootPath);

        var fileStatuses = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in detailedFiles)
        {
            if (!fileStatuses.ContainsKey(f.RelativePath))
                fileStatuses[f.RelativePath] = f.Status;
        }

        _pendingUiActions.Enqueue(() => _explorer.UpdateGitStatuses(fileStatuses, workingDir, isPathIgnored));

        var root = _projectService.RootPath;
        var diffUpdates = await _editorManager.CollectGitDiffMarkersAsync(
            path => _gitService.GetLineDiffMarkersAsync(root, path)!);
        _pendingUiActions.Enqueue(() => _editorManager.ApplyGitDiffMarkers(diffUpdates));

        var branch = await _gitService.GetBranchAsync(_projectService.RootPath);
        var log = await _gitService.GetLogAsync(_projectService.RootPath, 15);
        var sidePanelFiles = detailedFiles
            .Select(f => (f.RelativePath, f.AbsolutePath, f.Status, f.IsStaged))
            .ToList();
        _pendingUiActions.Enqueue(() => _sidePanel.UpdateGitPanel(branch, sidePanelFiles, log));
    }

    public async Task RefreshGitDiffMarkersForFileAsync(string filePath)
    {
        var markers = await _gitService.GetLineDiffMarkersAsync(_projectService.RootPath, filePath);
        _pendingUiActions.Enqueue(() => _editorManager.UpdateGitDiffMarkers(filePath, markers));
    }

    public async Task RefreshExplorerAndGitAsync()
    {
        _pendingUiActions.Enqueue(() => _explorer.Refresh());
        await RefreshGitFileStatusesAsync();
    }

    public async Task GitCommandAsync(string command)
    {
        _outputPanel.ClearBuildOutput();
        _outputPanel.SwitchToBuildTab();

        await _buildService.RunAsync(
            $"git -C {_projectService.RootPath} {command}",
            line => _buildLines.Enqueue(line),
            _ct);

        await RefreshGitStatusAsync();
    }

    public async Task GitStageFileAsync(string absolutePath)
    {
        await _gitService.StageAsync(_projectService.RootPath, absolutePath);
        await RefreshGitStatusAsync();
    }

    public async Task GitUnstageFileAsync(string absolutePath)
    {
        await _gitService.UnstageAsync(_projectService.RootPath, absolutePath);
        await RefreshGitStatusAsync();
    }

    public async Task GitAddToGitignoreAsync(string absolutePath, bool isDirectory)
    {
        await _gitService.AddToGitignoreAsync(_projectService.RootPath, absolutePath, isDirectory);
        var gitignorePath = Path.Combine(_projectService.RootPath, ".gitignore");
        ReloadIfOpen(gitignorePath);
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitRemoveFromGitignoreAsync(string absolutePath)
    {
        await _gitService.RemoveFromGitignoreAsync(_projectService.RootPath, absolutePath);
        var gitignorePath = Path.Combine(_projectService.RootPath, ".gitignore");
        ReloadIfOpen(gitignorePath);
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitStageAllAsync()
    {
        await _gitService.StageAllAsync(_projectService.RootPath);
        await RefreshGitStatusAsync();
    }

    public async Task GitUnstageAllAsync()
    {
        await _gitService.UnstageAllAsync(_projectService.RootPath);
        await RefreshGitStatusAsync();
    }

    public async Task GitDiscardFileAsync(string absolutePath)
    {
        var confirmed = await GitDiscardConfirmDialog.ShowAsync(_ws!, absolutePath);
        if (!confirmed) return;
        await _gitService.DiscardChangesAsync(_projectService.RootPath, absolutePath);
        ReloadIfOpen(absolutePath);
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitDiscardAllAsync()
    {
        var confirmed = await GitDiscardConfirmDialog.ShowAllAsync(_ws!);
        if (!confirmed) return;
        await _gitService.DiscardAllAsync(_projectService.RootPath);
        ReloadAllOpenFiles();
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitShowDiffAsync(string absolutePath)
    {
        var diff = await _gitService.GetDiffAsync(_projectService.RootPath, absolutePath);
        if (string.IsNullOrEmpty(diff))
        {
            diff = await _gitService.GetStagedDiffAsync(_projectService.RootPath, absolutePath);
        }
        if (string.IsNullOrEmpty(diff)) return;

        var fileName = Path.GetFileName(absolutePath);
        OpenReadOnlyTab($"Diff: {fileName}", diff, new DiffSyntaxHighlighter());
    }

    public async Task GitShowDiffAllAsync()
    {
        var diff = await _gitService.GetDiffAllAsync(_projectService.RootPath);
        if (string.IsNullOrEmpty(diff)) return;
        OpenReadOnlyTab("Diff: All Changes", diff, new DiffSyntaxHighlighter());
    }

    public async Task GitCommitAsync()
    {
        var status = await _gitService.GetStatusSummaryAsync(_projectService.RootPath);
        var message = await GitCommitDialog.ShowAsync(_ws!, status);
        if (message == null) return;

        var result = await _gitService.CommitAsync(_projectService.RootPath, message);
        _outputPanel.ClearBuildOutput();
        _outputPanel.AppendBuildLine(result.StartsWith("Error")
            ? result
            : $"Committed: {result}");
        _outputPanel.SwitchToBuildTab();
        await RefreshGitStatusAsync();
    }

    public async Task GitStashAsync()
    {
        var message = await GitStashDialog.ShowAsync(_ws!);
        if (message == null) return;

        var result = await _gitService.StashAsync(_projectService.RootPath, message);
        _outputPanel.ClearBuildOutput();
        _outputPanel.AppendBuildLine(result);
        _outputPanel.SwitchToBuildTab();
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitStashPopAsync()
    {
        var result = await _gitService.StashPopAsync(_projectService.RootPath);
        _outputPanel.ClearBuildOutput();
        _outputPanel.AppendBuildLine(result);
        _outputPanel.SwitchToBuildTab();
        ReloadAllOpenFiles();
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitSwitchBranchAsync()
    {
        var branches = await _gitService.GetBranchesAsync(_projectService.RootPath);
        if (branches.Count == 0) return;
        var current = branches.Count > 0 ? branches[0] : "";
        var selected = await GitBranchPickerDialog.ShowAsync(_ws!, branches, current);
        if (selected == null) return;

        var result = await _gitService.CheckoutAsync(_projectService.RootPath, selected);
        _outputPanel.ClearBuildOutput();
        _outputPanel.AppendBuildLine(result.StartsWith("Error")
            ? result
            : $"Switched to branch: {result}");
        _outputPanel.SwitchToBuildTab();
        ReloadAllOpenFiles();
        await RefreshExplorerAndGitAsync();
    }

    public async Task GitNewBranchAsync()
    {
        var name = await GitNewBranchDialog.ShowAsync(_ws!);
        if (name == null) return;

        var result = await _gitService.CreateBranchAsync(_projectService.RootPath, name);
        _outputPanel.ClearBuildOutput();
        _outputPanel.AppendBuildLine(result.StartsWith("Error")
            ? result
            : $"Created branch: {result}");
        _outputPanel.SwitchToBuildTab();
        await RefreshGitStatusAsync();
    }

    public async Task ShowCommitDetailAsync(GitLogEntry entry)
    {
        var detail = await _gitService.GetCommitDetailAsync(_projectService.RootPath, entry.Sha);
        OpenReadOnlyTab($"Commit: {entry.ShortSha}", detail, new CommitDetailSyntaxHighlighter());
    }

    public async Task GitShowLogAsync()
    {
        var entries = await _gitService.GetLogAsync(_projectService.RootPath);
        if (entries.Count == 0) return;
        var lines = entries.Select(e => $"{e.ShortSha}  {e.Author,-16}  {e.When:yyyy-MM-dd HH:mm}  {e.MessageShort}");
        OpenReadOnlyTab("Git Log", string.Join('\n', lines));
    }

    public async Task GitShowFileLogAsync(string absolutePath)
    {
        var entries = await _gitService.GetFileLogAsync(_projectService.RootPath, absolutePath);
        if (entries.Count == 0) return;
        var fileName = Path.GetFileName(absolutePath);
        var lines = entries.Select(e => $"{e.ShortSha}  {e.Author,-16}  {e.When:yyyy-MM-dd HH:mm}  {e.MessageShort}");
        OpenReadOnlyTab($"Log: {fileName}", string.Join('\n', lines));
    }

    public async Task GitShowBlameAsync(string absolutePath)
    {
        var blameLines = await _gitService.GetBlameAsync(_projectService.RootPath, absolutePath);
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
        OpenReadOnlyTab($"Blame: {fileName}", string.Join('\n', output));
    }

    public void OpenReadOnlyTab(string title, string content, ISyntaxHighlighter? highlighter = null)
    {
        _editorManager.OpenReadOnlyTab(title, content, highlighter);
    }

    public void ReloadIfOpen(string absolutePath)
    {
        var idx = _editorManager.GetTabIndexForPath(absolutePath);
        if (idx >= 0)
            _editorManager.ReloadTabFromDisk(idx);
    }

    public void ReloadAllOpenFiles()
    {
        for (int i = 0; i < _editorManager.TabCount; i++)
            _editorManager.ReloadTabFromDisk(i);
    }

    // Post-init: set the window system for dialog support
    private ConsoleWindowSystem? _ws;

    public void SetWindowSystem(ConsoleWindowSystem ws)
    {
        _ws = ws;
    }
}
