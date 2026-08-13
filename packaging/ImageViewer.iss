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
#define AppVersion     "0.1.0"
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
PrivilegesRequiredOverridesAllowed=dialog
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "associate";   Description: "Add Image Viewer to the ""Open with"" list for image files"; GroupDescription: "File types:"

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

procedure RegisterSupportedTypes();
var
  RootKey: Integer;
  KeyPath, Remaining, Extension: String;
  SpaceAt: Integer;
begin
  { Match the root the rest of the installer used, so a per-user install stays per-user. }
  if IsAdminInstallMode then
    RootKey := HKEY_LOCAL_MACHINE
  else
    RootKey := HKEY_CURRENT_USER;

  KeyPath := 'Software\Classes\Applications\ImageViewer.exe\SupportedTypes';

  { Split on spaces by hand rather than with StringSplitEx, which only exists in Inno Setup 6.3
    and later. This works on any 6.x compiler. }
  Remaining := Trim(SupportedExtensions) + ' ';

  repeat
    SpaceAt := Pos(' ', Remaining);
    Extension := Trim(Copy(Remaining, 1, SpaceAt - 1));
    Remaining := Trim(Copy(Remaining, SpaceAt + 1, Length(Remaining))) + ' ';

    if Extension <> '' then
      RegWriteStringValue(RootKey, KeyPath, Extension, '');
  until Trim(Remaining) = '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Only once the files are in place, and only if the user opted into associations. }
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('associate') then
    RegisterSupportedTypes();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RootKey: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if IsAdminInstallMode then
      RootKey := HKEY_LOCAL_MACHINE
    else
      RootKey := HKEY_CURRENT_USER;

    { [Registry] cleans up what it wrote; this key was created in code so it must be removed here. }
    RegDeleteKeyIncludingSubkeys(RootKey, 'Software\Classes\Applications\ImageViewer.exe');
  end;
end;
