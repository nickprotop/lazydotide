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
                DeleteDirectoryRobust(InstallDir);
            }

            Directory.CreateDirectory(InstallDir);

            // 5. Extract to a temp directory first, then move contents to InstallDir.
            //    The archive nests everything under a "netcoredbg/" folder whose name
            //    collides with the "netcoredbg" binary inside it, so extracting directly
            //    into InstallDir and then flattening fails on the File.Move.
            var extractDir = Path.Combine(Path.GetTempPath(), "netcoredbg_extract_" + Path.GetRandomFileName());
            Directory.CreateDirectory(extractDir);
            try
            {
                progress?.Report("Extracting...");

                if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    await using var fileStream = File.OpenRead(tempFile);
                    await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gzipStream, extractDir, overwriteFiles: true, ct);
                }
                else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(tempFile, extractDir, overwriteFiles: true);
                }

                // The archive may nest everything under a "netcoredbg/" subdirectory
                var sourceDir = Path.Combine(extractDir, "netcoredbg");
                if (!Directory.Exists(sourceDir))
                    sourceDir = extractDir;

                progress?.Report($"Installing to {InstallDir}...");

                foreach (var file in Directory.GetFiles(sourceDir))
                    File.Move(file, Path.Combine(InstallDir, Path.GetFileName(file)), overwrite: true);

                foreach (var dir in Directory.GetDirectories(sourceDir))
                    Directory.Move(dir, Path.Combine(InstallDir, Path.GetFileName(dir)));
            }
            finally
            {
                try { Directory.Delete(extractDir, recursive: true); } catch { /* ignore */ }
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

    /// <summary>
    /// Renames the directory out of the way, then deletes it. This avoids the
    /// "Directory not empty" race on Windows/WSL where Directory.Delete returns
    /// before the filesystem has fully released the handles.
    /// </summary>
    private static void DeleteDirectoryRobust(string path)
    {
        var trash = path + "_trash_" + Path.GetRandomFileName();
        Directory.Move(path, trash);
        try { Directory.Delete(trash, recursive: true); } catch { /* best-effort cleanup */ }
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
