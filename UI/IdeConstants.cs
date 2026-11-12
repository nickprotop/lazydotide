using System.Runtime.Versioning;

namespace DotNetIDE;

internal static class IdeConstants
{
    public const string GitStatusDefault = "[dim] git: --[/]";
    public const string ReadOnlyTabPrefix = "__readonly:";

    // Default layout track sizes, used when no workspace file supplies one.
    public const int DefaultExplorerWidth = 26;
    public const int DefaultSidePanelWidth = 30;
    public const int DefaultOutputHeight = 12;

    // Minimum usable sizes, applied as GridLength Min values so a splitter
    // drag is clamped by GridSplitterResize rather than checked by hand.
    public const int MinSideColumnWidth = 10;
    public const int MinEditorWidth = 20;
    public const int MinMainHeight = 8;
    public const int MinOutputHeight = 4;

    // Grid geometry: the row/column indices the layout is built on.
    public const int MenuRow = 0;
    public const int ToolbarRow = 1;
    public const int ColumnsRow = 2;
    public const int OutputRow = 3;
    public const int ExplorerCol = 0;
    public const int EditorCol = 1;
    public const int SidePanelCol = 2;
    public const int GridColumnCount = 3;

    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    public static bool IsDesktopOs => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();
}
