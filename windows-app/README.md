# AIClockBridge for Windows

`mac-app/` 菜单栏桥接的 Windows 移植版：同一套功能、同一套设备协议（固件感知不到
桥接跑在哪个系统上），以系统托盘图标形式常驻。

功能与 Mac 版一致：

- **USB 有线优先**：插入 SD2 后自动识别 CH340/CH343 串口并握手，额度、状态、网速、股票及显示/亮度命令优先走 USB；拔线后自动回退 Wi-Fi/LAN
- **设备默认显示**：托盘菜单的“屏幕显示”会直接写入设备；选择“固定 Codex（开机默认）”后，设备重启仍显示 Codex
- **重置时间显示**：托盘可切换倒计时或具体日期时间，同时应用于 5 小时和周额度、写入设备并在重启后保留；需配合 `0.4.4-sonic` 固件。时间跟随 Windows 本地时区，USB 关闭 Wi-Fi 时也能使用。
- **USB 省 Wi-Fi**：可在托盘菜单开启“USB 直连时关闭设备 Wi-Fi”；USB 桥接握手成功后固件关闭无线，断开 USB 后的普通开机仍可照常连 Wi-Fi
- **左键托盘图标** → ESP8266 屏幕实时镜像（额度环 + 桌宠动画 + 网速图 + 音乐页，
  与设备渲染同一份数据），底部附 自动/Claude/Codex/双栏/网速/音乐/股票 快速切换
- **右键托盘图标** → 控制菜单：Claude/Codex 完整额度（5h/周 + 重置倒计时）、
  自动查找并配对设备、设置设备地址、屏幕显示模式、petdex 桌宠画廊、恢复默认动画、
  把本机设为设备桥接、桥接服务地址
- 可在托盘菜单选择绑定网卡；设备搜索、桥接 IP、设备请求和网速统计统一使用所选网卡
- 本地 HTTP 服务监听所选网卡与 `127.0.0.1`（默认端口 8765，可改，见下）：`/status`、`/net`、`/music`、`/music/cover.raw`、
  `/music/text.raw`、`POST /event`（Claude Code / Codex hooks 秒级状态推送）
- 数据来源同 Mac 版：`%USERPROFILE%\.claude\projects` / `%USERPROFILE%\.codex\sessions`
  的 JSONL 日志 + 各自官方用量接口（凭据读
  `%USERPROFILE%\.claude\.credentials.json` 和 `%USERPROFILE%\.codex\auth.json`，
  token 只发给各自官方 API）
- 音乐页读系统级 Now Playing（WinRT `GlobalSystemMediaTransportControlsSessionManager`，
  Spotify / 浏览器 / 本地播放器都能识别）；网速取绑定网卡的字节计数并以 4Hz 采样，
  自动模式优先选择带默认网关的物理网卡，也可显式选择 VPN/虚拟网卡

与 Mac 版的差异：

