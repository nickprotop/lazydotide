using System.Runtime.InteropServices;
using SharpConsoleUI.Helpers;

if (SharpConsoleUI.PtyShim.RunIfShim(args)) Environment.Exit(127);

// Validate terminal before entering raw mode (which suppresses Ctrl+C and redirects stdout)
if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    try
    {
        // If we can't read terminal dimensions, the terminal likely can't render our UI
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;
        if (width <= 0 || height <= 0)
        {
            Console.Error.WriteLine("Error: Terminal reports zero dimensions. LazyDotIDE requires an interactive terminal.");
            Console.Error.WriteLine("Try running in a standalone terminal (not an embedded terminal panel).");
            return 1;
        }
    }
    catch
    {
        Console.Error.WriteLine("Error: Cannot query terminal dimensions. LazyDotIDE requires an interactive terminal.");
        Console.Error.WriteLine("Try running in a standalone terminal (not an embedded terminal panel).");
        return 1;
    }
}
else
{
    Console.Error.WriteLine("Error: LazyDotIDE requires an interactive terminal (stdin/stdout must not be redirected).");
    return 1;
}

var projectPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();

try
{
    using var app = new DotNetIDE.IdeApp(projectPath);
    app.Run();
}
catch (Exception ex)
{
    Console.Clear();
    ExceptionFormatter.WriteException(ex);
    return 1;
}

return 0;
