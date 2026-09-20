# DSH 主机重启排查手册

面向"DSH 主机频繁重启 / 会话中途消失"这类反馈。所有结论都对应代码位置与日志字符串，便于从一份 `%LOCALAPPDATA%\Cetus\logs` 目录直接定位。

## 0. 两条杀进程路径

Cetus 只有两把"刀"，其余都是触发条件：

| 路径 | 代码 | 说明 |
| --- | --- | --- |
| `DshHost.StopAsync` → `DshSidecarProcess.StopAsync` | `DshHost.cs`、`DshSidecarProcess.cs` | `Kill(entireProcessTree: true)`，随后关闭 Job 句柄 |
| `SidecarJob.Dispose` | `SidecarJob.cs` | `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`：句柄一关，整棵树被内核终止 |

**推论：DSH 没有独立生命。任何 Cetus 进程退出（托盘退出、关闭窗口且未开最小化到托盘、崩溃、安装程序结束进程）都会杀掉主机与正在跑的 agent turn。**

## 1. 先看日志映射

日志目录：`%LOCALAPPDATA%\Cetus\logs\`

| 日志行 | 代码 | 含义 |
| --- | --- | --- |
| `DSH spawn: endpoint=…, grace=15s` | `DshHost.StartAsync` | 每行代表**一次启动尝试**（含复用分支） |
| `stale endpoint detected: … treating the port as occupied` | `DshHost.StartAsync` | 端口上是一个归属已失效的残留宿主，已按占用处理 |
| `DSH process exited: exitCode=N` | `DshHost.OnSidecarExited` | 主机自己退出 |
| `DSH runtime failure: kind=…, detail=…` | `DshHost.ReportRuntimeFailure` | 判定失败，附探测级原因 |
| `automatic recovery attempt N/3 after Xs` | `DesktopRuntime.RecoverFromRuntimeFailureAsync` | 自动恢复开始 |
| `port fallback: A occupied, switching to B (saved)` | `DesktopRuntime.StartHostCoreAsync` | 端口被占，**已持久化**新端口 |
| `DSH health probe rejected (HTTP 401): … 保留主机不重启` | `DshHost.MonitorHealthAsync` | 鉴权被拒，**不杀主机** |
| `DSH session polling failed (N consecutive)` | `DshSessionWatcher` | 会话轮询失败（与主机存活无关） |

配套的 sidecar 日志是 `dsh-<yyyyMMdd-HHmmss>.log`；`cetus-<UTC 日期>.log` 里**行时间是本地时间**，跨零点对时要注意。

## 2. 症状 → 根因 → 处置

| 症状 | 根因 | 处置 |
| --- | --- | --- |
| 启动后约 30~40 秒出现一次 `HealthCheckFailed` + `automatic recovery attempt`，随后正常 | 3080 上有一个**上次 Cetus 留下的残留宿主**：它健康响应，但 owner 已退出，Job 句柄关闭时它必然死亡 | 已由归属记录修复（见 §3）。若日志出现 `stale endpoint detected` 即命中该路径 |
| 反复 `HealthCheckFailed`，每次间隔数秒~数十秒 | 旧版本（09-19 之前）探针不带会话 Cookie，DSH 0.1.5/0.1.6 起返回 401，被当作"死亡" | 已修复：401/403 归类为 `AuthRejected`，永不触发杀进程 |
| `DSH spawn` 之后约 90 秒出现新的 `Cetus.exe`、并伴随"更新失败"气泡，循环往复 | 便携版自更新回滚后旧版本立刻再次静默安装同一版本 | 已修复：健康预算 90s→180s；回滚写入版本黑名单，启动时跳过该版本 |
| 打开工作区 / Jump List / `cetus://` / 任务栏跳转后页面重载、会话变空 | `FocusSessionCoreAsync` 会 `NavigateHomeAsync()` 并**新建会话** | 预期行为（不是主机重启）；若托盘"重试连接 DSH"变灰，是被 `LoadingBrowser` 卡住，已修复 |
| `session/list` 每 10 秒失败一次 | 参数形状过时（`{}` → 需 `_request`） | 已修复 |
| 主机在长任务中被杀、进度丢失 | 事件循环被同步 SQLite 等操作阻塞，2 秒探针 ×3 次即判死 | 已放宽：超时 5s、间隔 3s、阈值 10 次，并在杀之前加一次复核探测 |

