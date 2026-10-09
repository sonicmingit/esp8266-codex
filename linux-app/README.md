# AIClock Linux / Docker 额度服务

为 ESP8266 AIClock 小屏幕提供一个无桌面依赖的服务，同时在中文网页显示 Codex / Claude 额度，并管理 petdex 宠物和设备基础设置。

功能范围：

- **额度概览**：按账号返回的实际窗口显示已用比例、剩余比例、重置倒计时、成功查询时间和失败原因。支持后台定时刷新、手动刷新、限流退避、上次成功数据缓存。
- **Codex 两种模式**：只读映射已有凭据；或使用独立目录，通过 Codex App Server 查询额度并按需续期。
- **Claude 只读模式**：读取现有 Claude Code 的订阅登录凭据。凭据失效时在宿主机重新登录。
- **petdex**：搜索、分页、九种动画预览、上传到 Claude/Codex 插槽、恢复默认宠物。
- **基础设置**：设备地址、对外服务地址、监听端口、刷新间隔、凭据目录、显示模式和亮度。

不包含股票、音乐、网速、CPU/内存监控、USB 串口、工作状态日志采集。服务只显示额度，`/status` 中状态为 idle/offline，宠物保持待机；不会为了刷新凭据调用模型或执行项目任务。

## 1. 最快开始：Docker 只读模式

适合在已经登录 Codex / Claude Code 的 Linux 电脑上部署。需要 Docker Engine 和 Compose v2 插件。

```bash
cd /你的项目路径/esp8266-codex/linux-app
bash deploy.sh readonly
```

脚本在第一次运行时生成 `.env`，使用当前用户的 UID/GID，并自动识别当前用户的 `.codex`、`.claude` 目录；来源不存在时挂载空目录。默认启用 Codex，Claude 可在网页开启。

浏览器打开 `http://电脑局域网IP:8765/`：

1. 在“基础设置”填写小屏幕 IP，例如 `192.168.1.50`。
2. 填写“设备可访问的服务地址”，例如 `192.168.1.20:8765`，这里是**宿主机**的局域网 IP，不是容器 IP。
3. 确认 Codex 凭据目录为 `/credentials/codex`，模式为“只读映射”。按需开启 Claude，其目录是 `/credentials/claude`。
4. 保存设置，再点击“测试设备连接”和“将设备指向本服务”。
5. 切换到“额度概览”查看数据，或去“选择宠物”预览、上传动画。

设备也可通过自带网页手动将 Bridge host 设置为同一服务地址。现有固件的 `/status` 协议无需修改。电脑、容器和设备需能在局域网互相访问；无线客户端隔离会阻止设备连接。

### 映射其他目录 / 修改对外端口

编辑 `.env`：

```dotenv
AICLOCK_HOST_PORT=8766
AICLOCK_UID=1000
AICLOCK_GID=1000
AICLOCK_DATA_PATH=/home/你的用户/aiclock-data
AICLOCK_CODEX_PATH=/home/你的用户/.codex
AICLOCK_CLAUDE_PATH=/home/你的用户/.claude
AICLOCK_ADVERTISED_HOST=192.168.1.20:8766
```

先创建数据目录并确保配置的 UID/GID 有写入权限，凭据来源目录须已存在。未使用的来源也要指向已存在的空目录：

```bash
mkdir -p /home/你的用户/aiclock-data
docker compose -f compose.yaml up -d --build
```

Docker 内部始终监听 8765；`AICLOCK_HOST_PORT` 控制宿主机端口。改端口后重新创建容器，再在网页更新“设备可访问的服务地址”，保存并重新指向设备。

`AICLOCK_ADVERTISED_HOST` 和目录/模式环境变量仅在**没有 settings.json 的首次启动**时预填配置。之后以网页保存的设置为准，不会用环境变量覆盖你的网页修改。

映射整个 `.codex` 目录，而不只映射 `auth.json`：宿主机可能通过原子替换更新凭据文件，目录挂载更适合读取这种变化。来源目录只读，服务不会修改宿主机凭据。

