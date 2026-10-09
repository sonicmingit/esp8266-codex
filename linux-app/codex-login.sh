#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
# Stop background credential access while the dedicated service account logs in.
# A one-off managed container uses the same /data volume and user, with no ports.
task_compose=(docker compose -f compose.yaml -f compose.managed.yaml)
"${task_compose[@]}" stop aiclock
trap '"${task_compose[@]}" up -d aiclock' EXIT
"${task_compose[@]}" run --rm --no-deps --entrypoint codex aiclock \
  -c 'cli_auth_credentials_store="file"' login --device-auth
printf '登录完成，服务重新启动后会自动查询额度。\n'