## 3. 健康判定语义（修改后）

- 探针：`GET http://127.0.0.1:<port>/`，需要 `200` **且** 正文含 `id="root"`；**不经过任何代理**（库内部固定 `UseProxy = false`，回环探测不应被代理吞掉）。
- `200` 且带标记 → `Healthy`。
- `401/403` → `AuthRejected`：主机在响应，只是 Cookie 被拒。**只记日志、绝不杀进程**；启动阶段则视为"该端口不是我们的 DSH"，走端口回退。
- 其它情况 → `Unhealthy`：连续 `HealthFailureThreshold`（默认 10）次、间隔 3 秒、且追加一次复核探测后才报告失败。真实宕机约 30~40 秒内被发现，误杀窗口从 6 秒放宽到 30 秒以上。

## 4. 归属记录（防"认领残留宿主"）

`%LOCALAPPDATA%\Cetus\dsh-host-owner.json` 记录 Cetus 自己 spawn 的宿主 PID 与启动时间。

启动时若端口上有健康服务：

- 记录中的进程仍在运行（且启动时间匹配，排除 PID 复用）→ 复用；
- 记录存在但进程已消失 / PID 被复用 → 判定为残留宿主，按"端口被占"处理（回退到空闲端口），**绝不认领**；
- 无记录（例如首次升级到本版本）→ 同样按端口被占处理，因为无法证明归属。

副作用：不再支持"复用用户手工启动的 3080 服务"。若确需该能力，应改为显式的用户配置项，而不是默认行为。

## 5. 更新失败不再循环

- 便携更新的健康预算 `PortableUpdateApplier.HealthBudget` = 180 秒，必须大于应用自身最坏启动路径（端口宽限 15s + 就绪等待 90s + WebView2 初始化 + 导航）。
- 回滚时写入 JSON 失败通知（含版本号）；失败版本由 `UpdateRejection` 记入 `%LOCALAPPDATA%\Cetus\updates\rejected-versions.txt`，启动时的静默更新会跳过它并提示。用户主动在更新对话框点击安装会清除该记录。

## 6. 复现与验证手段

- 独立看门狗（每 5 秒记录监听 PID / HTTP 码 / 延迟 / sidecar 日志名，PID 变化即标记重启）：本项目仓库外的一次性脚本模式，排查时优先跑它，比读日志更快抓到"重启现场"。
- `DshEndpointProbeTests`：探针状态机（200+标记 / 401 / 403 / 缺标记 / 传输错误 / 代理存在）。
- `DshHostFailureClassificationTests`：鉴权被拒不杀主机、残留宿主不被认领、真实宕机只报一次且带原因。
- `HostOwnerStateTests`：归属记录的写入、死进程、PID 复用、清理。
- `UpdateRejectionTests`：黑名单与失败通知解析（含旧版纯文本格式）。
- `UpdateNoticeStateTests`：页内更新提示的 C# 侧载荷契约。

### 页内更新提示的跨语言验证

更新卡片是注入 DSH 页面的 JavaScript，C# 单测覆盖不到，用 `tests/bridge/` 做真实执行验证：

```powershell
node tests\bridge\extract-bridge.js src\Cetus.Desktop\Browser\BrowserSession.cs
node tests\bridge\run-bridge-test.js src\Cetus.Desktop\Browser\BrowserSession.cs.extracted.js
```

第二步在最小 DOM 垫片上真跑脚本，断言：无更新不渲染；有更新时卡片显示版本与说明；点主按钮发出 `cetus-update-install`；进行中禁用按钮并显示百分比；关闭按钮移除卡片并发出 `cetus-update-dismiss`；设置页那行显示"新版本 x.y.z"；页面加载时主动请求状态。

该垫片**确实抓到过真问题**，所以每次改桥脚本都要重跑这条命令，退出码非 0 即失败。

## 7. 仍然存在的已知风险

- 探针无法区分"事件循环卡死"和"进程已死"；阈值放宽只是降低误判概率，不消除。
- `NavigateHomeAsync` 系列会整页重载并新建会话，用户仍可能把这种观感当成"主机重启"。
- 安装版（Inno Setup）路径会 `Stop-Process {app}\runtime\node.exe`，属于预期的一次性终止；`CloseApplications=no`，无其它进程清理机制。
