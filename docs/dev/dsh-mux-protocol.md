# DSH 流复用协议笔记（/api/remote.mux）

研究目的：评估"任务等待输入"托盘通知的可行性。结论：协议可逆向、可由 .NET `ClientWebSocket` 实现，但等待输入的判定字段还需跟进会话事件类型枚举，整体按 3-4 小时独立立项。本文记录 2026-09-20 对 `@deepseek-ai/dsh@0.1.6-alpha.2` 的逆向结果（`dsh-api-gateway/lib/index.js`）。

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

## session/follow

- 订阅粒度为**单个会话**：请求为 durable address（`{ address, cursor? }`，具体 schema 见 `dsh-api-session-controller` 的 `validateFollowRequest`），返回"开头快照 + 之后逐事件"。
- 要监控所有会话需要：轮询/跟随会话列表 + 为每个会话各开一条流，并处理会话创建/销毁。

## 待跟进（实现前必查）

1. 会话事件日志的事件类型枚举——"等待工具审批/等待输入"对应哪个（或哪些）事件 `type`；UI 层（dsh-client-ui-conversation）渲染等待态所消费的字段。
2. `address` 的构造方式（workspaceId/sessionId 组合）。
3. 心跳参数：`websocketHeartbeatIntervalMs` 默认值；`MAX_MISSED_HEARTBEATS = 2`，客户端需响应 ping。
4. 帧中 `value` 的 typert 解码（controller 用 strict codec，字段名以 typert.host.js 为准）。
