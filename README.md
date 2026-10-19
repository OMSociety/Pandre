<div align="center">
  <img src="https://raw.githubusercontent.com/OMSociety/Pandre/master/assets/icons/pandre-app-icon.svg" alt="盘多录 Pandre" width="96">
  <h1>盘多录 Pandre</h1>
  <p>百度网盘与夸克网盘的 Windows 桌面客户端，把两个盘放进同一个窗口。</p>
  <p>纯 C# 直连官方 Cookie 接口，没有浏览器内核、没有中间服务器、不破解限速。<br>扫码登录，一处浏览、搜索、上传、下载两个网盘。</p>

  <p>
    <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET"></a>
    <a href="https://github.com/OMSociety/Pandre"><img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4" alt="Windows"></a>
    <a href="LICENSE"><img src="https://img.shields.io/github/license/OMSociety/Pandre?color=4f6ef7" alt="License"></a>
    <a href="https://github.com/OMSociety/Pandre/stargazers"><img src="https://img.shields.io/github/stars/OMSociety/Pandre?color=4f6ef7" alt="Stars"></a>
    <a href="https://github.com/OMSociety/Pandre/issues"><img src="https://img.shields.io/github/issues/OMSociety/Pandre?color=4f6ef7" alt="Issues"></a>
  </p>

<a href="#这是什么">这是什么</a> • <a href="#核心特性">核心特性</a> • <a href="#效果">效果</a> • <a href="#快速开始">快速开始</a> • <a href="#配置项">配置项</a> • <a href="#架构">架构</a> • <a href="#开发">开发</a> • <a href="#致谢与许可">致谢与许可</a>
</div>

## 这是什么

百度网盘与夸克网盘是两套互不相通的账号体系，各有一个功能臃肿的客户端。日常要在两个盘之间来回切换时，装两个客户端、切两次账号、看两套界面。

**盘多录**把这两条链路收进一个原生 Windows 窗口：左侧是网盘切换，右侧是同一个文件管理器，工具栏随当前盘的能力自动增减（离线下载只在百度出现）。它走的是两个网盘官方 Web 端自己在用的 HTTP 接口，登录靠手机 App 扫码，凭据用 Windows DPAPI 加密后存在本机。

范围上它只做「浏览与搬运」——列目录、搜索、上传、下载、转存、分享、删除。不做自动备份、不做多账号、不碰在线播放，也不提供秒传链接功能（上传走的是网盘自身的秒传判定，不需要用户填链接）。

## 核心特性

