[Setup]
AppId={{8908BB9E-E539-45D7-AFA4-F041C955B928}
AppName=Frame Trace
AppVersion=0.7.10
AppPublisher=Frame Trace contributors
DefaultDirName={localappdata}\Programs\FrameTrace
DefaultGroupName=Frame Trace
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..
OutputBaseFilename=FrameTrace-0.7.10-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=src/Assets/FrameTrace.ico
WizardImageFile=src/Assets/InstallerWelcome.bmp
WizardSmallImageFile=src/Assets/InstallerHeader.bmp
WizardImageStretch=yes
UninstallDisplayIcon={app}\FrameTrace.exe
CloseApplications=yes
RestartApplications=no
LicenseFile=LICENSE

[InstallDelete]
Type: files; Name: "{app}\Frameglass.exe"
Type: files; Name: "{app}\Frameglass.dll"
Type: files; Name: "{app}\Frameglass.deps.json"
Type: files; Name: "{app}\Frameglass.runtimeconfig.json"
Type: files; Name: "{autoprograms}\Frameglass\Frameglass.lnk"
Type: files; Name: "{autodesktop}\Frameglass.lnk"

[Files]
Source: "prepare-update.ps1"; Flags: dontcopy
Source: "artifacts\app-0.7.10\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\Frame Trace"; Filename: "{app}\FrameTrace.exe"
Name: "{autodesktop}\Frame Trace"; Filename: "{app}\FrameTrace.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FrameTrace.exe"; Description: "Open Frame Trace"; Flags: shellexec nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PowerShell, Arguments: String;
  ExitCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('prepare-update.ps1');
  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\prepare-update.ps1') + '" -InstallDirectory "' + ExpandConstant('{app}') + '"';
  if not Exec(PowerShell, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    Result := 'Could not check running Frame Trace processes. Close Frame Trace and retry. Windows error: ' + IntToStr(ExitCode);
    Exit;
  end;
  if ExitCode = 5 then begin
    Log('Update preparation requires elevation to release existing installation files.');
    if not ShellExec('runas', PowerShell, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      Result := 'Windows permission is needed to close an administrator capture process. Allow the permission prompt, or close Frame Trace and restart Windows before updating.';
      Exit;
    end;
  end;
  if ExitCode <> 0 then
    Result := 'Frame Trace files are still in use or not writable. Close Frame Trace, restart Windows if a capture process remains, and retry. No update files have been replaced. Preparation exit code: ' + IntToStr(ExitCode);
end;

