# trancn-proxy 设计文档

> Trae CN(企业版)→ OpenAI / Anthropic 兼容 API 网关
> 状态:**动态模型目录与 TRAE IDE Agent bridge 已验证通过**

> 2026-08-27 起，IDE bridge 只保留为开发期协议对照。独立 headless Agent 传输尚未通过真实验收，不能作为 Docker 或生产部署能力声明。

---

## 1. 已验证的可行性结论(2026-08-25 实测)

| # | 结论 | 证据 |
|---|------|------|
| 1 | Trae CN 本地认证数据可纯算法解密,无需 Keychain/DPAPI | `storage.json` 的 `iCubeAuthInfo://icube.cloudide` 键,"tc" 格式:base64 → `[6B头][32B随机][AES-128-CBC密文]`,密钥 = SHA-512(SHA-512(random) XOR 硬编码盐) 前 32 字节;明文中含 `{token, refreshToken, userId, expiredAt, refreshExpiredAt, account, userRegion}` |
| 2 | 本机账号为企业版(SaaS),上游不是消费版 mchost.guru | `local_env.json` 的 `host_map`:`371467520 → https://console.enterprise.trae.cn`;产品含 `ent_*` 企业权益(`productType:231`,`ai_coding_cue: 50 亿额度`) |
| 3 | 鉴权方式:`Authorization: Cloud-IDE-JWT <token>` + `x-cloudide-token` + 完整设备头 | 实测 `GetUserInfo` 返回 `code:0`;缺 `x-device-type` 等设备头会 401/400 |
| 4 | 对话端点:`POST /api/agent/v3/llm_utils_chat`,SSE 流 | 实测完整流:`metadata → timing_cost → output → extra_info → token_usage → done`,模型正常回复 |
| 5 | 请求体必须含 `app_version_code`(int64),body 字段为 camelCase | 缺该字段 → `4001`;字段为 snake_case → 400 空响应 |
| 6 | 模型目录:`POST /api/ide/v1/batch_get_detail_param` | 返回 1.2MB 配置,`chat_v3` 函数下 47 个模型配置,含豆包/GLM/Kimi/DeepSeek/Qwen/MiniMax + 企业自定义模型(Claude-4-Sonnet、GPT-5、Gemini 等) |
| 7 | 刷新:refreshToken 有效期至 2027-02-13;刷新端点 `/cloudide/api/v3/trae/oauth/ExchangeToken`(body: `{ClientID, RefreshToken, ClientSecret, UserID}`) | 端点路径从 IDE 代码(排除列表)与 trae-cli 二进制双向确认;**尚未实测调用**(token 未过期,不主动轮换) |
| 8 | 企业网络必须走 HTTP 代理 | 本机 `hkproxy.mindray.com:8080`(环境变量注入);直连失败、经代理成功 |
| 9 | C# tc 加解密与 Node 参考实现双向互通 | 本项目 C# 加密结果被 laojichao/trae-local-api 的 JS 成功解密;JS 加密数据被 C# 成功解密 |
| 10 | `llm_utils_chat` 会忽略模型选择并回落租户默认模型 | 实测请求 `glm-5.2__max` 后 metadata 仍为 `Doubao-Seed-Evolving`;代理现会拒绝模型不匹配的响应，避免将错模型结果返回给调用方 |
| 11 | TRAE IDE Agent 链路可按目录中的精确模型 ID 调用 | 通过 CDP 页面初始化 hook 复用 TRAE 产品会话创建与 Aha IPC；已实测 `glm-5.3__dev`、`DeepSeek-V4-Pro-Official__dev`、`kimi-k2.7-code__dev`，并严格校验返回 metadata |

### demo 已验证功能清单

- [x] `dotnet run` 自动:缓存检测 → storage.json 解密 → 有效性校验(GetUserInfo)
- [x] token 无效时:refreshToken 续期 → 回写缓存 + storage.json(tc 加密,IDE 可继续用)
- [x] 完全无授权时:独立网页授权(浏览器 `/authorization` + PKCE → 本地回调捕获 refreshToken → ExchangeToken),不依赖 IDE
- [x] 后台定时刷新服务(每 30 分钟检查,过期前 1 小时续期)
- [x] OpenAI 兼容:`GET /v1/models`、`POST /v1/chat/completions`(流式+非流式)
- [x] OpenAI Responses 兼容:`POST /v1/responses`(流式+非流式)
- [x] Anthropic 兼容:`POST /v1/messages`(流式事件序列符合规范)
- [x] 动态模型目录与 TRAE IDE Agent 精确选模，模型不匹配时明确失败
- [x] 网关 API Key 鉴权(401/200 正确)
- [x] 代理支持(读 `HTTPS_PROXY` 环境变量)

