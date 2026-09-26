[Setup]
AppId={{443FF778-DC03-4A24-967A-A4F4D42D3175}
AppName=Frame Trace Preview
AppVersion=0.7.13
AppPublisher=Frame Trace contributors
DefaultDirName={localappdata}\Programs\FrameTracePreview
DefaultGroupName=Frame Trace Preview
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=artifacts\preview
OutputBaseFilename=FrameTracePreview-0.7.13-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=src/Assets/FrameTrace.ico
WizardImageFile=src/Assets/InstallerWelcome.bmp
WizardSmallImageFile=src/Assets/InstallerHeader.bmp
WizardImageStretch=yes
UninstallDisplayIcon={app}\FrameTracePreview.exe
CloseApplications=yes
RestartApplications=no
LicenseFile=LICENSE

[Files]
Source: "artifacts\preview-0.7.13\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\Frame Trace Preview"; Filename: "{app}\FrameTracePreview.exe"
Name: "{autodesktop}\Frame Trace Preview"; Filename: "{app}\FrameTracePreview.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FrameTracePreview.exe"; Description: "Open Frame Trace Preview"; Flags: shellexec nowait postinstall skipifsilent
