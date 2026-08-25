# trancn-proxy

> 非官方实验项目。将已获授权的 Trae CN 企业账号接入为基础 OpenAI / Anthropic 兼容 API。

`trancn-proxy` 读取本机 Trae CN 登录状态，通过本地 HTTP 服务转发文本对话请求，便于接入兼容 OpenAI 或 Anthropic API 的工具。

## 已知限制

- 当前上游 `llm_utils_chat` 端点会忽略请求的 `model`，回落为租户默认模型。代理会比对上游 SSE `metadata.model`；若实际模型与请求模型不一致，请求会失败，不会将错模型内容伪装为成功响应。
- `GET /v1/models` 返回企业模型目录，不代表其中每个模型都能通过当前对话端点调用。
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
dotnet run
```

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| `--port <port>` | 监听端口，默认 `9220` |
| `--listen <address>` | 监听地址，默认 `127.0.0.1` |
| `--api-key <key>` | 设置网关 API Key |
| `--login` | 强制从 Trae CN 本地存储重新读取授权 |
| `--weblogin` | 使用独立网页授权，不依赖 Trae CN IDE |
| `--test` | 发送一条真实对话自测 |
| `--model <model>` | 配合 `--test` 指定模型，默认 `glm-5.2__max` |
| `--tc-test` | 校验本地 `tc` 加解密实现 |

网关 Key 也可由 `TRANCN_API_KEY` 环境变量提供。企业网络需要代理时，程序会读取 `HTTPS_PROXY` 或 `HTTP_PROXY`。

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
	-d '{"model":"glm-5.2__max","messages":[{"role":"user","content":"你好"}]}'
```

## 安全与合规

- 会读取、缓存并在非独立授权模式下可能回写 Trae CN 的 access token 与 refresh token。缓存文件为 `~/.config/trancn-proxy/auth.json`，非 Windows 平台会尝试设置为仅当前用户可读写。
- 默认仅监听 `127.0.0.1`。使用 `--listen 0.0.0.0` 前必须设置 `--api-key` 或 `TRANCN_API_KEY`，并自行配置访问控制。
- 本项目与 Trae、字节跳动无隶属关系。使用前请确认符合组织 IT 政策、账号授权范围与服务条款；不要公开暴露企业账号或令牌。
