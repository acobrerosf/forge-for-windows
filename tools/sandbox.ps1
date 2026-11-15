<#
.SYNOPSIS
  Tries Forge for Windows in Windows Sandbox: a clean, throwaway Windows that is
  wiped when you close it. Nothing touches your own install, settings or tokens.

.DESCRIPTION
  By default it installs the newest dist\*-x64-setup.exe (build one with -Build) and
  starts the app, so you see exactly what a new user sees. The sandbox has no Forge
  token: add one in Settings. Use a token WITHOUT write scopes (no
  site:manage-deploys, server:manage-services, site:manage-commands) so nothing you
  click in the sandbox can deploy or restart anything for real.

  Needs Windows Sandbox, which Windows 11 Pro and Enterprise include but leave off.
  Turn it on in "Turn Windows features on or off" > Windows Sandbox (admin), then restart.

.PARAMETER Setup
  The installer to try. Default: the newest dist\*-x64-setup.exe.

.PARAMETER Build
  Publish a Release build and compile the installer first (needs the .NET SDK and Inno Setup 6).

.PARAMETER Portable
  Run publish\win-x64 directly instead of installing (run tools\install.ps1 -Build or a publish first).

.PARAMETER FromGitHub
  Download and install the latest published release from GitHub inside the sandbox.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\sandbox.ps1 -Build
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\sandbox.ps1 -FromGitHub
#>
param(
    [string]$Setup,
    [switch]$Build,
    [switch]$Portable,
    [switch]$FromGitHub
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

$sandbox = Join-Path $env:windir 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandbox)) {
    throw ("Windows Sandbox isn't turned on. Open 'Turn Windows features on or off', tick 'Windows Sandbox', " +
           "and restart. (Or, as admin: Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM)")
}
if (Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer -ErrorAction SilentlyContinue) {
    throw 'A Windows Sandbox is already open. Only one can run at a time; close it first.'
}

# Everything the sandbox sees goes in one throwaway folder, shared read-only.
$work = Join-Path $env:TEMP 'forge-sandbox'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

$installed = '$env:LOCALAPPDATA\Programs\ForgeForWindows\ForgeForWindows.exe'
if ($FromGitHub) {
    $script = @"
`$release = Invoke-RestMethod 'https://api.github.com/repos/acobrerosf/forge-for-windows/releases/latest'
`$asset = `$release.assets | Where-Object name -like '*x64-setup.exe' | Select-Object -First 1
if (-not `$asset) { Write-Host 'The latest release has no x64 setup.'; Read-Host 'Press Enter'; exit 1 }
Write-Host "Downloading `$(`$asset.name)..."
Invoke-WebRequest `$asset.browser_download_url -OutFile "`$env:TEMP\setup.exe" -UseBasicParsing
Start-Process "`$env:TEMP\setup.exe" -ArgumentList '/SILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
Start-Process "$installed"
"@
}
elseif ($Portable) {
    $app = Join-Path $root 'publish\win-x64'
    if (-not (Test-Path (Join-Path $app 'ForgeForWindows.exe'))) {
        throw "No build at $app. Run tools\install.ps1 -Build, or publish first (see README)."
    }
    Copy-Item $app (Join-Path $work 'app') -Recurse
    $script = "Start-Process 'C:\forge\app\ForgeForWindows.exe'"
}
else {
    if ($Build) {
        Write-Host 'Publishing a Release build...'
        dotnet publish (Join-Path $root 'Forge.App') -c Release -r win-x64 -p:Platform=x64 -o (Join-Path $root 'publish\win-x64') -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
        $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $iscc) { throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup).' }
        Write-Host 'Compiling the installer...'
        & $iscc /Q '/DAppVersion=0.0.0-sandbox' '/DArch=x64' "/DSourceDir=$root\publish\win-x64" (Join-Path $root 'tools\installer.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    }
    if (-not $Setup) {
        $Setup = Get-ChildItem (Join-Path $root 'dist') -Filter '*-x64-setup.exe' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $Setup -or -not (Test-Path $Setup)) {
        throw 'No installer found in dist\. Run again with -Build, or pass -Setup <path>.'
    }
    Write-Host "Installer: $Setup"
    Copy-Item $Setup (Join-Path $work 'setup.exe')
    # /SILENT shows the progress window, so you can watch it install.
    $script = @"
Start-Process 'C:\forge\setup.exe' -ArgumentList '/SILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
Start-Process "$installed"
"@
}
Set-Content -Path (Join-Path $work 'start.ps1') -Value $script -Encoding ASCII

$config = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$work</HostFolder>
      <SandboxFolder>C:\forge</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\forge\start.ps1</Command>
  </LogonCommand>
  <MemoryInMB>4096</MemoryInMB>
</Configuration>
"@
$wsb = Join-Path $work 'forge.wsb'
Set-Content -Path $wsb -Value $config -Encoding ASCII

Write-Host 'Starting Windows Sandbox. Forge appears in its tray once it has installed.'
Write-Host 'Add a read-only Forge token in Settings; closing the sandbox throws everything away.'
Start-Process $sandbox -ArgumentList "`"$wsb`""
