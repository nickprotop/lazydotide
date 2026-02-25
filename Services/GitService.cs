using LibGit2Sharp;

namespace DotNetIDE;

public enum GitFileStatus { Modified, Added, Deleted, Renamed, Untracked, Conflicted }

public class GitService
{
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

    // Async wrappers for UI call sites
    public Task<string> GetBranchAsync(string path) => Task.Run(() => GetBranch(path));
    public Task<string> GetStatusSummaryAsync(string path) => Task.Run(() => GetStatusSummary(path));
    public Task<Dictionary<string, GitFileStatus>> GetFileStatusesAsync(string path) => Task.Run(() => GetFileStatuses(path));
}
