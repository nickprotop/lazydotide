using LibGit2Sharp;

namespace DotNetIDE;

public enum GitFileStatus { Modified, Added, Deleted, Renamed, Untracked, Conflicted }

public enum GitLineChangeType { Added, Modified, Deleted }

public record GitLogEntry(string Sha, string ShortSha, string Author, DateTimeOffset When, string MessageShort);

public record GitBlameLine(string Sha, string ShortSha, string Author, DateTimeOffset When, int OriginalLineNumber);

public record GitDetailedFileEntry(string RelativePath, string AbsolutePath, GitFileStatus Status, bool IsStaged);

public class GitService
{
    // ──────────────────────────────────────────────────────────────
    // Read-only queries (existing)
    // ──────────────────────────────────────────────────────────────

    public string GetBranch(string path)
    {
        try
        {
            var repoPath = Repository.Discover(path);
            if (repoPath == null) return "";
            using var repo = new Repository(repoPath);
            return repo.Head.FriendlyName;
        }
        catch { return ""; }
    }

    public string GetStatusSummary(string path)
    {
        try
        {
            var repoPath = Repository.Discover(path);
            if (repoPath == null) return "";
            using var repo = new Repository(repoPath);
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                IncludeIgnored = false
            });

            int modified = 0, added = 0, deleted = 0;
            foreach (var entry in status)
            {
                if (entry.State.HasFlag(FileStatus.ModifiedInWorkdir) ||
                    entry.State.HasFlag(FileStatus.ModifiedInIndex))
                    modified++;
                else if (entry.State.HasFlag(FileStatus.NewInWorkdir))
                    added++;
                else if (entry.State.HasFlag(FileStatus.NewInIndex))
                    added++;
                else if (entry.State.HasFlag(FileStatus.DeletedFromWorkdir) ||
                         entry.State.HasFlag(FileStatus.DeletedFromIndex))
                    deleted++;
            }

