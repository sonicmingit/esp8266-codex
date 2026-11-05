# 当前 0.2.0 验证记录 · 2026-11-05

## 本次修改及交付

- APK：`dist/AIClockWatch-0.2.0.apk`，versionCode 2、包名和本地签名保持一致，可覆盖旧版。
- 新增多地址持久化、Wi-Fi IPv4 前缀匹配、显式 CIDR 规则、候选失败后回退、手动固定地址。Wi-Fi 改变时取消旧请求并清除旧数据，前台监听网络变化，后台注销监听。
- 迁移已有单地址；首页显示数据来源地址和手表 Wi-Fi IP，单应用采用左宠物/右信息布局，双栏保留汇总。
- Petdex 原生浏览器：中文/slug 搜索、8 项分页、预览后应用、两家独立槽位、恢复默认、下载/缓存/取消、私有目录原子保存、分状态动画。进入后台暂停浏览器请求，回来恢复浏览，避免因键盘页面切换丢失入口。

## 已验证

- `watch-app/test.ps1`：**67 项**断言通过（原有 39 项，加 28 项网络配置和宠物目录断言）。
- Android SDK 35 / JDK 17 / D8 构建通过；aapt2 Manifest、v2/v3 签名、zipalign 检查通过。
- 9 种共享绘图计划的 Java2D 圆屏预览生成并人工检查，包含左右布局、双栏、耗尽、缓存、未知、待审批、大字体与未取得数据。占位数字仅为测试数据，预览不是手表截图。
- ADB 只读核对当前在线设备：M2505W1、API 34、480×480、320dpi，旧应用仍为 0.1.0。
- 读取当前 WLAN 地址 `192.168.1.203/23`；电脑 Wi-Fi `192.168.0.118/23`，用对应测试确认跨第三段仍匹配；旧设置保存 `http://192.168.0.118:18765/status`，此地址可迁移并匹配。
- 本机既有桥接 `/status?client=watch` 可读，未读取账号凭据。
- 从官方 Petdex HTTPS 下载目录并解析，当前含 5209 项；下载一个实际 WebP 用 Pillow 核对为 1536×1872。未把下载测试素材打入 APK，也未把账号或个人配置写入源码。

## 安装限制和待实测

本轮设备在线，但向手表安装 0.2.0 的命令被自动审批拒绝，返回 `blocked by policy`；该命令未执行，未通过其他方式绕过安装限制。保留旧应用配置，交付已签名 APK 供手动覆盖安装。

待安装后核对：真实 Android Canvas 排版、点按/长按/滑动、原生多行地址输入与 Spinner、切换 Wi-Fi 的及时重新选择、同网段服务失败后回退、公网/eSIM 回退、旧请求取消、Petdex HTTPS 下载/原生 WebP 解码/应用、断网及重启后的宠物恢复、键盘切换恢复、TalkBack、大字体和前后台行为。离线绘图与纯 JVM 测试不能替代这些验收。

Windows 0.4.6-sonic 的只读手表标识兼容继续复用上轮结果；本轮未修改 Windows、固件或 Linux Docker 清单。

---

# 历史 0.1.0 验证记录 · 2026-11-05

## 当前交付

- 代码基线：`main` 的 `4604435`，通过本机代理 fetch 后确认与 `origin/main` 一致；开发分支 `codex/watch-quota`。
- APK：仓库根 `dist/AIClockWatch-0.1.0.apk`，包名 `cn.sonic.aiclockwatch`，versionCode 1。用于个人安装的本地开发签名。
- Windows：`dist/watch-windows/AIClockBridge.exe`，0.4.6-sonic、win-x64、自包含单文件。新增只读手表标识过滤，避免影响硬件配对。
- 本次未修改固件和 Linux Docker 构建清单。

## 已完成

- JDK 17 + Android SDK 35 / Build Tools 35.0.0 编译，D8、资源链接通过。
- apksigner 验证 v2/v3 签名通过；zipalign 校验通过；Manifest 确认 minSdk 26、targetSdk 34、启动 Activity、INTERNET 权限及应用版本。
- APK 内容检查：classes.dex、resources.arsc、图标和 Claude/Codex 各 6 帧 PNG 均在包内；没有原生库、个人账号配置或 CLI 凭据。
- `watch-app/test.ps1`：39 项协议、模式、地址及 HTTP 断言通过，9 种圆屏布局预览生成通过。
- 人工检查 `qa/generated/contact-sheet.png`，覆盖 Codex 周额度、Claude 双窗口、双栏、额度耗尽、断线缓存、未知额度、待审批、大字体日期及未配置引导。大字体下调整了文字行距；预览文字处于圆形安全区域。
- `windows-app/tests/Usage.Tests.csproj`：既有额度测试通过，真实 HTTP 测试确认带 `client=watch` 的请求仍获额度、不会触发实体设备发现；普通硬件请求仍触发发现。
- Windows Debug build 和 Release 自包含 publish 成功。

Java2D 预览与 JVM 测试验证共享绘制计划和协议行为，不代表 Activity、Android 字体/Canvas、键盘、生命周期或设备网络已经运行验收。

## 待真机验证

当前 `adb devices -l` 无在线设备，未安装、启动或截取手表画面。后续应明确 ADB 目标后完成：

1. 确认当前设备型号、API、480×480 分辨率、320dpi 与 ABI，安装 APK，核对显示版本及应用截图。
2. 在圆屏输入 Windows/Linux 地址，保存后退出再进入，检查配置保留；也可由安装脚本预填。
3. 对照桥接 `/status`，核对真实账户 5 小时/周额度、剩余切换和重置时间；Windows 与 Linux 分别实测。
4. 检查点按、长按、左右滑动、原生返回、下拉 Spinner、键盘、最大字体与 TalkBack。
5. 人为断开网络，检查缓存/红环提示与恢复，确认切后台/熄屏后停止轮询和动画；测试可选保持亮屏。
6. 使用测试状态验证真实 Android 上的自动轮换、待审批、耗尽倒计时。提醒仅用于电脑审批，手表不提交审批操作。
7. Windows 连接真实 SD2 的同时连接手表，核对实体配对 IP 保持正确。

账号数据是否最新由桥接端决定；本次没有读取个人 CLI 凭据、调用真实额度 API 或验证账号授权。
