namespace DotNetIDE;

public static class DapDetector
{
    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netcoredbg", "netcoredbg"),
        "/usr/local/bin/netcoredbg",
        "/usr/bin/netcoredbg",
    ];

    public static DapServer? Find()
    {
        // 1. Env override
        var env = Environment.GetEnvironmentVariable("DOTNET_IDE_DAP");
        if (env != null && File.Exists(env))
            return new DapServer(env, ["--interpreter=vscode"]);

        // 2. PATH
        if (IsInPath("netcoredbg"))
            return new DapServer("netcoredbg", ["--interpreter=vscode"]);

        // 3. Known locations
        foreach (var path in SearchPaths)
            if (File.Exists(path))
                return new DapServer(path, ["--interpreter=vscode"]);

        return null;
    }

    private static bool IsInPath(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(2000);
            return p != null && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
