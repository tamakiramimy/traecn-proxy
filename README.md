# trancn-proxy

> 非官方实验项目。将已获授权的 Trae CN 企业账号接入为基础 OpenAI / Anthropic 兼容 API。

`trancn-proxy` 读取本机 Trae CN 登录状态，通过本地 HTTP 服务转发文本对话请求，便于接入兼容 OpenAI 或 Anthropic API 的工具。

## 已知限制

- 可选模型通过当前运行中的 TRAE IDE Agent 调用，而不是旧的 `llm_utils_chat` HTTP 端点。TRAE 必须使用 `--remote-debugging-port=9333` 启动并保持登录。
- 首次 Agent 请求会自动安装页面初始化 hook 并重载一次 TRAE workbench。请求通过同一个 IDE UI 串行执行，并会在 TRAE 中创建对应任务记录。
- 独立 headless Agent 协议仍处于取证与可行性验证阶段，尚未通过真实验收；当前 bridge 不能作为 Linux Docker 部署方案。
- `llm_utils_chat` 可以直接以 HTTP/SSE 对话，但已验证它会忽略指定模型并回退，因此不能承载精确选模。项目已实现纯 .NET 的 CUE Agent task SSE 客户端和模型确认状态机，但远端 session 创建契约尚未验证；调用者无需打开目录或操作 TRAE UI，尚未通过该闸门前也不会将 Docker headless 声称为可用。
- Agent bridge 始终使用当前 TRAE IDE 登录账号。账号池的会话粘滞和负载均衡不会切换 bridge 身份；请求模型不属于当前 IDE 账号时会明确失败。
- 代理会校验 TRAE 返回的实际模型 metadata。实际模型与请求 ID 不一致时返回 `model_selection_mismatch`，不会静默回退到其他模型。
- 仅验证了基础文本对话。工具调用、复杂多模态输入、完整 Anthropic 交错消息规则及思考内容分离尚未实现。
- macOS 已实测。Windows 发布包可用，但 Trae CN 的本地数据读取尚未完成端到端验证。

完整的调研与协议说明见 [docs/design.md](docs/design.md)。

## 下载与运行

