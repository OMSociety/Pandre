# AGENTS.md — Pandre（盘多录）Agent 宪法

适用范围：本仓库全部目录；子目录另有 AGENTS.md 时，以更接近改动点的那份为准。
最后更新：2026-10-19

## 项目概览

- 一句话定位：盘多录 Pandre —— 百度网盘 + 夸克网盘双盘聚合的 Windows 桌面客户端，纯 C# 直连官方 Cookie 接口：无浏览器内核、无第三方服务器、不破解限速、绿色免安装，许可证 GPL-3.0。
- 技术栈：.NET 10（三项目均 `net10.0`，`Nullable=enable`）/ Avalonia 12.1.4 + Fluent 主题 / CommunityToolkit.Mvvm 8.4.2 / QRCoder 1.8.0 / Core 侧 `System.Security.Cryptography.ProtectedData` 10.0.12。下载引擎是随包携带的 `assets/windows/aria2c.exe`（GPL-2.0 独立子进程，许可证文本同目录）。
- 无单元测试工程、无 CI 配置：验收靠「构建 0 错误 + 真实链路实测」，口径见「验收标准」。
- 文档索引：
  - 协议与实现规格：[docs/开发文档.md](./docs/开发文档.md)——两盘接口逆向细节、扫码链路、下载链路、架构决策（§2）、工程约定（§5）、阶段计划 P0–P3（§6）、UI 实况与陷阱（§7）、许可证裁决（§8）。**改协议或改 UI 前必读。**

## 常用命令

所有 `dotnet` 命令都在**仓库内**的工作目录执行（原因见「禁止操作」第 1 条）。

| 目的 | 命令 |
|---|---|
| 构建（验收口径） | `dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release` |
| 还原依赖 | `dotnet restore Pandre.slnx` |
| 本地运行 | `dotnet run --project src/Pandre.Desktop/Pandre.Desktop.csproj -c Release` |
| 核当前生效的 SDK | `dotnet --version`（仓库内应为 10.0.4xx，即 `global.json` 锁的 10.0.400 + `latestFeature`；出 11.0.100-preview 说明 cwd 不对） |

```bash
dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release
```

产物在 `src/Pandre.Desktop/bin/Release/net10.0/`。aria2c 由 csproj 从 `assets/windows/` 拷进输出目录的 `assets/windows/`，改 `assets/` 后必须重新构建才生效。

判定构建结果要认**全量**构建：增量构建会跳过 XAML 编译错误（尤其改过 `.axaml` 时），必要时删掉 `src/Pandre.Desktop/obj`、`bin` 再构建，以全量构建的报错为准。

## 架构边界

```text
Pandre.Desktop (Avalonia MVVM：Views/ + ViewModels/ + Services/)
   ├──> Pandre.Core      (纯协议：IPanClient / 扫码登录 / 凭据 / HTTP)
   └──> Pandre.Download  (aria2 子进程 + 下载队列) ──> Pandre.Core（只用 IPanClient / Models）
```

- 允许：Desktop 引用 Core 与 Download；Download 引用 Core。
- 禁止：Core 引用 Avalonia 或任何 UI 库。原因：Core 是纯协议层，上传/转存/离线下载都落在这里，必须能脱离界面单独跑起来。
- 禁止：Download 引用 `BaiduPanClient` / `QuarkPanClient` 具体类。原因：队列对两盘同构，只认 `IPanClient`；绑死具体类会破坏双盘对称。

## 关键目录

| 我要… | 改这里 |
|---|---|
| 加或改网盘接口调用 | `src/Pandre.Core/Baidu/BaiduPanClient.cs`、`src/Pandre.Core/Quark/QuarkPanClient.cs`；抽象签名在 `src/Pandre.Core/IPanClient.cs` |
| 改 HTTP 管道、UA、Cookie 口径 | `src/Pandre.Core/PanHttp.cs`；所有请求都要经 `PanHttp.CreateClient` |
| 加界面 | `src/Pandre.Desktop/Views/*.axaml` + `ViewModels/*.cs`；全局样式与资源键只改 `App.axaml` |
| 挂网盘操作弹窗、加功能开关 | `src/Pandre.Desktop/Views/MainWindow.axaml`（窗口级 `FeatureOverlay` 与各操作弹窗）+ `ViewModels/FeaturePanelViewModel.cs`；不要挂到 `PanView` 级 |
| 改下载行为 | `src/Pandre.Download/Aria2Engine.cs`（进程与 RPC 参数）、`src/Pandre.Download/DownloadManager.cs`（队列语义、逐任务选项） |
| 改图标与品牌图形 | 设计稿 `assets/icons/`、代码常量 `src/Pandre.Desktop/Icons.Brand.cs`、多尺寸成品 `src/Pandre.Desktop/Assets/pandre.ico` |

