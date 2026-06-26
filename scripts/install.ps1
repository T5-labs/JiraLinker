<#
.SYNOPSIS
  Installs JiraLinker for the current user: copies the exe to a stable location,
  starts it, and sets it to auto-start on login.
.DESCRIPTION
  Run this AFTER you have a built JiraLinker.exe (either from scripts\build.ps1 or
  downloaded from a colleague / GitHub Release). No admin rights required.

  The script looks for JiraLinker.exe in, in order:
    1. the path you pass via -ExePath
    2. the same folder as this script
    3. ..\dist\JiraLinker.exe (a local build)
#>
param(
    [string]$ExePath
)
$ErrorActionPreference = 'Stop'

# Locate the exe.
if (-not $ExePath) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'JiraLinker.exe'),
        (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\JiraLinker.exe')
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "Could not find JiraLinker.exe. Build it with scripts\build.ps1 or pass -ExePath <path>."
}

# Install to %LOCALAPPDATA%\JiraLinker.
$installDir = Join-Path $env:LOCALAPPDATA 'JiraLinker'
New-Item -ItemType Directory -Path $installDir -Force | Out-Null

# Stop any running copy before overwriting.
Get-Process JiraLinker -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$installedExe = Join-Path $installDir 'JiraLinker.exe'
Copy-Item $ExePath $installedExe -Force

# Auto-start on login via a Startup-folder shortcut.
$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup 'JiraLinker.lnk'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $installedExe
$sc.WorkingDirectory = $installDir
$sc.Description = 'Jira Linker - auto-hyperlink Jira ticket keys'
$sc.Save()

# Launch now.
Start-Process $installedExe

Write-Host ""
Write-Host "JiraLinker installed and running." -ForegroundColor Green
Write-Host "  Location:   $installedExe"
Write-Host "  Auto-start: $lnk"
Write-Host "  Tray icon:  look for the (i) icon by the clock - right-click to pause or exit."
