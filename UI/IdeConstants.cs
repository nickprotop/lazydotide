using System.Runtime.Versioning;

namespace DotNetIDE;

internal static class IdeConstants
{
    public const string GitStatusDefault = "[dim] git: --[/]";
    public const string ReadOnlyTabPrefix = "__readonly:";

    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    public static bool IsDesktopOs => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();
}
