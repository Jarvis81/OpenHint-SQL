; ===========================================================================
;  OpenHint SQL  -  Inno Setup installer script
;
;  Supported: SSMS 18.12.1 / 19.3 / 20.2.1 / 21.x / 22.x
;  Architecture: SSMS 18-20 are 32-bit; SSMS 21-22 are 64-bit
;
;  Build with:  scripts\build-installer.bat
;  Output:      dist\OpenHintSQLSetup-{#AppVersion}.exe
; ===========================================================================

#define AppName      "OpenHint SQL"
#define AppId        "OpenHintSQL.63D8CFAD-D1FF-40EB-80DB-7728DEDD7A91"
#define AppVersion   "1.1.0"
#define AppPublisher "OpenHintSQL"
#define AppURL       "https://github.com/Jarvis81/OpenHint-SQL"
#define ExtSubdir    "Extensions\OpenHintSQL"
#define BuildDir     "..\src\OpenHintSQL\bin\Release\net48"

; --------------------------------------------------------------------------
[Setup]
; --------------------------------------------------------------------------
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases

PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=

; We install to multiple directories detected at runtime — {app} is unused
; but Inno Setup requires DefaultDirName to be set.
DefaultDirName={autopf}\Microsoft SQL Server Management Studio
DisableDirPage=yes
DirExistsWarning=no

OutputDir=..\dist
OutputBaseFilename=OpenHintSQLSetup-{#AppVersion}

Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern
ShowLanguageDialog=no
MinVersion=10.0

Uninstallable=yes
UninstallDisplayName={#AppName}
CreateUninstallRegKey=yes

VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=SQL autocomplete and syntax suggestions for SSMS
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; --------------------------------------------------------------------------
[Languages]
; --------------------------------------------------------------------------
Name: "english"; MessagesFile: "compiler:Default.isl"

; --------------------------------------------------------------------------
[Messages]
; --------------------------------------------------------------------------
WelcomeLabel1=Welcome to {#AppName} Setup
WelcomeLabel2=This installs {#AppName} {#AppVersion} into every SQL Server Management Studio version it detects (18, 19, 20, 21, 22).%n%nPlease close all open SSMS windows before continuing.%n%nClick Next to continue.
FinishedLabel=OpenHint SQL {#AppVersion} has been installed.%n%nOpen SSMS and type in a query window to use it.%n%nTo verify it loaded: View > Output > select "OpenHint SQL" from the dropdown.

; --------------------------------------------------------------------------
; [Files]
;
; Each SSMS version has its own set of entries guarded by a Check: function.
; Versions that are not installed on this machine are skipped automatically.
;
; SAFETY: all DestDir values are "{code:GetExtDir|NN}" which resolves to
; <SsmsRoot>\Extensions\OpenHintSQL — never to any SSMS core directory.
; --------------------------------------------------------------------------
[Files]

; ── SSMS 18 ──────────────────────────────────────────────────────────────
Source: "{#BuildDir}\*.dll";                                    DestDir: "{code:GetExtDir|18}"; Check: Ssms18Found; Flags: ignoreversion
Source: "{#BuildDir}\OpenHintSQL.pkgdef";                       DestDir: "{code:GetExtDir|18}"; Check: Ssms18Found; Flags: ignoreversion
Source: "{#BuildDir}\Config\snippets.json";                       DestDir: "{code:GetExtDir|18}\Config"; Check: Ssms18Found; Flags: ignoreversion
Source: "..\src\OpenHintSQL\source.extension.vsixmanifest"; \
        DestDir: "{code:GetExtDir|18}"; \
        DestName: "extension.vsixmanifest"; \
        Check: Ssms18Found; Flags: ignoreversion

; ── SSMS 19 ──────────────────────────────────────────────────────────────
Source: "{#BuildDir}\*.dll";                                    DestDir: "{code:GetExtDir|19}"; Check: Ssms19Found; Flags: ignoreversion
Source: "{#BuildDir}\OpenHintSQL.pkgdef";                       DestDir: "{code:GetExtDir|19}"; Check: Ssms19Found; Flags: ignoreversion
Source: "{#BuildDir}\Config\snippets.json";                       DestDir: "{code:GetExtDir|19}\Config"; Check: Ssms19Found; Flags: ignoreversion
Source: "..\src\OpenHintSQL\source.extension.vsixmanifest"; \
        DestDir: "{code:GetExtDir|19}"; \
        DestName: "extension.vsixmanifest"; \
        Check: Ssms19Found; Flags: ignoreversion

; ── SSMS 20 ──────────────────────────────────────────────────────────────
Source: "{#BuildDir}\*.dll";                                    DestDir: "{code:GetExtDir|20}"; Check: Ssms20Found; Flags: ignoreversion
Source: "{#BuildDir}\OpenHintSQL.pkgdef";                       DestDir: "{code:GetExtDir|20}"; Check: Ssms20Found; Flags: ignoreversion
Source: "{#BuildDir}\Config\snippets.json";                       DestDir: "{code:GetExtDir|20}\Config"; Check: Ssms20Found; Flags: ignoreversion
Source: "..\src\OpenHintSQL\source.extension.vsixmanifest"; \
        DestDir: "{code:GetExtDir|20}"; \
        DestName: "extension.vsixmanifest"; \
        Check: Ssms20Found; Flags: ignoreversion

; ── SSMS 21 ──────────────────────────────────────────────────────────────
Source: "{#BuildDir}\*.dll";                                    DestDir: "{code:GetExtDir|21}"; Check: Ssms21Found; Flags: ignoreversion
Source: "{#BuildDir}\OpenHintSQL.pkgdef";                       DestDir: "{code:GetExtDir|21}"; Check: Ssms21Found; Flags: ignoreversion
Source: "{#BuildDir}\Config\snippets.json";                       DestDir: "{code:GetExtDir|21}\Config"; Check: Ssms21Found; Flags: ignoreversion
Source: "..\src\OpenHintSQL\source.extension.vsixmanifest"; \
        DestDir: "{code:GetExtDir|21}"; \
        DestName: "extension.vsixmanifest"; \
        Check: Ssms21Found; Flags: ignoreversion

; ── SSMS 22 ──────────────────────────────────────────────────────────────
Source: "{#BuildDir}\*.dll";                                    DestDir: "{code:GetExtDir|22}"; Check: Ssms22Found; Flags: ignoreversion
Source: "{#BuildDir}\OpenHintSQL.pkgdef";                       DestDir: "{code:GetExtDir|22}"; Check: Ssms22Found; Flags: ignoreversion
Source: "{#BuildDir}\Config\snippets.json";                       DestDir: "{code:GetExtDir|22}\Config"; Check: Ssms22Found; Flags: ignoreversion
Source: "..\src\OpenHintSQL\source.extension.vsixmanifest"; \
        DestDir: "{code:GetExtDir|22}"; \
        DestName: "extension.vsixmanifest"; \
        Check: Ssms22Found; Flags: ignoreversion

; --------------------------------------------------------------------------
; [Code]
; --------------------------------------------------------------------------
[Code]

{ ── Per-version state ──────────────────────────────────────────────────── }

{ Each entry: [Major, SsmsRoot, found-bool].
  Populated by DetectAllVersions in InitializeSetup. }
var
  GSsmsRoot: array[0..4] of string;   { index 0=v18, 1=v19, 2=v20, 3=v21, 4=v22 }
  GSsmsFound: array[0..4] of Boolean;
  GFoundCount: Integer;

{ ── Path helpers ────────────────────────────────────────────────────────── }

function VersionIndex(const V: string): Integer;
begin
  case StrToIntDef(V, 0) of
    18: Result := 0;
    19: Result := 1;
    20: Result := 2;
    21: Result := 3;
    22: Result := 4;
  else  Result := -1;
  end;
end;

{ Called by [Files] DestDir entries: returns <SsmsRoot>\Extensions\OpenHintSQL }
function GetExtDir(Version: string): string;
var
  Idx: Integer;
begin
  Idx := VersionIndex(Version);
  if (Idx >= 0) and GSsmsFound[Idx] then
    Result := GSsmsRoot[Idx] + '\{#ExtSubdir}'
  else
    Result := ExpandConstant('{tmp}') + '\OpenHintSQL_unused';
end;

{ ── SSMS detection ──────────────────────────────────────────────────────── }

function FindSsmsRoot(Major: Integer; out Root: string): Boolean;
var
  MajorStr, DirName: string;
  PF86, PF64, PF86Release, PF64Release: string;
begin
  MajorStr := IntToStr(Major);
  DirName  := 'Microsoft SQL Server Management Studio ' + MajorStr + '\Common7\IDE';
  PF86     := ExpandConstant('{pf32}') + '\' + DirName;
  PF86Release := ExpandConstant('{pf32}') + '\Microsoft SQL Server Management Studio ' + MajorStr + '\Release\Common7\IDE';
  if IsWin64 then
  begin
    PF64 := ExpandConstant('{pf64}') + '\' + DirName;
    PF64Release := ExpandConstant('{pf64}') + '\Microsoft SQL Server Management Studio ' + MajorStr + '\Release\Common7\IDE';
  end
  else
  begin
    PF64 := PF86;
    PF64Release := PF86Release;
  end;

  { SSMS 18–20 install in Program Files (x86); 21–22 in Program Files.
    Check both paths — Microsoft may change this in a future release. }
  if FileExists(PF86 + '\Ssms.exe') then
    begin Root := PF86; Result := True; Exit; end;
  if FileExists(PF64 + '\Ssms.exe') then
    begin Root := PF64; Result := True; Exit; end;
  if FileExists(PF86Release + '\Ssms.exe') then
    begin Root := PF86Release; Result := True; Exit; end;
  if FileExists(PF64Release + '\Ssms.exe') then
    begin Root := PF64Release; Result := True; Exit; end;

  Root   := '';
  Result := False;
end;

procedure DetectAllVersions;
var
  Majors: array[0..4] of Integer;
  I: Integer;
begin
  Majors[0] := 18;
  Majors[1] := 19;
  Majors[2] := 20;
  Majors[3] := 21;
  Majors[4] := 22;

  GFoundCount := 0;
  for I := 0 to 4 do
  begin
    GSsmsFound[I] := FindSsmsRoot(Majors[I], GSsmsRoot[I]);
    if GSsmsFound[I] then
      GFoundCount := GFoundCount + 1;
  end;
end;

{ Per-version Check: functions used by [Files] entries }
function Ssms18Found: Boolean; begin Result := GSsmsFound[0]; end;
function Ssms19Found: Boolean; begin Result := GSsmsFound[1]; end;
function Ssms20Found: Boolean; begin Result := GSsmsFound[2]; end;
function Ssms21Found: Boolean; begin Result := GSsmsFound[3]; end;
function Ssms22Found: Boolean; begin Result := GSsmsFound[4]; end;

{ ── SSMS process management ─────────────────────────────────────────────── }

function IsSsmsRunning: Boolean;
var
  WbemLocator, WbemService, WbemObjectSet: Variant;
begin
  Result := False;
  try
    WbemLocator   := CreateOleObject('WbemScripting.SWbemLocator');
    WbemService   := WbemLocator.ConnectServer('', 'root\cimv2');
    WbemObjectSet := WbemService.ExecQuery(
        'SELECT ProcessId FROM Win32_Process WHERE Name="Ssms.exe"');
    Result := (WbemObjectSet.Count > 0);
  except
    Result := False;
  end;
end;

function TerminateSsms: Boolean;
var RC: Integer;
begin
  Result := Exec(ExpandConstant('{sys}') + '\cmd.exe',
                 '/C taskkill /F /IM Ssms.exe >nul 2>&1',
                 '', SW_HIDE, ewWaitUntilTerminated, RC);
  Sleep(2500);
end;

{ ── Cache clearing ───────────────────────────────────────────────────────── }

procedure ExecHidden(CommandLine: string);
var
  RC: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C ' + CommandLine, '', SW_HIDE, ewWaitUntilTerminated, RC);
end;

procedure ClearCacheRoot(Root: string);
begin
  if Root = '' then
    Exit;
  if not DirExists(Root) then
    Exit;

  if DirExists(Root + '\ComponentModelCache') then
    DelTree(Root + '\ComponentModelCache', True, True, True);

  if DirExists(Root + '\Extensions') then
  begin
    ExecHidden('del /F /Q "' + Root + '\Extensions\extensions.*.cache" >nul 2>&1');
    ExecHidden('del /F /Q "' + Root + '\Extensions\ExtensionMetadata*.mpack" >nul 2>&1');
    SaveStringToFile(Root + '\Extensions\extensions.configurationchanged', '', False);
  end;

  DeleteFile(Root + '\privateregistry.bin');
  ExecHidden('del /F /Q "' + Root + '\privateregistry.bin.LOG*" >nul 2>&1');
end;

procedure ClearCachesForVersion(Major: Integer);
var
  LocalAppData, IsoShell, SsmsRoot: string;
  FindRec: TFindRec;
begin
  LocalAppData := ExpandConstant('{localappdata}');
  IsoShell := LocalAppData +
              '\Microsoft\SQL Server Management Studio\' +
              IntToStr(Major) + '.0_IsoShell';

  ClearCacheRoot(IsoShell);

  SsmsRoot := LocalAppData + '\Microsoft\SSMS';
  if FindFirst(SsmsRoot + '\' + IntToStr(Major) + '.0_*', FindRec) then
  begin
    try
      repeat
        ClearCacheRoot(SsmsRoot + '\' + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure ClearAllCaches;
var
  Majors: array[0..4] of Integer;
  I: Integer;
begin
  Majors[0] := 18;  Majors[1] := 19;  Majors[2] := 20;
  Majors[3] := 21;  Majors[4] := 22;
  for I := 0 to 4 do
    ClearCachesForVersion(Majors[I]);

  { Also wipe OpenHintSQL's own disk schema cache }
  DelTree(ExpandConstant('{localappdata}') + '\OpenHintSQL\schemacache',
          True, True, True);
end;

{ ── Installer lifecycle ─────────────────────────────────────────────────── }

function CleanExistingExtensionDirs: string;
var
  I: Integer;
  ExtDir: string;
begin
  Result := '';

  for I := 0 to 4 do
  begin
    if GSsmsFound[I] then
    begin
      ExtDir := GSsmsRoot[I] + '\{#ExtSubdir}';
      if DirExists(ExtDir) then
      begin
        if not DelTree(ExtDir, True, True, True) then
        begin
          Result :=
            'Could not remove the existing OpenHint SQL installation from:'#13#10 +
            ExtDir + #13#10#13#10 +
            'Please close SSMS, check folder permissions, and run the installer again.';
          Exit;
        end;

        if DirExists(ExtDir) then
        begin
          Result :=
            'The existing OpenHint SQL installation folder still exists after cleanup:'#13#10 +
            ExtDir + #13#10#13#10 +
            'Please remove it manually, then run the installer again.';
          Exit;
        end;
      end;
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  FoundList: string;
  Majors: array[0..4] of Integer;
  I: Integer;
begin
  DetectAllVersions;

  if GFoundCount = 0 then
  begin
    MsgBox(
      'No supported SSMS installation was found on this computer.'#13#10#13#10 +
      'OpenHint SQL supports SSMS 18.12.1, 19.3, 20.2.1, 21.x, and 22.x.'#13#10 +
      'Please install one of those versions first.'#13#10#13#10 +
      'Download: https://aka.ms/ssmsfullsetup',
      mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  { Build a list of what will be installed for the welcome page }
  Majors[0]:=18; Majors[1]:=19; Majors[2]:=20; Majors[3]:=21; Majors[4]:=22;
  FoundList := '';
  for I := 0 to 4 do
    if GSsmsFound[I] then
      FoundList := FoundList + '  • SSMS ' + IntToStr(Majors[I]) +
                   '  (' + GSsmsRoot[I] + ')'#13#10;

  { Confirm with the user which installations will be updated }
  if MsgBox(
    'OpenHint SQL will be installed into the following SSMS version(s):'#13#10#13#10 +
    FoundList + #13#10 +
    'Any existing OpenHint SQL files in those extension folders will be removed first.'#13#10#13#10 +
    'Click OK to continue, or Cancel to exit.',
    mbInformation, MB_OKCANCEL) = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var Answer: Integer;
begin
  Result := '';
  NeedsRestart := False;

  if IsSsmsRunning then
  begin
    Answer := MsgBox(
      'SQL Server Management Studio is currently running.'#13#10#13#10 +
      'The installer needs to replace extension files that SSMS holds open.'#13#10#13#10 +
      'Click YES to close SSMS automatically.'#13#10 +
      'Click NO to cancel so you can close it manually.',
      mbConfirmation, MB_YESNO);

    if Answer = IDNO then
    begin
      Result := 'Please close SSMS and run the installer again.';
      Exit;
    end;

    if not TerminateSsms then
    begin
      Result := 'Could not close SSMS automatically. ' +
                'Please close it manually, then re-run the installer.';
    end;
  end;

  if Result = '' then
    Result := CleanExistingExtensionDirs;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ClearAllCaches;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExtDir: string;
  Majors: array[0..4] of Integer;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  { On uninstall, clean up extension dirs from all versions that were
    targeted at install time (they may or may not still exist). }
  Majors[0]:=18; Majors[1]:=19; Majors[2]:=20; Majors[3]:=21; Majors[4]:=22;
  for I := 0 to 4 do
  begin
    if FindSsmsRoot(Majors[I], ExtDir) then
    begin
      ExtDir := ExtDir + '\{#ExtSubdir}';
      if DirExists(ExtDir) then
        DelTree(ExtDir, True, True, True);
    end;
  end;

  ClearAllCaches;
end;