在 [GitHub Releases](https://github.com/tamakiramimy/traecn-proxy/releases) 下载与平台匹配的自包含 ZIP：

- `trancn-proxy-v*-osx-arm64.zip`：Apple Silicon Mac
- `trancn-proxy-v*-osx-x64.zip`：Intel Mac
- `trancn-proxy-v*-win-x64.zip`：Windows x64
- `trancn-proxy-v*-win-arm64.zip`：Windows ARM64

解压后运行 `trancn-proxy`（Windows 为 `trancn-proxy.exe`）。macOS 首次运行如被系统拦截，可在确认来源可信后移除隔离属性：

```bash
xattr -dr com.apple.quarantine trancn-proxy
./trancn-proxy
```

也可从源码运行，需安装 .NET 8 SDK：

```bash
open -a "Trae CN" --args --remote-debugging-port=9333
dotnet run
```

如果 TRAE 已在运行，请先完全退出后再用上述参数启动。可通过 `GET /v1/status` 的 `ide_bridge.available` 确认 bridge 是否就绪。

## Docker

多架构镜像（`linux/amd64`、`linux/arm64`）已发布到 Docker Hub，自下一个 Release 起同时发布到 GitHub Container Registry：

```bash
docker pull tamakiramimy/traecn-proxy:latest
# Docker Hub 拉取失败时的备用源
docker pull ghcr.io/tamakiramimy/traecn-proxy:latest
```

```bash
docker run -d --name trancn-proxy \
  -p 9220:9220 \
  -v trancn-data:/data \
  -e TRANCN_API_KEY=请替换为你的网关密钥 \
  -e TRANCN_ADMIN_KEY=请替换为你的管理密钥 \
  tamakiramimy/traecn-proxy:0.3.1
```

镜像内的 `appsettings.json` 已将 `Server.Listen` 设为 `0.0.0.0`、`Accounts.DataDirectory` 设为 `/data`，并关闭 IdeBridge（容器内没有 Trae IDE，仅走 Standalone 模式）。首次启动会在日志中打印网页授权地址，复制到浏览器完成登录后，凭据保存在 `/data` 卷中。请务必通过环境变量覆盖默认密钥，不要在未鉴权的情况下把端口暴露到公网。

自行构建镜像（基于 Release 中的自包含产物）：

```powershell
pwsh scripts/prepare-docker-artifacts.ps1 -Version v0.3.1
docker buildx build --platform linux/amd64,linux/arm64 `
  --build-arg VERSION=0.3.1 `
  -t <账号>/traecn-proxy:0.3.1 --push .
```

## 配置

启动目录中的 `appsettings.json` 用于服务配置，发布产物会自带不含密钥的默认文件：

```json
{
	"Server": {
		"Port": 9220,
		"Listen": "127.0.0.1",
		"PublicBaseUrl": "https://proxy.example.com"
	},
	"Security": {
		"ApiKey": "业务接口密钥",
		"AdminKey": "管理端密钥"
	},
	"Accounts": {
		"DataDirectory": ""
	},
	"IdeBridge": {
		"Enabled": true,
		"DebugEndpoint": "http://127.0.0.1:9333",
		"RequestTimeoutSeconds": 300,
		"PollIntervalMilliseconds": 35
	}
}
```

`Accounts:DataDirectory` 留空时使用 `~/.config/trancn-proxy`。覆盖优先级为：命令行参数最高，其次是环境变量，最后是 `appsettings.json`。环境变量 `TRANCN_API_KEY`、`TRANCN_ADMIN_KEY`、`TRANCN_PUBLIC_BASE_URL` 分别覆盖对应配置项。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| `--port <port>` | 监听端口，默认 `9220` |
| `--listen <address>` | 监听地址，默认 `127.0.0.1` |
| `--api-key <key>` | 设置网关 API Key |
| `--login` | 强制从 Trae CN 本地存储重新读取授权 |
| `--weblogin` | 使用独立网页授权，不依赖 Trae CN IDE |
| `--test` | 发送一条真实对话自测 |
| `--model <model>` | 配合 `--test` 指定模型，默认 `Doubao-Seed-Evolving` |
| `--account <alias>` | 指定网页登录、IDE 导入或自测使用的账号别名，默认 `default` |
| `--account-list` | 列出本地已保存的账号 |
| `--account-import <file>` | 导入单个账号或账号数组 JSON 文件 |
| `--data-dir <path>` | 覆盖账号库目录，默认 `~/.config/trancn-proxy` |
| `--public-base-url <url>` | OAuth 回调对浏览器可访问的服务根地址 |
| `--protocol-evidence-dir <path>` | 开发期协议取证目录；必须位于当前工作区外，仅写入脱敏 JSONL |
| `--tc-test` | 校验本地 `tc` 加解密实现 |

网关 Key 也可由 `TRANCN_API_KEY` 环境变量提供。管理端使用独立的 `TRANCN_ADMIN_KEY`。企业网络需要代理时，程序会读取 `HTTPS_PROXY` 或 `HTTP_PROXY`。

## 开发期协议取证

该开关仅用于开发阶段确认普通 Agent 的真实会话创建和流式协议，不属于生产部署能力。以带调试端口的 TRAE 启动后，使用一次无敏感内容的短消息：

```bash
dotnet run -- --protocol-evidence-dir /tmp/trae-protocol-evidence
```

bridge 会记录活动请求关联的 Aha `request_stream` 出入站 envelope。文件保存在指定目录，内容仅保留字段结构、事件、方法、模型和同次运行可关联的伪匿名 ID；token、用户、设备、路径、消息正文和其他字符串值会被移除或替换。录制完成后应先扫描文件确认没有敏感信息，再将合成/脱敏 fixture 提交到测试目录。不要提交原始 HAR、TRAE 二进制、bundle 或录制文件。

## 多账号与管理端

代理对外仍只提供一个 `/v1` 入口，账号池用于模型目录、授权维护和旧 HTTP 路径。可选 Agent 模型的实际推理由当前 TRAE IDE 登录账号执行，同一时间只处理一个 bridge 请求。

账号库位于 `accounts.json`，首次运行会自动迁移旧的 `auth.json` 为 `default` 账号。JSON 可通过 `--account-import` 或管理端导入。多账号推荐使用独立网页授权，避免与 IDE 登录态竞争 refresh token。

设置 `TRANCN_ADMIN_KEY` 后访问 `http://127.0.0.1:9220/admin`。管理端支持账号列表、JSON 导入、启停、刷新、测试、删除和 Trae 网页登录。远程部署网页登录时必须配置浏览器可访问的 `--public-base-url https://proxy.example.com`。

sub2api 只需配置一个 OpenAI 兼容上游：

```text
base_url: http://trancn-proxy:9220/v1
api_key: <TRANCN_API_KEY>
```

建议先请求 `/v1/models` 获取当前账号的精确 ID。2026-08-27 已实测：

- `glm-5.3__dev`
- `DeepSeek-V4-Pro-Official__dev`
- `kimi-k2.7-code__dev`

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/v1/status` | 服务和授权状态 |
| `GET` | `/v1/models` | 企业模型目录 |
| `POST` | `/v1/chat/completions` | OpenAI Chat Completions，支持流式和非流式 |
| `POST` | `/v1/responses` | OpenAI Responses，支持流式和非流式 |
| `POST` | `/v1/messages` | Anthropic Messages，支持流式和非流式 |

示例：

```bash
curl http://127.0.0.1:9220/v1/chat/completions \
	-H 'Authorization: Bearer your-gateway-key' \
	-H 'Content-Type: application/json' \
	-d '{"model":"glm-5.3__dev","messages":[{"role":"user","content":"你好"}]}'
```

## 安全与合规

- 会读取、缓存并在非独立授权模式下可能回写 Trae CN 的 access token 与 refresh token。缓存文件为 `~/.config/trancn-proxy/auth.json`，非 Windows 平台会尝试设置为仅当前用户可读写。
- 默认仅监听 `127.0.0.1`。使用 `--listen 0.0.0.0` 前必须设置 `--api-key` 或 `TRANCN_API_KEY`，并自行配置访问控制。
- 管理端必须设置独立且强随机的 `TRANCN_ADMIN_KEY`；OAuth 回调只接受五分钟内创建的 `state`，不会接受任意回调写入账号。
- 本项目与 Trae、字节跳动无隶属关系。使用前请确认符合组织 IT 政策、账号授权范围与服务条款；不要公开暴露企业账号或令牌。
