# AIClock Linux / Docker 额度服务

为 ESP8266 AIClock 小屏幕提供一个无桌面依赖的服务，同时在中文网页显示 Codex / Claude 额度，并管理 petdex 宠物和设备基础设置。

功能范围：

- **额度概览**：按账号返回的实际窗口显示已用比例、剩余比例、重置倒计时、成功查询时间、当前刷新间隔和失败原因。支持动态刷新、手动刷新、限流退避、上次成功数据缓存。
- **Codex 两种模式**：只读映射已有凭据；或使用独立目录，通过 Codex App Server 查询额度并按需续期。
- **Claude 只读模式**：读取现有 Claude Code 的订阅登录凭据。凭据失效时在宿主机重新登录。
- **petdex**：搜索、分页、九种动画预览、上传到 Claude/Codex 插槽、恢复默认宠物。
- **多屏幕管理**：添加多台小屏幕，分别打开设备网页、测试连接和配对，也可指定目标上传宠物和调整显示模式/亮度。
- **重置时间显示**：小屏幕设置可选择倒计时或具体日期时间，5 小时与周额度共用并在设备重启后保留。需更新固件，日期时间跟随服务所在时区（Docker 默认 `Asia/Shanghai`）。
- **页面定时与夜间亮度**：为每台屏幕单独设置每日开屏/关屏时间、开屏页面模式，以及夜间时段和夜间亮度。
- **Codex 定时消息**：每天指定时间使用所选模型发送一条简单消息，显示回复、执行状态和发送后的额度窗口；支持立即发送与最近 20 条记录。
- **基础设置**：对外服务地址、监听端口、动态刷新的最短/最长间隔与无变化次数阈值（也可切换固定间隔）、分别保存的只读与自动续期凭据目录。

不包含股票、音乐、网速、CPU/内存监控、USB 串口、工作状态日志采集。`/status` 中状态为 idle/offline，宠物保持待机。普通额度查询和凭据续期不会调用模型；只有手动发送或启用定时消息后才会发起文本请求。

## 1. 最快开始：Docker 只读模式

适合在已经登录 Codex / Claude Code 的 Linux 电脑上部署。需要 Docker Engine 和 Compose v2 插件。

```bash
cd /你的项目路径/esp8266-codex/linux-app
bash deploy.sh
```

脚本在第一次运行时生成 `.env`，使用当前用户的 UID/GID，并自动识别当前用户的 `.codex`、`.claude` 目录；来源不存在时挂载空目录。默认启用 Codex，Claude 可在网页开启。

每次运行脚本都会先执行 `git pull --ff-only`，有新提交时继续部署；没有更新时会询问是否仍要部署，默认选择取消。

浏览器打开 `http://电脑局域网IP:8765/`：

1. 在“基础设置”填写“设备可访问的服务地址”，例如 `192.168.1.20:8765`，这里是**宿主机**的局域网 IP，不是容器 IP。
2. 在“小屏幕配对与连接”添加每台屏幕的名称和 IP，可分别测试连接并配对。
3. 按需设置起始刷新间隔、最短/最长间隔与连续无变化次数。确认 Codex 凭据目录为 `/credentials/codex`，模式为“只读映射”；Claude 目录是 `/credentials/claude`。
4. 保存设置，切换到“额度概览”查看数据，或去“选择宠物”并选择目标屏幕后上传动画。

设备卡片中的“打开屏幕页面”会直达该设备自身的设置网页。基础设置下方的“小屏幕设置”区域通过右侧下拉框选择目标，再分别设置显示模式、常规亮度、定时开关屏、关闭亮度和夜间亮度；所有选项集中在对应的独立行中。服务每 30 秒检查一次，Docker 固定按北京时间 `Asia/Shanghai`（UTC+8）执行；原生运行按服务所在电脑的本地时区执行。设备需保持网络可达，服务停止时不会执行定时操作。页面关闭时使用单独配置的亮度（默认 0），页面开启期间恢复设备的常规亮度，夜间时段使用夜间亮度。

设备也可通过自带网页手动将 Bridge host 设置为同一服务地址。现有固件的 `/status` 协议无需修改。电脑、容器和设备需能在局域网互相访问；无线客户端隔离会阻止设备连接。

