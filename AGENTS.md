# Repository instructions

## Linux Docker build context

- The Linux image is built with `linux-app` as its Docker build context, and `linux-app/.dockerignore` uses a deny-all whitelist (`*`).
- Whenever a source file or asset is added to a `COPY` instruction in `linux-app/Dockerfile`, add the matching allowlist entry to `linux-app/.dockerignore` in the same change. Keep the `COPY` inputs and whitelist synchronized.
- Before committing Linux Docker build changes, verify every file referenced by `COPY` is present in the build context; run `docker compose build` when Docker is available.