| 特性 | 说明 |
|---|---|
| **双盘扫码登录** | 百度、夸克各自扫码确认即登录；凭据经 Windows DPAPI 按当前用户加密，存于 `%APPDATA%\Pandre\credentials\`，不上传、不外发 |
| **一套文件管理器** | 两个盘共用同一套浏览、搜索、多选、上传、新建文件夹、重命名、删除交互；工具栏按当前盘能力增减 |
| **全盘搜索** | 不限当前目录，按官方搜索接口翻页取全。夸克按服务端返回的总数拉满，翻页超过保护上限会明确报错而不是静默截断 |
| **图片缩略图** | 列表内直接预览图片缩略图；抓取走独立管道、不携带登录 Cookie，失败进 60 秒负缓存防重试风暴 |
| **aria2 下载队列** | 随包携带 aria2，多线程分片，可暂停与继续；引擎意外退出会自动重启，进行中的任务转为出错态，重新添加同一链接即从断点续传。任务列表是内存态，应用重启后队列不恢复（已下载的分片文件保留） |
| **夸克上传断点续传** | 分片逐片记账，中断后按文件身份（路径 + 大小 + 修改时刻）认出上次会话接着传；记录只存服务端会话标识，不含凭据，7 天过期 |
| **转存与分享** | 粘贴分享链接（含提取码）存进自己盘；创建分享可选有效期与提取码，可列出与两步取消 |
| **离线下载** | 磁力、电驴链接交给百度云端拉取（仅百度支持），下载页可看云端任务状态 |
| **功能开关** | 转存、离线下载、分享管理三项可在设置里整体隐藏，界面按需收敛 |
| **原生 Windows 观感** | Fluent 风格、无边框圆角窗口，Mica / Acrylic / Aero / 无材质四档，跟随系统深浅色 |
| **托盘常驻** | 关闭按钮可设为「最小化到托盘」，应用在后台继续下载，从托盘唤回原窗口 |

## 效果

<table>
<tr>
<td width="50%" align="center"><img src="https://raw.githubusercontent.com/OMSociety/Pandre/master/docs/shots/baidu.png" alt="百度网盘页：文件列表与工具栏"></td>
<td width="50%" align="center"><img src="https://raw.githubusercontent.com/OMSociety/Pandre/master/docs/shots/quark.png" alt="夸克网盘页：同一套交互，离线下载按钮不出现"></td>
</tr>
<tr>
<td align="center">百度页：列表、路径、多选与全套工具栏</td>
<td align="center">夸克页：同一套交互；离线下载只在百度出现</td>
</tr>
</table>

<table>
<tr>
<td align="center"><img src="https://raw.githubusercontent.com/OMSociety/Pandre/master/docs/shots/settings.png" alt="设置页：个性化、功能开关与下载参数" width="640"></td>
</tr>
<tr>
<td align="center">设置页：主题与材质、功能开关、下载目录与并发参数</td>
</tr>
</table>

## 快速开始

项目**尚未发布二进制包**，当前只能从源码构建。以下命令在仓库根目录执行。

**1. 准备环境**

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（`global.json` 锁定 `10.0.400` 及以上，`rollForward: latestFeature`）。运行需要 .NET 10 桌面运行时。

```powershell
dotnet --version
```

**2. 构建**

```powershell
dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release
```

**3. 运行**

```powershell
src\Pandre.Desktop\bin\Release\net10.0\Pandre.Desktop.exe
```

**4. 登录两个网盘**

在百度页、夸克页分别点右上角「扫码登录」，用对应的手机 App 扫码确认。登录态过期时界面会明确提示重新扫码，不会静默失败。

**5. 开始搬运**

选中文件点「下载选中」进入下载队列；点「上传」选本地文件传到当前目录；拿到的分享链接点「转存」存进自己盘。

> **提示：** 下载落盘目录默认是 `%USERPROFILE%\Downloads\Pandre`，可在设置页改。「单任务连接数」与「同时下载数」由 aria2 在应用启动时固定，改后需重启应用生效。

> **提示：** `assets/windows/aria2c.exe` 必须与主程序保持这个相对位置——程序会先找程序目录旁的 `assets\windows\aria2c.exe`，找不到再退回 PATH。

## 配置项

设置存在 `%APPDATA%\Pandre\settings.json`，由设置页写入，不需要手改。下面是全部键与默认值：

```json
{
  "Theme": "system",
  "Material": "Mica",
  "DownloadDir": "C:\\Users\\<用户名>\\Downloads\\Pandre",
  "Aria2Connections": 16,
  "Aria2MaxConcurrent": 5,
  "ShowSaveShare": true,
  "ShowOfflineDownload": true,
  "ShowShareManage": true,
  "CloseBehavior": "tray"
}
```

缺失的键按默认值处理；JSON 写坏不会让应用起不来——会退回默认设置，并在窗口底部状态条报出原因。

### 外观

| 配置项 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `Theme` | 字符串 | `system` | `system` 跟随系统 / `light` 浅色 / `dark` 深色 |
| `Material` | 字符串 | `Mica` | 窗口背景材质：`Mica` / `Acrylic` / `Aero` / `None`。每个值都给一串候选，由系统取第一个支持的；`Mica` 在 Windows 10 上自动退到模糊或实底。旧值 `MicaAlt` 按 `Mica` 处理 |
| `CloseBehavior` | 字符串 | `tray` | 标题栏关闭按钮行为：`tray` 最小化到托盘 / `exit` 退出应用 |

### 下载

| 配置项 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `DownloadDir` | 字符串 | `%USERPROFILE%\Downloads\Pandre` | 下载文件落盘目录 |
| `Aria2Connections` | 整数 | `16` | 单任务连接数（1–16，aria2 硬上限 16），改后重启生效 |
| `Aria2MaxConcurrent` | 整数 | `5` | 同时下载任务数（1–20），改后重启生效 |

### 功能开关

三项都是全局开关，对百度与夸克同时生效，关掉后对应按钮从工具栏消失。

| 配置项 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `ShowSaveShare` | 布尔 | `true` | 转存：把外部分享链接保存到当前目录 |
| `ShowOfflineDownload` | 布尔 | `true` | 离线下载：云端拉取 magnet / ed2k（仅百度） |
| `ShowShareManage` | 布尔 | `true` | 分享管理：集中管理已创建的分享链接 |

### 数据落点

| 路径 | 内容 | 凭据 |
|---|---|---|
| `%APPDATA%\Pandre\settings.json` | 全部设置 | 无 |
| `%APPDATA%\Pandre\credentials\` | 登录凭据（DPAPI 加密） | 是 |
| `%APPDATA%\Pandre\upload-resume.json` | 夸克上传续传记录 | 否，仅服务端会话标识 |

退出登录或删除 `credentials` 目录即清除本机凭据。程序不写注册表、不加右键菜单、不开机自启。

## 架构

三个项目，依赖单向向下：

| 项目 | 职责 | 依赖 |
|---|---|---|
| `src/Pandre.Core` | 网盘协议层：接口抽象、两个盘的 HTTP 实现、模型、凭据存储 | 无第三方网盘 SDK |
| `src/Pandre.Download` | aria2 引擎进程管理与下载队列 | `Pandre.Core` |
| `src/Pandre.Desktop` | Avalonia 界面、视图模型、本地设置与文件 | 上面两者 |

`IPanClient` 是唯一的网盘契约：P1 级别的方法（列表、搜索、取下载链）必须实现，P2 级别的（上传、转存、分享、离线下载、移动、复制）带默认实现抛 `NotSupportedException`，由各客户端按实际能力覆盖。界面按钮的可见性据此判定——例如「离线下载」只对百度显示，而两个盘都实现了的方法（上传、转存、分享）则直接可用。

技术栈：.NET 10、Avalonia 12.1.4、CommunityToolkit.Mvvm、QRCoder（扫码二维码本地生成）。

## 开发

```powershell
# 构建
dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release

