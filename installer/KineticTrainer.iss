; Inno Setup script: wraps the self-contained publish folder into a per-user setup.exe.
; Build with publish.ps1 (runs ISCC automatically when Inno Setup 6 is installed).

#define AppName "Kinetic Trainer"
#define AppVersion "0.2.0"
#define AppExe "KineticTrainer.exe"
#define SourceDir "..\dist\KineticTrainer-win-x64"

[Setup]
AppId={{7D1C4E2A-5B6F-4C2E-9C3A-1F2E3D4C5B6A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Kinetic Trainer (personal build)
DefaultDirName={localappdata}\Programs\KineticTrainer
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=KineticTrainer-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\KineticTrainer.App\Assets\kinetic-trainer.ico
DisableProgramGroupPage=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
