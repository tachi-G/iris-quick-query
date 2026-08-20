#define MyAppName "IRIS 元素化快捷查询"
#define MyAppVersion "1.0.19"
#define MyAppPublisher "Hospital Operations"
#define MyAppExeName "IrisQuickQuery.exe"
#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{C7428D6E-0D2F-4B92-A2A6-F9B7D3774369}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\IrisQuickQuery
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=IrisQuickQuery-Setup-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\IrisQuickQuery.App\Assets\iris-app-icon.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 仅覆盖程序文件。连接配置与 DPAPI 密码保存在 LocalAppData\IrisQuickQuery，升级和卸载均不删除。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('此版本仅支持 Windows 10/11 64 位系统。', mbError, MB_OK);
    Result := False;
  end;
end;