---

## 2. 总体架构

```
sub2api / one-api / Claude Code / 其他工具
        │  OpenAI/Anthropic 协议 + 网关 Key
        ▼
┌─────────────────────────────────────┐
│  trancn-proxy (net8.0 控制台+Kestrel) │
│  ┌───────────────┐ ┌──────────────┐ │
│  │ 协议转换层     │ │ 模型映射/目录 │ │
│  │ OpenAI/Anthr  │ │ (batch_get_  │ │
│  │ opic ↔ Trae   │ │  detail_param)│ │
│  └──────┬────────┘ └──────┬───────┘ │
│  ┌──────┴─────────────────┴───────┐ │
│  │ TraeClient(目录/授权) │ IDE Bridge│ │
│  │ HTTP + 企业代理       │ CDP + Aha │ │
│  └──────┬─────────────────┴─────────┘ │
│  ┌──────┴──────┐  ┌────────────────┐│
│  │ TokenStore   │  │ TokenRefresh   ││
│  │ 缓存+解密+回写│◄─┤ 定时/触发续期  ││
│  └─────────────┘  └────────────────┘│
└─────────────────────────────────────┘
        │
        ▼
console.enterprise.trae.cn(上游,经企业代理)
```

---

## 3. 模块设计(正式版)

### 3.1 TcCrypto(已验证)
- 解密:base64 → 头识别(`74 63 05 10 00 00` = AES,`12 39 20 20 02 03` = AES_PRIVATE)→ 取 32B 随机数 → 派生密钥 → AES-128-CBC/PKCS7 → 校验前 64B SHA-512 → 返回明文。
- 加密:同算法反向,用于回写 storage.json。
- 已与 Node 参考实现互测通过。

### 3.2 TokenStore(已验证)
- 来源优先级:本地缓存 `~/.config/trancn-proxy/auth.json`(0600)→ `storage.json` 解密。
- 回写:`ExchangeToken` 成功后将新 token tc 加密写回 `storage.json`(先备份 `.bak`),保证 IDE 与网关共用同一 token,避免互相踢下线。
- 设备信息:`device_id` 取自 `ModularData/ckg_server/local_env.json`,`machine_id` 取自 `storage.json` 的 `telemetry.machineId`。

### 3.3 TraeClient(已验证)
- 上游 host:优先 `storage.json` 的 `iCubeHostInfo.apiHost`,回退 `console.enterprise.trae.cn`。
- 固定头:`Cloud-IDE-JWT`、`x-cloudide-token`、`x-app-id`、`x-app-version(-code)`、`x-device-*`、`x-ide-version(-code/-type)`、`request-traffic-type: prod`、`x-request-id`。
- 代理:`HTTPS_PROXY/HTTP_PROXY` 环境变量 → `WebProxy`。
- 端点:
  - 对话:`POST {host}/api/agent/v3/llm_utils_chat`(body camelCase,含 `app_version_code`)
  - 模型目录:`POST {host}/api/ide/v1/batch_get_detail_param`
  - 校验:`POST {host}/cloudide/api/v3/trae/GetUserInfo`
  - 刷新:`POST {host}/cloudide/api/v3/trae/oauth/ExchangeToken`

### 3.4 协议转换层(demo 已有雏形,正式版增强)
- OpenAI:`/v1/chat/completions`(流式/非流式),`/v1/models`(来自模型目录,按 config_name 分组)。
- OpenAI Responses:`/v1/responses`(流式/非流式)。
- Anthropic:`/v1/messages` + `/v1/messages/count_tokens`。
- 可选模型对话通过当前 TRAE IDE Agent 执行；首次请求安装初始化 hook 并重载 workbench，请求串行化，实际模型 metadata 必须与请求 ID 一致。
- **待增强**:tool_use/tool_result 结构化往返(参考 laojichao 项目的 `<tool_call>` 解析策略)、reasoning_content 与 response 的分离输出、Anthropic 严格交错校验(role 交替)。