### 只读模式能持续刷新多久？

额度是向服务器独立查询的，不需要 Codex 一直运行。每次查询重新打开凭据文件，后台约每 5 秒检查文件指纹；文件内容变化后清除旧账户缓存，并在查询节流允许时读取新凭据。

但是，只读映射不会给凭据续期。如果宿主机 CLI 长期没有使用，access token 失效后查询会失败。此时在宿主机重新登录/使用 CLI 更新凭据，或者使用下面的独立自动续期模式。账户登出、凭据撤销等情况仍需要重新登录。

## 2. Docker 自动续期模式

自动续期镜像附带 Codex CLI（默认固定 `0.162.0`），使用 `/data/codex` 作为**服务专用的可写目录**。不会在宿主机只读挂载目录中续期。

```bash
cd /你的项目路径/esp8266-codex/linux-app
bash deploy.sh managed
bash codex-login.sh
```

登录脚本暂停服务，启动共用持久化数据目录的一次性容器，执行 `codex login --device-auth`；根据终端显示的链接与设备码，在浏览器登录。退出后脚本重新启动服务。若账户尚未启用设备码登录，需要按官方登录指引启用；设备码不可用时可在服务专用目录中用正常的 `codex login` 完成浏览器登录。

在网页确认：

- Codex 模式：**自动续期**。
- Codex 凭据目录：**`/data/codex`**。

如果之前运行过只读模式，这两项需要在网页手动修改并保存，因为持久化设置优先于首次启动默认值。

服务通过 stdio 启动短暂的 `codex app-server` 会话，完成 initialize/initialized 握手，读取账户类型与 `account/rateLimits/read`。认证失效时调用一次 `account/read` 的 `refreshToken: true`，再重试额度查询。CLI 自带的认证恢复也可能在请求期间处理续期。刷新后的凭据保存到专用目录；服务不会发送 `thread/start` 或 `turn/start`。

**不要把同一份 refresh token 的副本交给宿主机与服务并发续期。** 推荐按上述步骤为服务单独登录。只读模式可以共享宿主机目录；自动续期模式应使用独立目录。不要并行运行多个续期服务或在续期查询期间另行登录同一目录。浏览器页面不会读取、显示或上传 token。

切换模式和升级时持续使用对应的 Compose 参数：

```bash
# 自动续期版更新 / 查看日志 / 停止
docker compose -f compose.yaml -f compose.managed.yaml up -d --build
docker compose -f compose.yaml -f compose.managed.yaml logs --tail=100 aiclock
docker compose -f compose.yaml -f compose.managed.yaml down
```

`down` 不会删除绑定的数据目录。默认轻量只读镜像没有 Codex CLI，在网页改为自动续期前，先切换到 managed 镜像。`AICLOCK_CODEX_VERSION` 可在 `.env` 修改，重新构建后生效；升级后应验证账户接口兼容性。

## 3. Linux 原生运行

需要 .NET 10 SDK。自动续期模式另外需要 Codex CLI；只读模式无需 CLI 可执行文件。

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

自动续期模式的原生登录示例（先停服务，使用独立目录）：

```bash
mkdir -p /home/你的用户/aiclock-data/codex
chmod 700 /home/你的用户/aiclock-data/codex
CODEX_HOME=/home/你的用户/aiclock-data/codex codex -c 'cli_auth_credentials_store="file"' login --device-auth
```

然后启动服务，在网页选择自动续期并填写这个专用目录。`AICLOCK_CODEX_EXECUTABLE` 可以指定 CLI 可执行文件的完整路径；不支持在此变量中填一整段 shell 命令。

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
- 默认每 120 秒查询，设置范围 60～3600 秒。手动刷新也遵守至少 60 秒节流；429 后至少等待 5 分钟。页面每 5 秒读取本地结果，设备每 5 秒读取 `/status`，都不会触发额外额度请求。
- 网络/认证失败时显示最近成功数据，并注明查询失败/数据过期；不会把未知额度显示成 0%。缓存超过 24 小时不在冷启动恢复。禁用来源、切换凭据目录/模式或凭据变化会清除对应旧数据；修改设备地址、端口或刷新间隔不会清除有效额度。
- 当前使用的供应商额度接口可能变化或限流。Claude API / Codex CLI 版本变动时，应重新检查解析和错误处理。
- 数据目录中保存 `settings.json` 和 `usage-cache.json`，不保存只读来源的 token。自动续期模式的 `data/codex/auth.json` 是实际登录凭据，不能提交到 Git 或分享。
- 页面与设备接口面向可信局域网，没有用户登录系统。需要在公网使用时，应由反向代理或 VPN 提供访问控制；现有固件通过普通 HTTP 读取状态。