### 映射其他目录 / 修改对外端口

编辑 `.env`：

```dotenv
AICLOCK_HOST_PORT=8766
AICLOCK_DATA_PATH=/home/你的用户/aiclock-data
AICLOCK_CODEX_PATH=/home/你的用户/.codex
AICLOCK_CLAUDE_PATH=/home/你的用户/.claude
AICLOCK_ADVERTISED_HOST=192.168.1.20:8766
```

先创建数据目录，凭据来源目录须已存在。未使用的来源也要指向已存在的空目录：

```bash
mkdir -p /home/你的用户/aiclock-data
docker compose -f compose.yaml up -d --build
```

Docker 内部始终监听 8765；`AICLOCK_HOST_PORT` 控制宿主机端口。改端口后重新创建容器，再在网页更新“设备可访问的服务地址”，保存并重新指向设备。

Compose 容器以 root 运行，并保留访问及调整挂载目录权限所需的能力。容器可修改绑定的数据目录中的宿主机文件；只读凭据挂载仍为只读。旧 `.env` 中的 `AICLOCK_UID` 和 `AICLOCK_GID` 不再生效。服务创建的文件可能归宿主机 root 所有。

`AICLOCK_ADVERTISED_HOST` 和目录/模式环境变量仅在**没有 settings.json 的首次启动**时预填配置。之后以网页保存的设置为准，不会用环境变量覆盖你的网页修改。

映射整个 `.codex` 目录，而不只映射 `auth.json`：宿主机可能通过原子替换更新凭据文件，目录挂载更适合读取这种变化。来源目录只读，服务不会修改宿主机凭据。

### 只读模式能持续刷新多久？

额度是向服务器独立查询的，不需要 Codex 一直运行。每次查询重新打开凭据文件，后台约每 5 秒检查文件指纹；文件内容变化后清除旧账户缓存，并在查询节流允许时读取新凭据。

但是，只读映射不会给凭据续期。如果宿主机 CLI 长期没有使用，access token 失效后查询会失败。此时在宿主机重新登录/使用 CLI 更新凭据，或者使用下面的独立自动续期模式。账户登出、凭据撤销等情况仍需要重新登录。

## 2. Codex 模式切换与网页登录

同一个 Docker 镜像始终包含 Codex CLI 和只读额度查询组件。设置页分别保存两套目录：只读模式默认读取 `/credentials/codex`，自动续期模式默认使用持久化的 `/data/codex`。切换查询方式会自动切换目录，不需要反复改路径。用户也可以分别调整这两项目录。

```bash
cd /你的项目路径/esp8266-codex/linux-app
bash deploy.sh
```

在“基础设置”选择“自动续期”，保存后点击“开始登录”。页面控制台会显示设备验证提示；完成 ChatGPT 设备验证后，服务会自动检测 CLI 登录结束并刷新额度。登录凭据保存在自动续期目录，与只读目录分开。

服务通过 stdio 启动短暂的 `codex app-server` 会话，读取账户类型与额度窗口。认证失效时尝试刷新 token。普通额度刷新不会发送模型消息；额度查询、模型列表、消息发送和网页登录使用所选的自动续期目录。

**不要把同一份 refresh token 的副本交给宿主机与服务并发续期。** 推荐使用页面登录生成服务专用凭据。只读模式可以共享宿主机目录；自动续期模式应使用独立目录。不要并行运行多个续期服务或在续期查询期间另行登录同一目录。浏览器页面不会读取、显示或上传 token。

切换模式和升级时持续使用对应的 Compose 参数：

```bash
# 更新服务 / 查看日志 / 停止
docker compose -f compose.yaml up -d --build
docker compose -f compose.yaml logs --tail=100 aiclock
docker compose -f compose.yaml down
```

`down` 不会删除绑定的数据目录。旧命令 `bash deploy.sh readonly` 和 `bash deploy.sh managed` 仍会兼容，但都会部署包含两种模式组件的同一镜像。`AICLOCK_CODEX_VERSION` 可在 `.env` 修改，重新构建后生效。

### Codex 启动时报权限或 bubblewrap 错误

