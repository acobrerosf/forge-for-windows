<#
.SYNOPSIS
  Removes Forge for Windows installed by tools\install.ps1.

.PARAMETER Purge
  Also delete settings (%LOCALAPPDATA%\ForgeForWindows) and the stored API tokens
  (Credential Manager entries ForgeForWindows:*). Tokens cannot be recovered afterwards.
#>
param([switch]$Purge)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ForgeForWindows'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Forge for Windows.lnk'

Get-Process ForgeForWindows -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ForgeForWindows' -ErrorAction SilentlyContinue
if (Test-Path $shortcut) { Remove-Item $shortcut }
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Write-Host 'Removed the app, its shortcut and its startup entry.'

if ($Purge) {
    $state = Join-Path $env:LOCALAPPDATA 'ForgeForWindows'
    if (Test-Path $state) { Remove-Item $state -Recurse -Force }
    cmdkey /list | Select-String 'ForgeForWindows:' | ForEach-Object {
        $target = ($_ -split 'ForgeForWindows:')[1].Trim()
        cmdkey /delete:"ForgeForWindows:$target" | Out-Null
        Write-Host "Deleted token ForgeForWindows:$target"
    }
    Write-Host 'Removed settings and tokens.'
} else {
    Write-Host 'Kept your settings and tokens; run with -Purge to remove them too.'
}
