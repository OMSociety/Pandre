# AGENTS.md — Pandre Agent 宪法

适用范围：本仓库全部目录；子目录另有 AGENTS.md 时，以更接近改动点的那份为准。
最后更新：2026-10-15

## 项目概览

- 一句话定位：盘多录 Pandre，百度网盘 + 夸克网盘双盘聚合的 Windows 桌面客户端，纯 C# 直连官方 Cookie 接口（无浏览器内核、无第三方服务器、不破解限速）。
- 技术栈：.NET 10（net10.0，需 .NET 10 SDK）/ Avalonia 12.1.4（Fluent 主题）/ CommunityToolkit.Mvvm 8.4.2 / QRCoder 1.8.0 / aria2（GPL-2.0 独立子进程，随仓库携带于 assets/windows/）。
- 文档索引：
  - 设计与接口规格：[docs/开发文档.md](./docs/开发文档.md)——两盘 API 逆向细节、扫码登录链路、下载链路、决策记录（§1）、UI 规范（§7）。**这是本项目的第一文档，改协议或 UI 前必读。**

## 常用命令

| 目的 | 命令 |
|---|---|
| 还原依赖 | `dotnet restore Pandre.slnx` |
| 构建（验收口径） | `dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release` |
| 本地运行 | `dotnet run --project src/Pandre.Desktop/Pandre.Desktop.csproj -c Release` |
| 测试 | 无单元测试工程；验收标准见下文「验收标准」 |

```bash
dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release
```

运行依赖：aria2c.exe 由 Pandre.Desktop.csproj 从 assets/windows/ 链接拷到输出目录，Aria2Engine 约定位置为 `<应用目录>/assets/windows/aria2c.exe`，找不到会抛 FileNotFoundException。凭据存放于 `%APPDATA%/Pandre/credentials/`（DPAPI 当前用户加密，删除该目录即登出）。

## 架构边界

```text
Pandre.Desktop (Avalonia MVVM) ──> Pandre.Download (aria2 队列)
        │                              │
        └────────────> Pandre.Core <───┘（仅依赖 IPanClient 接口）
                       (纯 HTTP：两盘客户端 / 扫码登录 / 凭据)
```

- 允许：Desktop 引用 Core 与 Download；Download 引用 Core 的 IPanClient/Models。
- 禁止：Core 引用 Avalonia 或任何 UI 库。原因：Core 是纯协议层，上传/转存/离线下载都落在这里，必须保持可单测、可复用。
- 禁止：Download 引用 BaiduPanClient / QuarkPanClient 具体类。原因：队列对两盘同构，只认 IPanClient，绑死具体类会破坏双盘对称。

## 修改契约

- 改网盘 API（src/Pandre.Core/Baidu/、src/Pandre.Core/Quark/）：先读 docs/开发文档.md 的接口规格（§3 百度、§4 夸克、§4b 百度分享实测）。所有参数均为抓包实测定死，**不得照常识增删改**；新结论实测后写回同一份文档。
- 改 UI 样式：只改 src/Pandre.Desktop/App.axaml 的 ThemeDictionaries 资源与控件样式；视图（Views/）内禁止硬编码颜色，一律 DynamicResource。规范细节（圆角、字号、卡片、指示条、禁用浮起位移）见 docs/开发文档.md §7。
- 改凭据存储（src/Pandre.Core/Credentials.cs）：必须保持 DPAPI 加密落盘；日志与异常消息禁止输出 Cookie 值。原因：Cookie 即账号等价物，明文落盘或进日志等于泄露账号。
- 改下载链路（src/Pandre.Download/）：aria2 JSON-RPC 全链路必须 `ConfigureAwait(false)`；完成后必须实测一次真实下载入队（见「已知风险区」）。
- 改启动流程（App.axaml.cs 的 InitSafelyAsync / MainViewModel.InitAsync / AppServices.StartDownloads）：改后必须实测一次真实下载入队。原因：登录页重构曾把当时的 AppServices.InitAsync（现已并入 MainViewModel.InitAsync / AppServices.StartDownloads）变成死代码，下载引擎从未启动而 UI 看似正常（提交 6edde4a）。
- 改图标或品牌图形：设计稿在 `assets/icons/`（`pandre-app.svg` 是 256 格坐标的应用标识，两个网盘图标是 24 格坐标），代码常量在 `src/Pandre.Desktop/Icons.Brand.cs`，窗口图标是 `src/Pandre.Desktop/Assets/pandre.ico`。三者是同一套图形的三种形态，改一处必须同步另两处，规格与底图约束见 docs/开发文档.md §7。原因：应用标识带 256 格坐标只能靠 Viewbox 缩放，品牌图标要与 `Icons.cs` 的 Fluent 图标混排，`.ico` 的帧格式混排是 Windows 取图的硬要求，各自漏改都会静默退化。
- 改主题色资源（App.axaml 的 `AccentColor` / `AccentBrush` / `NavSelectedBrush` / `OnAccentBrush`）：这四键在运行时由 `src/Pandre.Desktop/Services/AccentService.cs` 按 Windows 系统强调色覆盖，App.axaml 里的字面色只是读不到系统色时的回退；新增控件绑 `{DynamicResource AccentBrush}`，压在强调色实底上的前景用 `OnAccentBrush`，不要写死颜色。原因：强调色可变之后写死的前景色会失去对比度；且顶层资源键会遮蔽主题字典键，写到顶层就丢掉深浅两套色。
- 升级 Avalonia 版本：先对照新版 Fluent 主题源码核对 App.axaml 里覆盖的资源键（ComboBox*、Button*、Card* 系列均为内部键，绑定 12.1.4），键名漂移会静默失效。