统一镜像已安装 `bubblewrap`，升级已有部署后需要重新构建镜像。Linux 原生运行自动续期模式时，也需安装发行版提供的 `bubblewrap` 包，例如 Ubuntu/Debian 使用 `sudo apt install bubblewrap`。

自动续期目录中的 `auth.json` 可写还不够：Codex CLI 会在 `tmp/arg0` 创建 PATH 别名、设置目录权限，并使用缓存和日志目录。宿主机的默认位置是 `AICLOCK_DATA_PATH` 下的 `codex`；调整过网页凭据路径时，以实际挂载位置为准。文件系统的挂载限制或容器沙箱设置仍可能影响访问。

页面现在会为 App Server 提前退出显示退出码，以及权限、配置加载或缺少 bubblewrap 等错误类别。原始 CLI 错误输出可能包含账号与配置内容，不会直接返回网页。

## 3. Linux 原生运行

需要 .NET 10 SDK。自动续期模式需要 Codex CLI；只读额度查询不依赖 CLI。

```bash
cd /你的项目路径/esp8266-codex/linux-app
bash run.sh
```

默认端口 8765，默认读取运行用户的 `~/.codex`、`~/.claude`，数据保存在 `linux-app/data/`。网页中的目录必须写完整绝对路径，不使用 `~`。

也可以先发布，再用运行时启动：

```bash
dotnet publish AIClockBridge.Linux.csproj -c Release -o bin/publish
cd bin/publish
AICLOCK_DATA_DIR=/home/你的用户/aiclock-data dotnet AIClockBridge.Linux.dll
```

自动续期登录也可直接从设置页启动。原生运行时在“自动续期凭据目录”填入独立可写目录；`AICLOCK_CODEX_EXECUTABLE` 可以指定 CLI 可执行文件的完整路径，不支持在此变量中填一整段 shell 命令。

### systemd 常驻示例

先发布到固定目录，再根据实际用户和路径建立 `/etc/systemd/system/aiclock.service`：

```ini
[Unit]
Description=AIClock quota bridge
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=你的用户
WorkingDirectory=/你的项目路径/esp8266-codex/linux-app/bin/publish
ExecStart=/usr/bin/dotnet /你的项目路径/esp8266-codex/linux-app/bin/publish/AIClockBridge.Linux.dll
Environment=AICLOCK_DATA_DIR=/home/你的用户/aiclock-data
# 自动续期时按实际位置填写，可避免 systemd 的 PATH 找不到 codex：
# Environment=AICLOCK_CODEX_EXECUTABLE=/usr/local/bin/codex
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
UMask=0077

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now aiclock
journalctl -u aiclock --no-pager -n 50
```

## 4. 额度和凭据说明

- Codex：只读模式读取 `<Codex目录>/auth.json` 的 `tokens.access_token`。必须是 ChatGPT 账号登录凭据；API Key 不提供订阅账户额度。若凭据存储在系统 keyring 中，需要配置 `cli_auth_credentials_store = "file"` 并重新登录生成文件。
- Claude：读取 `<Claude目录>/.credentials.json` 的 `claudeAiOauth.accessToken`。本服务不读取 macOS Keychain，也不自动续期 Claude 凭据。
- 额度百分比按接口实际窗口解析，不能假定所有账号始终都有“5 小时 + 每周”两个窗口。窗口缺失时不编造数值；重置倒计时使用服务端返回的绝对时间。
- 默认启用动态刷新，从 2 分钟开始，在 1～10 分钟之间调整；Codex、Claude 各自计算。任一额度窗口的已用比例、窗口长度或绝对重置时间发生变化，间隔减半；连续 3 次成功查询都无变化，间隔加倍并重新计数。首次查询建立基准，失败会打断无变化计数、保留上次成功数据，不会被当作空闲。
- 基础设置支持调整起始间隔、最短/最长间隔（均为 1 分钟至 24 小时，最短不能大于最长）与连续无变化次数（1～100）。起始间隔超出范围时自动限制在范围内；最短与最长相同时保持该间隔。关闭动态刷新后，使用起始 / 固定刷新间隔。旧配置保留原有间隔作为起始值，并采用默认动态范围与次数。
- 活跃度根据服务端额度变化推测，不读取其他客户端的工作状态；空闲后再次使用，可能需要等待当前间隔结束才会感知变化。通过本服务发送消息并成功读取发送后额度时，直接切至最短间隔。概览显示各来源的当前间隔与下次查询时间。修改刷新策略会重新开始调整，不会清除有效额度；修改设备或端口保留当前间隔。
- 手动刷新也遵守至少 60 秒节流；429 后至少等待 5 分钟，退避可超过设置的最长间隔。后台约每 5 秒检查是否到期，因此实际发起查询可能稍晚于计划时间。页面每 5 秒读取本地结果，设备每 5 秒读取 `/status`，都不会触发额外额度请求。
- 网络/认证失败时显示最近成功数据，并注明查询失败/数据过期；不会把未知额度显示成 0%。缓存超过 24 小时不在冷启动恢复。禁用来源、切换凭据目录/模式或凭据变化会清除对应旧数据；修改设备地址、端口或刷新间隔不会清除有效额度。
- 当前使用的供应商额度接口可能变化或限流。Claude API / Codex CLI 版本变动时，应重新检查解析和错误处理。
- 数据目录中保存 `settings.json`、`usage-cache.json` 和 `message-history.json`。消息配置与回复会持久化，不保存只读来源的 token。自动续期模式的 `data/codex/auth.json` 是实际登录凭据，不能提交到 Git 或分享。
- 页面与设备接口面向可信局域网，没有用户登录系统。需要在公网使用时，应由反向代理或 VPN 提供访问控制；现有固件通过普通 HTTP 读取状态。

