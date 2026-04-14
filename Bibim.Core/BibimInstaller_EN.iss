; ============================================
; BIBIM - Inno Setup Installer Script (English)
; ============================================

#define MyAppName "BIBIM AI"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "SquareZero Inc."
#define MyAppURL "https://github.com/sqzrDev/BIBIM_AI"
#ifndef MyBuildId
  #define MyBuildId "manual"
#endif

[Setup]
AppId={{B1B1M-A1-V300-0000-000000000001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=BIBIM_AI_v{#MyAppVersion}_{#MyBuildId}_EN_Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=Assets\Icons\bibim-icon-blue.ico
UninstallDisplayIcon={app}\bibim-icon-blue.ico
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "bin\Release_EN\2027\*"; DestDir: "{app}\2027"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "bin\Release_EN\2026\*"; DestDir: "{app}\2026"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "bin\Release_EN\2025\*"; DestDir: "{app}\2025"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "bin\Release_EN\2024\*"; DestDir: "{app}\2024"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "bin\Release_EN\2023\*"; DestDir: "{app}\2023"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "bin\Release_EN\2022\*"; DestDir: "{app}\2022"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "Assets\Icons\bibim-icon-blue.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs
Source: "Config\*"; DestDir: "{app}\Config"; Flags: ignoreversion recursesubdirs

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; \
  Parameters: "/silent /install"; \
  StatusMsg: "Installing WebView2 Runtime..."; \
  Check: NeedsWebView2Runtime; \
  Flags: waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\Bibim.Core.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\Bibim.Core.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\Bibim.Core.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\Bibim.Core.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\Bibim.Core.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2022\Bibim.Core.addin"

[Code]
function IsRevitRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(
    ExpandConstant('{cmd}'),
    '/c tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := (ResultCode = 0);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := not IsRevitRunning;
  if not Result then
  begin
    MsgBox(
      'Revit is currently running.' + #13#10 +
      'Please close all Revit 2024/2025/2026 instances before installing BIBIM AI.',
      mbError,
      MB_OK);
  end;
end;

function NeedsWebView2Runtime: Boolean;
var
  Version: string;
begin
  Result := not RegQueryStringValue(
    HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', Version);

  if Result then
    Result := not RegQueryStringValue(
      HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Version);
end;

procedure WriteRevitAddinManifest(Year: string);
var
  ManifestPath: string;
  AssemblyPath: string;
  Content: string;
begin
  ManifestPath := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + Year + '\Bibim.Core.addin');
  AssemblyPath := AddBackslash(ExpandConstant('{app}')) + Year + '\Bibim.Core.dll';

  if not FileExists(AssemblyPath) then
    exit;

  ForceDirectories(ExtractFileDir(ManifestPath));

  Content :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>BIBIM AI</Name>' + #13#10 +
    '    <Assembly>' + AssemblyPath + '</Assembly>' + #13#10 +
    '    <FullClassName>Bibim.Core.BibimApp</FullClassName>' + #13#10 +
    '    <AddInId>B1B1B1B1-B1B1-B1B1-B1B1-B1B100030001</AddInId>' + #13#10 +
    '    <VendorId>BIBIM</VendorId>' + #13#10 +
    '    <VendorDescription>BIBIM AI - AI-powered Revit code generation</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '  <AddIn Type="Command">' + #13#10 +
    '    <Name>BIBIM Show Panel</Name>' + #13#10 +
    '    <Assembly>' + AssemblyPath + '</Assembly>' + #13#10 +
    '    <FullClassName>Bibim.Core.BibimShowPanelCommand</FullClassName>' + #13#10 +
    '    <AddInId>B1B1B1B1-B1B1-B1B1-B1B1-B1B100030002</AddInId>' + #13#10 +
    '    <VendorId>BIBIM</VendorId>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>' + #13#10;

  SaveStringToFile(ManifestPath, Content, False);
end;

procedure WriteAllRevitAddinManifests;
begin
  WriteRevitAddinManifest('2027');
  WriteRevitAddinManifest('2026');
  WriteRevitAddinManifest('2025');
  WriteRevitAddinManifest('2024');
  WriteRevitAddinManifest('2023');
  WriteRevitAddinManifest('2022');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteAllRevitAddinManifests;
    MsgBox(
      'BIBIM AI v{#MyAppVersion} has been installed.' + #13#10 + #13#10 +
      'How to get started:' + #13#10 +
      '1. Launch Revit.' + #13#10 +
      '2. Click the BIBIM tab in the top ribbon.' + #13#10 +
      '3. Click [Open BIBIM] to open the panel.' + #13#10 +
      '4. Sign in and you''re ready to go.',
      mbInformation, MB_OK);
  end;
end;
