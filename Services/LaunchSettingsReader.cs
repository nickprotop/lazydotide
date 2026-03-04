using System.Text.Json;

namespace DotNetIDE;

public static class LaunchSettingsReader
{
    public static List<LaunchProfileEntry> Read(string projectDir)
    {
        var result = new List<LaunchProfileEntry>();
        var path = Path.Combine(projectDir, "Properties", "launchSettings.json");
        if (!File.Exists(path)) return result;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
                return result;

            foreach (var profile in profiles.EnumerateObject())
            {
                var obj = profile.Value;

                // Only handle "Project" command type
                var commandName = obj.TryGetProperty("commandName", out var cn) ? cn.GetString() : null;
                if (commandName != "Project") continue;

                var entry = new LaunchProfileEntry { Name = profile.Name };

                if (obj.TryGetProperty("commandLineArgs", out var argsEl))
                {
                    var argsStr = argsEl.GetString();
                    if (!string.IsNullOrWhiteSpace(argsStr))
                        entry.Args = SplitArgs(argsStr);
                }

                if (obj.TryGetProperty("workingDirectory", out var wd))
                    entry.WorkingDirectory = wd.GetString();

                if (obj.TryGetProperty("environmentVariables", out var envVars) &&
                    envVars.ValueKind == JsonValueKind.Object)
                {
                    entry.Env = new Dictionary<string, string>();
                    foreach (var envVar in envVars.EnumerateObject())
                    {
                        var val = envVar.Value.GetString();
                        if (val != null)
                            entry.Env[envVar.Name] = val;
                    }
                }

                result.Add(entry);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("launch", $"Failed to read launchSettings.json: {ex.Message}");
        }

        return result;
    }

    private static string[] SplitArgs(string commandLineArgs)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in commandLineArgs)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args.ToArray();
    }
}
