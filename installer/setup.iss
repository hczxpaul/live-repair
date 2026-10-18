; LivePhoto 修复器 —— Inno Setup 安装包脚本
; 前置：安装 Inno Setup 6（https://jrsoftware.org/isinfo.php）
; 编译： ISCC.exe installer\setup.iss
; 产物： installer\LivePhotoRepair-Setup.exe

#define MyAppName     "LivePhoto 修复器"
#define MyAppVersion  "1.0.0"
#define MyAppExeName  "LivePhoto.App.exe"
#define MyAppId       "LivePhotoRepair-6F2A-4C31-9D5E-8B7A1C0D4E22"

[Setup]
AppId={{#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=LivePhoto 修复器
DefaultDirName={autopf}\LivePhotoRepair
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
OutputDir=installer
OutputBaseFilename=LivePhotoRepair-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Uninstallable=yes
UninstallDisplayName={#MyAppName} {#MyAppVersion}
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 用自包含版产物 —— 目标机无需预先安装 .NET 运行时
Source: "..\publish\standalone\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "安装完成后立即运行"; Flags: nowait postinstall skipifsilent
