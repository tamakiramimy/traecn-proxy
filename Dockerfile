FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build

ARG TARGETARCH
WORKDIR /src

COPY TrancnProxy.csproj ./
RUN dotnet restore TrancnProxy.csproj --runtime linux-${TARGETARCH}

COPY . ./
RUN dotnet publish TrancnProxy.csproj \
    --configuration Release \
    --runtime linux-${TARGETARCH} \
    --self-contained true \
    --no-restore \
    --output /app/publish \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:PublishTrimmed=false

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy

ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="trancn-proxy" \
      org.opencontainers.image.description="Trae CN enterprise OpenAI/Anthropic compatible proxy" \
      org.opencontainers.image.version="${VERSION}"

WORKDIR /app
COPY --from=build /app/publish ./

RUN chmod +x /app/trancn-proxy \
    && sed -i \
        -e 's/"Listen": "127.0.0.1"/"Listen": "0.0.0.0"/' \
        -e 's#"DataDirectory": ""#"DataDirectory": "/data"#' \
        -e 's/"Enabled": true/"Enabled": false/' \
        /app/appsettings.json \
    && mkdir -p /data /home/app \
    && chown -R app:app /app /data /home/app

ENV HOME=/home/app \
    DOTNET_RUNNING_IN_CONTAINER=true

USER app
EXPOSE 9220
VOLUME ["/data"]
ENTRYPOINT ["/app/trancn-proxy"]