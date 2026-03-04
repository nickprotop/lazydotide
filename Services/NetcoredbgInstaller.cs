using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetIDE;

public static class NetcoredbgInstaller
{
    private const string ReleasesApi = "https://api.github.com/repos/Samsung/netcoredbg/releases/latest";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netcoredbg");

    public static string? GetAssetName()
    {
        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64  => "netcoredbg-linux-amd64.tar.gz",
                Architecture.Arm64 => "netcoredbg-linux-arm64.tar.gz",
                _ => null
            };
        }
        if (OperatingSystem.IsMacOS())
        {
            return "netcoredbg-osx-amd64.tar.gz";
        }
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSArchitecture == Architecture.X64
                ? "netcoredbg-win64.zip"
                : null;
        }
        return null;
    }

    public static async Task InstallAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var assetName = GetAssetName();
        if (assetName == null)
            throw new PlatformNotSupportedException(
                $"No netcoredbg build available for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}");

        progress?.Report("Fetching latest release info...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LazyDotIDE/1.0");

        // 1. Fetch latest release
        var json = await http.GetStringAsync(ReleasesApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";
        progress?.Report($"Found release {tagName}");

        // 2. Find matching asset URL
        string? downloadUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (downloadUrl == null)
            throw new InvalidOperationException($"Asset '{assetName}' not found in release {tagName}");

        progress?.Report($"Downloading {assetName}...");

        // 3. Download to temp file
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempFile);
                await response.Content.CopyToAsync(fs, ct);
            }

            progress?.Report($"Download complete ({new FileInfo(tempFile).Length / 1024 / 1024} MB)");

            // 4. Remove existing install
            if (Directory.Exists(InstallDir))
            {
                progress?.Report("Removing previous installation...");
                Directory.Delete(InstallDir, recursive: true);
            }

            Directory.CreateDirectory(InstallDir);

            // 5. Extract
            progress?.Report($"Extracting to {InstallDir}...");

            if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var fileStream = File.OpenRead(tempFile);
                await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzipStream, InstallDir, overwriteFiles: true, ct);
            }
            else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(tempFile, InstallDir, overwriteFiles: true);
            }

            // The archive may contain a nested directory — flatten if needed
            var nestedDir = Path.Combine(InstallDir, "netcoredbg");
            if (Directory.Exists(nestedDir))
            {
                foreach (var file in Directory.GetFiles(nestedDir))
                {
                    var dest = Path.Combine(InstallDir, Path.GetFileName(file));
                    File.Move(file, dest, overwrite: true);
                }
                foreach (var dir in Directory.GetDirectories(nestedDir))
                {
                    var dest = Path.Combine(InstallDir, Path.GetFileName(dir));
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(dir, dest);
                }
                Directory.Delete(nestedDir, recursive: true);
            }

            // 6. chmod +x on Unix
            if (!OperatingSystem.IsWindows())
            {
                progress?.Report("Setting executable permissions...");
                var exePath = Path.Combine(InstallDir, "netcoredbg");
                if (File.Exists(exePath))
                    File.SetUnixFileMode(exePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // 7. Verify
            progress?.Report("Verifying installation...");
            var verifyExe = Path.Combine(InstallDir, OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");
            if (!File.Exists(verifyExe))
                throw new FileNotFoundException($"netcoredbg executable not found at {verifyExe}");

            var versionOutput = await RunVersionCheckAsync(verifyExe, ct);
            progress?.Report($"\u2713 netcoredbg {versionOutput} installed successfully");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    private static async Task<string> RunVersionCheckAsync(string exe, CancellationToken ct)
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
            if (p == null) return "unknown";
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return output.Trim();
        }
        catch
        {
            return "unknown";
        }
    }
}