            var parts = new List<string>();
            if (modified > 0) parts.Add($"M:{modified}");
            if (added > 0)    parts.Add($"A:{added}");
            if (deleted > 0)  parts.Add($"D:{deleted}");
            return string.Join("  ", parts);
        }
        catch { return ""; }
    }

    public Dictionary<string, GitFileStatus> GetFileStatuses(string path)
    {
        var statuses = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var repoPath = Repository.Discover(path);
            if (repoPath == null) return statuses;
            using var repo = new Repository(repoPath);
            foreach (var entry in repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                IncludeIgnored = false
            }))
            {
                var gitStatus = MapStatus(entry.State);
                if (gitStatus.HasValue)
                    statuses[entry.FilePath] = gitStatus.Value;
            }
        }
        catch { }
        return statuses;
    }

    /// <summary>
    /// Returns detailed file entries with staged/unstaged info and absolute paths.
    /// A single file may appear twice if it has both staged and unstaged changes.
    /// </summary>
    public (List<GitDetailedFileEntry> Files, string? WorkingDir) GetDetailedFileStatuses(string repoRoot)
    {
        var files = new List<GitDetailedFileEntry>();
        string? workDir = null;
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return (files, null);
            using var repo = new Repository(repoPath);
            workDir = repo.Info.WorkingDirectory;

            foreach (var entry in repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                IncludeIgnored = false
            }))
            {
                var absPath = Path.GetFullPath(Path.Combine(workDir, entry.FilePath));

                // Check for staged changes (in index)
                if (entry.State.HasFlag(FileStatus.ModifiedInIndex) ||
                    entry.State.HasFlag(FileStatus.NewInIndex) ||
                    entry.State.HasFlag(FileStatus.DeletedFromIndex) ||
                    entry.State.HasFlag(FileStatus.RenamedInIndex))
                {
                    var status = MapStatus(entry.State) ?? GitFileStatus.Modified;
                    files.Add(new GitDetailedFileEntry(entry.FilePath, absPath, status, IsStaged: true));
                }

                // Check for unstaged changes (in workdir)
                if (entry.State.HasFlag(FileStatus.ModifiedInWorkdir) ||
                    entry.State.HasFlag(FileStatus.NewInWorkdir) ||
                    entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
                {
                    var status = MapStatus(entry.State) ?? GitFileStatus.Modified;
                    files.Add(new GitDetailedFileEntry(entry.FilePath, absPath, status, IsStaged: false));
                }
            }
        }
        catch { }
        return (files, workDir);
    }

    /// <summary>
    /// Gets the git status of a single file. Returns null if the file is unmodified or not in a repo.
    /// </summary>
    public GitFileStatus? GetFileStatus(string repoRoot, string absolutePath)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return null;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var status = repo.RetrieveStatus(relativePath);
            return MapStatus(status);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns true if the file has staged changes (in the index).
    /// </summary>
    public bool IsStaged(string repoRoot, string absolutePath)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return false;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var status = repo.RetrieveStatus(relativePath);
            return status.HasFlag(FileStatus.ModifiedInIndex)
                || status.HasFlag(FileStatus.NewInIndex)
                || status.HasFlag(FileStatus.DeletedFromIndex)
                || status.HasFlag(FileStatus.RenamedInIndex);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true if the file has unstaged working directory changes.
    /// </summary>
    public bool HasWorkingChanges(string repoRoot, string absolutePath)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return false;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var status = repo.RetrieveStatus(relativePath);
            return status.HasFlag(FileStatus.ModifiedInWorkdir)
                || status.HasFlag(FileStatus.NewInWorkdir)
                || status.HasFlag(FileStatus.DeletedFromWorkdir);
        }
        catch { return false; }
    }

    // ──────────────────────────────────────────────────────────────
    // Stage / Unstage
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stages a file or directory (recursive). Equivalent to <c>git add &lt;path&gt;</c>.
    /// </summary>
    public void Stage(string repoRoot, string absolutePath)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        var relativePath = MakeRelative(repo, absolutePath);

        if (Directory.Exists(absolutePath))
        {
            // Stage all files under the directory
            var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, IncludeIgnored = false });
            foreach (var entry in status)
            {
                if (entry.FilePath.StartsWith(relativePath, StringComparison.OrdinalIgnoreCase))
                    Commands.Stage(repo, entry.FilePath);
            }
        }
        else
        {
            Commands.Stage(repo, relativePath);
        }
    }

    /// <summary>
    /// Unstages a file or directory. Equivalent to <c>git reset HEAD &lt;path&gt;</c>.
    /// </summary>
    public void Unstage(string repoRoot, string absolutePath)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        var relativePath = MakeRelative(repo, absolutePath);

        if (Directory.Exists(absolutePath))
        {
            var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, IncludeIgnored = false });
            foreach (var entry in status)
            {
                if (entry.FilePath.StartsWith(relativePath, StringComparison.OrdinalIgnoreCase))
                    Commands.Unstage(repo, entry.FilePath);
            }
        }
        else
        {
            Commands.Unstage(repo, relativePath);
        }
    }

    /// <summary>
    /// Stages all modified, new, and deleted files. Equivalent to <c>git add -A</c>.
    /// </summary>
    public void StageAll(string repoRoot)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, "*");
    }

    /// <summary>
    /// Unstages all staged files. Equivalent to <c>git reset HEAD</c>.
    /// </summary>
    public void UnstageAll(string repoRoot)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        repo.Reset(ResetMode.Mixed);
    }

    // ──────────────────────────────────────────────────────────────
    // Discard changes
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Discards working directory changes for a file. Equivalent to <c>git checkout -- &lt;path&gt;</c>.
    /// For untracked files, deletes the file from disk.
    /// </summary>
    public void DiscardChanges(string repoRoot, string absolutePath)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        var relativePath = MakeRelative(repo, absolutePath);
        var status = repo.RetrieveStatus(relativePath);

        if (status.HasFlag(FileStatus.NewInWorkdir))
        {
            // Untracked file — just delete it
            if (File.Exists(absolutePath))
                File.Delete(absolutePath);
        }
        else
        {
            var opts = new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force };
            repo.CheckoutPaths(repo.Head.FriendlyName, new[] { relativePath }, opts);
        }
    }

    /// <summary>
    /// Discards all working directory changes. Equivalent to <c>git checkout -- .</c>.
    /// </summary>
    public void DiscardAll(string repoRoot)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        repo.Reset(ResetMode.Hard);
    }

    // ──────────────────────────────────────────────────────────────
    // Diff
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the unified diff (working directory vs index) for a single file.
    /// </summary>
    public string GetDiff(string repoRoot, string absolutePath)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "";
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var patch = repo.Diff.Compare<Patch>(new[] { relativePath });
            return patch.Content;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns the unified diff (index vs HEAD) for a single file.
    /// </summary>
    public string GetStagedDiff(string repoRoot, string absolutePath)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "";
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var patch = repo.Diff.Compare<Patch>(
                repo.Head.Tip?.Tree,
                DiffTargets.Index,
                new[] { relativePath });
            return patch.Content;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns the unified diff for all working directory changes.
    /// </summary>
    public string GetDiffAll(string repoRoot)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "";
            using var repo = new Repository(repoPath);
            var patch = repo.Diff.Compare<Patch>();
            return patch.Content;
        }
        catch { return ""; }
    }

    // ──────────────────────────────────────────────────────────────
    // Line-level diff markers (for gutter decorations)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a dictionary mapping 0-based line indices to change types
    /// for displaying gutter decorations (working directory vs index).
    /// </summary>
    public Dictionary<int, GitLineChangeType> GetLineDiffMarkers(string repoRoot, string absolutePath)
    {
        var markers = new Dictionary<int, GitLineChangeType>();
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return markers;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var patch = repo.Diff.Compare<Patch>(new[] { relativePath });

            foreach (var entry in patch)
            {
                // Parse the unified diff content line by line
                var lines = entry.Patch.Split('\n');
                int newLine = -1; // 0-based line index in new file
                int hunkDeletions = 0;
                int hunkAdditions = 0;
                var hunkAddedLines = new List<int>();

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("@@"))
                    {
                        // Flush previous hunk
                        FlushHunk(markers, hunkAddedLines, hunkDeletions, hunkAdditions, newLine);
                        hunkDeletions = 0;
                        hunkAdditions = 0;
                        hunkAddedLines = new List<int>();

                        // Parse @@ -old,count +new,count @@
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"\+(\d+)");
                        if (match.Success)
                            newLine = int.Parse(match.Groups[1].Value) - 1; // convert to 0-based
                        else
                            newLine = 0;
                    }
                    else if (newLine >= 0)
                    {
                        if (line.StartsWith("+"))
                        {
                            hunkAdditions++;
                            hunkAddedLines.Add(newLine);
                            newLine++;
                        }
                        else if (line.StartsWith("-"))
                        {
                            hunkDeletions++;
                            // Deleted lines don't advance the new-file line counter
                        }
                        else if (line.StartsWith(" ") || line.Length == 0)
                        {
                            // Context line — flush accumulated changes first
                            FlushHunk(markers, hunkAddedLines, hunkDeletions, hunkAdditions, newLine);
                            hunkDeletions = 0;
                            hunkAdditions = 0;
                            hunkAddedLines = new List<int>();
                            newLine++;
                        }
                    }
                }
                // Flush final hunk
                FlushHunk(markers, hunkAddedLines, hunkDeletions, hunkAdditions, newLine);
            }
        }
        catch { }
        return markers;
    }

    private static void FlushHunk(
        Dictionary<int, GitLineChangeType> markers,
        List<int> addedLines,
        int deletions,
        int additions,
        int currentNewLine)
    {
        if (additions == 0 && deletions == 0) return;

        if (additions > 0 && deletions > 0)
        {
            // Lines that have corresponding deletions are "Modified", excess are "Added"
            for (int i = 0; i < addedLines.Count; i++)
                markers[addedLines[i]] = i < deletions ? GitLineChangeType.Modified : GitLineChangeType.Added;
        }
        else if (additions > 0)
        {
            // Pure additions
            foreach (var line in addedLines)
                markers[line] = GitLineChangeType.Added;
        }
        else if (deletions > 0)
        {
            // Pure deletions — mark the line where content was removed
            // currentNewLine is the next line after the deletion point
            int deletedAt = currentNewLine >= 0 ? currentNewLine : 0;
            if (!markers.ContainsKey(deletedAt))
                markers[deletedAt] = GitLineChangeType.Deleted;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Commit
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a commit with the given message using the repo's configured user.
    /// Returns the short SHA of the new commit, or an error string.
    /// </summary>
    public string Commit(string repoRoot, string message)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "Error: not a git repository";
            using var repo = new Repository(repoPath);
            var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
            var commit = repo.Commit(message, sig, sig);
            return commit.Sha[..7];
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ──────────────────────────────────────────────────────────────
    // Stash
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stashes all working directory and staged changes.
    /// Returns the stash message or error.
    /// </summary>
    public string Stash(string repoRoot, string? message = null)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "Error: not a git repository";
            using var repo = new Repository(repoPath);
            var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
            var stash = repo.Stashes.Add(sig, message ?? "LazyDotIDE stash");
            return stash == null ? "Nothing to stash" : $"Stashed: {stash.Message}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Pops the most recent stash. Returns a status message.
    /// </summary>
    public string StashPop(string repoRoot)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "Error: not a git repository";
            using var repo = new Repository(repoPath);
            if (repo.Stashes.Count() == 0)
                return "No stashes to pop";
            var result = repo.Stashes.Pop(0);
            return result == StashApplyStatus.Applied ? "Stash applied" : $"Stash result: {result}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ──────────────────────────────────────────────────────────────
    // Branches
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all local branch names. The current branch is first.
    /// </summary>
    public List<string> GetBranches(string repoRoot)
    {
        var result = new List<string>();
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return result;
            using var repo = new Repository(repoPath);
            var current = repo.Head.FriendlyName;
            result.Add(current);
            foreach (var branch in repo.Branches.Where(b => !b.IsRemote && b.FriendlyName != current))
                result.Add(branch.FriendlyName);
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Creates a new local branch from HEAD.
    /// Returns the branch name or error.
    /// </summary>
    public string CreateBranch(string repoRoot, string branchName)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "Error: not a git repository";
            using var repo = new Repository(repoPath);
            var branch = repo.CreateBranch(branchName);
            return branch.FriendlyName;
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Checks out an existing local branch.
    /// Returns the branch name or error.
    /// </summary>
    public string Checkout(string repoRoot, string branchName)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return "Error: not a git repository";
            using var repo = new Repository(repoPath);
            var branch = repo.Branches[branchName];
            if (branch == null) return $"Error: branch '{branchName}' not found";
            Commands.Checkout(repo, branch);
            return branch.FriendlyName;
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ──────────────────────────────────────────────────────────────
    // Log
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns recent commit log entries for the repository.
    /// </summary>
    public List<GitLogEntry> GetLog(string repoRoot, int limit = 50)
    {
        var entries = new List<GitLogEntry>();
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return entries;
            using var repo = new Repository(repoPath);
            foreach (var commit in repo.Commits.Take(limit))
            {
                entries.Add(new GitLogEntry(
                    commit.Sha,
                    commit.Sha[..7],
                    commit.Author.Name,
                    commit.Author.When,
                    commit.MessageShort));
            }
        }
        catch { }
        return entries;
    }

    /// <summary>
    /// Returns recent commit log entries that touched a specific file.
    /// </summary>
    public List<GitLogEntry> GetFileLog(string repoRoot, string absolutePath, int limit = 50)
    {
        var entries = new List<GitLogEntry>();
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return entries;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var filter = new CommitFilter { SortBy = CommitSortStrategies.Time };

            Commit? previous = null;
            foreach (var commit in repo.Commits.QueryBy(filter))
            {
                if (entries.Count >= limit) break;

                if (previous != null)
                {
                    var changes = repo.Diff.Compare<TreeChanges>(commit.Tree, previous.Tree);
                    if (changes.Any(c => c.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        entries.Add(new GitLogEntry(
                            previous.Sha,
                            previous.Sha[..7],
                            previous.Author.Name,
                            previous.Author.When,
                            previous.MessageShort));
                    }
                }
                previous = commit;
            }
        }
        catch { }
        return entries;
    }

    // ──────────────────────────────────────────────────────────────
    // Blame
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns per-line blame information for a file.
    /// </summary>
    public List<GitBlameLine> GetBlame(string repoRoot, string absolutePath)
    {
        var result = new List<GitBlameLine>();
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return result;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            var blame = repo.Blame(relativePath);

            foreach (var hunk in blame)
            {
                for (int i = 0; i < hunk.LineCount; i++)
                {
                    result.Add(new GitBlameLine(
                        hunk.FinalCommit.Sha,
                        hunk.FinalCommit.Sha[..7],
                        hunk.FinalSignature.Name,
                        hunk.FinalSignature.When,
                        hunk.FinalStartLineNumber + i));
                }
            }
        }
        catch { }
        return result;
    }

    // ──────────────────────────────────────────────────────────────
    // Ignored files
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a function that checks whether a repo-relative path is ignored by git.
    /// Uses libgit2's ignore rules engine which handles all .gitignore patterns correctly.
    /// The returned function is safe to call from the UI thread (results are pre-cached).
    /// </summary>
    public Func<string, bool> CreateIgnoreChecker(string repoRoot)
    {
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return _ => false;
            using var repo = new Repository(repoPath);
            var workDir = repo.Info.WorkingDirectory;
            if (workDir == null) return _ => false;

            // Walk the file system and check each path against git's ignore rules
            void CheckDirectory(string dir, string relativePrefix)
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    {
                        var name = Path.GetFileName(entry);
                        if (name == ".git") continue;

                        var isDir = Directory.Exists(entry);
                        var relativePath = string.IsNullOrEmpty(relativePrefix)
                            ? name
                            : relativePrefix + "/" + name;
                        // libgit2 expects trailing slash for directory checks
                        var checkPath = isDir ? relativePath + "/" : relativePath;
                        bool ignored = repo.Ignore.IsPathIgnored(checkPath);
                        cache[relativePath] = ignored;

                        if (isDir && !ignored)
                            CheckDirectory(entry, relativePath);
                        // Don't recurse into ignored directories — all children are implicitly ignored
                    }
                }
                catch { }
            }

            CheckDirectory(workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "");
        }
        catch { }

        return path => cache.TryGetValue(path, out var ignored) && ignored;
    }

    // ──────────────────────────────────────────────────────────────
    // .gitignore operations
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks if a path has a matching entry in .gitignore.
    /// </summary>
    public bool IsInGitignore(string repoRoot, string absolutePath)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return false;
            using var repo = new Repository(repoPath);
            var relativePath = MakeRelative(repo, absolutePath);
            return repo.Ignore.IsPathIgnored(relativePath);
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Adds a path to .gitignore. Appends trailing / for directories.
    /// Creates .gitignore if it doesn't exist.
    /// </summary>
    public void AddToGitignore(string repoRoot, string absolutePath, bool isDirectory)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        var relativePath = MakeRelative(repo, absolutePath);
        if (isDirectory && !relativePath.EndsWith('/'))
            relativePath += '/';

        var gitignorePath = Path.Combine(repo.Info.WorkingDirectory!, ".gitignore");
        var content = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : "";

        // Ensure file ends with newline before appending
        if (content.Length > 0 && !content.EndsWith('\n'))
            content += "\n";

        content += relativePath + "\n";
        File.WriteAllText(gitignorePath, content);
    }

    /// <summary>
    /// Removes a path from .gitignore (with or without trailing /).
    /// </summary>
    public void RemoveFromGitignore(string repoRoot, string absolutePath)
    {
        var repoPath = Repository.Discover(repoRoot);
        if (repoPath == null) return;
        using var repo = new Repository(repoPath);
        var relativePath = MakeRelative(repo, absolutePath);
        var gitignorePath = Path.Combine(repo.Info.WorkingDirectory!, ".gitignore");
        if (!File.Exists(gitignorePath)) return;

        var lines = File.ReadAllLines(gitignorePath);
        var filtered = lines.Where(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) return true;
            var pattern = trimmed.TrimEnd('/');
            return !pattern.Equals(relativePath, StringComparison.Ordinal) &&
                   !pattern.Equals(relativePath.TrimEnd('/'), StringComparison.Ordinal);
        }).ToList();

        File.WriteAllLines(gitignorePath, filtered);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static GitFileStatus? MapStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Conflicted))
            return GitFileStatus.Conflicted;
        if (state.HasFlag(FileStatus.RenamedInIndex) || state.HasFlag(FileStatus.RenamedInWorkdir))
            return GitFileStatus.Renamed;
        if (state.HasFlag(FileStatus.ModifiedInWorkdir) || state.HasFlag(FileStatus.ModifiedInIndex))
            return GitFileStatus.Modified;
        if (state.HasFlag(FileStatus.NewInIndex))
            return GitFileStatus.Added;
        if (state.HasFlag(FileStatus.NewInWorkdir))
            return GitFileStatus.Untracked;
        if (state.HasFlag(FileStatus.DeletedFromWorkdir) || state.HasFlag(FileStatus.DeletedFromIndex))
            return GitFileStatus.Deleted;
        return null;
    }

    /// <summary>
    /// Converts an absolute filesystem path to a repo-relative path using forward slashes.
    /// </summary>
    private static string MakeRelative(Repository repo, string absolutePath)
    {
        var workDir = repo.Info.WorkingDirectory;
        if (workDir == null) return absolutePath;

        // Normalize to forward slashes for git
        var fullPath = Path.GetFullPath(absolutePath).Replace('\\', '/');
        var rootPath = Path.GetFullPath(workDir).Replace('\\', '/');
        if (!rootPath.EndsWith('/')) rootPath += '/';

        if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return fullPath[rootPath.Length..];

        return absolutePath;
    }

    // Async wrappers for UI call sites
    public Task<string> GetBranchAsync(string path) => Task.Run(() => GetBranch(path));
    public Task<string> GetStatusSummaryAsync(string path) => Task.Run(() => GetStatusSummary(path));
    public Task<Dictionary<string, GitFileStatus>> GetFileStatusesAsync(string path) => Task.Run(() => GetFileStatuses(path));
    public Task<(List<GitDetailedFileEntry> Files, string? WorkingDir)> GetDetailedFileStatusesAsync(string repoRoot) => Task.Run(() => GetDetailedFileStatuses(repoRoot));

    public Task<GitFileStatus?> GetFileStatusAsync(string repoRoot, string absolutePath) => Task.Run(() => GetFileStatus(repoRoot, absolutePath));
    public Task<bool> IsStagedAsync(string repoRoot, string absolutePath) => Task.Run(() => IsStaged(repoRoot, absolutePath));
    public Task<bool> HasWorkingChangesAsync(string repoRoot, string absolutePath) => Task.Run(() => HasWorkingChanges(repoRoot, absolutePath));

    public Task StageAsync(string repoRoot, string absolutePath) => Task.Run(() => Stage(repoRoot, absolutePath));
    public Task UnstageAsync(string repoRoot, string absolutePath) => Task.Run(() => Unstage(repoRoot, absolutePath));
    public Task StageAllAsync(string repoRoot) => Task.Run(() => StageAll(repoRoot));
    public Task UnstageAllAsync(string repoRoot) => Task.Run(() => UnstageAll(repoRoot));

    public Task DiscardChangesAsync(string repoRoot, string absolutePath) => Task.Run(() => DiscardChanges(repoRoot, absolutePath));
    public Task DiscardAllAsync(string repoRoot) => Task.Run(() => DiscardAll(repoRoot));

    public Task<string> GetDiffAsync(string repoRoot, string absolutePath) => Task.Run(() => GetDiff(repoRoot, absolutePath));
    public Task<string> GetStagedDiffAsync(string repoRoot, string absolutePath) => Task.Run(() => GetStagedDiff(repoRoot, absolutePath));
    public Task<string> GetDiffAllAsync(string repoRoot) => Task.Run(() => GetDiffAll(repoRoot));

    public Task<string> CommitAsync(string repoRoot, string message) => Task.Run(() => Commit(repoRoot, message));

    public Task<string> StashAsync(string repoRoot, string? message = null) => Task.Run(() => Stash(repoRoot, message));
    public Task<string> StashPopAsync(string repoRoot) => Task.Run(() => StashPop(repoRoot));

    public Task<List<string>> GetBranchesAsync(string repoRoot) => Task.Run(() => GetBranches(repoRoot));
    public Task<string> CreateBranchAsync(string repoRoot, string branchName) => Task.Run(() => CreateBranch(repoRoot, branchName));
    public Task<string> CheckoutAsync(string repoRoot, string branchName) => Task.Run(() => Checkout(repoRoot, branchName));

    public Task<List<GitLogEntry>> GetLogAsync(string repoRoot, int limit = 50) => Task.Run(() => GetLog(repoRoot, limit));
    public Task<List<GitLogEntry>> GetFileLogAsync(string repoRoot, string absolutePath, int limit = 50) => Task.Run(() => GetFileLog(repoRoot, absolutePath, limit));

    public Task<List<GitBlameLine>> GetBlameAsync(string repoRoot, string absolutePath) => Task.Run(() => GetBlame(repoRoot, absolutePath));

    public Task<Dictionary<int, GitLineChangeType>> GetLineDiffMarkersAsync(string repoRoot, string absolutePath) => Task.Run(() => GetLineDiffMarkers(repoRoot, absolutePath));

    public Task<string> GetCommitDetailAsync(string repoRoot, string sha) => Task.Run(() => GetCommitDetail(repoRoot, sha));

    public Task<Func<string, bool>> CreateIgnoreCheckerAsync(string repoRoot) => Task.Run(() => CreateIgnoreChecker(repoRoot));
    public Task<bool> IsInGitignoreAsync(string repoRoot, string absolutePath) => Task.Run(() => IsInGitignore(repoRoot, absolutePath));
    public Task AddToGitignoreAsync(string repoRoot, string absolutePath, bool isDirectory) => Task.Run(() => AddToGitignore(repoRoot, absolutePath, isDirectory));
    public Task RemoveFromGitignoreAsync(string repoRoot, string absolutePath) => Task.Run(() => RemoveFromGitignore(repoRoot, absolutePath));

    public string GetCommitDetail(string repoRoot, string sha)
    {
        try
        {
            var repoPath = Repository.Discover(repoRoot);
            if (repoPath == null) return $"Commit {sha} not found.";
            using var repo = new Repository(repoPath);
            var commit = repo.Lookup<Commit>(sha);
            if (commit == null) return $"Commit {sha} not found.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"commit {commit.Sha}");
            sb.AppendLine($"Author: {commit.Author.Name} <{commit.Author.Email}>");
            sb.AppendLine($"Date:   {commit.Author.When:ddd MMM d HH:mm:ss yyyy zzz}");
            sb.AppendLine();
            // Full message, indented like git log
            foreach (var line in commit.Message.TrimEnd().Split('\n'))
                sb.AppendLine($"    {line}");
            sb.AppendLine();

            // Diff stats against parent
            var parent = commit.Parents.FirstOrDefault();
            var tree = commit.Tree;
            var parentTree = parent?.Tree;
            var diff = repo.Diff.Compare<Patch>(parentTree, tree);

            int totalAdded = 0, totalDeleted = 0;
            foreach (var entry in diff)
            {
                var status = entry.Status switch
                {
                    ChangeKind.Added => "A",
                    ChangeKind.Deleted => "D",
                    ChangeKind.Modified => "M",
                    ChangeKind.Renamed => "R",
                    ChangeKind.Copied => "C",
                    _ => "?"
                };
                sb.AppendLine($" {status}  +{entry.LinesAdded,-4} -{entry.LinesDeleted,-4}  {entry.Path}");
                totalAdded += entry.LinesAdded;
                totalDeleted += entry.LinesDeleted;
            }

            sb.AppendLine();
            sb.AppendLine($" {diff.Count()} file(s) changed, {totalAdded} insertion(s), {totalDeleted} deletion(s)");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading commit {sha}: {ex.Message}";
        }
    }
}
