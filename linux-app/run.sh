#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
export AICLOCK_DATA_DIR="${AICLOCK_DATA_DIR:-$PWD/data}"
dotnet publish AIClockBridge.Linux.csproj -c Release -o bin/publish --nologo
cd bin/publish
exec dotnet AIClockBridge.Linux.dll
