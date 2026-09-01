# syntax=docker/dockerfile:1
# 使用 GitHub Release 中已发布的自包含 linux 产物，需先执行 scripts/prepare-docker-artifacts.ps1
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy

ARG TARGETARCH
ARG VERSION=0.0.0

LABEL org.opencontainers.image.title="trancn-proxy" \
      org.opencontainers.image.description="Trae CN 企业版 -> OpenAI/Anthropic 兼容代理" \
      org.opencontainers.image.source="https://github.com/tamakiramimy/traecn-proxy" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

WORKDIR /app
COPY .artifacts/publish/${TARGETARCH}/ ./

# 容器内没有 Trae IDE，可监听全部网卡并把账号数据固定到 /data
RUN set -eux; \
    chmod +x /app/trancn-proxy; \
    sed -i \
        -e 's/"Listen": "127.0.0.1"/"Listen": "0.0.0.0"/' \
        -e 's#"DataDirectory": ""#"DataDirectory": "/data"#' \
        -e 's/"Enabled": true/"Enabled": false/' \
        /app/appsettings.json; \
    mkdir -p /data /home/app; \
    chown -R app:app /app /data /home/app

ENV HOME=/home/app \
    DOTNET_RUNNING_IN_CONTAINER=true

USER app
EXPOSE 9220
VOLUME ["/data"]
ENTRYPOINT ["/app/trancn-proxy"]