## 5. petdex 使用

保存设备 IP，测试连接成功后打开“选择宠物”：搜索名称，选择宠物，再选择 Codex/Claude 插槽和动画，右侧显示实际生成的 GIF 预览。点击上传后，服务下载 WebP 精灵图，用 ImageSharp 裁剪、缩放并合成 GIF，通过设备原有 multipart 接口发送。

Claude 插槽为 111×120，Codex 插槽为 120×120；GIF 最多 8 帧，与当前固件限制一致。预览与上传使用同一份转换结果。上传时请保持设备 Wi-Fi 可达，板上解码可能需要几十秒。上传或恢复默认只影响选定插槽。

图库与素材需要访问 `assets.petdex.dev`。图库缓存 6 小时，最多缓存 32 份 GIF；未选中的图库条目不会批量下载精灵图。

## 6. API 与开发验证

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/` | 中文控制台 |
| GET | `/healthz` | 服务进程健康检查 |
| GET | `/status` | 现有 ESP8266 固件兼容数据 |
| GET | `/api/usage` | 额度、窗口、更新时间、过期状态、错误 |
| POST | `/api/usage/refresh` | 在节流允许时手动刷新 |
| GET / POST | `/api/settings` | 查询 / 保存基础设置 |
| GET | `/api/device` | 测试连接并获取设备信息 |
| POST | `/api/device/pair` | 将设备指向已保存的对外服务地址 |
| POST | `/api/device/display` | `{"mode":"auto\|claude\|codex\|dual"}` |
| POST | `/api/device/brightness` | `{"level":0到100}` |
| GET | `/api/pets?search=关键词&page=0` | 搜索 / 分页 |
| GET | `/api/pets/{slug}/preview?slot=codex&animation=idle` | GIF 预览 |
| POST | `/api/pets/{slug}/apply` | `{"slot":"codex","animation":"idle"}` |
| POST | `/api/pets/reset` | `{"slot":"codex"}` |

POST 路由要求 `Content-Type: application/json`，浏览器请求要求同源。无需参数的 POST 发送 `{}` 即可。

```bash
curl http://127.0.0.1:8765/healthz
curl http://127.0.0.1:8765/status
curl -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8765/api/usage/refresh
dotnet build AIClockBridge.Linux.csproj -c Release
dotnet run --project tests/Bridge.Tests.csproj -c Release
node --check wwwroot/app.js
```

测试使用虚构凭据、模拟 HTTP 与模拟 App Server，覆盖真实窗口长度、周额度独占窗口、缓存、原子替换凭据、429、设置变更、续期握手与重试、GIF 生成和设备表单上传；不需要账号，也不消耗模型额度。

构建、接口与浏览器可在 Windows 验证，因为服务没有 Windows 桌面依赖。Docker 镜像与 systemd 部署仍需在目标 Linux 主机上验证；ImageSharp 的许可证沿用项目现有第三方依赖，分发/商业使用请检查其条款及仓库 NOTICE。

参考：[Codex 认证](https://learn.chatgpt.com/docs/auth)、[Codex App Server 账户接口](https://learn.chatgpt.com/docs/app-server#account-endpoints)、[凭据维护与并发续期](https://learn.chatgpt.com/docs/auth/ci-cd-auth#operational-rules-that-matter)、[Docker 目录挂载](https://docs.docker.com/engine/storage/bind-mounts/)。
