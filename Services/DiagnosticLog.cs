namespace DotNetIDE;

/// <summary>
/// Shared diagnostic log for troubleshooting. Each category writes to its own file
/// in the temp directory (e.g., lazydotide-lsp.log, lazydotide-git.log).
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Lock = new();

    private static string GetLogPath(string category) =>
        Path.Combine(Path.GetTempPath(), $"lazydotide-{category}.log");

    public static void Write(string category, string message)
    {
        try { lock (Lock) File.AppendAllText(GetLogPath(category), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); }
        catch { } // Cannot log if log file write itself fails
    }

    public static void Error(string category, string context, Exception ex) =>
        Write(category, $"{context}: {ex.Message}");
}
