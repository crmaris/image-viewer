; Inno Setup script for Image Viewer.
;
; Build with packaging/build-installer.ps1, which publishes first and then invokes ISCC.
; Requires Inno Setup 6: https://jrsoftware.org/isdl.php
;
; File associations here are ADDITIVE. Each extension gets an OpenWithProgids entry, which puts
; Image Viewer in the "Open with" list without seizing the default handler. Windows 10 and 11
; deliberately block installers from silently changing default file associations anyway, so
; attempting it would fail on modern Windows and be hostile on older versions. The user picks the
; default themselves via "Open with -> Choose another app", or Settings > Default apps.

#define AppName        "Image Viewer"
#ifndef AppVersion
  #define AppVersion   "0.2.2"
#endif
#define AppPublisher   "Aris Mpitziopoulos"
#define AppExeName     "ImageViewer.exe"
#define ProgId         "ImageViewer.Image"

; Set by build-installer.ps1 via /DSourceDir=... ; this default matches a local win-x64 publish.
#ifndef SourceDir
  #define SourceDir "..\build\portable\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\build"
#endif

[Setup]
AppId={{7C3F1A62-9E4D-4B8A-9F21-2D6B5E0C4A17}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir={#OutputDir}
OutputBaseFilename=ImageViewer-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user by default so no elevation prompt appears; the user can still choose all-users.
;
; "commandline" is not optional decoration. Without it Inno Setup silently IGNORES /ALLUSERS and
; /CURRENTUSER, and since PrivilegesRequired=lowest the installer then falls back to asking - or,
; when run silently, to a per-user install. That matters most for the auto-updater, which detects
; how this copy was installed and passes the matching switch: ignored, it would "update" an
; all-users installation in Program Files by dropping a second copy into %LOCALAPPDATA%, leaving
; the original in place and still owning every file association.
PrivilegesRequiredOverridesAllowed=commandline dialog
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
SetupIconFile=..\src\ImageViewer\app.ico
MinVersion=10.0
; Makes Setup call SHChangeNotify(SHCNE_ASSOCCHANGED) after installing and uninstalling. Without
; it the registry is correct but Explorer keeps serving its cached association data, so the
; application does not appear under "Open with" until the user signs out and back in.
ChangesAssociations=yes
; Broadcasts WM_SETTINGCHANGE after the PATH task runs, so a newly opened console
; picks the change up without a sign-out.
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "associate";   Description: "Add Image Viewer to the ""Open with"" list for image files"; GroupDescription: "File types:"
Name: "addtopath";  Description: "Add to PATH, so ""imageviewer"" works from a command prompt"; GroupDescription: "Command line:"; Flags: unchecked

[Files]
; The whole published folder. Not a single-file build on purpose: bundling native libraries makes
; .NET extract them to a temp directory on first run, costing about a second - unacceptable for an
; application whose entire point is starting fast.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; ---- The application's own ProgID -------------------------------------------------------------
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Image"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate

; ---- Registered application, so it shows in Settings > Default apps ---------------------------
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: associate

; ---- "Open with" entries ----------------------------------------------------------------------
; Kept in step with SupportedFormats.AssociatableExtensions in the C# source; the self-test parses
; this file and fails if the two lists drift apart.
Root: HKA; Subkey: "Software\Classes\.jpg\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jpeg\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jpe\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jfif\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jif\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.png\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.gif\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.bmp\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.dib\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.tif\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.tiff\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.ico\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.cur\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.wdp\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jxr\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.hdp\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.dds\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.heic\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.heif\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.hif\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.avif\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.avifs\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.webp\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jxl\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.cr2\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.cr3\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.crw\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.nef\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.nrw\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.arw\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.srf\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.sr2\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.orf\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.rw2\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.raf\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.pef\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.ptx\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.srw\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.dng\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.3fr\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.fff\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.iiq\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.rwl\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.x3f\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.mrw\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.erf\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.kdc\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.dcr\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.svg\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.svgz\OpenWithProgids";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.psd\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.tga\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.jp2\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.exr\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.qoi\OpenWithProgids";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

; ---- PATH, for the command-line interface ------------------------------------------------------
; {olddata} splices in the existing value WITHOUT expanding it, and preservestringtype keeps the
; REG_EXPAND_SZ type. Reading PATH into a variable and writing it back would expand %SystemRoot%
; and its friends into literal paths, silently changing the meaning of the user's environment -
; which is why this is declarative rather than code. Note the deliberate absence of
; uninsdeletevalue: that would delete the whole of PATH on uninstall. Removal is done in [Code].
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Flags: preservestringtype; Tasks: addtopath; Check: IsAdminInstallMode and NeedsAddPath()
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Flags: preservestringtype; Tasks: addtopath; Check: (not IsAdminInstallMode) and NeedsAddPath()

[UninstallDelete]
; Settings are written under the user's roaming profile and are not tracked by the installer.
Type: filesandordirs; Name: "{userappdata}\ImageViewer"

[Code]
{
  Registers SupportedTypes for the application.

  OpenWithProgids alone gets an application into the "Choose another app" dialog, but the shell
  also consults Applications\<exe>\SupportedTypes when building the Open With list. Without it the
  application was registered for all 55 extensions and still did not appear as an option, which is
  exactly the bug this exists to fix.

  Written in code rather than as 55 more [Registry] lines so the extension list is stated once.
}

const
  SupportedExtensions =
    '.jpg .jpeg .jpe .jfif .jif .png .gif .bmp .dib .tif .tiff .ico .cur .wdp .jxr .hdp .dds ' +
    '.heic .heif .hif .avif .avifs .webp .jxl ' +
    '.cr2 .cr3 .crw .nef .nrw .arw .srf .sr2 .orf .rw2 .raf .pef .ptx .srw .dng .3fr .fff .iiq ' +
    '.rwl .x3f .mrw .erf .kdc .dcr ' +
    '.svg .svgz .psd .tga .jp2 .exr .qoi';

function GetRegistrationRoot(): Integer;
begin
  { Match the root the rest of the installer used, so a per-user install stays per-user. }
  if IsAdminInstallMode then
    Result := HKEY_LOCAL_MACHINE
  else
    Result := HKEY_CURRENT_USER;
end;

{
  Writes the two per-extension registrations in one pass:

  SupportedTypes    - the shell consults this when building the Open With menu.
  FileAssociations  - part of the Default Programs "Capabilities" block, which is what lists the
                      application BY NAME in Settings > Default apps so every file type it handles
                      can be reassigned in one place. Without it, searching Settings for
                      "Image Viewer" finds nothing.
}
procedure RegisterExtensions();
var
  RootKey: Integer;
  SupportedPath, AssocPath, Remaining, Extension: String;
  SpaceAt: Integer;
begin
  RootKey := GetRegistrationRoot();
  SupportedPath := 'Software\Classes\Applications\ImageViewer.exe\SupportedTypes';
  AssocPath := 'Software\ImageViewer\Capabilities\FileAssociations';

  { Split on spaces by hand rather than with StringSplitEx, which only exists in Inno Setup 6.3
    and later. This works on any 6.x compiler. }
  Remaining := Trim(SupportedExtensions) + ' ';

  repeat
    SpaceAt := Pos(' ', Remaining);
    Extension := Trim(Copy(Remaining, 1, SpaceAt - 1));
    Remaining := Trim(Copy(Remaining, SpaceAt + 1, Length(Remaining))) + ' ';

    if Extension <> '' then
    begin
      RegWriteStringValue(RootKey, SupportedPath, Extension, '');
      RegWriteStringValue(RootKey, AssocPath, Extension, 'ImageViewer.Image');
    end;
  until Trim(Remaining) = '';
end;

{ The Capabilities block itself, plus the pointer that makes Windows read it. }
procedure RegisterCapabilities();
var
  RootKey: Integer;
  CapPath: String;
begin
  RootKey := GetRegistrationRoot();
  CapPath := 'Software\ImageViewer\Capabilities';

  RegWriteStringValue(RootKey, CapPath, 'ApplicationName', '{#AppName}');
  RegWriteStringValue(RootKey, CapPath, 'ApplicationDescription',
    'A plain, fast image viewer. Opens practically any image format.');
  RegWriteStringValue(RootKey, CapPath, 'ApplicationIcon',
    ExpandConstant('{app}\{#AppExeName}') + ',0');

  RegWriteStringValue(RootKey, 'Software\RegisteredApplications',
    '{#AppName}', 'Software\ImageViewer\Capabilities');
end;

{ Which environment key holds PATH depends on the install mode: the machine-wide one lives under
  Session Manager, while the per-user one is simply HKCU\Environment. }
function EnvironmentKey(): String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

{ True when PATH does not already list the install folder. A semicolon is added at both ends before
  searching, so a folder cannot match a longer one that merely begins with the same characters. }
function NeedsAddPath(): Boolean;
var
  Existing: String;
begin
  if not RegQueryStringValue(GetRegistrationRoot(), EnvironmentKey(), 'Path', Existing) then
    Existing := '';

  Result := Pos(
    ';' + Uppercase(ExpandConstant('{app}')) + ';',
    ';' + Uppercase(Existing) + ';') = 0;
end;

{ Takes the install folder back out of PATH on uninstall.

  Refuses to touch a PATH that still holds an unexpanded variable. RegQueryStringValue hands back an
  already-expanded string for a REG_EXPAND_SZ value, so writing it back would bake %SystemRoot% and
  anything like it into literal paths - considerably worse than leaving one stale entry behind.

  One observable side effect, verified 2026-08-21 by installing and uninstalling for real: the value
  is rewritten as REG_EXPAND_SZ, so a user PATH that happened to be a plain REG_SZ comes back as
  REG_EXPAND_SZ. The content is byte-identical, and because the guard above means we only ever reach
  this write when the value holds no '%' at all, the two types are semantically identical here.
  REG_EXPAND_SZ is also the type Windows itself uses for PATH, and the type the install would have
  created had the value not already existed. Inno's scripting has no way to query a value's type, so
  preserving it exactly is not available; this is the closest safe behaviour. }
procedure RemoveFromPath();
var
  RootKey: Integer;
  Key, Existing, Folder, Updated: String;
  Position: Integer;
begin
  RootKey := GetRegistrationRoot();
  Key := EnvironmentKey();

  if not RegQueryStringValue(RootKey, Key, 'Path', Existing) then exit;
  if Pos('%', Existing) > 0 then exit;

  Folder := ExpandConstant('{app}');

  Position := Pos(';' + Uppercase(Folder) + ';', ';' + Uppercase(Existing) + ';');
  if Position = 0 then exit;

  { Position indexes the semicolon-padded copy, which is offset by one from the original; that
    offset is exactly cancelled by wanting to drop the separator along with the folder. }
  Updated := Copy(Existing, 1, Position - 1) +
             Copy(Existing, Position + Length(Folder) + 1, Length(Existing));

  { Tidy any leading or trailing separator the removal left behind. }
  while (Length(Updated) > 0) and (Updated[1] = ';') do
    Updated := Copy(Updated, 2, Length(Updated));
  while (Length(Updated) > 0) and (Updated[Length(Updated)] = ';') do
    Updated := Copy(Updated, 1, Length(Updated) - 1);

  RegWriteExpandStringValue(RootKey, Key, 'Path', Updated);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Only once the files are in place, and only if the user opted into associations. }
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('associate') then
  begin
    RegisterCapabilities();
    RegisterExtensions();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RootKey: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RootKey := GetRegistrationRoot();

    { [Registry] cleans up what it wrote; these were created in code so they must go here.
      Leaving the RegisteredApplications pointer behind would list a phantom app in Settings. }
    RegDeleteKeyIncludingSubkeys(RootKey, 'Software\Classes\Applications\ImageViewer.exe');
    RegDeleteKeyIncludingSubkeys(RootKey, 'Software\ImageViewer');
    RegDeleteValue(RootKey, 'Software\RegisteredApplications', '{#AppName}');

    RemoveFromPath();
  end;
end;