## 数据流

`App.OnFrameworkInitializationCompleted` → `MainViewModel.InitAsync()`（`Task.Run(AppServices.StartDownloads)` 起 aria2，同时 `Login.InitAsync()` 读 `%APPDATA%/Pandre/credentials/` 并 `Attach`）→ 页面 `PanViewModel` 调 `IPanClient` → `DownloadManager.EnqueueAsync` → aria2 JSON-RPC → `PollLoopAsync` 每秒回刷任务状态。

## 修改契约

- 改或新增网盘 API（`src/Pandre.Core/Baidu/`、`src/Pandre.Core/Quark/`）：先读 [docs/开发文档.md](./docs/开发文档.md) §3/§4/§4b/§4c。端点与参数都是抓包/实测钉死的，**不得按常识增删改，新增端点同样要拿实测证据**；改完把新结论写回同一份文档。阶段划分（P0–P3）见该文档 §6。
- 在 `IPanClient` 加方法：给默认实现抛 `NotSupportedException`，由各盘按能力覆盖，否则会一次性打断另一盘（P2 起的方法全按这个口径加）。
- 改 UI 样式：只改 `src/Pandre.Desktop/App.axaml` 的 `ThemeDictionaries` 资源与控件样式；`Views/` 内颜色一律 `{DynamicResource ...}`（主题无关的 `Transparent` 例外：窗口底、拖动区等透明占位），不写字面色值（规范见 docs §7）。
- 改凭据存储（`src/Pandre.Core/Credentials.cs`）：Windows 必须保持 DPAPI（当前用户域）加密落盘；日志与异常消息不得输出 Cookie 值。
- 改下载链路（`src/Pandre.Download/`）：`await` 一律带 `ConfigureAwait(false)`；`EnqueueAsync` 的逐任务选项必须与引擎级同源（`_connections`），写死数值会让设置页的连接数对实际下载失效（历史上就写死过 16）。
- 改启动流程（`App.axaml.cs` 的 `InitSafelyAsync`、`MainViewModel.InitAsync`、`AppServices.StartDownloads`）：改完必须真跑一次应用并实测一次真实下载入队。原因：登录页重构曾让下载引擎的启动调用变成死代码，下载从未可用而界面看起来完全正常。
- 改强调色资源（`App.axaml` 的 `AccentColor` / `AccentBrush` / `NavSelectedBrush` / `OnAccentBrush`）：这四键运行期由 `src/Pandre.Desktop/Services/AccentService.cs` 按 Windows 系统强调色覆盖，必须写在两层 `ThemeDictionaries` 里——写到顶层会遮蔽主题字典键，深浅两套色就丢了。压在强调色实底上的前景用 `OnAccentBrush`，不写死黑白。
- 升级 Avalonia：先核对 `App.axaml` 覆盖的 Fluent 内部资源键（ComboBox / Button / Card 系列）。键名绑定 12.1.4，漂移会静默失效、不报错。
- 改图标：`assets/icons/pandre-app.svg`（256 格标记）、`assets/icons/pandre-app-hollow.svg`（同一标记的镂空版，设置页「关于」卡用）、`assets/icons/pandre-app-icon.svg`（48 格成品源，`.ico` 的 48 帧及以上由其渲出）、`assets/icons/pandre-app-icon-small.svg`（48 格满幅版，`.ico` 的 16/24/32 帧由其渲出，任务栏与托盘按这几帧渲染）、`assets/icons/*.flat.svg`（四份设计稿的填充展开稿，与 `Icons.Brand.cs` 常量逐字符相同）、`Assets/pandre.ico`（由两份成品源重渲 7 帧）是同一套图形的前后段，改一处必须同步其余；csproj 的 `ApplicationIcon` 与 MainWindow 的 `Icon` 两处都要设。

## 禁止操作

