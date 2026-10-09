; The WinMux setup program. Built by scripts/build-installer.ps1 from the folder scripts/publish.ps1
; produces; see ADR 0030 for why it is shaped the way it is.
;
; Per-user, never per-machine. WinMux updates itself by replacing its own files (ADR 0023), and it
; can only do that in a folder the user can write to. An installation under Program Files would
; install once and then fail every update, so this installs to %LOCALAPPDATA%\Programs\WinMux, asks
; for no administrator rights, and does not offer to install for all users.

#ifndef AppVersion
  #error Pass /DAppVersion=<the product version>, e.g. 0.7.8-test.8
#endif
#ifndef NumericVersion
  #error Pass /DNumericVersion=<four numbers>, e.g. 0.7.8.0
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<the folder publish.ps1 produced>
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Never change this. Windows recognises an installed WinMux by it, upgrades find their way to the
; same folder by it, and the in-app updater (UpdateInstaller.InstallerAppId) writes the new version
; under it. The doubled brace is how Inno Setup spells a literal one.
AppId={{8D6AF144-4CE1-44AA-83DD-DBA38FEF9DA8}
AppName=WinMux
AppVersion={#AppVersion}
AppVerName=WinMux {#AppVersion}
AppPublisher=WinMux contributors
AppPublisherURL=https://github.com/rennerdo30/winmux
AppSupportURL=https://github.com/rennerdo30/winmux/issues
AppUpdatesURL=https://github.com/rennerdo30/winmux/releases
AppCopyright=Copyright (c) 2026 rennerdo30 and WinMux contributors
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=WinMux setup

PrivilegesRequired=lowest
DefaultDirName={autopf}\WinMux
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes

SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

LicenseFile=..\LICENSE
SetupIconFile=..\assets\winmux.ico
UninstallDisplayIcon={app}\WinMux.exe
UninstallDisplayName=WinMux
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=WinMux-{#AppVersion}-win-x64-setup

; WinMux and its pane hosts hold their files open. Restart Manager asks them to close, and WinMux
; saves the session continuously, so closing loses nothing a restart would not.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinMux"; Filename: "{app}\WinMux.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\WinMux"; Filename: "{app}\WinMux.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WinMux.exe"; Description: "{cm:LaunchProgram,WinMux}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The in-app updater adds files the installer never listed — a new library in a later release —
; and leaves *.winmux-old copies until the next start. Removing the folder is the only way an
; uninstall leaves nothing behind. Sessions and settings live in %APPDATA%\WinMux and are kept.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\WinMux\update-staged"

[Code]
const
  AppPathsKey = 'Software\Microsoft\Windows\CurrentVersion\App Paths\WinMux.exe';
  RuntimeDownload = 'https://dotnet.microsoft.com/download/dotnet/10.0';

{ The .NET 10 desktop runtime, wherever the machine-wide dotnet lives. WinMux is published
  framework-dependent, and so are its updates, so bundling a runtime here would help only until the
  first update replaced the files that used it. }
function DesktopRuntimeInstalled: Boolean;
var
  Root: String;
  Found: TFindRec;
begin
  if not RegQueryStringValue(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
    Root := ExpandConstant('{commonpf64}\dotnet');
  Result := FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', Found);
  if Result then
    FindClose(Found);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if DesktopRuntimeInstalled or WizardSilent then
    Exit;

  if SuppressibleMsgBox(
      'WinMux needs the .NET 10 Desktop Runtime, which is not installed on this computer.' + #13#10 + #13#10 +
      'Open the download page now? Setup continues either way; WinMux starts once the runtime is installed.',
      mbConfirmation, MB_YESNO, IDNO) = IDYES then
    ShellExec('open', RuntimeDownload, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Quirks: String;
begin
  { The quirks database is meant to be edited by hand. Keep the user's copy beside the new one,
    exactly as the in-app updater does. }
  if CurStep = ssInstall then
  begin
    Quirks := ExpandConstant('{app}\foreign-app-quirks.json');
    if FileExists(Quirks) then
      FileCopy(Quirks, Quirks + '.before-update', False);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Registered: String;
begin
  { WinMux registers itself under App Paths on first launch so that "winmux" works in the Run
    dialog. Remove that — but only if it points here; another copy elsewhere keeps its own. }
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, AppPathsKey, '', Registered) and
       (CompareText(Registered, ExpandConstant('{app}\WinMux.exe')) = 0) then
      RegDeleteKeyIncludingSubkeys(HKCU, AppPathsKey);
end;
