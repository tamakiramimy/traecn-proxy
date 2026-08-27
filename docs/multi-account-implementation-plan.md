# 多账号实施计划

## 阶段 1：账户核心

1. 新增 `TraeAccount`、`TraeAccountStore`、`MultiAccountManager` 和 `AccountLease`。
2. 支持旧 `auth.json` 自动迁移、账号数组 JSON 导入和原子写入。
3. 每个账号维护独立 `TraeClient`、刷新锁、并发计数和运行状态。
4. 验证多个账号的持久化、调度和会话绑定不串号。

## 阶段 2：统一 API 接入

1. 将 `/v1/models`、`/v1/chat/completions`、`/v1/responses` 和 `/v1/messages` 从单一闭包 `client` 改为每请求 `AccountLease`。
2. 为 OpenAI `user`、Responses `user` 和 Anthropic `metadata.user_id` 提取统一会话键。
3. 在非流式和流式路径的 `finally` 中释放账号 lease。
4. `/v1/status` 输出账号池概要，且不泄漏敏感凭据。

## 阶段 3：认证和刷新

1. 把现有命令行网页登录逻辑拆成可重用 PKCE 登录会话。
2. 为每个账号单独刷新并持久化新 token。
3. 在认证错误且尚未开始响应时进行一次受控 failover。

## 阶段 4：管理端

1. 新增受 `TRANCN_ADMIN_KEY` 保护的管理 API。
2. 提供内嵌 `/admin` 页面：账号列表、导入、OAuth 登录、启停、测试和删除。
3. 对每项写操作记录脱敏审计日志。

## 验收

- 两个账号同时请求时，Authorization、x-uid、模型目录和刷新结果均来自正确账号。
- 同一会话在账号健康时稳定复用；账号禁用或冷却后安全迁移。
- JSON 导入失败不会破坏现有账号文件。
- OAuth 回调 state 不匹配或过期时拒绝写入账号。
- 已开始输出的流不会切换到其他账号。
- 单账号旧用法和现有三协议 API 回归可用。