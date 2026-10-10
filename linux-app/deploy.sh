#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
mode="${1:-readonly}"
if [[ "$mode" != readonly && "$mode" != managed ]]; then
  printf '用法：bash deploy.sh [readonly|managed]\n' >&2
  exit 1
fi
command -v docker >/dev/null || { printf '请先安装 Docker Engine 和 Compose 插件。\n' >&2; exit 1; }
docker compose version >/dev/null
if [[ ! -f .env ]]; then
  task_user_home="$(getent passwd "$(id -u)" | cut -d: -f6)"
  task_codex_path="$PWD/data/empty-codex"
  task_claude_path="$PWD/data/empty-claude"
  [[ ! -d "$task_user_home/.codex" ]] || task_codex_path="$task_user_home/.codex"
  [[ ! -d "$task_user_home/.claude" ]] || task_claude_path="$task_user_home/.claude"
  # Single-quoted dotenv values preserve spaces and literal $ characters.
  if [[ "$PWD$task_codex_path$task_claude_path" == *"'"* ]]; then
    printf '目录含单引号，请手动复制 .env.example 配置 .env。\n' >&2; exit 1
  fi
  umask 077
  {
    printf 'AICLOCK_HOST_PORT=8765\nAICLOCK_UID=%s\nAICLOCK_GID=%s\n' "$(id -u)" "$(id -g)"
    printf "AICLOCK_DATA_PATH='%s/data'\n" "$PWD"
    printf "AICLOCK_CODEX_PATH='%s'\nAICLOCK_CLAUDE_PATH='%s'\n" "$task_codex_path" "$task_claude_path"
    printf 'AICLOCK_ADVERTISED_HOST=\nAICLOCK_CODEX_VERSION=0.162.0\n'
  } > .env
  printf '已生成 .env，可在其中调整目录与对外端口。\n'
fi
mkdir -p data/empty-codex data/empty-claude data/codex
chmod 700 data/codex
task_compose=(docker compose -f compose.yaml)
[[ "$mode" != managed ]] || task_compose+=(-f compose.managed.yaml)
"${task_compose[@]}" up -d --build
printf '部署完成。访问 http://本机局域网IP:对外端口/，默认端口 8765。\n'
printf '容器时区：北京时间 Asia/Shanghai（UTC+8），所有定时任务按北京时间执行。\n'
if [[ "$mode" == managed ]]; then
  printf '首次自动续期部署：运行 bash codex-login.sh，在浏览器完成独立登录。\n'
  printf '若已有 data/settings.json，请在网页将 Codex 模式改为自动续期，目录改为 /data/codex。\n'
fi
