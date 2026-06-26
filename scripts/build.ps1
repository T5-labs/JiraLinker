<#
.SYNOPSIS
  Builds JiraLinker into a single self-contained .exe (no .NET runtime needed to run it).
.DESCRIPTION
  Output: dist\JiraLinker.exe  — this is the only file you need to share.
  Requires the .NET SDK (10.0+) to BUILD; the resulting exe runs on any Windows 10/11 machine.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Stop a running instance so the exe isn't locked.
Get-Process JiraLinker -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

dotnet publish "$root\JiraLinker.csproj" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o "$root\dist"

Write-Host ""
Write-Host "Built: $root\dist\JiraLinker.exe" -ForegroundColor Green
