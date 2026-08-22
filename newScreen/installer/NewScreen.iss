; NewScreen Inno Setup 스크립트
; 컴파일: installer\build-installer.ps1 (publish 먼저 필요)

#define MyAppName "NewScreen"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "ScreenEditor"
#define MyAppExeName "NewScreen.exe"

[Setup]
; 조상 프로그램 ScreenEditor와 <b>다른</b> AppId다 — 같으면 서로를 업그레이드로 인식해 덮어쓴다.
AppId={{3401B117-E7C6-4893-95A3-76890D3A21F9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 자체 포함(self-contained) x64 빌드
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows.Graphics.Capture(WGC)가 Windows 10 2004(빌드 19041) 이상을 요구한다.
; 여기서 막지 않으면 설치는 되고 캡처 시작에서야 실패한다.
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=NewScreen-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 자체 포함 publish 폴더 전체를 설치 (.NET 런타임 포함 — 별도 설치 불필요)
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
