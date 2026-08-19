#ifndef SourceDir
  #error SourceDir is required
#endif
#define AppId "{84149F30-D0D9-4A0A-B796-A59CC37727AE}"
[Setup]
AppId={#AppId}
AppName=Threadsmith.NET
AppVersion={#AppVersion}
DefaultDirName={autopf}\Threadsmith.NET
ArchitecturesAllowed={#if TargetRid == "win-arm64"}arm64{#else}x64compatible{#endif}
ArchitecturesInstallIn64BitMode={#if TargetRid == "win-arm64"}arm64{#else}x64compatible{#endif}
ChangesEnvironment=yes
OutputDir={#OutputDir}
OutputBaseFilename=Threadsmith-{#AppVersion}-{#TargetRid}-setup
Compression=lzma2
SolidCompression=yes
Uninstallable=yes
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}'))
[Code]
function NeedsAddPath(Param: string): Boolean;
var Paths: string;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths) then Paths := '';
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;
