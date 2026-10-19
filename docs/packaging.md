# 打包与发布

本仓库没有 CI，发版靠本地构建 + 上传 Release 资产。本文是把源码变成可分发产物的唯一入口，版本号以 `src/*/*.csproj` 的 `<Version>` 为准。

## 产物形态

| 产物 | 分发形态 | 内容 |
|---|---|---|
| `Pandre-<版本>-portable-win-x64.zip` | 便携版：解压即用 | 单文件 `Pandre.Desktop.exe`（自包含 .NET 运行时）+ `assets/windows/aria2c.exe` 与 `aria2-COPYING.txt` |
| `Pandre-<版本>-setup-win-x64.exe` | 安装包（Inno Setup） | 完整自包含运行时目录树，默认装到 `%LOCALAPPDATA%\Programs\Pandre`，不请求管理员权限 |

两者都是 win-x64 自包含发布，用户无需预装 .NET 10 桌面运行时。

## 环境

- .NET 10 SDK（`global.json` 锁定，必须在仓库内执行）
- Inno Setup 6：`winget install --id JRSoftware.InnoSetup`，装完编译器在 `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`（winget 按用户级安装，不在 `C:\Program Files (x86)`）。安装器界面的简体中文取自 `build/isl/ChineseSimplified.isl`（Inno Setup 官方源只带常驻语言包，该文件随仓库版本化，离线可编；来源与维护者见该文件头部注释）。

## 打一次包

全程在仓库根执行，产物落在仓库外（暂存区），不进版本库。

```powershell
cd D:\WorkSpace\Github\Pandre
$ver = (Select-Xml -Path src\Pandre.Desktop\Pandre.Desktop.csproj -XPath '//Version').Node.InnerText
$bench = "D:\WorkSpace\WorkBench\pandre-$ver"
Remove-Item $bench -Recurse -Force -ErrorAction SilentlyContinue

# 1) 分散发布：安装包用，也是验收跑的那一份
dotnet publish src/Pandre.Desktop/Pandre.Desktop.csproj -c Release -r win-x64 --self-contained true `
  -p:DebugType=None -p:DebugSymbols=false -o "$bench\dist"

# 2) 单文件发布：便携版用（原生库自解压到临时目录）
dotnet publish src/Pandre.Desktop/Pandre.Desktop.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$bench\single"

# 3) SkiaSharp / HarfBuzz 的 pdb 不受 DebugType 控制，逐包单独剔除
Get-ChildItem "$bench\dist","$bench\single" -Recurse -Filter *.pdb | Remove-Item -Force

# 4) 便携版：单个 exe + aria2 引擎与许可证文本（引擎必须在 exe 旁，不能被打进包里）
New-Item -ItemType Directory -Force "$bench\portable\assets\windows" | Out-Null
Copy-Item "$bench\single\Pandre.Desktop.exe" "$bench\portable\"
Copy-Item "$bench\single\assets\windows\aria2c.exe","$bench\single\assets\windows\aria2-COPYING.txt" "$bench\portable\assets\windows\"
Compress-Archive -Path "$bench\portable\*" -DestinationPath "$bench\Pandre-$ver-portable-win-x64.zip" -CompressionLevel Optimal

# 5) 安装包（版本号由 csproj 传给安装器脚本，脚本内不得另写一份）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "/DDistDir=$bench\dist" "/DOutDir=$bench\setup" "/DAppVersion=$ver" build\pandre.iss
```

产物清单：`$bench\Pandre-$ver-portable-win-x64.zip`、`$bench\setup\Pandre-$ver-setup-win-x64.exe`。

## 包内必须核对

- `dist\assets\windows\aria2c.exe` 与 `aria2-COPYING.txt` 都在。前者缺了应用能启动、但一点下载就报「找不到 aria2c.exe」；后者是 GPL-2.0 的随包分发义务，缺了即违反许可证。
- `LICENSE`（GPL-3.0）在安装包里由 Inno Setup 的许可证页呈现，便携版不含该文件——README 的 License 链接指向仓库。
- 单文件 exe 的属性里 `文件版本` 应是 `1.0.0.0`、`产品名称` 是「盘多录 Pandre」。版本号来自 csproj，改版本只改那一处。

## 发布顺序

版本号与 CHANGELOG 属于同一次发布：先提交、再打 tag、后建 Release，别把 tag 当草稿挪来挪去。

```powershell
cd D:\WorkSpace\Github\Pandre
git add -A; git commit -m "发版：1.0.0"
git tag -a v1.0.0 -m "盘多录 Pandre 1.0.0"
git push origin master v1.0.0
gh release create v1.0.0 --title "盘多录 Pandre 1.0.0" --notes-file <发布说明.md> `
  "$bench\Pandre-1.0.0-portable-win-x64.zip" "$bench\setup\Pandre-1.0.0-setup-win-x64.exe"
```

传完对账：Release 资产数量、`size` 与本地 `Get-FileHash -Algorithm SHA256` 三方一致才算完成，别只看「上传成功」。

## 当前限制

- 只有 win-x64。README 路线图里的 macOS / Linux beta 与三系统 CI 都还没有落地，别把这两个产物读成跨平台支持。
- 自包含发布的体积主要来自 .NET 运行时与 SkiaSharp：单文件 exe 约 48 MB，安装包解压后约 113 MB。
