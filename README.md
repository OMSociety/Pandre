# 盘多录 Pandre

百度网盘 + 夸克网盘双盘聚合的 Windows 桌面客户端：扫码登录，一处浏览、搜索、下载两个网盘。

纯 C# 直连官方 Cookie 接口——无浏览器内核、无第三方服务器、不破解限速。

## 功能（P1）

- 双盘扫码登录：App 扫码确认即登录，凭据本机 DPAPI 加密存储
- 双盘文件浏览、全盘搜索、图片缩略图
- aria2 下载队列：多线程 + 断点续传，夸克已解开桌面端大文件下载链
- Fluent / Win11 风格界面：Mica 材质、深浅色主题、无边框圆角窗口

## 功能（P2）

- 上传：夸克五段链（预创建 → 增量哈希 → 分片 PUT → 合并 → 完成）、百度预创建 / 分片 / 合并
- 转存：粘贴分享链接（含提取码）存进自己盘，百度与夸克各一套链路
- 分享管理：创建（有效期可选）、列出、两步取消
- 离线下载：磁力 / 电驴链接交给云端拉取（仅百度），下载页可看云端任务状态
- 文件管理：新建文件夹、重命名、移动、复制、删除

## 运行

需要 .NET 10 桌面运行时；aria2c.exe 已随包携带（须与主程序的相对位置保持 `assets/windows/aria2c.exe`）。绿色免安装：不写注册表、不加右键菜单、不开机自启，删除目录即卸载。退出登录或删除 `%APPDATA%/Pandre/credentials/` 即清除本机凭据。

首次使用在两个网盘页分别点右上角「扫码登录」，用百度 / 夸克 App 扫码确认。

## 构建

```bash
dotnet build src/Pandre.Desktop/Pandre.Desktop.csproj -c Release
```

需要 .NET 10 SDK。

## 路线图

- P3：macOS / Linux（beta）、更新检查、托盘

## 致谢

- [BaiduPCS-Go](https://github.com/qjfoidnh/BaiduPCS-Go)：百度接口签名与上传 / 转存链路参考
- [QuarkPanTool](https://github.com/ihmily/QuarkPanTool)：夸克转存/分享接口与 UA 门线索（Apache-2.0）
- [QuarkPan](https://github.com/lich0821/QuarkPan)：夸克上传与分享转存接口参考（MIT）
- [Ghost Downloader 3](https://github.com/XiaoYouChR/Ghost-Downloader-3)：交互与视觉质感的参照
- [aria2](https://github.com/aria2/aria2)：下载引擎（GPL-2.0 独立子进程，许可证文本随包附带）
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) · [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) · [QRCoder](https://github.com/codebude/QRCoder) · [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons)

## 免责声明

本工具仅供学习与技术交流，不得用于任何违法违规用途。接口均为网盘官方 Web / 桌面端自身使用的 HTTP 接口，未绕过任何加密与鉴权；请遵守百度、夸克的用户协议。账号限速、接口变动属官方行为，本项目不承诺下载速度。使用本工具产生的一切后果由使用者自行承担。

## License

[GPL-3.0](LICENSE)
