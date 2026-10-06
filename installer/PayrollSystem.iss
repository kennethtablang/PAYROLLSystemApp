; Inno Setup script for Payroll Management System.
;
; Build (from the repository root):
;   1. dotnet publish PAYROLLSystemApp/PAYROLLSystemApp.csproj -f net10.0-windows10.0.19041.0 -c Release
;        -p:RuntimeIdentifierOverride=win-x64 -p:SelfContained=true -p:WindowsPackageType=None
;        -p:WindowsAppSDKSelfContained=true -o Export/app
;   2. "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\PayrollSystem.iss
;
; The published app is self-contained (.NET and the Windows App SDK travel with
; it), so the target PC needs nothing installed first.
;
; Uninstalling removes the program only. The database lives in the user's
; AppData folder and backups and exports in Documents\Payroll MS; both are left
; alone so an uninstall or reinstall never loses payroll data.

#define AppName "Payroll Management System"
#define AppVersion "1.0"
#define AppPublisher "Payroll MS"
#define AppExe "PAYROLLSystemApp.exe"

[Setup]
; Never change AppId: it is how a newer installer finds and upgrades this one.
AppId={{7F3C2A91-5B4E-4D8A-9C61-2E0F8B7D4A35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without needing an administrator, unless the
; person installing chooses "all users" on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Kept in the project and committed (through Git LFS) so the installer travels with the code.
OutputDir=..\PAYROLLSystemApp\Installer
OutputBaseFilename=PayrollSystemSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
; Copied from the build's generated app icon (Resources/AppIcon); refresh it if the logo changes.
SetupIconFile=appicon.ico
UninstallDisplayName={#AppName}
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\Export\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
