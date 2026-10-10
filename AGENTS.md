# 仓库协作规则

## Linux Docker 构建上下文

- Linux 镜像以 `linux-app` 作为 Docker 构建上下文，`linux-app/.dockerignore` 使用了默认排除、逐项放行的白名单（`*`）。
- 在 `linux-app/Dockerfile` 的 `COPY` 指令中新增源文件或资源时，必须在同一次修改中为其补充 `linux-app/.dockerignore` 放行规则，保持两处清单一致。
- 提交 Linux Docker 构建相关修改前，检查每个 `COPY` 引用的文件都已包含在构建上下文中；环境支持 Docker 时，运行 `docker compose build` 验证。
