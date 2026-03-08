using SharpConsoleUI.Helpers;

if (SharpConsoleUI.PtyShim.RunIfShim(args)) Environment.Exit(127);

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
