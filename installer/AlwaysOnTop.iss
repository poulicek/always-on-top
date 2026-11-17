; One-click installer (Inno Setup 6). Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=1.2.3 /DSourceDir=<folder with "Always on Top.exe"> installer\AlwaysOnTop.iss
; Per-user install (no administrator rights) into %LOCALAPPDATA%\Always on Top - the folder the app keeps its
; settings in and the updater replaces the exe in. No wizard pages: double-click installs, starts the app and closes.
; If the .NET 10 Desktop Runtime is missing it is downloaded from Microsoft and installed (one UAC prompt).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\publish"
#endif
#define AppName "Always on Top"
#define AppExe "Always on Top.exe"
#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
; AppId must never change: it identifies the installation for upgrades and uninstall (Updater.InstallerAppId).
AppId={{5A363F0F-92AB-4EC4-BB93-C3F06D362A5C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Always on Top
AppPublisherURL=https://github.com/poulicek/always-on-top
AppSupportURL=https://github.com/poulicek/always-on-top/issues
AppUpdatesURL=https://github.com/poulicek/always-on-top/releases
DefaultDirName={localappdata}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableWelcomePage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no
OutputDir=..\out
OutputBaseFilename=AlwaysOnTop-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Adds ""Always on Top"" to the title-bar menu of every window"

[Run]
Filename: "{app}\{#AppExe}"; Flags: nowait

[UninstallDelete]
; settings.json, log files and leftovers of self-updates (.old / .new)
Type: filesandordirs; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

// Ask a running copy to exit (it un-pins its windows and removes its menu items), then make sure it is gone.
procedure StopRunningApp();
var
  Code: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im "{#AppExe}"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function IsDesktopRuntimeInstalled(): Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found);
  if Result then FindClose(Found);
end;

function InstallDesktopRuntime(): String;
var
  Code: Integer;
  Installer: String;
begin
  Result := '';
  WizardForm.PreparingLabel.Caption := 'Downloading the .NET 10 Desktop Runtime from Microsoft...';
  WizardForm.PreparingLabel.Visible := True;
  try
    DownloadTemporaryFile('{#RuntimeUrl}', 'windowsdesktop-runtime-win-x64.exe', '', nil);
  except
    Result := 'The .NET 10 Desktop Runtime could not be downloaded: ' + GetExceptionMessage + #13#10 +
              'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.';
    Exit;
  end;
  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  WizardForm.PreparingLabel.Caption := 'Installing the .NET 10 Desktop Runtime...';
  if not ShellExec('runas', Installer, '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) or
     not IsDesktopRuntimeInstalled() then
    Result := 'The .NET 10 Desktop Runtime was not installed. Install it from ' +
              'https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsDesktopRuntimeInstalled() then
  begin
    Result := InstallDesktopRuntime();
    if Result <> '' then Exit;
  end;
  StopRunningApp();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp();
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{#AppName}');
  end;
end;
