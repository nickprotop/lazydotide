namespace DotNetIDE;

public class FileWatcher : IDisposable
{
    private const int FileChangedDebounceMs = 300;
    private const int StructureChangedDebounceMs = 500;
    private const int GitChangedDebounceMs = 500;

    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private readonly Dictionary<string, Timer> _fileDebounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _structureDebounceTimer;
    private Timer? _gitDebounceTimer;
    private readonly HashSet<string> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private int _suppressGitCount;

    public event EventHandler<string>? FileChanged;
    public event EventHandler? StructureChanged;
    public event EventHandler? GitChanged;

    public void Watch(string rootPath)
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;

            if (!Directory.Exists(rootPath)) return;

            _watcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFswChanged;
            _watcher.Created += OnFswCreatedOrDeleted;
            _watcher.Deleted += OnFswCreatedOrDeleted;
            _watcher.Renamed += OnFswRenamed;
            _watcher.Error   += (_, _) => { }; // swallow inotify overflow etc.
        }
    }

    public void SuppressGitChanges() => Interlocked.Increment(ref _suppressGitCount);
    public void ResumeGitChanges() => Interlocked.Decrement(ref _suppressGitCount);

    public void SuppressNext(string path)
    {
        lock (_lock)
            _suppressedPaths.Add(Path.GetFullPath(path));
    }

    // ── FSW handlers ───────────────────────────────────────────────

    private void OnFswChanged(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath)) return;
        if (Directory.Exists(e.FullPath)) return;
        if (IsGitInternalPath(e.FullPath)) { ScheduleGitRefresh(); return; }
        ScheduleFileChanged(e.FullPath);
    }

    private void OnFswCreatedOrDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsGitInternalPath(e.FullPath)) { ScheduleGitRefresh(); return; }
        ScheduleStructureRefresh();
    }

    private void OnFswRenamed(object sender, RenamedEventArgs e)
    {
        if (IsGitInternalPath(e.FullPath)) { ScheduleGitRefresh(); return; }
        // Covers atomic-save pattern (write temp → rename to final path)
        ScheduleFileChanged(e.FullPath);
        ScheduleStructureRefresh();
    }

    // ── Debounce ───────────────────────────────────────────────────

    private void ScheduleFileChanged(string fullPath)
    {
        lock (_lock)
        {
            if (_fileDebounceTimers.TryGetValue(fullPath, out var existing))
            {
                existing.Change(FileChangedDebounceMs, Timeout.Infinite);
                return;
            }

            var timer = new Timer(_ =>
            {
                lock (_lock)
                {
                    _fileDebounceTimers.Remove(fullPath);
                    if (_suppressedPaths.Remove(fullPath)) return;
                }
                FileChanged?.Invoke(this, fullPath);
            }, null, FileChangedDebounceMs, Timeout.Infinite);

            _fileDebounceTimers[fullPath] = timer;
        }
    }

    private void ScheduleStructureRefresh()
    {
        lock (_lock)
        {
            if (_structureDebounceTimer == null)
            {
                _structureDebounceTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        _structureDebounceTimer?.Dispose();
                        _structureDebounceTimer = null;
                    }
                    StructureChanged?.Invoke(this, EventArgs.Empty);
                }, null, StructureChangedDebounceMs, Timeout.Infinite);
            }
            else
            {
                _structureDebounceTimer.Change(StructureChangedDebounceMs, Timeout.Infinite);
            }
        }
    }

    private void ScheduleGitRefresh()
    {
        if (Volatile.Read(ref _suppressGitCount) > 0) return;
        lock (_lock)
        {
            if (_gitDebounceTimer == null)
            {
                _gitDebounceTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        _gitDebounceTimer?.Dispose();
                        _gitDebounceTimer = null;
                    }
                    GitChanged?.Invoke(this, EventArgs.Empty);
                }, null, GitChangedDebounceMs, Timeout.Infinite);
            }
            else
            {
                _gitDebounceTimer.Change(GitChangedDebounceMs, Timeout.Infinite);
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static bool IsGitInternalPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.git/") || normalized.EndsWith("/.git");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;
            foreach (var t in _fileDebounceTimers.Values) t.Dispose();
            _fileDebounceTimers.Clear();
            _structureDebounceTimer?.Dispose();
            _structureDebounceTimer = null;
            _gitDebounceTimer?.Dispose();
            _gitDebounceTimer = null;
        }
    }
}