- 禁止在仓库外的工作目录跑 `dotnet` 命令。原因：`global.json` 是 dotnet 从当前目录逐级向上查找的，站在仓库外（如 `D:\WorkSpace`）找不到它，实测会改用本机的 11.0.100-preview SDK 并报 NETSDK1057，产物与验收环境不一致。
- 禁止在 UI 线程同步等待异步调用（`.Result` / `.GetAwaiter().GetResult()`）。原因：`Aria2Engine.CallAsync` 未加 `ConfigureAwait(false)` 时被 UI 线程 `GetResult()` 死锁，进程活着但主窗口永不显示。可被同步等待的库方法，内部 `await` 一律 `ConfigureAwait(false)`；确需 fire-and-forget 时用 `Task.Run` 起独立上下文并自己接异常。
- 禁止把 Transform 对象当 Avalonia 动画目标。原因：TransformAnimator 要求目标是 Visual，否则抛 `InvalidCastException` 闪退。写法见 `MainWindow.axaml.cs` 的 `UpdateNavIndicator`（Transitions 挂在 Border 上）。
- 禁止用 `SystemDecorations="None"` 实现无边框。原因：会丢系统缩放与圆角；既定方案是 `WindowDecorations="BorderOnly"` + 自绘标题栏。
- 禁止提交真实 Cookie 或令牌、禁止在日志与界面输出 Cookie 值。原因：Cookie 即账号等价物，进仓库或日志等于公开账号。凭据只在 `%APPDATA%/Pandre/credentials/`，不进仓库。
- 禁止写注册表、加右键菜单、进「此电脑」、开机自启，禁止引入任何统计或上报 SDK。原因：绿色免安装与零遥测是设计规范（docs §5），不是可选项。
- 禁止复制 AGPL 项目（OpenList / AList）的代码。原因：会传染本项目许可证；许可裁决表见 docs §8。参考参数设计可以，复制别家代码前先核许可证。
- 禁止删掉 README 的「致谢」与「License」两节。原因：MIT / Apache-2.0 项目允许参考但要求署名与许可声明，删掉即违反许可义务；裁决表见 docs §8。
- 禁止给行卡片（`Border.row-card`，样式定义在 `src/Pandre.Desktop/App.axaml`，被 `PanView` / `DownloadsView` / `MainWindow` 的行列表复用）的悬停加位移或阴影。原因：用户验收结论——浮起会被列表裁切，只保留背景变色。

## 验收标准

改动完成 = 下列全部通过：

1. 在仓库内做一次**全量**构建 `dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release`，0 错误 0 警告；增量构建的结果不作数（见「出错怎么办」）。
2. 真跑一次应用，走通受影响的流程：登录 / 列目录 / 搜索 / 下载 / 文件管理（移动·复制·重命名·删除）/ 转存 / 分享 / 上传，按改动面至少走通对应的一条。
3. 凡触及下载链路、启动流程：必须实测一次真实入队，只看界面不算；动夸克取链时用一个 ≥100MB 的文件，UA 门只在 ≥100MB 触发（100MB 整这一边界值未实测，见 docs §4）。
4. UI 改动：深色、浅色两主题各截图核对一次。运行期两个验收开关：`PANDRE_AUTOCHECK=3` 启动后直跳某页（0 百度 / 1 夸克 / 2 下载 / 3 设置），`PANDRE_LOGINPANEL=1` 强制弹开扫码弹窗。
5. 需要真实账号或手工操作的步骤（扫码登录、真实入队）必须由用户配合完成，不能只凭构建与界面就报通过。

收尾：强制结束应用进程不会带走 `aria2c` 子进程（只有正常退出路径的 `desktop.Exit` 才收尾），用强杀收尾时顺手结束残留的 `aria2c`。

## 提交与协作

- Commit：单行中文摘要，格式 `模块：改动`；模块用 `Core（百度）` / `Core（夸克）` / `Download` / `Desktop` / `docs` / `工程`。一次提交只写一个主模块，跨模块改动归入主责模块或拆成多次提交。历史提交里另有 `fix:` / `修复：` / `扩展：` / `浏览器接管：` / `P1 MVP:` 等旧形态前缀，这些不再沿用。
- 行尾由 `.gitattributes` 强制：`*` text=auto 兜底；显式规则 `.cs` / `.axaml` / `.md` / `.json` / `.yml` / `.gitattributes` 用 LF，`.csproj` / `.slnx` 用 CRLF，`ico` / `png` / `jpg` / `exe` / `dll` 标记 binary。不要为了迁就编辑器去改这份文件。

