
using SharpConsoleUI;
namespace DotNetIDE;

public static class ColorScheme
{
    // Backgrounds
    public static readonly Color WindowBackground     = Color.Grey11;
    public static readonly Color StatusBarBackground   = Color.Grey15;
    public static readonly Color SidebarBackground     = Color.Grey19;

    // Borders / separators
    public static readonly Color RuleColor   = Color.Grey23;
    public static readonly Color BorderColor = Color.Grey23;

    // Text
    public static readonly Color PrimaryText   = Color.Cyan1;
    public static readonly Color SecondaryText = Color.Grey70;
    public static readonly Color MutedText     = Color.Grey50;

    // Status
    public static readonly Color SuccessColor = Color.Green;
    public static readonly Color WarningColor = Color.Yellow;
    public static readonly Color ErrorColor   = Color.Red;
    public static readonly Color InfoColor    = Color.Cyan1;

    // Markup string constants (for use in Spectre markup tags)
    public const string PrimaryMarkup   = "cyan1 bold";
    public const string SecondaryMarkup = "grey70";
    public const string MutedMarkup     = "grey50";
    public const string SuccessMarkup   = "green";
    public const string WarningMarkup   = "yellow";
    public const string ErrorMarkup     = "red";
    public const string InfoMarkup      = "cyan1";
}