### 3.5 开发期协议证据(进行中)
- 使用 `--protocol-evidence-dir /tmp/trae-protocol-evidence` 执行一次普通 Agent 的“新建会话 -> 首条纯文本消息”取证；目录不能位于工作区内。
- bridge 只捕获活动请求关联的 Aha `request_stream` 出入站 envelope。落盘前执行 schema 投影：保留事件、方法、服务、模型、数值与布尔值；同次 ID 使用随机盐伪匿名化；token、用户、设备、路径和消息内容一律替换。
- 每次录制后扫描 JSONL，人工把必要的字段结构转为合成 fixture。原始录制、HAR、用户消息、客户端 bundle 和二进制均不得提交。
- 取证目标是确认真实会话创建路径、服务端返回的 `session_id/project_id`、后续 Agent 请求路径/字段，以及 SSE 事件顺序。证据缺失时不得猜测随机 session ID。
- **当前硬门禁未通过**：静态交叉检查确认 CUE HTTP 端点只有 `create_agent_task` 和 `commit_toolcall_result`；`chat/create_new_session` 是本地 Aha IPC。native `ai-agent` 模块包含 `create_lite_session`、本地 `chat_session` 存储和工作树创建符号，尚无可验证的远端 create-session HTTP 契约。随机 session ID 已被上游以“session not found”拒绝。
- 已实现独立的 .NET `TraeAgentClient`：复用账号 HTTP 鉴权/代理，调用已确认的 task endpoint 并按标准 SSE 分帧；`TraeAgentSessionRunner` 强制 server-issued session、模型 metadata 先于 output，且要求明确终止事件。这些组件不引用 Electron、CDP、Aha 或 TRAE 本地目录。
- **不采用 UI 作为会话前置条件**：若后续获得并验证远端会话契约，headless transport 才自动请求 session，并生成或使用管理员配置的 workspace descriptor。普通文本聊天不要求用户打开 IDE 目录；只有显式启用文件工具时，才允许服务端挂载并传递受控工作目录。

### 3.6 TokenRefreshService(已实现,待实测)
- 周期 30 分钟;`expiredAt - 1h` 触发 `ExchangeToken`;成功后缓存 + 回写。
- refresh 到期(`refreshExpiredAt`)前 7 天告警;彻底过期 → 走登录引导。
- **风险点**:ExchangeToken 会轮换 token。需实测确认:轮换后 IDE 在线会话是否受影响(预期:IDE 下次请求 401 后自行用新 refresh 恢复,或直接读回写的 storage.json)。

### 3.7 登录引导(独立网页授权,已实现,待完整走通)
完全逆向自 IDE 的 `loginUrlBuilder.js` / `saas/oauthService.js`:

1. 本地 `127.0.0.1:<随机端口>` 起回调服务器(路由 `/authorize`)
2. 打开 `{consoleHost}/authorization?login_version=1&auth_from=trae&login_channel=native_ide&client_id=ono9krqynydwx5&auth_callback_url=http://127.0.0.1:{port}/authorize&...&code_challenge=...&code_challenge_method=S256`(参数与 IDE 完全一致)
3. 用户浏览器登录后页面 302 回本地回调,query 携带 `refreshToken`、`host`、`consoleHost`
4. `POST {host}/cloudide/api/v3/trae/oauth/ExchangeToken` body `{RefreshToken, ClientSecret:"-", UserID:""}` → `Data.{Token, RefreshToken, TokenExpireAt, RefreshExpireAt}`
5. `POST {host}/cloudide/api/v3/trae/GetUserInfo`(header `x-cloudide-token`)→ 补全账号信息,保存缓存

独立会话标记 `Standalone=true`:刷新只更新缓存,**不写 IDE 的 storage.json**,与 IDE 会话互不干扰。
`--weblogin` 强制走此流程;`--login` 仍从 IDE storage.json 读取。

### 3.8 多账号运行时(已实现基础版)
- 对外仍是单个 `/v1` API；内部 `MultiAccountManager` 管理多个 Trae 账号，按 `priority` 或 `balanced` 策略选择账号。
- 请求开始时获得固定 `AccountLease`，流式输出开始后不切换账号；每账号有独立 `TraeClient`、刷新锁和并发上限。
- 会话粘滞键来自 `X-Trancn-Session-Id`、OpenAI/Responses `user` 或 Anthropic `metadata.user_id`，默认有效期一小时。
- 凭据保存于 `~/.config/trancn-proxy/accounts.json`，首启自动迁移旧 `auth.json`。独立网页登录账号不会回写 IDE `storage.json`。
- 管理端为 `/admin`，账号 JSON 导入与网页登录使用独立 `TRANCN_ADMIN_KEY`；网页登录回调有 PKCE、随机 state 和五分钟有效期保护。
- 详细设计与实施计划见 [multi-account-design.md](multi-account-design.md) 和 [multi-account-implementation-plan.md](multi-account-implementation-plan.md)。

