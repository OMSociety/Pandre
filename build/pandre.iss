; 盘多录 Pandre 安装包脚本（Inno Setup 6）
; 用法见 docs/packaging.md：先发布出 win-x64 目录，再用 ISCC.exe 编译，三个 #define 都由命令行传入。
; 版本号不从本文件取，只认 src/Pandre.Desktop/Pandre.Desktop.csproj 的 <Version>，避免两处版本漂移。

#ifndef DistDir
  #error 必须用 /DDistDir=<win-x64 发布目录> 指定源目录
#endif
#ifndef OutDir
  #error 必须用 /DOutDir=<安装包输出目录> 指定输出目录
#endif
#ifndef AppVersion
  #error 必须用 /DAppVersion=<版本号> 指定版本，取值同 csproj 的 <Version>
#endif

#define AppName "盘多录 Pandre"
#define AppPublisher "OMSociety"
#define AppUrl "https://github.com/OMSociety/Pandre"
#define AppExe "Pandre.Desktop.exe"

[Setup]
; AppId 是安装位置与升级识别键，一次定下之后不得再改，否则升级会装成第二份
AppId={{F65CEACF-37B0-4B3E-9AB6-6C84F3813756}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\Pandre
; 绿色免安装是本项目设计规范：装到用户目录，安装器不请求管理员权限
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutDir}
OutputBaseFilename=Pandre-{#AppVersion}-setup-win-x64
SetupIconFile=..\src\Pandre.Desktop\Assets\pandre.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
; 官方安装器只带常驻语言包，简体中文取自 issrc 仓库的 Files/Languages/ChineseSimplified.isl，随仓库版本化
Name: "chinesesimplified"; MessagesFile: "isl\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即运行{#AppName}"; Flags: nowait postinstall skipifsilent
