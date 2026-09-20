# DSH 流复用协议笔记（/api/remote.mux）

研究目的：评估"任务等待输入"托盘通知的可行性。结论：协议可逆向、可由 .NET `ClientWebSocket` 实现。基础协议客户端已落地为 `Cetus.Runtime/Hosting/DshStreamMuxClient.cs`（真实 WebSocket 端到端测试见 `DshStreamMuxClientTests`）；"等待输入"业务接线仍需跟进会话事件类型枚举，按 2-3 小时独立立项。本文记录 2026-09-20 对 `@deepseek-ai/dsh@0.1.6-alpha.2` 的逆向结果（`dsh-api-gateway/lib/index.js`）。

## 传输

- WebSocket 升级到 `ws://127.0.0.1:<port>/api/remote.mux`，必须携带 `DshAuth` 浏览器会话 Cookie（升级请求经 `requestRejection` 校验，401/403 直接拒绝，与 HTTP API 同一认证）。
- 全部业务帧为 **JSON 文本帧**；二进制帧会被服务端以 1003 关闭。心跳为服务端 ws **ping 控制帧**（.NET `ClientWebSocket` 自动回 pong，无需处理）。

## 客户端 → 服务端

```json
{ "type": "open", "streamId": "<非空字符串，客户端生成，需唯一>", "endpoint": "session/follow", "payload": { ... } }
{ "type": "cancel", "streamId": "..." }
```

- `payload` 与 POST RPC 同构：`{ "args": { ... } }`（同一 `openWireStream` dispatcher）。
- 重复 `streamId` 的 open 会直接抛错关闭连接。

## 服务端 → 客户端

```json
{ "type": "item", "streamId": "...", "value": { ... } }   // 流数据，可多条
{ "type": "end",  "streamId": "..." }                      // 流正常结束
{ "type": "error", "streamId": "...", "error": { "code", "message", "details" } }
```

## 已知流端点

`workspace/follow`、`session/follow`、`terminal/*` 流（见各 controller 的 typert 绑定）。

## session/follow（已逆向完成）

- 订阅粒度为**单个会话**。wire 请求：`open { endpoint: "session/follow", payload: { args: { request: { address, maxMessages?, assistantStream? } } } }`，其中 `address = { kind: "session", sessionId }` 或 `{ kind: "subagent", parentSessionId, childSessionId, mode }`（typert `SessionFollowRequest`，strict 校验）。
- 帧序列（`item.value`）：
  1. `{ type: "snapshot", header, cursor, records: SessionEventEntry[], hasMore, projections }` —— 开头快照；
  2. `{ type: "event", event }` 增量（`event = { type, seq, time, data }`）；
  3. `{ type: "assistant-stream", frame }`（仅 `assistantStream: true` 订阅）。
- `SessionEventType` 全集（`dsh-session` SessionEventMap）：`turn/start`、`turn/end`（reason: `completed | aborted | blocked | error | max-tokens | interrupted`）、`step/start`、`step/end`、`user/message`、`system/message`、`assistant/message`、`assistant/attempt`、`tool/call`、`tool/result`、`request/header`、`request/context`、`session/end-seed`、`max-tokens`。
- 要监控所有会话需要：轮询/跟随会话列表 + 为每个会话各开一条流，并处理会话创建/销毁。

## "等待输入"判定结论

- **Ask question / 工具审批不产生会话日志事件**：`@deepseek-ai/dsh-user-questions` 的 `user-questions/request` 是 waterfall 服务，agent 同步挂起等待 answerer（期间 `running` 大概率保持 true），问题状态在内存中、不进 `session.list` 投影（仅 title/sessionListMetadata{blank,lastPromptAt}/subagent/agentPreset）。**轮询 `session.list` 原理上检测不到等待审批**。
- 可靠检测必须订阅 `session/follow` 并在客户端维护"未闭合 ask 展示"状态；UI 侧 i18n 存在 `ask.waiting`（"waiting"）。
- 粗粒度替代：`turn/end(kind=completed)` 是**实时完成信号**（比 10s 轮询快且准）；`conversationPhase` 的 `engaging`（`!running && promptAttempted`）可标"空闲等待下一条消息"。
