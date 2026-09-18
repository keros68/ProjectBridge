; ProjectBridge 安装程序。由 scripts\publish.ps1 调用：ISCC /DAppVersion=x.y.z /DSourceDir=... /DOutputDir=...
; 安装到当前用户目录，无需管理员权限；项目与连接设置在 %LOCALAPPDATA%\LocalProjectBridge，卸载时保留。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ProjectBridge"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6F4C2B1E-8D3A-4E5B-9C7F-2A1D0E9B8C64}
AppName=ProjectBridge
AppVersion={#AppVersion}
AppVerName=ProjectBridge {#AppVersion}
AppPublisher=keros68
AppPublisherURL=https://github.com/keros68/ProjectBridge
AppUpdatesURL=https://github.com/keros68/ProjectBridge/releases/latest
DefaultDirName={localappdata}\Programs\ProjectBridge
DefaultGroupName=ProjectBridge
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ProjectBridge-Setup
SetupIconFile=..\src\LocalProjectBridge\Assets\ProjectBridge.ico
UninstallDisplayIcon={app}\ProjectBridge.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; 覆盖安装前清掉旧版内置组件，避免残留文件与新版本混用。
Type: filesandordirs; Name: "{app}\backends"
Type: filesandordirs; Name: "{app}\runtime"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ProjectBridge"; Filename: "{app}\ProjectBridge.exe"
Name: "{group}\{cm:UninstallProgram,ProjectBridge}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ProjectBridge"; Filename: "{app}\ProjectBridge.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ProjectBridge.exe"; Description: "{cm:LaunchProgram,ProjectBridge}"; Flags: nowait postinstall skipifsilent
