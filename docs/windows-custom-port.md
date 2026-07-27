# Windows 桥接端自定义端口

## 背景

Windows 版 `AIClockBridge` 原本固定监听 TCP `8765`。部分软件（例如百度输入法的 `baidupinyin.exe`）也会占用该端口，并可能由后台服务自动重启，因此桥接程序会因端口冲突无法正常启动。

本地已将 Windows 桥接端端口改为 `18765`。该端口已检查为空闲。

## 源码改动

修改文件：`windows-app/AIClockBridge/Program.cs`

```diff
- const int Port = 8765;
+ const int Port = 18765;
```

程序其余需要端口的逻辑都复用该 `Port` 常量，包括：

- 本地 HTTP 服务监听；
- 设备自动配对时下发的桥接端口；
- 托盘应用中的桥接配置。

同时已更新 `windows-app/README.md` 中的服务地址、hooks 示例和验证命令，统一使用 `18765`。

## 生成单文件 EXE

后续更新项目后，直接双击仓库根目录的 `build-windows-single-exe.bat` 即可重新打包。
脚本会检查 .NET SDK，并将单文件 EXE 输出到 `windows-app/AIClockBridge/publish-single/`。

也可以在命令行手动执行以下命令：

在 `windows-app/AIClockBridge` 目录执行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\publish-single
```

参数说明：

- `--self-contained true`：将 .NET 运行时打入产物，目标电脑无需安装 .NET；
- `PublishSingleFile=true`：生成单一 EXE；
- `-r win-x64`：面向 64 位 Windows；
- `IncludeNativeLibrariesForSelfExtract=true`：将原生依赖一并包含。

生成文件：

```text
windows-app/AIClockBridge/publish-single/AIClockBridge.exe
```

## 运行与设备配置

1. 运行 `AIClockBridge.exe`；首次启动时，在 Windows 防火墙提示中允许局域网访问。
2. 打开设备管理页面，或使用其桥接地址配置功能。
3. 将 **Bridge host** 设置为：

   ```text
   <电脑局域网 IP>:18765
   ```

   示例：`192.168.1.100:18765`。

4. 如配置了 Claude Code 或 Codex hooks，将原地址改为：

   ```text
   http://127.0.0.1:18765/event
   ```

5. 验证桥接服务：

   ```powershell
   curl.exe -s http://localhost:18765/status | python -m json.tool
   ```

## 检查端口

启动前可检查端口是否被占用：

```powershell
Get-NetTCPConnection -LocalPort 18765 -State Listen -ErrorAction SilentlyContinue
```

没有输出表示端口空闲。

## 重新同步上游

日后从上游更新代码时，保留 `Program.cs` 中的端口常量改动，并重新执行“生成单文件 EXE”的命令即可。
