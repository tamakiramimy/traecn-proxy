# trancn-proxy

> 非官方实验项目。将已获授权的 Trae CN 企业账号接入为基础 OpenAI / Anthropic 兼容 API。

`trancn-proxy` **独立运行，不需要安装或启动 Trae CN IDE**：通过网页 OAuth 授权拿到企业账号凭据后，直接以 HTTP/SSE 调用上游对话接口，对外提供 OpenAI / Anthropic 兼容 API。也可以从本机已登录的 Trae CN 导入账号。

## 当前能力

- 对话走企业控制面 `chat_v3` 通道直连上游，**精确选模有效**：代理会校验上游回传的实际模型 metadata，不一致时返回 `model_selection_mismatch`，不会静默回退到其他模型。
- 支持多账号池（网页登录逐个添加）、会话粘滞、优先级/并发均衡调度、Token 自动刷新。
- Anthropic Messages 已支持流式与非流式输出、`tool_use` / `tool_result`，并按模型分类 `reasoning_content`：GLM/Kimi/DeepSeek/Qwen 中的工具协议转为工具块，Markdown/代码转为正文，显式 `<think>` 才转为 thinking。
- 已在 Linux 容器中实测可用：模型目录、精确选模、OpenAI / Anthropic 端点、流式输出、管理端逐模型测试。

## 能力与限制

- OpenAI Responses 目前具备基础文本转发与流式封装，尚待 Codex 客户端端到端验证。
- Anthropic `thinking.budget_tokens`、OpenAI `reasoning_effort` 与 Responses `reasoning.effort` 已映射到 Trae 的 `high` / `extra_high`；`adaptive` 保留 Trae 默认强度。字段已进入低层请求体，但仍需按模型做真实行为对照，不能仅凭 HTTP 成功认定上游已执行该强度。
- Claude Desktop 与其他自动执行工具的客户端仍需进行受控长会话回归；代理会拒绝缺少 `input_schema.required` 参数的工具调用，避免将无效 `tool_use` 交给客户端反复执行。
- 复杂多模态输入与完整 Anthropic 交错消息规则尚未覆盖。
- SOLO / 消费版服务面（`solo_work_lite`）按 `config_name` 选模，与企业面的 `__dev` / `__max` ID 不通用，尚未做完整回归。
- 账号类型目前只支持显式声明与按 `Upstream:ChatApiHost` 推断，尚未实现按租户信息推断或端点探测兜底。
- IDE Bridge 已不参与对话，仅保留给开发期协议取证（见下文），需要 TRAE 以 `--remote-debugging-port=9333` 启动才可用；`GET /v1/status` 的 `ide_bridge` 字段仅反映该取证通道状态。
- macOS 与 Linux 容器已实测。Windows 发布包可用，但 Trae CN 的本地数据读取尚未完成端到端验证。
- 本项目与 Trae、字节跳动无隶属关系。使用前请确认符合组织 IT 政策、账号授权范围与服务条款。

完整的调研与协议说明见 [docs/design.md](docs/design.md)。

## Docker 部署

镜像支持 `linux/amd64` 和 `linux/arm64`：

- `tamakiramimy/traecn-proxy:latest`：最新正式版本
- `tamakiramimy/traecn-proxy:0.5.0`：固定版本
- `ghcr.io/tamakiramimy/traecn-proxy:latest`：Docker Hub 不可用时的备用源

### Docker Run

```bash
docker run -d --name traecn-proxy \
	--restart unless-stopped \
	-p 127.0.0.1:9220:9220 \
	-v traecn-data:/data \
	-e TRANCN_API_KEY=替换为强随机网关密钥 \
	-e TRANCN_ADMIN_KEY=替换为不同的强随机管理密钥 \
	tamakiramimy/traecn-proxy:latest
```

### Docker Compose

