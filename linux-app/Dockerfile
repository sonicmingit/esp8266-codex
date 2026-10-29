FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY AIClockBridge.Linux.csproj ./
RUN dotnet restore
COPY Models.cs SettingsStore.cs QuotaParser.cs Credentials.cs CodexRpc.cs CodexLoginService.cs QuotaService.cs MessageScheduleService.cs DeviceService.cs DeviceScheduleService.cs PetdexService.cs Program.cs ./
COPY wwwroot ./wwwroot
RUN dotnet publish -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS readonly
WORKDIR /app
COPY --from=build /out ./
ENV TZ=Asia/Shanghai AICLOCK_DOCKER=1 AICLOCK_PORT=8765 AICLOCK_DATA_DIR=/data \
    AICLOCK_CODEX_HOME=/credentials/codex AICLOCK_CLAUDE_HOME=/credentials/claude \
    AICLOCK_CODEX_MODE=readonly DOTNET_EnableDiagnostics=0
EXPOSE 8765
# Compose overrides this user to run as root for bind-mount access.
# Direct docker run defaults to the non-root .NET application user.
USER $APP_UID
ENTRYPOINT ["dotnet", "AIClockBridge.Linux.dll"]

FROM readonly AS managed
USER root
ARG CODEX_VERSION=0.162.0
RUN apt-get update && apt-get install -y --no-install-recommends nodejs npm ca-certificates bubblewrap \
    && npm install -g @openai/codex@${CODEX_VERSION} \
    && codex --version \
    && bwrap --version \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
ENV AICLOCK_CODEX_MODE=readonly AICLOCK_CODEX_HOME=/credentials/codex AICLOCK_MANAGED_CODEX_HOME=/data/codex
USER $APP_UID