## 已知风险区

下列是改动时容易踩的坑，不等于当前故障（这几条链路的最新实测结论以 docs 为准）。

| 路径 | 风险 | 改动前置 |
|---|---|---|
| src/Pandre.Core/Quark/QuarkPanClient.cs | file/download 有 UA 门：网页 UA 对 ≥100MB 报 `23018 download file size limit`；Cookie 缺 `__puus` 时 OSS 回调 403 `require login [auth miss]`，须先调 `auth/pc/flush` 补发 | 读 docs §4 再改 |
| src/Pandre.Core/Baidu/BaiduPanClient.cs | 分享链三个 URL 口径易错：`share/verify` 的 `surl` 与 `share/list` 的 `shorturl` 都要去掉首字符 1，`api/shorturlinfo` 的 `shorturl` 要带首字符 1；`verify` 与 `transfer` 都必须带分享页 Referer | 读 docs §4b 再改 |
| src/Pandre.Core/Baidu/BaiduQrLogin.cs | `qrbdusslogin` 响应是伪 JSON，bduss 与 stoken 只能用正则从 `session` 字段抽；`Set-Cookie` 里的 BDUSS 对 pan 接口无效 | 读 docs §3 再改 |
| src/Pandre.Core/PanHttp.cs | 百度若干端点把 Content-Type 写成 `charset=utf8`（无连字符），.NET 不认这个别名；归一在 `CharsetFixHandler` 里 | 新增任何 HttpClient 前先确认走了 `PanHttp.CreateClient` |
| src/Pandre.Desktop/App.axaml | 控件观感靠覆盖 Avalonia Fluent 内部资源键实现，键名绑定 12.1.4 | 升级 Avalonia 前先对键名 |
| src/Pandre.Desktop/Services/AppServices.cs | `StartDownloads`（历史名 `InitAsync`）曾因启动链断裂漏被调用，下载引擎从未启动而界面无报错 | 改启动流程后真跑应用并实测入队 |

## 出错怎么办

| 症状（可检索片段） | 处理 |
|---|---|
| `XamlLoadException: No precompiled XAML found for Pandre.Desktop.App` | 改了 `.axaml` 而增量构建跳过了 XAML 编译错误（例如 axaml 里有 `PointerPressed="X"` 但 code-behind 没有 `X`，只在全量构建报 AVLN3000）。删掉 `src/Pandre.Desktop/obj`、`bin` 再全量重建，以全量构建的报错为准 |
| `NETSDK1057` 提示正在使用 .NET 预览版 | cwd 不在仓库内，`global.json` 没生效；回到仓库根再跑 dotnet |
| 下载加入失败：aria2 引擎未启动，或 `找不到 aria2c.exe` | 检查 `MainViewModel.InitAsync` → `AppServices.StartDownloads` 调用链，以及输出目录里有没有 `assets/windows/aria2c.exe`（由 csproj 从仓库 `assets/` 拷贝） |
| `InvalidCastException` 提到 `ScaleTransform` 与 `Avalonia.Visual` | 动画目标写成了 Transform 对象，见「禁止操作」 |
| 夸克 `23018 download file size limit` | `file/download` 用了网页 UA；换 `PanHttp.QuarkDesktopUa` |
| 夸克 403 `RequestDeniedByCallback: require login [auth miss]` | Cookie 缺 `__puus`；登录后须调 `auth/pc/flush` 补发 |
| 百度接口 `errno=-6` | BDUSS 无效或未登录；核对用的是 `qrbdusslogin` 响应 `session` 里的值，不是 `Set-Cookie` |
| `'utf8' is not a supported encoding name` | 新客户端没走 `PanHttp.CreateClient`（charset 归一的管道在那里） |
| 转存报「分享页解析失败（locals.mset 未找到）」 | 这是已废弃的页面解析路径；转存元数据必须走 `api/shorturlinfo` + `share/list`，规格见 docs §4b |

## 维护

本文件与代码在同一次提交里更新。分工：协议与接口结论写 [docs/开发文档.md](./docs/开发文档.md)，改动面、模块边界、风险区、禁止操作写本文件。
必须同步本文件的情形：验收命令、模块边界、风险区、禁止操作发生变化。
发现内容与代码不符时，先改本文件再继续改代码。
