; Toolbox for Windows: the installer (Inno Setup 7).
;
; Compiled by "tools\build.ps1 package", never by hand: the build says what
; to pack and what to call it.
;
;   ISCC --define=Version=0.3.1 --define=Build=12 --define=Arch=arm64
;        --define=Stage=<the packaged folder> --define=OutputDir=<folder>
;        --define=OutputName=Toolbox-0.3.1-win-arm64-setup Toolbox.iss
;
; What it makes installs for the current user only, with no administrator:
; into %LOCALAPPDATA%\Programs\Toolbox unless another folder is chosen. The
; app replaces its own files when it updates itself, so it has to live where
; its user may write, which is also why this is not a Windows Installer
; package: that would put back, or complain about, every file an update
; changed.
;
; The app and this script agree on two things:
;  - AppId below is also in app\Toolbox\Support\InstalledCopy.cs, which
;    keeps the version shown under "Installed apps" right after an update
;    (a test compares the two). Never change it: it is how Windows, and the
;    next installer, know an installed copy.
;  - files.txt, in every package, lists what that version put into its
;    folder. The app's updater removes what the old list names; so does this
;    installer when it replaces a copy, and so does the uninstaller, since a
;    copy that has updated itself holds files this installer never wrote.

#ifndef Version
  #error Compile this through tools\build.ps1 package: it passes Version, Build, Arch, Stage, OutputDir and OutputName.
#endif

#define AppName "Toolbox"
#define AppExe "Toolbox.exe"
#define AppId "{89DB47A7-75BF-4E60-83DB-D1927B1EB6AD}"
#define Repository "https://github.com/tacticalbadger/Toolbox_Windows"

#if Arch == "arm64"
  #define Processors "arm64"
#elif Arch == "x64"
  #define Processors "x64compatible"
#else
  #error Arch must be arm64 or x64.
#endif

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher=tacticalbadger
AppPublisherURL={#Repository}
AppSupportURL={#Repository}
AppUpdatesURL={#Repository}/releases
VersionInfoVersion={#Version}.{#Build}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} {#Version} Setup ({#Arch})
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

; For this user, without an administrator, somewhere the app can update itself.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; PrepareToInstall below says more than "the folder exists".
DirExistsWarning=no

ArchitecturesAllowed={#Processors}
ArchitecturesInstallIn64BitMode={#Processors}
MinVersion=10.0.19041

; The app is asked to be closed, in words, rather than closed by Windows:
; it may have sessions open.
CloseApplications=no

SourceDir={#Stage}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
; Setup's own tables are left readable, so that the build can look through
; what it hands out for paths of the machine it was built on.
InternalCompressLevel=none
WizardStyle=modern dynamic

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Updates the app downloaded for itself. Connections, credentials and
; settings (%APPDATA%\Toolbox) are the user's and stay.
Type: filesandordirs; Name: "{localappdata}\{#AppName}\updates"
Type: dirifempty; Name: "{app}"

[Code]
const
  Manifest = 'files.txt';

{ Whether a program is running from this file: Windows lets nobody open a
  running program for writing. }
function InUse(const FileName: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  if not FileExists(FileName) then Exit;
  try
    Stream := TFileStream.Create(FileName, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

function HasEntries(const Folder: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if FindFirst(Folder + '\*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then Result := True;
      until Result or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

{ A line of the manifest as a path below the app's folder, or nothing when
  it is anything else: the list decides what gets deleted. }
function ListedName(const Line: String): String;
begin
  Result := Trim(Line);
  StringChangeEx(Result, '/', '\', True);
  if (Result = '') or (Pos('..', Result) > 0) or (Pos(':', Result) > 0) or (Result[1] = '\') then Result := '';
end;

{ Removes what the version in a folder says it put there, and the folders
  that are empty afterwards. Files nobody listed stay. A folder without the
  app in it is not looked at: a files.txt there is somebody else's. }
procedure RemoveListedFiles(const Folder: String);
var
  Lines: TArrayOfString;
  I: Integer;
  Name, Parent: String;
begin
  if not FileExists(Folder + '\{#AppExe}') then Exit;
  if not LoadStringsFromFile(Folder + '\' + Manifest, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do begin
    Name := ListedName(Lines[I]);
    if Name <> '' then DeleteFile(Folder + '\' + Name);
  end;
  for I := 0 to GetArrayLength(Lines) - 1 do begin
    Parent := ExtractFileDir(ListedName(Lines[I]));
    while Parent <> '' do begin
      RemoveDir(Folder + '\' + Parent);
      Parent := ExtractFileDir(Parent);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  App: String;
begin
  Result := '';
  App := ExpandConstant('{app}');
  if DirExists(App) and HasEntries(App) and not FileExists(App + '\{#AppExe}') then
    Result := 'The folder ' + App + ' holds other things. Choose an empty folder, or a new one.'
  else if InUse(App + '\{#AppExe}') then
    Result := '{#AppName} is running from ' + App + '. Close it, then run this installer again.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Over a copy that is already there: what it listed goes first, so that
    nothing of a version that no longer exists is left among the new files. }
  if CurStep = ssInstall then RemoveListedFiles(ExpandConstant('{app}'));
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if InUse(ExpandConstant('{app}\{#AppExe}')) then begin
    SuppressibleMsgBox('{#AppName} is running. Close it, then uninstall again.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Before the uninstaller removes what it installed (files.txt among it):
    a copy that updated itself holds files of later versions, which only
    its own list knows. }
  if CurUninstallStep = usUninstall then RemoveListedFiles(ExpandConstant('{app}'));
end;
