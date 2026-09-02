# traecn-toolparse 执行看板

开始时间：2026-09-02 08:50
项目名：traecn-toolparse
执行路径：2 → 1（先第4层止血，再第2/5层重构）
决策依据：用户不在线（"Work autonomously and make good decisions"），沿用昨天 2026-09-01 会话结论。

- [x] 任务总进度: 10/10
- 待办列表:
    - [x] [阶段0] 恢复上下文：读取 VS Code Copilot 会话记录 + 仓库未提交改动
    - [x] [阶段0] 盘点 TraeToolProtocol.cs 现状与手写修复逻辑清单
    - [x] [阶段2] 第2层：引入 JsonRepairSharp 替换手写修复
    - [x] [阶段1] 第4层：解析失败不泄漏原文（TraeToolCallFailureBlock）
    - [x] [阶段1] 第4层：失败载荷落盘语料（TraeToolCorpus）
    - [x] [阶段1] 第4层：解析/校验失败自动重试一次（流式 + 非流式）
    - [x] [阶段2] 第5层：语料库 + 数据驱动全量回放测试
    - [x] [阶段3] dotnet test 102/102 全绿
    - [x] [阶段3] 容器部署 + 多模型端到端采样
    - [x] [阶段3] 提交代码（`50a01d8`，未推送）

---

## 执行记录

### 步骤01 恢复上下文
- 会话 `8251a64f-7d9e-4bc8-aedc-355241e7a2b2`（08-31 ~ 09-01，63 轮）是昨天未完成任务的来源。
- 未提交改动：`TraeToolProtocol.cs`、`tests/.../TraeToolProtocolTests.cs`。
- 昨天结论：不再逐个打补丁，改按 5 层架构收敛；建议先 2（止血）再 1（重构）。

### 步骤02 第2层 —— 用库替换手写修复
- 新增依赖 `JsonRepairSharp 1.2.4`（NuGet 下载量 82k）。
- 删除手写 `CloseUnbalanced`（栈式补括号）。
- 新增 `TraeToolProtocol.TryParseJsonObject`：标准解析 → 失败再落 jsonrepair。
- 新增公开 `TraeToolProtocol.TryParseArguments`，`Program.cs` 两处裸 `JsonNode.Parse` 全部改走它。
- 覆盖能力从「缺括号」扩到单引号、尾随逗号、未加引号的键、截断等。

### 步骤03 第4层 —— 不泄漏 + 重试
- 新增 `TraeToolCallFailureBlock(ToolName, Reason, RawPayload)`。
- 原先两处 `TraeTextBlock(OpenTag + payload)` 泄漏点改为发失败块，原文再也不会进 `text_delta`。
- `Program.cs`：`LogRejectedToolCall` 同时写 `[tool-reject]` 与语料文件。
- `WriteAnthropicStream` 抽出 `RunAttempt`，失败即中断当前上游，携带已产出正文向上游追问一次。
- `CollectAnthropic` 同构改造为 attempt 循环。
- 工具校验失败（缺必填项 / 未知工具）也纳入重试，不再直接吐错误文案。
- 两次都失败才输出 `InvalidToolCallMessage`。

### 步骤04 第5层 —— 语料库回放
- `TraeToolCorpus.Configure(dataDir/tool-failures)`，按天写 JSONL。
- `tests/TrancnProxy.Tests/corpus/*.txt` 6 条真实/合成畸形样本，`DynamicData` 全量回放。
- 新增用例：失败块不泄漏敏感原文、截断不泄漏、`TryParseArguments` 修复能力。
- 结果：**100/100 通过**（原 88）。

### 步骤05 部署验证
- `dotnet publish -r linux-arm64 --self-contained` → `docker build` → `traecn-proxy:toolparse`。
- 旧容器保留为 `traecn-proxy-prev-20260902`（已停止，可一键回滚）。
- 新容器已 Up，端口 10005 与数据卷不变。

### 步骤06 真实流量反证（本次最重要的发现）
多模型采样跑完后语料库立刻抓到 3 条真实失败，直接推翻了「上库就够了」的判断：

