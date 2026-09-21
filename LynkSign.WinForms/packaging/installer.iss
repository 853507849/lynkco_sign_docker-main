; ============================================================================
;  领克自动签到 · Windows 桌面版 —— Inno Setup 6 安装脚本
;
;  ⚠ 不要直接双击本文件编译。
;     请用 packaging\build-installer.ps1 调用：它会先做 dotnet 发布与凭据检查，
;     再把源码目录 / 输出目录 / 版本号通过 /D 传进来。
;
;  安装形态：
;     · PrivilegesRequired=lowest → 免管理员权限，{autopf} 落到
;       %LOCALAPPDATA%\Programs\LynkSignGUI，避免 UAC 弹窗；
;     · 目标机不需要预装 .NET（发布是自包含的）；
;     · 运行数据写在 %APPDATA%\LynkSign，卸载不会被删。
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\dist\LynkSignGUI-win-x64"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif
#ifndef MyIconFile
  #define MyIconFile "..\app.ico"
#endif

#define MyAppName "领克自动签到"
#define MyAppPublisher "LynkSign"
#define MyAppExeName "LynkSignGUI.exe"

[Setup]
; AppId 一旦发布就不能改，否则会被当成另一个软件、无法覆盖升级
AppId={{7E2A9C41-5D3B-4F88-9A10-2C6B4E8D1F73}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

DefaultDirName={autopf}\LynkSignGUI
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 免管理员
PrivilegesRequired=lowest

OutputDir={#MyOutputDir}
OutputBaseFilename=LynkSignGUI-Setup-{#MyAppVersion}
SetupIconFile={#MyIconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; 体积优先：桌面程序本体约 150MB，LZMA2 固实压缩后单个 setup 约 45-55MB
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; 覆盖升级时自动关掉正在运行的托盘程序
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自动启动（登录后静默运行在托盘）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 勾了「开机自动启动」就写 HKCU\...\Run（只写注册表，不弹窗口）
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-autostart"; Tasks: autostart; Flags: runhidden waituntilterminated
; 装完可直接运行
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先清掉自启注册表项，否则会留下一个指向不存在路径的启动项
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-autostart"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveAutostart"

[UninstallDelete]
; 程序目录整体清掉（数据在 %APPDATA%\LynkSign，不受影响）
Type: filesandordirs; Name: "{app}"