## 5. Codex 定时消息

自动续期模式需要 Codex CLI，并在独立可写目录登录 ChatGPT 账号。只读模式不支持发送。Docker 部署始终包含两种模式；已有部署重新运行 `bash deploy.sh` 更新后，在基础设置切换到自动续期并从页面启动登录即可。

在“定时消息”页面：

1. 点击“加载可用模型”，从 CLI 返回的文本模型列表中选择模型。
2. 设置每日发送时间和简单消息，例如“只回复 OK。”，保存计划。
3. 点击“立即发送已保存消息”验证模型回复；定时发送不必开启即可手动发送。
4. 勾选每日发送并保存。页面显示服务时区和下次计划时间；Docker 固定使用北京时间 `Asia/Shanghai`（UTC+8）。已有部署重新运行 `bash deploy.sh` 更新后生效，原有每日时分设置会按北京时间解释。

每次使用一个独立的临时会话，不携带历史对话；消息最长 1000 字符，回复上限 16000 字符。发送最长等待 3 分钟，立即发送至少间隔 60 秒。模型不可用、认证失败、限流、超时或服务重启都会记录结果；不会自动重发。浏览器关闭或离开页面不会中断已经提交的请求。默认关闭定时发送。

服务每 10 秒检查一次，每个服务本地日期最多自动发送一次。发送尝试先持久化，再调用模型，所以当天修改时间、重启服务或失败不会重复自动发送。允许计划时间后 5 分钟内执行；服务停机错过此范围后跳过当天，不在启动时补发。立即发送与每日计划独立，但遵守同一发送锁和 60 秒间隔。停机期间无法执行计划。

成功回复后，在同一 CLI 会话查询额度，显示发送后的 5 小时窗口已用比例与服务端重置时间，并更新额度概览。额度查询失败仍保留成功回复，后台继续正常刷新。**回复成功不等于确认开启新窗口**：窗口是否开始和起算方式由服务端决定，已有窗口仍按原时间重置；本功能不会任意设置或重启窗口。每次真实请求都会消耗订阅额度。

## 6. petdex 使用

先在基础设置添加一个或多个设备。打开“选择宠物”后可通过 Petdex 中文站链接访问图库源；在本页搜索名称、选择宠物、目标屏幕、Codex/Claude 插槽和动画，右侧显示实际生成的 GIF 预览。点击上传后，服务下载 WebP 精灵图，用 ImageSharp 裁剪、缩放并合成 GIF，通过设备原有 multipart 接口发送。

Claude 插槽为 111×120，Codex 插槽为 120×120；GIF 最多 8 帧，与当前固件限制一致。预览与上传使用同一份转换结果。上传时请保持设备 Wi-Fi 可达，板上解码可能需要几十秒。上传或恢复默认只影响选定插槽。