| # | 长度 | 原因 | JsonRepairSharp 单独处理结果 |
| --- | --- | --- | --- |
| 0 | 20000 | arguments 非法 JSON | `ArgumentOutOfRangeException`（库自身崩） |
| 1 | 14741 | arguments 非法 JSON | `JSONRepairError: Object key expected` |
| 2 | 31 | 缺 content | 修复成功但只有 file_path |

进一步定位后确认两类真实成因：
- **#1**：`content` 正常闭合后多出一个 `)`，jsonrepair 无法处理。
- **#0**：不是截断，而是 **模型在长 content 里漏转义引号**（JS 的 `"…" + BOMB_PENALTY`），
  字符串边界在第 16608 字符处被认错，后面 3384 字符全是垃圾。若采信就会写出半截文件。

对应新增第三级 `SalvageJsonObject`：逐个抢救顶层键值对，**只有干净走到 `}` 才采信**；
中途读不出键即整体作废并触发重试。同时对「结尾仍在字符串内」的载荷跳过 jsonrepair，
避免它自作主张补引号造出貌似合法的半截文件。

另修正语料机制自身缺陷：`MaxPayloadChars` 从 20K 提到 200K，否则样本本身会被日志截断，
回放的是一个并不存在的畸形。

### 步骤07 最终验证
- `dotnet test` **102/102 通过**。
- 镜像重建并重新部署，glm-5.3 复跑 3 次：`tool_use` 正常、`text_delta=0`、原文泄漏 `0`、无 reject。
- 提交 `50a01d8`（16 个文件，+922/-171），**未推送**，等你确认。

## 遗留问题（本次未处理）
1. **OpenAI / Responses 协议不解析工具调用**：本次改动只覆盖 Anthropic 路径，Codex 侧仍是原文直通。
2. **`Doubao_1_6` 被模型降级校验误杀**：其 display_name 实为 `Doubao-Seed-Code`，上游回
   `trae_tob_seed-code-lite-dev-0602-fixed` 是对的。校验只比对 model id，未比对 display_name。
   放宽与否需用户拍板（原始需求写明「不可放宽」）。
3. **`max_tokens` / `thinking.budget_tokens` 未转发上游**，对 Trae 无约束力；是否透传需先探明字段。
4. 排查时 `TRANCN_API_KEY` 被打印到终端，建议轮换。
5. **未做 Claude Desktop 真实验证**，全部结论来自 curl。

## 2026-09-02 下午：长任务专项

### 断流定性（用户问「技术上能解决吗」）
新增诊断后复现，拿到决定性证据：

```
[stream-abort] glm-5.3__dev: TraeUpstreamException: Trae 上游完成但未返回有效内容。
```

不是 `IncompleteStream`（网络断连），是上游**正常发 done 但零 output**。同请求第二次即成功
→ 随机性问题 → 可重试。这是最安全的重试场景：下游未提交任何内容，重发零副作用。

### 长任务三类真因
| 现象 | 真因 | 处理 |
| --- | --- | --- |
| 数分钟只有 ping，最后 error | 上游 done 但零 output | 自动重发一次 |
| 只有思考、无答案（GLM-5.3） | 58K 字符代码草稿写进 `reasoning_content`，烧光预算 | 系统提示禁止在推理里起草方案 + `sawAnswerContent` 判定后重试 |
| 62KB HTML 被吞进 file_path | Form A JSON 与 Form B `<parameter>` 混写 | `SplitEmbeddedParameters` 按边界拆回各键 |

### 新增支持的方言（全部来自线上日志）
- `<function name="x">` 标签
- `<parameter name="x" string="true">` 带额外属性
- `<tool_call>Read{...}` 裸工具名开头
- `<tool_call>Read a="1" />` 裸工具名 + XML 属性

### 模型目录
`--chat-models` 现在会列出被过滤项。结论：**GLM-5.3-Flash 不在企业 chat_v3 目录里**
（26 个被过滤项中也没有），不是我们没跟上；目录全量透传，上游开通后 TTL 过期自动出现。

### 终验（用户指定的 5 个常用模型）
短任务 3 轮 × 5 模型 = **15/15**，零 tool-reject。

