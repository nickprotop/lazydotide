using System.Runtime.CompilerServices;
using System.Text;

namespace DotNetIDE;

public record SearchResult(string FilePath, int Line, int Column, string LineText, int MatchLength);

public class FileSearchService
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".bin", ".obj", ".o", ".so", ".dylib",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
        ".zip", ".gz", ".tar", ".rar", ".7z", ".nupkg",
        ".woff", ".woff2", ".ttf", ".eot",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".mp3", ".mp4", ".wav", ".avi", ".mov",
        ".db", ".sqlite", ".mdb",
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", ".idea", "packages",
        "TestResults", "Debug", "Release",
    };

    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string rootPath,
        string searchTerm,
        bool caseSensitive,
        Func<string, bool>? isIgnored,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(searchTerm))
            yield break;

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var dirs = new Stack<string>();
        dirs.Push(rootPath);

        while (dirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = dirs.Pop();

            // Enqueue subdirectories
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (SkipDirectories.Contains(dirName))
                        continue;

                    // Check .gitignore
                    if (isIgnored != null)
                    {
                        var relPath = Path.GetRelativePath(rootPath, subDir).Replace('\\', '/');
                        if (isIgnored(relPath))
                            continue;
                    }

                    dirs.Push(subDir);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }

            // Search files in current directory
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(file);
                if (BinaryExtensions.Contains(ext))
                    continue;

                // Check .gitignore
                if (isIgnored != null)
                {
                    var relPath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    if (isIgnored(relPath))
                        continue;
                }

                // Skip large files
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxFileSize)
                        continue;
                }
                catch { continue; }

                // Search within file
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    int idx = line.IndexOf(searchTerm, comparison);
                    if (idx >= 0)
                    {
                        yield return new SearchResult(file, i + 1, idx + 1, line, searchTerm.Length);
                    }
                }

                // Yield control between files for UI responsiveness
                await Task.Yield();
            }
        }
    }

    private static void LogError(string context, Exception ex)
    {
        DiagnosticLog.Write("search", $"[{context}] {ex.Message}");
    }
}
