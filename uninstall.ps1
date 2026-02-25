# LazyDotIDE Uninstallation Script for Windows
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\LazyDotIDE'
$binaryPath = Join-Path $installDir 'lazydotide.exe'

Write-Host 'LazyDotIDE Uninstaller'
Write-Host '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━' -ForegroundColor Cyan
Write-Host ''

# Check if LazyDotIDE is installed
if (-not (Test-Path $binaryPath)) {
    Write-Host "LazyDotIDE is not installed (binary not found at $binaryPath)"
    exit 0
}

Write-Host 'This will remove:'
Write-Host "  - Binary: $binaryPath"
Write-Host "  - Directory: $installDir"
Write-Host ''

$confirm = Read-Host 'Continue with uninstallation? [y/N]'
if ($confirm -notmatch '^[Yy]$') {
    Write-Host 'Uninstallation cancelled.'
    exit 0
}

Write-Host ''

# Remove binary and directory
try {
    Remove-Item -Path $installDir -Recurse -Force
    Write-Host "Removed: $installDir" -ForegroundColor Green
} catch {
    Write-Host "Error removing $installDir : $($_.Exception.Message)" -ForegroundColor Red
}

# Remove from user PATH
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($userPath -like "*$installDir*") {
    $paths = $userPath -split ';' | Where-Object { $_ -ne $installDir -and $_ -ne '' }
    $newPath = $paths -join ';'
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    Write-Host "Removed $installDir from user PATH" -ForegroundColor Green
}

Write-Host ''
Write-Host '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━' -ForegroundColor Cyan
Write-Host 'LazyDotIDE uninstalled successfully' -ForegroundColor Green
Write-Host '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━' -ForegroundColor Cyan