长任务（16K tokens，完整 H5 游戏）修复前后对比：

| 模型 | 修复前 | 修复后 |
| --- | --- | --- |
| GLM-5.3 | 244s，**tool=0**，think delta 1961 | 144s，tool=1，think delta **16** |
| Qwen3.8-Max | 341s，tool=1 | 276s，tool=1（重试救回 1 次） |
| Kimi-K3 | 292s，tool=1（重试救回 1 次） | 124s，tool=1 |
| DeepSeek-V4-Pro | 102s，tool=1 | 105s，tool=1 |
| DeepSeek-V4-Flash | 50s，tool=1 | 44s，tool=1 |

**5/5 通过。** 耗时普遍下降，因为不再把预算烧在推理里写代码。测试 106/106。

## 2026-09-02 傍晚：降级校验修复 + 多轮验证

### 模型降级校验（原「遗留问题 2」）
先取证再动手：`--chat-models-raw` 导出上游原始目录后确认 **上游根本不声明真实后端模型名**
（`ali-deepseek-v4-pro` / `trae_tob_*` / `seed-code-*` 在原始目录中出现均为 0 次），
包含判定对部分 config 结构上就不可能成立。目录里还有 `enable_llm_error_model_degrade`，
说明上游自带降级机制 —— 这条校验不能放宽。

方案：默认拒绝行为一字不改，新增 `Upstream:ModelAliases` 人工核验白名单，显式登记后才放行：
- `Doubao_1_6` → `trae_tob_seed-code-lite-dev-0602-fixed`
- `Doubao-Seed-2.0-Code` → `seed-code-pro-0130-dev`

单测覆盖「未登记必须拒绝、已登记才放行」；实测 `Doubao_1_6__dev` 已能正常返回。测试 107/107。

### 多轮 agentic 验证（此前完全未覆盖的路径）
Claude Desktop 当前未接入本代理（`claude_desktop_config.json` 仅 `{"deploymentMode":"3p"}`，
`~/.claude/settings.json` 指向 `api.cortexflueo.com`，cc-switch 已无 trae 档位），
未擅自改用户配置。改用 `agent_loop.py` 覆盖真实客户端最常走的路径：
tool_use → 真实执行 → tool_result 回传 → 下一轮。

任务：写 H5 游戏 → Read 读回确认 → FileStats 统计行数（最少 3 次工具往返）。

| 模型 | 结果 | 轮次 | 工具序列 |
| --- | --- | --- | --- |
| GLM-5.3 | done | 4 | Write→Read→FileStats |
| DeepSeek-V4-Pro | done | 4 | Write→**Write**→Read→FileStats（首次空参数被重试救回） |
| DeepSeek-V4-Flash | done | 4 | Write→Read→FileStats |
| Qwen3.8-Max | done | 3 | 两组完整调用 |
| Kimi-K3 | 1 失败 / 2 成功 | — | 偶发「连续两次只输出推理」，复跑 2/2 通过 |

全程 `tool_errors=0`，沙箱产物 `whack.html` 实际落盘。

### 累计验证成绩
- 单轮 3 轮 × 5 模型：15/15
- 长任务 16K tokens × 5 模型：5/5
- 多轮 agentic：6/7 次（唯一失败为上游偶发，重试已触发）

### 部署
镜像 `traecn-proxy:toolparse` v0.4.8 已更新到 10005，模型数 41，回滚点 `traecn-proxy-prev-20260902`。

## 遗留问题（最新）
1. **OpenAI / Responses 协议不解析工具调用**：只覆盖 Anthropic 路径，Codex 侧仍原文直通。
2. **`max_tokens` / `thinking.budget_tokens` 未转发上游**；目录里有 `max_tokens: 16000` 声明，
   是否透传需先探明上游字段。
3. **Kimi-K3 多轮偶发只输出推理**：重试两次仍可能落空，需观察是否要提到 3 次。
4. 排查时 `TRANCN_API_KEY` 被打印到终端，建议轮换。
5. **仍未做 Claude Desktop 真实验证**，全部结论来自 HTTP 层。

