; Cetus installer script — Inno Setup 6
; Compile: ISCC.exe Cetus.iss /DVersion=0.1.0
; Save as UTF-8 with BOM (Inno requirement for non-ASCII text).

#ifndef Version
  #define Version "0.1.0"
#endif

[Setup]
AppId={{18FD3179-CFBD-49CE-9E26-04A84A16756B}
AppName=Cetus 鲸鱼座
AppVersion={#Version}
AppVerName=Cetus 鲸鱼座 {#Version}
AppPublisher=Cetus
AppComments=DeepSeek Harness Windows 桌面壳
DefaultDirName={localappdata}\Cetus
DefaultGroupName=Cetus 鲸鱼座
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Cetus-Setup-{#Version}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Cetus.Desktop.exe
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{userprograms}\Cetus 鲸鱼座"; Filename: "{app}\Cetus.Desktop.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Cetus 鲸鱼座"; Filename: "{app}\Cetus.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Cetus.Desktop.exe"; Description: "启动 Cetus 鲸鱼座"; Flags: nowait postinstall skipifsilent

[Code]
// Ask to close a running Cetus (and its orphaned node sidecar) before install.
function IsCetusRunning(): Boolean;
begin
  Result := FindWindowByWindowName('Cetus · 鲸鱼座') <> 0;
end;

procedure KillCetusProcesses();
var
  ResultCode: Integer;
begin
  // The shell itself
  Exec('taskkill.exe', '/IM Cetus.Desktop.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Any orphaned sidecar node.exe left behind by a force-killed shell.
  // The command line always contains runtime\node.exe for Cetus sidecars,
  // so this never touches unrelated node processes.
  Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process | Where-Object {$_.CommandLine -like ''*runtime\node.exe*''} | ForEach-Object {Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
