# 用量监控托盘（C# 版）

Python 版 `trae-usage-tray` 的 C#/.NET 8 重构版本，功能与 Python 版完全对齐：

| 能力 | 实现 |
|---|---|
| 系统托盘 | WinForms `NotifyIcon`（左键打开面板 / 右键菜单 / 图标随用量变色） |
| TRAE 企业版 | `HttpClient` 直连三个内部接口；Cookie 失效自动经内置浏览器重登并回写 config.json |
| 火山方舟 AFP | `ClientWebSocket` 手写 CDP 客户端；登录 cookie 持久化 + `Network.setCookie` 注入，重启免登录 |
| Web 面板 | `HttpListener`（仅 localhost）+ Liquid Glass 液态玻璃单页（详情卡片/30 天曲线/配置/明暗主题） |
| 通知 | `ShowBalloonTip` 阈值提醒（默认 85%） |
| 历史 | `history.csv` 每日快照 |
| 其他 | 并行刷新、config.json 变更监视自动重载、Chrome 空闲自动退出、单实例互斥 |

## 构建 / 发布

需要 .NET 8 SDK（本项目使用用户级安装：`%LOCALAPPDATA%\Microsoft\dotnet`）：

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

产物：`publish\TRAE Usage.exe`（自包含，无需目标机安装 .NET）。

## 运行

`publish\` 目录需要 `config.json`（与 Python 版格式完全兼容，可直接复制）。
可选复制 `ark-cookies.json` / `trae-cookies.json`（Python 版的登录态快照）——
方舟源通过 cookie 注入即可免登录复用。

## 与 Python 版的差异

- 单文件 exe 无需 Python 环境与 PyInstaller
- 原生托盘实现（NotifyIcon 代替 pystray），阈值提醒用系统 Balloon 通知
- CDP 为手写 `ClientWebSocket` 客户端（协议层一致：Runtime.evaluate / Network.getCookies / Network.setCookie）
- 配置字段、接口路径、Web 面板样式与 Python 版一一对应，二者可互换使用
  （注意不要同时运行：CDP 端口 9223/9224 与面板端口 9876 会冲突）

## 代码结构

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口、单实例互斥 |
| `Core.cs` | 路径/配置模型/历史 CSV/圆环图标/格式化/日志 |
| `Cdp.cs` | CDPTab（WebSocket）、ChromeManager（进程/tab/cookie API） |
| `BrowserSession.cs` | 登录编排、cookie 持久化与注入、空闲退出 |
| `Sources.cs` | Source 基类 + TraeSource + ArkAFPSource |
| `TrayApp.cs` | 托盘、菜单、刷新循环、阈值通知、配置监视 |
| `WebPanel.cs` | HttpListener 服务端 + 状态 API |
| `HtmlTemplate.cs` | Liquid Glass 面板页面 |