## 禁止操作

- 禁止在 UI 线程同步等待异步调用（.Result / .GetAwaiter().GetResult()）。原因：Aria2Engine.CallAsync 未加 ConfigureAwait(false) 时被 UI 线程 GetResult() 死锁，主窗口永不显示。fire-and-forget 用 Task.Run。
- 禁止把 Transform 对象本身当 Avalonia 动画目标。原因：TransformAnimator 要求目标是 Visual，否则抛 InvalidCastException 闪退。正确写法见 MainWindow.axaml.cs 的 UpdateNavIndicator（Transitions 挂在 Border 上）。
- 禁止用 SystemDecorations=None 实现无边框。原因：会丢系统缩放与圆角。既定方案是 WindowDecorations="BorderOnly" + 自绘标题栏（MainWindow.axaml）。
- 禁止提交真实凭据，禁止在日志中打印 Cookie。原因：Cookie 即账号等价物，进仓库或日志等于公开账号。凭据只在 %APPDATA%/Pandre/credentials/，不进仓库。
- 禁止给行卡片（row-card）悬停加位移或阴影。原因：用户验收结论——浮起会被列表裁切，只保留背景变色（App.axaml）。

## 验收标准

改动完成 = 下列全部通过：

1. `dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release` 0 错误。
2. 手动跑通受影响流程（扫码登录 / 列目录 / 搜索 / 下载）；凡触及下载链路或启动流程，必须实测一次真实下载入队。
3. UI 改动：深色、浅色两主题各截图核对一次。

## 已知风险区

| 路径 | 风险 | 改动前后置动作 |
|---|---|---|
| src/Pandre.Core/Quark/QuarkPanClient.cs | file/download 有 UA 门：Web UA 对 ≥100MB 报 23018 download file size limit，必须用桌面客户端 UA；Cookie 缺 __puus 时 OSS 回调 403 require login [auth miss]，需先调 auth/pc/flush | 读 docs/开发文档.md §4 |
| src/Pandre.Core/Baidu/BaiduPanClient.cs | 分享创建走 share/pset（path_list 按路径、纯 Cookie），旧 share/set 会 errno=-3；文档规格在 docs/开发文档.md §4b | 改分享前跑 BaiduProbe 验证 |
| src/Pandre.Core/Baidu/BaiduQrLogin.cs | qrbdusslogin 响应是伪 JSON，bduss/stoken 须用正则从 session 字段抽取；Set-Cookie 里的 BDUSS 对 pan 接口无效 | 读 docs/开发文档.md §4 |
| src/Pandre.Desktop/Services/AppServices.cs | StartDownloads（历史名 InitAsync）曾因启动链断裂漏被调用，下载引擎从未启动且界面无报错 | 改启动流程后实测下载一次 |
| src/Pandre.Desktop/App.axaml | 控件观感靠覆盖 Avalonia Fluent 内部资源键实现，键名绑定 12.1.4 | 升级 Avalonia 前先对键名 |

## 出错怎么办

| 症状（报错里的可检索片段） | 处理 |
|---|---|
| 下载加入失败：aria2 引擎未启动 | AppServices.StartDownloads 未执行或引擎启动失败；检查 MainViewModel.InitAsync 与 AppServices.StartDownloads 的调用链（参见提交 6edde4a） |
| InvalidCastException: Unable to cast object of type 'Avalonia.Media.ScaleTransform' to type 'Avalonia.Visual' | 动画目标写成了 Transform 对象，见「禁止操作」第 2 条 |
| 夸克接口 23018 download file size limit | file/download 用了 Web 端 UA；换桌面客户端 UA，规格见 docs/开发文档.md §4 |
| 夸克下载 403 RequestDeniedByCallback: require login [auth miss] | Cookie 缺 __puus；登录后需调 auth/pc/flush 补发，见 docs/开发文档.md §4 |
| 百度接口 errno=-6 | bduss 无效或未登录；检查是否误用了 Set-Cookie 的 BDUSS（应用 qrbdusslogin 响应 session 里的值） |
| XamlLoadException: No precompiled XAML found for Pandre.Desktop.App | 改了 .axaml 后增量构建会跳过 XAML 编译错误并产出缺预编译 XAML 的程序集；先删 src/Pandre.Desktop/obj、bin 再全量重建，以全量构建的 AVLN 报错为准 |
| The character set provided in ContentType is invalid / 'utf8' is not a supported encoding name | 百度部分端点（api/create、share/cancel 等）把 Content-Type 写成 charset=utf8（无连字符），.NET 不认这个别名。已由 PanHttp.CreateClient 管道里的 CharsetFixHandler 统一归一；若在新客户端再遇到，说明该客户端没走 PanHttp.CreateClient |
| 转存报「分享页解析失败（locals.mset 未找到）」 | 这是已废弃的页面解析路径。转存元数据必须走 api/shorturlinfo + share/list（两者 shorturl 口径相反），规格见 docs/开发文档.md §4b |

## 维护

本文件与代码同 PR 更新。改动以下内容时必须同步本文件：验收命令、模块边界、风险区、禁止操作。
发现内容与代码不符时，先改本文件再继续改代码。
