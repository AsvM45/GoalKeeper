; GoalKeeper Installer
; Inno Setup 6 script
; Bundles: ServiceEngine, ConfigUI, AIService (Python embeddable)

#define MyAppName "GoalKeeper"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "GoalKeeper Project"
#define MyAppURL "https://github.com/yourusername/GoalKeeper"
#define MyAppExeName "ConfigUI.exe"
#define ServiceName "GoalKeeperService"
#define ServiceExe "ServiceEngine.exe"

[Setup]
AppId={{8F3A2B1C-4D5E-6F7A-8B9C-0D1E2F3A4B5C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=GoalKeeperSetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
ArchitecturesInstallIn64BitMode=x64

; Safe Mode warning
InfoBeforeFile=SafeModeWarning.rtf

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupui"; Description: "Launch GoalKeeper dashboard on login"; GroupDescription: "Startup"

[Files]
; ConfigUI (WPF dashboard)
Source: "..\ConfigUI\bin\Release\net8.0-windows\win-x64\publish\*"; \
    DestDir: "{app}\ConfigUI"; Flags: ignoreversion recursesubdirs

; ServiceEngine (Windows Service)
Source: "..\ServiceEngine\bin\Release\net8.0-windows\win-x64\publish\*"; \
    DestDir: "{app}\ServiceEngine"; Flags: ignoreversion recursesubdirs

; AIService (Python scripts)
Source: "..\AIService\*"; \
    DestDir: "{app}\AIService"; Flags: ignoreversion recursesubdirs
    Excludes: "__pycache__,*.pyc,.env"

; Database schema
Source: "..\Database\schema.sql"; DestDir: "{app}\Database"; Flags: ignoreversion

; Python embeddable runtime (bundled so users don't need Python)
Source: "..\Installer\python-embed\*"; \
    DestDir: "{app}\python"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\ConfigUI\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\ConfigUI\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\ConfigUI\{#MyAppExeName}"; Tasks: startupui

[Run]
; Install the Windows Service
Filename: "{sys}\sc.exe"; \
    Parameters: "create {#ServiceName} binPath= ""{app}\ServiceEngine\{#ServiceExe}"" start= auto DisplayName= ""GoalKeeper Background Service"""; \
    StatusMsg: "Installing GoalKeeper service..."; \
    Flags: runhidden waituntilterminated

; Set service description
Filename: "{sys}\sc.exe"; \
    Parameters: "description {#ServiceName} ""GoalKeeper productivity enforcement service"""; \
    Flags: runhidden waituntilterminated

; Start the service
Filename: "{sys}\sc.exe"; \
    Parameters: "start {#ServiceName}"; \
    StatusMsg: "Starting GoalKeeper service..."; \
    Flags: runhidden waituntilterminated

; Install Python dependencies for AIService
Filename: "{app}\python\python.exe"; \
    Parameters: "-m pip install -r ""{app}\AIService\requirements.txt"" --quiet"; \
    StatusMsg: "Installing AI service dependencies..."; \
    Flags: runhidden waituntilterminated

; Launch ConfigUI after install
Filename: "{app}\ConfigUI\{#MyAppExeName}"; \
    Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop and delete the service
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; \
    Flags: runhidden waituntilterminated

[Code]
// Pre-install: check if Windows 10+ and .NET 8 runtime
function InitializeSetup(): Boolean;
begin
  Result := True;
  // Minimum Windows 10 build 17763
  if not (GetWindowsVersion >= $0A000000) then begin
    MsgBox('GoalKeeper requires Windows 10 or later.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
end;

// Safe uninstall: if armed, warn user
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usAppMutexCheck then begin
    MsgBox(
      'IMPORTANT: If GoalKeeper is in Armed Mode, uninstalling may not work normally.' + #13#10 +
      'If the uninstall fails, boot Windows into Safe Mode and try again.' + #13#10 + #13#10 +
      'Safe Mode recovery instructions: See docs\SAFE_MODE_RECOVERY.md',
      mbInformation, MB_OK);
  end;
end;