- 无固件刷写入口（刷写请用网页版刷写工具）
- 唯一的第三方依赖是 [ImageSharp](https://github.com/SixLabors/ImageSharp)——
  System.Drawing 解不了 petdex 的 WebP 精灵图、也编不了多帧 GIF

## USB 有线优先

Windows 客户端会每 2 秒扫描串口。默认优先识别 WCH CH340/CH343（USB VID
`1A86`），发送 `#HELLO` 并且仅在固件返回 `#DEVICE` 后接管连接，避免误用其他
串口设备。连接成功后托盘会显示“USB COMx · 有线优先”。

- 指定端口：启动前设置 `AICLOCK_SERIAL_PORT=COM8`
- 扫描所有串口：设置 `AICLOCK_SERIAL_SCAN_ALL=1`（仍需通过设备握手）
- USB 断开、端口被占用或写入失败时，显示/亮度命令自动回退设备 HTTP API
- “屏幕显示（写入设备，重启保持）”和“USB 直连时关闭设备 Wi-Fi”都是硬件持久化设置；前者通过 USB 或 Wi-Fi 均可写入，后者在下一次 USB 握手起生效
- 额度、工作状态、网速和股票可在不配置 Wi-Fi 时通过 USB 显示
- 音乐封面、音乐文字条、自定义 GIF、网页设置和亮度持久化仍依赖 Wi-Fi；USB
  亮度命令只对当前运行周期生效
- 烧录固件前请退出托盘客户端，因为 Windows 串口同一时间只能由一个程序独占

## 构建 / 运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 10
19041+ / Windows 11）：

```powershell
cd windows-app\AIClockBridge
dotnet run                # 前台运行（托盘出现小电脑图标）
# 或发布免安装单文件（包含 .NET 运行时）：
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
# 产物在 bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AIClockBridge.exe
```

首次启动 Windows 会弹防火墙授权（HTTP 服务监听所选网卡的 8765 端口，设备要从局域网访问，
选"允许"）。

**开机自启**：`Win+R` → `shell:startup` → 把 `AIClockBridge.exe` 的快捷方式放进去。

### 换服务端口

默认 8765。这个端口有可能被别的软件占着——最常见的是**百度输入法**
（`baidupinyin.exe` 监听 `0.0.0.0:8765`，输入法里改不了，杀掉进程后
BaiduPinyinCore 服务还会把它拉起来）。端口被占时程序会弹窗提示，此时换一个端口即可：

- 托盘右键 →「服务端口…」→ 填 8766（留空恢复默认），按提示重启程序
- 或环境变量：`set AICLOCK_PORT=8766` 后再启动
- 或启动参数：`AIClockBridge.exe --port 8766`（快捷方式里加也行）

优先级：`--port` > `AICLOCK_PORT` > 托盘里存的设置 > 8765。启动日志会打印实际监听的
端口（`[bridge] serving /status on 0.0.0.0:8766`）。

改完端口记得让设备也知道：重启后点一次托盘「把本机设为设备桥接」，或在设备网页里把
Bridge host 改成 `<本机IP>:8766`（固件本来就是按 `host:port` 存的，不用重新刷）。
Hooks 里的 `http://127.0.0.1:8765/event` 也要跟着改成新端口。

**Hooks 实时状态**（可选，同主 README §7）：Claude Code / Codex 的 hooks 往
`http://127.0.0.1:8765/event` POST 事件即可，Windows 下 curl 自带。

## 验证

```powershell
curl.exe -s http://localhost:8765/status | python -m json.tool
```

配置持久化在 `%APPDATA%\AIClockBridge\settings.json`（设备地址等）。

额度缓存和重置时刻的离线回归检查（不读取账号凭据、不发起额度请求）：

```powershell
dotnet run --project windows-app/tests/Usage.Tests.csproj
```

以上测试命令从仓库根目录执行。

## 代码结构

| 文件 | 对应 Mac 版 | 说明 |
|---|---|---|
| `Program.cs` | `main.swift` | 入口 + 路由表 + 被动发现 |
| `TrayAppContext.cs` | `MenuBarController.swift` | 托盘图标 + 控制菜单 |
| `MirrorForm.cs` | `MirrorPopover.swift` | 240x240 屏幕镜像弹窗 |
| `PetPickerForm.cs` | `PetPickerWindow.swift` | petdex 桌宠选择器 |
| `PetdexService.cs` | `PetdexService.swift` | manifest / 精灵图 / GIF 合成 |
| `StatusService.cs` | `StatusReader.swift` | JSONL 日志扫描 + hook 事件 |
| `UsageFetcher.cs` | `UsageFetcher.swift` | 官方额度接口 |
| `NetSpeedMonitor.cs` | `NetSpeedMonitor.swift` | 4Hz 网速采样环 |
| `NowPlayingMonitor.cs` | `NowPlayingMonitor.swift` | 系统 Now Playing + 封面/文字条 RGB565 |
| `DeviceClient.cs` | `DeviceClient.swift` | 设备 HTTP API + 自动配对/子网扫描 |
| `SerialLink.cs` | `SerialLink.swift` | USB 串口发现、握手、数据推送与有线优先 |
| `MiniHttpServer.cs` | `HTTPServer.swift` | 所选网卡 + 127.0.0.1:8765 极简 HTTP 服务 |
| `Rgb565.cs` | （MirrorPopover 内联） | RGB565 大端编解码 |
