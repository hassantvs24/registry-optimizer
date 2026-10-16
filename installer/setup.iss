; setup.iss — Inno Setup script for Registry Optimizer
; Build with: iscc.exe setup.iss   (run from publish.ps1, or manually after dotnet publish)
; Assumes the portable single-file exe already exists at ..\dist\RegistryOptimizer.exe

#define MyAppName      "Registry Cleaner && Optimizer"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "RegistryOptimizer"
#define MyAppExe       "RegistryOptimizer.exe"

[Setup]
AppId={{B7A4C2E1-9D3F-4A5B-8C6E-RegistryOpt1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Scan and fix registry issues, defragment and compact registry hives.
DefaultDirName={autopf}\Registry Optimizer
DefaultGroupName=Registry Optimizer
OutputDir=..\dist
OutputBaseFilename=RegistryOptimizer-Setup
SetupIconFile=..\RegistryOptimizer\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\RegistryOptimizer.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&&', '&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; Keep user backups in ProgramData on uninstall (safety) - do NOT delete them automatically.
