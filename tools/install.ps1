<#
.SYNOPSIS
  Installs Forge for Windows for the current user. No admin rights needed.

.DESCRIPTION
  Copies a self-contained build to %LOCALAPPDATA%\Programs\ForgeForWindows, adds a
  Start menu shortcut, and starts the app. Run it again to update; your accounts,
  settings and tokens are kept. tools\uninstall.ps1 undoes it.

.PARAMETER Build
  Publish a fresh Release build first (needs the .NET 10 SDK).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Build
#>
param(
    [switch]$Build,
    [string]$Source = (Join-Path $PSScriptRoot '..\publish\win-x64')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ForgeForWindows'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Forge for Windows.lnk'

if ($Build) {
    Write-Host 'Publishing a Release build...'
    dotnet publish (Join-Path $root 'Forge.App') -c Release -r win-x64 -p:Platform=x64 -o $Source -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
if (-not (Test-Path (Join-Path $Source 'ForgeForWindows.exe'))) {
    throw "No build at $Source. Run again with -Build, or publish first (see README)."
}

# A running copy holds its files open.
Get-Process ForgeForWindows -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "Copying to $dest"
New-Item -ItemType Directory -Force $dest | Out-Null
robocopy $Source $dest /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

$exe = Join-Path $dest 'ForgeForWindows.exe'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.IconLocation = "$exe,0"
$link.Description = 'Laravel Forge in the tray'
$link.Save()
Write-Host "Start menu shortcut: Forge for Windows"

Start-Process $exe
Write-Host 'Started. Look for the icon in the tray (it may be under the ^ overflow arrow).'
