; LunchHelper InnoSetup installer script
; ------------------------------------------------
; Free installer that places LunchHelper.exe into
; C:\Program Files\LunchHelper\  (required so the
; uiAccess="true" manifest is actually honored by
; Windows on default UAC policy).
;
; Build:   install Inno Setup, then run
;          iscc installer\LunchHelper.iss
; Input:   the SIGNED LunchHelper.exe produced by
;          the CI / Release pipeline (see .github/
;          workflows). Put it at bin\Release\ before
;          running iscc, or adjust the [Files] Source.
;
; NOTE: replace <your-username> below and generate a
;       fresh AppId GUID (run: iscc /?  or use any
;       GUID generator). Keep AppId stable across
;       versions so upgrades overwrite cleanly.

#define MyAppName "LunchHelper"
#define MyAppVersion "1.0.0"
#define MyPublisher "LunchHelper Contributors"
#define MyURL "https://github.com/<your-username>/LunchHelper"

[Setup]
AppId={{REPLACE-WITH-A-STABLE-GUID}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyPublisher}
AppPublisherURL={#MyURL}
AppSupportURL={#MyURL}
AppUpdatesURL={#MyURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=installer\Output
OutputBaseFilename=LunchHelper-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppName}.exe

[Languages]
Name: "Chinese"; MessagesFile: "compiler:Default.isl"

[Files]
; Signed exe from the release/CI build.
Source: "..\bin\Release\{#MyAppName}.exe"; DestDir: "{app}"; Flags: ignoreversion
; Debug symbols (optional, harmless if absent).
Source: "..\bin\Release\{#MyAppName}.pdb"; DestDir: "{app}"; Flags: ignoreversion; Check: FileExists('..\bin\Release\{#MyAppName}.pdb')

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "额外任务:"

[Run]
Filename: "{app}\{#MyAppName}.exe"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
