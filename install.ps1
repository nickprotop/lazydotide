# LazyDotIDE Quick Install Script for Windows
# Downloads and installs the latest release from GitHub
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

$ErrorActionPreference = 'Stop'

$repo = 'nickprotop/lazydotide'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\LazyDotIDE'

Write-Host 'Installing LazyDotIDE from latest release...'
Write-Host ''

# 1. Detect architecture
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
    'X64'  { $rid = 'win-x64' }
    'Arm64' { $rid = 'win-arm64' }
    default {
        Write-Host "Error: Unsupported architecture: $arch" -ForegroundColor Red
        Write-Host 'Supported: X64, Arm64'
        exit 1
    }
}

$binaryName = "lazydotide-$rid.exe"
Write-Host "Detected architecture: $arch"
Write-Host "Binary to download: $binaryName"
Write-Host "Install directory: $installDir"
Write-Host ''

# 2. Get latest release info
Write-Host 'Fetching latest release...'
try {
    $releaseInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'LazyDotIDE-Installer' }
    $releaseTag = $releaseInfo.tag_name
} catch {
    Write-Host 'Error: Could not fetch latest release information' -ForegroundColor Red
    Write-Host "Please check your internet connection or install manually from:"
    Write-Host "https://github.com/$repo/releases"
    exit 1
}

if (-not $releaseTag) {
    Write-Host 'Error: Could not determine latest release tag' -ForegroundColor Red
    exit 1
}

Write-Host "Latest release: $releaseTag"
Write-Host ''

# 3. Construct download URL
$binaryUrl = "https://github.com/$repo/releases/download/$releaseTag/$binaryName"

# 4. Create install directory
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

# 5. Download binary
$destPath = Join-Path $installDir 'lazydotide.exe'
Write-Host 'Downloading LazyDotIDE binary...'
try {
    Invoke-WebRequest -Uri $binaryUrl -OutFile $destPath -UseBasicParsing
    Write-Host "Installed binary to $destPath" -ForegroundColor Green
} catch {
    Write-Host "Error: Failed to download binary from $binaryUrl" -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

# 6. Add to user PATH if needed
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($userPath -notlike "*$installDir*") {
    $newPath = "$installDir;$userPath"
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    $env:PATH = "$installDir;$env:PATH"
    Write-Host "Added $installDir to user PATH" -ForegroundColor Green
} else {
    Write-Host "$installDir is already in PATH" -ForegroundColor Green
}

Write-Host ''
Write-Host '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━' -ForegroundColor Cyan
Write-Host 'Installation complete!' -ForegroundColor Green
Write-Host '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Open a new terminal window, then run:'
Write-Host ''
Write-Host '  lazydotide' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Usage:'
Write-Host '  lazydotide [path]      - Open a .NET project (default: current directory)'
Write-Host '  lazydotide --help      - Show all options'
Write-Host ''
Write-Host 'To uninstall:'
Write-Host '  irm https://raw.githubusercontent.com/nickprotop/lazydotide/main/uninstall.ps1 | iex'
Write-Host ''
Write-Host "Documentation: https://github.com/$repo"
