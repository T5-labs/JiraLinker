<#
.SYNOPSIS
  Removes JiraLinker: stops it, deletes the auto-start shortcut and install folder.
#>
$ErrorActionPreference = 'SilentlyContinue'

Get-Process JiraLinker | Stop-Process -Force
Start-Sleep -Milliseconds 300

$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'JiraLinker.lnk'
Remove-Item $lnk -Force

$installDir = Join-Path $env:LOCALAPPDATA 'JiraLinker'
Remove-Item $installDir -Recurse -Force

Write-Host "JiraLinker uninstalled." -ForegroundColor Green
