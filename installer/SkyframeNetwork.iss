#define MyAppName "Skyframe Network"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Skyframe"

[Setup]
AppId={{B4EFA3A9-28F5-4EB9-9666-05DAA835DA49}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SkyframeNetwork
DefaultGroupName=Skyframe Network
UninstallDisplayIcon={app}\Ledger.Dashboard\Ledger.Dashboard.exe
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
OutputDir=..\artifacts\installer
OutputBaseFilename=SkyframeNetworkSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
SetupLogging=yes

[Dirs]
Name: "{commonappdata}\Skyframe\data"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "..\artifacts\publish\Ledger.Dashboard\*"; DestDir: "{app}\Ledger.Dashboard"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\Ledger.Node\*"; DestDir: "{app}\Ledger.Node"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\InstallService.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion
Source: "..\scripts\UninstallService.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Skyframe Network\Skyframe Dashboard"; Filename: "{app}\Ledger.Dashboard\Ledger.Dashboard.exe"; WorkingDir: "{app}\Ledger.Dashboard"
Name: "{autodesktop}\Skyframe Dashboard"; Filename: "{app}\Ledger.Dashboard\Ledger.Dashboard.exe"; WorkingDir: "{app}\Ledger.Dashboard"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a Skyframe Dashboard desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Code]
function RunHidden(const FileName, Params, WorkDir: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(FileName, Params, WorkDir, SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop SkyframeNode', '', ExitCode);
  Sleep(1500);
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete SkyframeNode', '', ExitCode);
  Sleep(1000);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  PowerShell, ScriptPath, Params: String;
begin
  if CurStep = ssPostInstall then
  begin
    PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    ScriptPath := ExpandConstant('{app}\Scripts\InstallService.ps1');
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
    if (not RunHidden(PowerShell, Params, ExpandConstant('{app}'), ExitCode)) or (ExitCode <> 0) then
      RaiseException('Skyframe files were installed, but Windows Service or firewall setup failed. Run InstallService.ps1 as Administrator to finish setup.');
  end;
end;

function InitializeUninstall(): Boolean;
var
  ExitCode: Integer;
  PowerShell, ScriptPath, Params: String;
  DeleteWallet: Boolean;
begin
  DeleteWallet := False;
  if not WizardSilent then
    DeleteWallet := MsgBox('Delete the encrypted Skyframe wallet from this computer? Choose No to keep it for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  ScriptPath := ExpandConstant('{app}\Scripts\UninstallService.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if DeleteWallet then Params := Params + ' -DeleteWallet';
  Result := RunHidden(PowerShell, Params, ExpandConstant('{app}'), ExitCode) and (ExitCode = 0);
  if (not Result) and (not WizardSilent) then
    MsgBox('The node service did not cleanly stop. The uninstaller has been cancelled so its files and data remain available. Stop the service and try again.', mbError, MB_OK);
end;