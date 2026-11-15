; Inno Setup script for the Forge for Windows installer.
;
; It installs the same way tools\install.ps1 does: for the current user only, with no
; admin rights, into %LOCALAPPDATA%\Programs\ForgeForWindows, with a Start menu shortcut.
; Either one updates an install made by the other. Uninstalling (Settings > Apps) removes
; the app, its shortcut and its start-up entry, and keeps settings and stored tokens,
; like tools\uninstall.ps1 without -Purge.
;
; Built by .github/workflows/release.yml; by hand, after a publish:
;   iscc /DAppVersion=1.2.3 /DArch=x64 /DSourceDir=..\publish\win-x64 tools\installer.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif

#define AppName "Forge for Windows"
#define AppExe "ForgeForWindows.exe"

[Setup]
; Never change the AppId: Windows matches upgrades and the uninstaller by it.
AppId={{36546F5F-1EAE-48B9-9B7B-0676EF749A0C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=acobrerosf
AppPublisherURL=https://github.com/acobrerosf/forge-for-windows
AppSupportURL=https://github.com/acobrerosf/forge-for-windows/issues
AppUpdatesURL=https://github.com/acobrerosf/forge-for-windows/releases
DefaultDirName={localappdata}\Programs\ForgeForWindows
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=ForgeForWindows-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\Forge.App\Assets\forge.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The tray app holds its files open; close it rather than ask the user to.
CloseApplications=force
RestartApplications=no

[InstallDelete]
; Like the script's mirror copy: files a newer version no longer ships don't linger.
; Only the program folder; settings and tokens live elsewhere.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The same shortcut tools\install.ps1 makes, so the two never leave two entries.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Laravel Forge in the tray"

[Registry]
; The app writes this value itself when "Start with Windows" is on; only remove it on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ForgeForWindows"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopForge"

[Code]
// A running copy (the one tools\install.ps1 started, say) is stopped before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(800);
  Result := '';
end;
