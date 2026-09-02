# traecn-toolparse 执行看板

开始时间：2026-09-02 08:50
项目名：traecn-toolparse
执行路径：2 → 1（先第4层止血，再第2/5层重构）
决策依据：用户不在线（"Work autonomously and make good decisions"），沿用昨天 2026-09-01 会话结论。

- [ ] 任务总进度: 8/10
- 待办列表:
    - [x] [阶段0] 恢复上下文：读取 VS Code Copilot 会话记录 + 仓库未提交改动
    - [x] [阶段0] 盘点 TraeToolProtocol.cs 现状与手写修复逻辑清单
    - [x] [阶段2] 第2层：引入 JsonRepairSharp 替换手写修复
    - [x] [阶段1] 第4层：解析失败不泄漏原文（TraeToolCallFailureBlock）
    - [x] [阶段1] 第4层：失败载荷落盘语料（TraeToolCorpus）
    - [x] [阶段1] 第4层：解析/校验失败自动重试一次（流式 + 非流式）
    - [x] [阶段2] 第5层：语料库 + 数据驱动全量回放测试
    - [x] [阶段3] dotnet test 100/100 全绿
    - [ ] [阶段3] 容器部署 + 多模型端到端采样
    - [ ] [阶段3] 提交代码

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