### 3.9 安全设计(针对多人共享)
| 项 | 设计 |
|----|------|
| 服务配置 | `appsettings.json` 保存监听地址、端口、业务 Key、管理 Key、账号目录和 OAuth 回调地址；命令行与环境变量可覆盖对应项 |
| 令牌落盘 | 缓存文件 0600;正式版可选 DPAPI/Keychain 加密 |
| 监听 | 默认 `127.0.0.1`;`--listen 0.0.0.0` 需显式开启并提示 |
| 网关 Key | 必填才对外;`Authorization: Bearer` 或 `x-api-key` 校验;正式版支持多 key + 每 key 限速/限额 |
| 日志 | 永不打印完整 token(只打前缀/过期时间) |
| 租户隔离 | 单账号代理无租户概念;如需多人配额隔离,在网关层按 key 统计用量 |
| 审计 | 请求日志(模型、token 用量、来源 IP) |

---

## 4. 已知问题 / 待解决(按优先级)

1. **IDE bridge 稳定性**(高):模型选择已通过产品 UI/Aha 事件链解决；TRAE 升级后 DOM 选择器或 IPC 事件结构可能变化，需要用端到端探针及时发现。bridge 绑定当前 IDE 登录账号并串行执行，不参与多账号负载均衡。
2. **ExchangeToken 实测**(高):8-30 前后自动触发或手动把 `expiredAt` 改早触发,确认响应字段与轮换副作用。
3. **reasoning 输出混入 response**(中):部分模型把思考内容放在 `response` 字段;需按 `reasoning_content` 分离处理。
4. **多端 token 竞争**(中):网关与 IDE 同时刷新会互相轮换。方案:以 storage.json 为准的单写者(刷新互斥 + 回写),或网关刷新后立即回写并在 30s 内不回读旧缓存。
5. **用量/配额**(中):企业租户 50 亿 cue 额度;网关层需要用量统计与限额,避免共享用户耗尽额度。`/api/v1/commercial/get_session_usage` 可查用量(从 IDE 代码发现)。
6. **Anthropic 工具调用**(中):Claude Code 完整接入需要 tool_use/tool_result 双向映射。
7. **Windows/Linux 兼容**(低):当前按 macOS 验证;Windows 需确认设备头与数据路径(DPAPI 不涉及,因 tc 加密与系统无关)。

---

## 5. 风险与合规提示(重要)

- **账号类型**:本机登录的是**迈瑞企业租户**账号(wuweicheng@mindray.com,租户 tob_online),非个人消费账号。
- **共享他人使用可能违反** Trae CN / 企业采购条款;企业租户的用量与操作(命令黑名单、内容安全、审计)受公司 IT 管控,`ent_ai_code_tracking`、`ent_content_security` 等权益说明企业具备审计能力。**建议仅限本人及团队内部使用,并经公司授权**;不建议公网开放。
- 账号共享导致的封号/回收风险由使用者承担;本工具已做最小化:日志脱敏、回环默认、网关 Key。
- 上游协议可能随 IDE 升级变化(版本头 `3.3.87`、`app_version_code` 需跟随更新)。

---

## 6. 后续开发计划

- **阶段 1(多账号基础)**:账号存储、统一 API 选择、会话粘滞、JSON 导入、管理端 OAuth 和账号级刷新已完成；后续补充失败冷却、流式输出前 failover 和更多测试。
- **阶段 2(协议增强)**:IDE bridge 稳定性与错误恢复(#1)→ reasoning 分离(#3)→ Anthropic tool_use/count_tokens。
- **阶段 3(运维)**:账号用量统计、日志轮转、优雅停机、Docker 部署说明；如需多实例再引入数据库和分布式锁。

---

## 7. Demo 使用方式

```bash
cd /Volumes/MAC-DATA/Github/trancn-proxy
dotnet run                          # 自动授权 + 启动 http://127.0.0.1:9220
dotnet run -- --weblogin            # 强制网页授权(独立会话)
dotnet run -- --test                # 自测模式(发一条消息后退出)
dotnet run -- --login               # 强制重新读取 IDE 本地授权
dotnet run -- --port 19900 --api-key my-key   # 自定义端口 + 网关 Key
dotnet run -- --tc-test             # tc 加解密自检
```

sub2api / one-api 接入示例:

```bash
curl http://127.0.0.1:9220/v1/chat/completions \
  -H "Authorization: Bearer my-key" -H "Content-Type: application/json" \
  -d '{"model":"glm-5.3__dev","messages":[{"role":"user","content":"你好"}]}'
```
