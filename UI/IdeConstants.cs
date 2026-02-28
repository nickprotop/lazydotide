namespace DotNetIDE;

internal static class IdeConstants
{
    public const string GitStatusDefault = "[dim] git: --[/]";
    public const string ReadOnlyTabPrefix = "__readonly:";
    public static bool IsDesktopOs => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();
}
