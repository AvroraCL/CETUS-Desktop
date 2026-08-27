; Cetus installer script — Inno Setup 6 (NET 10 build)
; Compile: ISCC.exe Cetus.iss /DVersion=0.1.9 /DFileVersion=0.0.1.9
; Save as UTF-8 with BOM (Inno requirement for non-ASCII text).

#ifndef Version
  #define Version "0.1.9"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.1.9"
#endif
#ifndef AppSourceDir
  #define AppSourceDir "..\dist\app-" + Version
#endif

[Setup]
AppId={{588C7C05-5114-479B-90D3-0FB5829FB0EF}
AppName=CETUS鲸鱼座
AppVersion={#Version}
AppVerName=CETUS鲸鱼座 {#Version}
AppPublisher=AvroraCL
AppComments=DeepSeek Harness Windows 桌面壳
DefaultDirName={localappdata}\Cetus
DefaultGroupName=CETUS鲸鱼座
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Cetus-Setup-{#Version}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Cetus.exe
SetupIconFile=..\src\Cetus.Desktop\Assets\cetus.ico
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#FileVersion}
VersionInfoProductName=CETUS鲸鱼座
VersionInfoProductVersion={#Version}
VersionInfoCopyright=AvroraCL

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{userprograms}\Cetus 鲸鱼座"; Filename: "{app}\Cetus.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Cetus 鲸鱼座"; Filename: "{app}\Cetus.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Cetus.exe"; Description: "启动 Cetus 鲸鱼座"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Legacy WebView2 default profile location (pre-0.1.0 installs wrote it next to the exe).
Type: filesandordirs; Name: "{app}\Cetus.exe.WebView2"
; Current per-user state lives below {app} because the application itself is
; installed in %LOCALAPPDATA%\Cetus. Remove it on a full uninstall.
Type: filesandordirs; Name: "{app}\WebView2"
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\settings.json"

[Code]
// Ask to close only the Cetus installed in {app}, plus its own node sidecar.
// Matching on the executable path avoids terminating a portable copy or a
// separate Cetus installation that happens to use the same process name.
function QuotePowerShellString(Value: String): String;
begin
  StringChangeEx(Value, '''', '''''', True);
  Result := '''' + Value + '''';
end;

function MatchingProcessQuery(const TargetPath: String): String;
begin
  Result := 'Get-CimInstance Win32_Process | Where-Object {' +
    '$_.ExecutablePath -and [string]::Equals($_.ExecutablePath, ' +
    QuotePowerShellString(TargetPath) +
    ', [System.StringComparison]::OrdinalIgnoreCase)}';
end;

function IsProcessRunningAtPath(const TargetPath: String): Boolean;
var
  ResultCode: Integer;
  Params: String;
begin
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$matches = @(' + MatchingProcessQuery(TargetPath) + '); ' +
    'if ($matches.Count -gt 0) { exit 0 } else { exit 1 }"';
  Result := Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function IsCetusRunning(): Boolean;
begin
  Result := IsProcessRunningAtPath(ExpandConstant('{app}\Cetus.exe'));
end;

procedure StopProcessesAtPath(const TargetPath: String);
var
  ResultCode: Integer;
  Params: String;
begin
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    MatchingProcessQuery(TargetPath) + ' | ' +
    'ForEach-Object {Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}"';
  Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure KillCetusProcesses();
begin
  StopProcessesAtPath(ExpandConstant('{app}\Cetus.exe'));
  // Any orphaned sidecar left behind by a force-killed shell is matched by its
  // executable path too, so unrelated Node.js processes are never touched.
  StopProcessesAtPath(ExpandConstant('{app}\runtime\node.exe'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsCetusRunning() then
  begin
    if MsgBox('Cetus 正在运行。安装需要先关闭它（及其后端进程），是否继续？',
              mbConfirmation, MB_YESNO) = IDYES then
      KillCetusProcesses()
    else
      Result := '请先关闭 Cetus，再重新运行安装程序。';
  end;
end;