```yaml
services:
	traecn-proxy:
		image: tamakiramimy/traecn-proxy:latest
		container_name: traecn-proxy
		restart: unless-stopped
		ports:
			- "127.0.0.1:9220:9220"
		environment:
			TRANCN_API_KEY: ${TRANCN_API_KEY:?set a strong gateway key}
			TRANCN_ADMIN_KEY: ${TRANCN_ADMIN_KEY:?set a different strong admin key}
			# 宿主机端口不是 9220 时必须显式设置，否则 OAuth 会回调到错误端口。
			TRANCN_PUBLIC_BASE_URL: ${TRANCN_PUBLIC_BASE_URL:-http://127.0.0.1:9220}
		volumes:
			- ./data:/data
```

首次启动后打开 [http://127.0.0.1:9220/admin](http://127.0.0.1:9220/admin)，使用 `TRANCN_ADMIN_KEY` 连接管理端，在“添加网页登录账号”中填写账号别名和最大并发并完成 Trae CN 授权。新账号默认最大并发为 10，可在添加时覆盖，也可在账号池中随时调整。可重复添加多个账号；授权凭据和账号设置保存在 `/data`，容器重启后仍会保留。

如果改用其他宿主机端口，例如 `127.0.0.1:10005:9220`，请同时设置 `TRANCN_PUBLIC_BASE_URL=http://127.0.0.1:10005`，并从该地址打开管理端重新发起授权。不要复用修改配置前生成的 OAuth 回调链接。

验证账号和模型目录：

```bash
curl http://127.0.0.1:9220/v1/models \
	-H "Authorization: Bearer $TRANCN_API_KEY"
```

容器内默认配置如下：

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `Server.Listen` | `0.0.0.0` | 允许 Docker 发布容器端口 |
| `Server.Port` | `9220` | API 与管理端口 |
| `Accounts.DataDirectory` | `/data` | 持久化账号和实例锁 |
| `IdeBridge.Enabled` | `false` | 开发期取证功能，生产链路不需要 |

支持的环境变量：

| 环境变量 | 说明 |
| --- | --- |
| `TRANCN_API_KEY` | `/v1` 业务接口密钥，容器部署必须设置 |
| `TRANCN_ADMIN_KEY` | `/admin` 管理端密钥，必须与业务密钥不同 |
| `TRANCN_PUBLIC_BASE_URL` | 远程部署时浏览器可访问的 OAuth 回调根地址 |
| `TRANCN_CHAT_API_HOST` | 覆盖上游 chat API 主机 |
| `HTTPS_PROXY` / `HTTP_PROXY` | 企业网络出站代理 |

## 本机发布包

在 [GitHub Releases](https://github.com/tamakiramimy/traecn-proxy/releases) 下载与平台匹配的自包含 ZIP：

- `trancn-proxy-v*-osx-arm64.zip`：Apple Silicon Mac
- `trancn-proxy-v*-osx-x64.zip`：Intel Mac
- `trancn-proxy-v*-win-x64.zip`：Windows x64
- `trancn-proxy-v*-win-arm64.zip`：Windows ARM64
- `trancn-proxy-v*-linux-x64.zip` / `trancn-proxy-v*-linux-arm64.zip`：Linux

解压后运行 `trancn-proxy`（Windows 为 `trancn-proxy.exe`）。macOS 首次运行如被系统拦截，可在确认来源可信后移除隔离属性：

```bash
xattr -dr com.apple.quarantine trancn-proxy
./trancn-proxy
```

首次启动没有账号时会自动打开浏览器完成 Trae CN 网页授权；也可以显式指定：

```bash
./trancn-proxy --weblogin          # 独立网页授权，不依赖 Trae CN IDE
./trancn-proxy --login             # 从本机已登录的 Trae CN 导入账号
```

设置了 `TRANCN_ADMIN_KEY` 时，零账号也会直接启动服务，改为在 `/admin` 网页里添加账号（远程与容器部署用这种方式）。

也可从源码运行，需安装 .NET 8 SDK：

```bash
dotnet run -- --weblogin
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
		"Enabled": false,
		"DebugEndpoint": "http://127.0.0.1:9333",
		"RequestTimeoutSeconds": 300,
		"PollIntervalMilliseconds": 35
	}
}
```

`Accounts:DataDirectory` 留空时使用 `~/.config/trancn-proxy`。覆盖优先级为：命令行参数最高，其次是环境变量，最后是 `appsettings.json`。环境变量 `TRANCN_API_KEY`、`TRANCN_ADMIN_KEY`、`TRANCN_PUBLIC_BASE_URL` 分别覆盖对应配置项。

`IdeBridge` 只服务于开发期协议取证，对话链路不会用到它；不做取证时可以设为 `false`（Docker 镜像内已默认关闭）。

## 服务面与账号类型

上游有两套不兼容的服务面，每个账号由 `accounts.json` 中的 `kind` 字段决定用哪一套：

| `kind` | chat 通道 | 模型目录端点 | 模型 ID 语义 |
| --- | --- | --- | --- |
| `enterprise` | `chat_v3` | `/api/ide/v1/batch_get_detail_param` | `model` 与 `config_name` 分离（如 `glm-5.3__dev` / `glm-5.3`） |
| `solo` | `solo_work_lite` | `/api/ide/v1/get_detail_param` | 两者都用 `config_name` |
| `auto`（默认） | 按是否配置了独立 `Upstream:ChatApiHost` 推断 | | |

同一个账号池里可以混用两种类型，各自使用自己的通道、目录与默认模型，互不影响。`Upstream:DefaultAccountKind` 决定新建账号的默认类型。

客户端画像（版本号、设备品牌、OS 版本）已可配置，上游要求新版本时改配置即可，不必重新发版：

```json
{
	"Upstream": {
		"ChatApiHost": "",
		"DefaultAccountKind": "auto",
		"Enterprise": { "IdeVersion": "3.3.87", "IdeVersionCode": "20260806" },
		"Solo": { "IdeVersion": "0.1.43", "IdeVersionCode": "20260716", "DeviceBrand": "83DG" },
		"Reasoning": {
			"ExtraHighBudgetThreshold": 8192,
			"CarrierModelPatterns": [ "glm", "kimi", "deepseek", "qwen" ],
			"NativeThinkingModelPatterns": [],
			"NativeThinkingWhenEnabledModelPatterns": [ "kimi-k3" ]
		}
	}
}
```

留空的客户端画像字段沿用内置默认值；企业面的设备信息默认取本机环境，SOLO 面固定使用该服务接受的 SOLO 客户端形态。

`ExtraHighBudgetThreshold` 是 Anthropic `enabled` thinking 从 `high` 切换到 `extra_high` 的阈值；大于阈值时使用 `extra_high`。`CarrierModelPatterns` 表示这些模型会把正文和工具协议承载在 `reasoning_content` 中，因此代理会重新分类；`NativeThinkingModelPatterns` 优先级更高，可把命中的模型强制恢复为原生 thinking 语义。`NativeThinkingWhenEnabledModelPatterns` 仅在客户端启用 thinking 时生效，未启用时仍保留 carrier 回退。

模型 ID 为 `__max` 时，代理发送 `model_auto_selection.strategy=max`；`context_window_size` 仅使用模型目录明确给出的 `context_window_tokens.max`。普通 `__dev` 模型对应 `manual` 和目录中的 dev 窗口，不会把 DeepSeek-V4-Flash 的 112k/200k 硬套到其他模型。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| `--port <port>` | 监听端口，默认 `9220` |
| `--listen <address>` | 监听地址，默认 `127.0.0.1` |
| `--api-key <key>` | 设置网关 API Key |
| `--login` | 强制从 Trae CN 本地存储重新读取授权 |
| `--weblogin` | 使用独立网页授权，不依赖 Trae CN IDE |
| `--test` | 发送一条真实对话自测 |
| `--model <model>` | 配合 `--test` 指定模型，缺省时用当前账号服务面的默认模型 |
| `--account <alias>` | 指定网页登录、IDE 导入或自测使用的账号别名，默认 `default` |
| `--account-list` | 列出本地已保存的账号 |
| `--account-import <file>` | 导入单个账号或账号数组 JSON 文件 |
| `--data-dir <path>` | 覆盖账号库目录，默认 `~/.config/trancn-proxy` |
| `--public-base-url <url>` | OAuth 回调对浏览器可访问的服务根地址 |
| `--protocol-evidence-dir <path>` | 开发期协议取证目录；必须位于当前工作区外，仅写入脱敏 JSONL |
| `--tc-test` | 校验本地 `tc` 加解密实现 |

网关 Key 也可由 `TRANCN_API_KEY` 环境变量提供。管理端使用独立的 `TRANCN_ADMIN_KEY`。企业网络需要代理时，程序会读取 `HTTPS_PROXY` 或 `HTTP_PROXY`。

## 开发期协议取证

IDE Bridge 仅用于开发阶段的协议取证，不属于生产部署能力，也不影响 `chat_v3` 容器链路。需要取证时，以调试端口启动 TRAE，并使用一次无敏感内容的短消息：

```bash
open -a "Trae CN" --args --remote-debugging-port=9333
dotnet run -- --protocol-evidence-dir /tmp/trae-protocol-evidence
```

bridge 会记录活动请求关联的 Aha `request_stream` 出入站 envelope。文件保存在指定目录，内容仅保留字段结构、事件、方法、模型和同次运行可关联的伪匿名 ID；token、用户、设备、路径、消息正文和其他字符串值会被移除或替换。录制完成后应先扫描文件确认没有敏感信息，再将合成/脱敏 fixture 提交到测试目录。不要提交原始 HAR、TRAE 二进制、bundle 或录制文件。

## 多账号与管理端

代理对外只提供一个 `/v1` 入口。每个请求在完整响应周期内锁定一个账号，模型目录、对话与授权维护都走该账号自己的凭据，不依赖任何 IDE 登录态。

账号库位于 `accounts.json`，首次运行会自动迁移旧的 `auth.json` 为 `default` 账号。JSON 可通过 `--account-import` 或管理端导入。多账号推荐使用网页授权逐个添加，避免与 IDE 登录态竞争 refresh token。

设置 `TRANCN_ADMIN_KEY` 后访问 `http://127.0.0.1:9220/admin`。管理端支持账号列表、JSON 导入、启停、刷新、Token 校验、逐模型测试对话、删除、账号级最大并发和 Trae 网页登录。模型测试会向所选模型发送一句「请回复&lt;模型名&gt;」并回显上游实际模型，用于确认该账号在该模型上可用。最大并发允许设置为 1 到 100；调度设置中的“新账号默认并发”只影响后续添加的账号，不会批量覆盖已有账号。在线降低并发不会中断正在进行的响应，只会暂时阻止新请求，直到在途请求数回落到新上限以内。远程部署网页登录时必须配置浏览器可访问的 `--public-base-url https://proxy.example.com`。

sub2api 的账户并发和 traecn-proxy 的账号最大并发是两层独立限制。生产部署时应将两者配置为一致或让 sub2api 的限制更低；修改其中一层不会自动同步另一层。

sub2api 可按调用协议配置为 OpenAI 或 Anthropic API Key 上游。Base URL 不要附加 `/v1`，由 sub2api 网关拼接具体 API 路径。

OpenAI 兼容调用：

```text
platform: openai
type: apikey
base_url: http://traecn-proxy:9220
api_key: <TRANCN_API_KEY>
passthrough: enabled
```

Anthropic Messages 调用：

```text
platform: anthropic
type: apikey
base_url: http://traecn-proxy:9220
api_key: <TRANCN_API_KEY>
auth_scheme: authorization_bearer
passthrough: enabled
```

建议先请求 `/v1/models` 获取当前账号的精确 ID。2026-08-28 已在 Linux 容器内实测：

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
- 管理端必须设置独立且强随机的 `TRANCN_ADMIN_KEY`；OAuth 回调只接受五分钟内创建的 `login_trace_id`，不会接受任意回调写入账号。
- 本项目与 Trae、字节跳动无隶属关系。使用前请确认符合组织 IT 政策、账号授权范围与服务条款；不要公开暴露企业账号或令牌。