图库与素材需要访问 `assets.petdex.dev`。图库缓存 6 小时，最多缓存 32 份 GIF；未选中的图库条目不会批量下载精灵图。

## 7. API 与开发验证

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/` | 中文控制台 |
| GET | `/healthz` | 服务进程健康检查 |
| GET | `/status` | 现有 ESP8266 固件兼容数据 |
| GET | `/api/usage` | 额度、窗口、更新时间、过期状态、错误 |
| POST | `/api/usage/refresh` | 在节流允许时手动刷新 |
| GET | `/api/codex/login` | 查看网页设备登录状态与控制台输出 |
| POST | `/api/codex/login/start` / `/cancel` | 启动 / 取消自动续期模式登录 |
| GET | `/api/messages` | 发送计划、服务时区、下次执行时间和最近 20 条结果 |
| GET | `/api/messages/models` | 自动续期模式的 Codex 可用文本模型列表 |
| POST | `/api/messages/schedule` | 保存计划，`{"enabled":false,"time":"08:00","model":"账号可用模型ID","message":"只回复 OK。"}` |
| POST | `/api/messages/send` | 立即发送已保存消息，等待最终结果；无需开启每日计划 |
| GET / POST | `/api/settings` | 查询 / 保存基础设置 |
| GET | `/api/devices` | 列出已保存的小屏幕 |
| POST | `/api/devices` | 添加设备，`{"name":"书桌屏幕","host":"192.168.1.50"}` |
| POST | `/api/devices/{id}/update` / `/delete` | 修改 / 删除指定设备 |
| GET | `/api/devices/{id}/info` | 测试指定设备连接并获取设备信息 |
| POST | `/api/devices/{id}/pair` | 将指定设备指向已保存的对外服务地址 |
| POST | `/api/devices/{id}/display` | `{"mode":"auto\|claude\|codex\|dual"}` |
| POST | `/api/devices/{id}/brightness` | `{"level":0到100}` |
| POST | `/api/devices/{id}/schedule` | 保存该设备每日页面开关和夜间亮度时段 |
| GET | `/api/pets?search=关键词&page=0` | 搜索 / 分页 |
| GET | `/api/pets/{slug}/preview?slot=codex&animation=idle` | GIF 预览 |
| POST | `/api/pets/{slug}/apply` | `{"deviceId":"设备ID","slot":"codex","animation":"idle"}` |
| POST | `/api/pets/reset` | `{"deviceId":"设备ID","slot":"codex"}` |

POST 路由要求 `Content-Type: application/json`，浏览器请求要求同源。无需参数的 POST 发送 `{}` 即可。

```bash
curl http://127.0.0.1:8765/healthz
curl http://127.0.0.1:8765/status
curl -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8765/api/usage/refresh
dotnet build AIClockBridge.Linux.csproj -c Release
dotnet run --project tests/Bridge.Tests.csproj -c Release
node --check wwwroot/app.js
```

测试使用虚构凭据、模拟 HTTP、可推进的时钟与模拟 App Server，覆盖动态间隔上下限、无变化阈值、来源独立调整、固定模式、设置迁移、错误恢复与退避、真实窗口长度、周额度独占窗口、缓存、原子替换凭据、设置变更、续期握手与重试、模型选择、消息流式结果、失败与重启不重复发送、计划时间、并发目录锁、GIF 生成和设备表单上传；不需要账号，也不消耗模型额度。

构建、接口与浏览器可在 Windows 验证，因为服务没有 Windows 桌面依赖。Docker 镜像与 systemd 部署仍需在目标 Linux 主机上验证；ImageSharp 的许可证沿用项目现有第三方依赖，分发/商业使用请检查其条款及仓库 NOTICE。

参考：[Codex 认证](https://learn.chatgpt.com/docs/auth)、[Codex App Server 账户接口](https://learn.chatgpt.com/docs/app-server#account-endpoints)、[凭据维护与并发续期](https://learn.chatgpt.com/docs/auth/ci-cd-auth#operational-rules-that-matter)、[Docker 目录挂载](https://docs.docker.com/engine/storage/bind-mounts/)。