# 只构建协议层
dotnet build src/Pandre.Core/Pandre.Core.csproj -c Release
```

仓库没有单元测试工程，也没有 CI 配置；验收以实际运行两个网盘的真实接口为准，接口规格与逆向取证结论记在 [`docs/开发文档.md`](docs/开发文档.md)。

改动的工程约定与验收口径见 [`AGENTS.md`](AGENTS.md)。

## 路线图

- 多账号切换
- macOS / Linux（beta）
- 更新检查
- CI 三系统构建（win-x64 / osx-x64 + arm64 / linux-x64）
- 发布 GitHub Release

## 免责声明

本工具仅供学习与技术交流，不得用于任何违法违规用途。所用的接口均为网盘官方 Web / 桌面端自身使用的 HTTP 接口，未绕过任何加密与鉴权。请遵守百度、夸克的用户协议；使用本人账号操作存在被风控、限制或封禁的风险，自行权衡。本项目为独立工具，与百度、夸克公司无任何关联。账号限速、接口变动属官方行为，本项目不承诺下载速度。使用本工具产生的一切后果由使用者自行承担。

## 致谢与许可

接口签名与上传 / 转存链路参考 [BaiduPCS-Go](https://github.com/qjfoidnh/BaiduPCS-Go)；夸克转存 / 分享接口与 UA 门线索参考 [QuarkPanTool](https://github.com/ihmily/QuarkPanTool)（Apache-2.0）与 [QuarkPan](https://github.com/lich0821/QuarkPan)（MIT）；夸克预览参数参考 [OpenList](https://github.com/OpenListTeam/OpenList) / [AList](https://github.com/AlistGo/alist)（AGPL-3.0，仅参数设计，未复制代码）；交互与视觉质感参照 [Ghost Downloader 3](https://github.com/XiaoYouChR/Ghost-Downloader-3)。

下载引擎为 [aria2](https://github.com/aria2/aria2)（GPL-2.0，独立子进程，许可证文本随包附带）。界面基于 [Avalonia](https://github.com/AvaloniaUI/Avalonia) 与 [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)，二维码由 [QRCoder](https://github.com/codebude/QRCoder) 生成，图标取意于 [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons)。

本项目以 [GPL-3.0](LICENSE) 发布。
