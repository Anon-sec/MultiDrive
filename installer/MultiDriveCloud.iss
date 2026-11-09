#ifndef SourceDir
  #define SourceDir "..\dist\MultiDriveCloud"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#define AppName "MultiDrive Cloud"
#define AppVersion "1.0.0"

[Setup]
AppId={{6B0F2B1C-3C57-4D3E-9B52-6B1B6D7E1A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MultiDrive Cloud
DefaultDirName={autopf}\MultiDriveCloud
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=MultiDriveCloud-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\MultiDriveCloud.exe

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\MultiDriveCloud.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MultiDriveCloud.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MultiDriveCloud.exe"; Description: "Lancer {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MultiDriveCloud /f"; Flags: runhidden
