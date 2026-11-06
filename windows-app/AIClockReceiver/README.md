# Windows PC 接收端

`AIClockReceiver.exe` 是一个独立的 Windows 桌面小窗。发射端负责读取账号状态，接收端每 15 秒读取一次桥接缓存。只需要知道桥接地址，接收电脑无需登录 Claude 或 Codex。

## 使用

1. 在发射电脑启动 Windows Bridge 0.4.6-sonic 或 Linux Bridge。Windows 托盘菜单的「桥接服务地址」可查看实际地址和端口。
2. [下载 Windows 桌面接收端](https://github.com/sonicmingit/esp8266-codex/releases/download/v0.4.6-sonic/AIClockReceiver-0.4.6-sonic-Windows.exe) 后双击运行。首次打开，点右上角齿轮，在「桥接地址」填写发射电脑的地址，例如 `192.168.1.20:8765`。两端运行在同一台电脑时可填 `127.0.0.1:8765`。也支持完整 HTTP(S) 地址和反向代理子路径。
3. 在同一设置窗口选「窗口形态」，点击「保存」立即切换。图钉控制置顶，拖动标题可移动窗口，右下角按钮可立即刷新。

| 窗口形态 | 显示内容 |
| --- | --- |
| 单宠物（默认） | 深色窗口，用较大字号显示一个来源的额度、重置时间和宠物。来源可固定为 Claude、Codex，或自动优先显示待确认及正在工作的一方。 |
| 双栏 | 浅色窗口，同时显示两家的宠物、状态和额度。只有周额度时直接显示周重置时间；有两个窗口时，将指针停在额度上可查看对应重置时间。 |
| 贴边窄栏 | 一行显示当前来源、动作与实际存在的额度窗口。完整视图右上角的折叠按钮直接收起；点窄栏右侧箭头回到之前的完整视图。右键窄栏可进入设置或关闭窗口。 |

拖动标题区域或窄栏，靠近当前显示器工作区的左边或右边时会吸附；向中间拖出即可解除。收起、展开和重启时会恢复吸附的一侧。设置中的「额度显示」可在已使用量和剩余量之间切换，标签、读数与进度条同步更新。旧设置默认沿用已用口径。

在设置的「桌宠」区域，分别点击 Claude 或 Codex 旁的「更换…」，按名称或标识搜索 Petdex 宠物。选中结果可预览待机、工作、待确认三种动作。点「选用」，再点设置页「保存」。也可以恢复各自的内置宠物。新宠物首次使用需要网络；已下载图集保存在 `%LOCALAPPDATA%\AIClockReceiver\pets`。如果 Windows 提示无法解码 WebP，请安装系统的 WebP 图像扩展。

窗口偏好保存在 `%LOCALAPPDATA%\AIClockReceiver\settings.json`。同一地址下切换视图会立即复用已有数据。正常连接时空闲宠物也会播放待机帧，并遵循 Windows 动画效果设置。桥接失联会暂停动画，保留读数并标记「缓存数据」。首次连接失败会显示错误，并继续定期重试。

## 协议与构建

接收端发送 `GET /status?client=watch`，复用 Windows 0.4.6-sonic 已识别的只读标记，避免接收电脑被当成实体设备。Linux Bridge 的 `/status` 也接受该查询。像素宠物直接嵌入仓库已有的手表帧，原始素材归属见 [NOTICE.md](../../NOTICE.md)。

在 64 位 Windows 10/11、.NET Framework 4.8 环境中，从仓库根目录运行 PowerShell：

```powershell
./windows-app/AIClockReceiver/build.ps1
./windows-app/AIClockReceiver/test.ps1
# 同时验证真实 Petdex 搜索、下载、换宠物与三种动作
./windows-app/AIClockReceiver/test.ps1 -OnlinePets
```

构建脚本使用系统自带的 C# 编译器与 WPF，输出单文件 `dist/AIClockReceiver.exe`。旧 EXE 被占用时，新版输出到 `dist/receiver-update/AIClockReceiver.exe`。窗口测试使用临时设置和本机回环 HTTP 服务，检查协议、视图切换、保存恢复与断线缓存，并将原生窗口渲染图输出到 `dist/receiver-qa/`。
