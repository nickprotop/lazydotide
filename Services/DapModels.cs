namespace DotNetIDE;

// DAP detection result
public record DapServer(string Exe, string[] Args);

// DAP event/state types
public record DapStoppedEventArgs(string Reason, int ThreadId, string? Description);

// Launch configuration
public record LaunchProfile(string Name, string Program, string? Cwd, string[]? Args, Dictionary<string, string>? Env);

// DAP breakpoint types
public record DapBreakpoint(int Line, bool Verified, string? Message);
public record SourceBreakpoint(int Line);
public record DapSource(string? Name, string? Path);

// DAP thread/frame/scope/variable types
public record DapThread(int Id, string Name);
public record DapStackFrame(int Id, string Name, DapSource? Source, int Line, int Column);
public record DapScope(string Name, int VariablesReference, bool Expensive);
public record DapVariable(string Name, string Value, string? Type, int VariablesReference);

// Debug session state
public enum DebugSessionState
{
    Idle,
    Running,
    Paused,
    Terminated
}

// Workspace persistence types
public class WorkspaceBreakpoint
{
    public string Path { get; set; } = "";
    public int Line { get; set; } // 1-based
}

public class LaunchProfileEntry
{
    public string Name { get; set; } = "Default";
    public string Project { get; set; } = "";  // relative .csproj path
    public string[]? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}
