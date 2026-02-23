namespace DotNetIDE;

public static class ConfigService
{
    private const string DefaultConfigJson = """
{
  "tools": [
    {
      "name": "htop",
      "command": "htop",
      "args": null,
      "workingDir": null
    },
    {
      "name": "Lazygit",
      "command": "lazygit",
      "args": null,
      "workingDir": null
    },
    {
      "name": "Git Log",
      "command": "bash",
      "args": ["-c", "git log --oneline --graph --all --color=always; exec bash"],
      "workingDir": null
    }
  ]
}
""";

    public static string GetConfigPath()
    {
        string dir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lazydotide")
            : Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                "lazydotide");
        return Path.Combine(dir, "config.json");
    }

    public static void EnsureDefaultConfig()
    {
        var path = GetConfigPath();
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultConfigJson);
        }
        catch { }
    }

    public static IdeConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return new IdeConfig();
        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<IdeConfig>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new IdeConfig();
        }
        catch { return new IdeConfig(); }
    }
}
