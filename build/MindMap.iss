; Inno Setup script for MindMap.
; Compiled by the "Release" GitHub Action with ISCC. The version and the
; published-output directory are injected from the workflow via /D defines:
;   ISCC /DAppVersion=1.2.3 /DSourceDir=C:\...\publish\win-x64 build\MindMap.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef AppUrl
  #define AppUrl "https://github.com"
#endif

#define AppName "MindMap"
#define AppExe "MindMap.exe"
#define AppPublisher "MindMap"

[Setup]
; A stable AppId keeps upgrades/uninstall pointing at the same install.
AppId={{94CF3F77-A245-411A-AE74-A2830FFEE797}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\MindMap
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=installer
OutputBaseFilename=MindMap-{#AppVersion}-setup
SetupIconFile=..\MindMap\Assets\app-icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; net10.0 is published as win-x64; install into the native Program Files.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; In-app updates run setup silently, so the postinstall entry above is skipped.
; /RELAUNCH is our own switch for that case.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
function CmdLineParamExists(const Value: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function RelaunchRequested: Boolean;
begin
  Result := CmdLineParamExists('/RELAUNCH');
end;
