; Windows installer for SimpleHPSDR (Inno Setup 6). Built by CI after
; scripts/make-windows.sh: iscc /DAppVersion=<ver> /DSourceDir=<folder> this file.
; Output: artifacts/SimpleHPSDR-<ver>-win-x64-setup.exe
;
; Installs for the current user only (no administrator password) in
; %LOCALAPPDATA%\Programs\SimpleHPSDR, with a Start menu entry, an optional
; desktop icon and an uninstaller. Settings live in %LOCALAPPDATA%\SimpleHPSDR,
; outside the program folder, so upgrades keep them and uninstalling leaves them.
; Not code-signed: SmartScreen still warns the first time.

#ifndef AppVersion
  #error AppVersion must be defined (/DAppVersion=...)
#endif
#ifndef SourceDir
  #error SourceDir must be defined (/DSourceDir=...)
#endif

[Setup]
; The AppId identifies SimpleHPSDR to Windows across versions: never change it.
AppId={{FD7D1942-9A79-4E72-B659-4F609C75089A}
AppName=SimpleHPSDR
AppVersion={#AppVersion}
AppVerName=SimpleHPSDR {#AppVersion}
AppPublisher=KC2RGW
AppPublisherURL=https://github.com/kingsqueak/SimpleHPSDR
AppSupportURL=https://github.com/kingsqueak/SimpleHPSDR/issues
AppUpdatesURL=https://github.com/kingsqueak/SimpleHPSDR/releases
DefaultDirName={localappdata}\Programs\SimpleHPSDR
DefaultGroupName=SimpleHPSDR
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\src\SimpleHPSDR\Assets\icon.ico
UninstallDisplayIcon={app}\SimpleHPSDR.exe
UninstallDisplayName=SimpleHPSDR
OutputDir=..\artifacts
OutputBaseFilename=SimpleHPSDR-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running SimpleHPSDR (and its engine) before replacing files.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; An upgrade starts from a clean program folder, so no file from an older
; version is left behind. Settings aren't in here.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SimpleHPSDR"; Filename: "{app}\SimpleHPSDR.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\SimpleHPSDR"; Filename: "{app}\SimpleHPSDR.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SimpleHPSDR.exe"; Description: "{cm:LaunchProgram,SimpleHPSDR}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop SimpleHPSDR and its engine (/T: the app's own process tree only, so a
; Zeus engine, also StationEngine.exe, is never touched).
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM SimpleHPSDR.exe"; Flags: runhidden; RunOnceId: "StopApp"
