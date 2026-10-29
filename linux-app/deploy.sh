#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if [[ $# -gt 1 || ( -n "${1:-}" && "${1:-}" != readonly && "${1:-}" != managed ) ]]; then
  printf '用法：bash deploy.sh [readonly|managed]\n旧参数会兼容接受，但现在部署始终包含两种模式。\n' >&2
  exit 1
fi
if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  printf '当前目录不在 Git 仓库中，无法更新代码。\n' >&2; exit 1
fi
previous_revision="$(git rev-parse HEAD)"
git pull --ff-only
current_revision="$(git rev-parse HEAD)"
if [[ "$previous_revision" == "$current_revision" ]]; then
  deploy_without_updates=''
  printf 'Git 仓库没有新提交。是否仍要继续部署？[y/N] ' >&2
  if ! read -r deploy_without_updates; then deploy_without_updates=''; fi
  case "$deploy_without_updates" in
    y|Y|yes|YES) ;;
    *) printf '已取消部署。\n'; exit 0 ;;
  esac
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
    printf 'AICLOCK_HOST_PORT=8765\n'
    printf "AICLOCK_DATA_PATH='%s/data'\n" "$PWD"
    printf "AICLOCK_CODEX_PATH='%s'\nAICLOCK_CLAUDE_PATH='%s'\n" "$task_codex_path" "$task_claude_path"
    printf 'AICLOCK_ADVERTISED_HOST=\nAICLOCK_CODEX_VERSION=0.162.0\n'
  } > .env
  printf '已生成 .env，可在其中调整目录与对外端口。\n'
fi
mkdir -p data/empty-codex data/empty-claude
mkdir -p -m 700 data/codex
docker compose -f compose.yaml up -d --build
printf '部署完成。访问 http://本机局域网IP:对外端口/，默认端口 8765。\n'
printf '容器时区：北京时间 Asia/Shanghai（UTC+8），所有定时任务按北京时间执行。\n'
printf '两种 Codex 模式已包含在同一部署中；请在网页切换模式并在自动续期模式下启动登录。\n'
