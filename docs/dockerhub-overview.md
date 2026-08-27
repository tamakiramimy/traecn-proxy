# trancn-proxy

> 非官方实验项目。把已获授权的 Trae CN 企业账号接入为基础的 OpenAI / Anthropic 兼容 API。
>
> Unofficial experimental project. Exposes an authorized Trae CN enterprise account through a minimal OpenAI / Anthropic compatible API.

- **源码 / Issue / Release**：<https://github.com/tamakiramimy/traecn-proxy>
- **镜像构建**：由仓库的 [Release workflow](https://github.com/tamakiramimy/traecn-proxy/blob/main/.github/workflows/release.yml) 基于 GitHub Release 中的 .NET 8 自包含产物自动构建
- **许可证**：[MIT](https://github.com/tamakiramimy/traecn-proxy/blob/main/LICENSE)

## 支持的标签

| 标签 | 说明 |
| --- | --- |
| `latest` | 最新正式版本 |
| `0.3.1`、`0.3` | 具体版本 / 次版本浮动标签 |

架构：`linux/amd64`、`linux/arm64`。

镜像同时发布到 GitHub Container Registry，Docker Hub 拉不动时可以换源：

```bash
docker pull ghcr.io/tamakiramimy/traecn-proxy:latest
```

如果 `docker pull` 报 `EOF` 或 TLS 超时，通常是本机 Docker 守护进程没有走代理（浏览器能打开 Docker Hub 不代表 daemon 能出网）。在 Docker Desktop 的 Settings → Resources → Proxies 里手动填入代理地址后重启即可。

## 能力与限制（请先阅读）

已在容器内实测可用（企业账号，`chat_v3` 通道直连上游）：模型目录、**精确选模**、OpenAI / Anthropic 端点、流式输出、多账号管理。上游实际模型与所选不符时会直接报错，不会静默降级。

限制：

- 容器内没有 Trae CN IDE，镜像默认关闭 `IdeBridge`，**依赖 IDE Agent bridge 的能力不可用**（该协议仍在取证阶段）。
- SOLO / 消费版服务面（`solo_work_lite`）按 `config_name` 选模，与企业面的 `__dev` / `__max` ID 不通用；该面在容器内尚未做完整回归。
- 仅验证了基础文本对话；工具调用、多模态、完整 Anthropic 交错消息规则尚未实现。
- 本项目与 Trae、字节跳动无隶属关系。使用前请确认符合组织 IT 政策、账号授权范围与服务条款。

## 快速开始

```bash
docker run -d --name trancn-proxy \
  -p 9220:9220 \
  -v trancn-data:/data \
  -e TRANCN_API_KEY=替换为强随机网关密钥 \
  -e TRANCN_ADMIN_KEY=替换为强随机管理密钥 \
  tamakiramimy/traecn-proxy:latest
```

首次启动后容器里还没有账号，打开管理台 <http://127.0.0.1:9220/admin> ，填入 `TRANCN_ADMIN_KEY` 连接后，在「添加网页登录账号」填一个别名并完成 Trae CN 授权。重复该步骤可添加多个账号，凭据保存在 `/data` 卷中，重启不丢。

管理台还支持按账号启停、优先级、Token 刷新与校验、**逐个模型发一句测试对话**（会回显上游实际模型）、JSON 批量导入。

验证服务：

```bash
curl -H "Authorization: Bearer $TRANCN_API_KEY" http://127.0.0.1:9220/v1/models
```

## 镜像内的默认配置

镜像基于 `mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy`，以非 root 用户 `app` 运行，并对发布产物中的 `appsettings.json` 做了三处容器化调整：

| 配置项 | 镜像内默认值 | 原因 |
| --- | --- | --- |
| `Server.Listen` | `0.0.0.0` | 容器需要对外暴露端口 |
| `Accounts.DataDirectory` | `/data` | 账号库落在数据卷上 |
| `IdeBridge.Enabled` | `false` | 容器内不存在 Trae CN IDE |

- 端口：`9220`
- 数据卷：`/data`（`accounts.json`、`instance.lock` 等）

## 环境变量

| 变量 | 说明 |
| --- | --- |
| `TRANCN_API_KEY` | `/v1` 业务接口密钥。**留空即不鉴权**，切勿在未设置时暴露端口 |
| `TRANCN_ADMIN_KEY` | `/admin` 管理端密钥，需与业务密钥不同且强随机 |
| `TRANCN_PUBLIC_BASE_URL` | 远程部署时浏览器可访问的服务根地址，用于 OAuth 回调 |
| `TRANCN_CHAT_API_HOST` | 覆盖上游 chat API 主机 |
| `HTTPS_PROXY` / `HTTP_PROXY` | 企业网络出站代理 |

也可以挂载自己的配置覆盖默认值：

```bash
-v ./appsettings.json:/app/appsettings.json:ro
```

命令行参数同样可用，直接追加在镜像名之后即可，例如 `--account-list`、`--port 9300`。

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/v1/status` | 服务与授权状态 |
| `GET` | `/v1/models` | 企业模型目录 |
| `POST` | `/v1/chat/completions` | OpenAI Chat Completions（流式 / 非流式） |
| `POST` | `/v1/responses` | OpenAI Responses（流式 / 非流式） |
| `POST` | `/v1/messages` | Anthropic Messages（流式 / 非流式） |
| `GET` | `/admin` | 管理端（需 `TRANCN_ADMIN_KEY`） |

```bash
curl http://127.0.0.1:9220/v1/chat/completions \
  -H "Authorization: Bearer $TRANCN_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"glm-5.3__dev","messages":[{"role":"user","content":"你好"}]}'
```

建议先调用 `/v1/models` 获取当前账号可用的精确模型 ID。

## docker compose

```yaml
services:
  trancn-proxy:
    image: tamakiramimy/traecn-proxy:latest
    restart: unless-stopped
    ports:
      - "9220:9220"
    environment:
      TRANCN_API_KEY: ${TRANCN_API_KEY:?set a strong key}
      TRANCN_ADMIN_KEY: ${TRANCN_ADMIN_KEY:?set a strong key}
    volumes:
      - trancn-data:/data

volumes:
  trancn-data:
```

## 安全提示

- 容器监听 `0.0.0.0`，**必须**设置 `TRANCN_API_KEY` 与 `TRANCN_ADMIN_KEY` 后再映射端口，公网暴露请额外配置反向代理与访问控制。
- `/data` 中保存的是 Trae CN 的 access / refresh token，请按凭据资产对待，不要备份到公开位置。
- 镜像不包含任何账号或密钥，所有授权数据都在你自己的卷里。
