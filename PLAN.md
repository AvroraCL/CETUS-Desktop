# Cetus · 鲸鱼座 — DeepSeek Harness .NET 桌面端设计思路

> 状态：设计草案 v0.2（2026-08-26）
> 定位：把 DeepSeek Harness Web GUI 包装成 Windows .NET 桌面程序，内置鲸鱼娘主题。
> **技术栈已终选：.NET 10 + WPF + WebView2**（2026-08-26 决策：仅 Windows 目标 + 实现交 AI Agent + 鲸鱼启动器 C# 代码可复用 + 社区 .NET 空白；.NET 8 LTS 于 2026-11 到期，故取当前 LTS .NET 10）。

---

## 0. 一句话定位

**Cetus 是 DeepSeek Harness 的 Windows .NET 桌面壳：双击即起，无需命令行、无需浏览器标签页、无需自己装 Node，开箱自带鲸鱼娘主题。**

核心哲学：**只做壳，不重做产品。** 官方 Web UI、agent 能力、插件生态 100% 复用；我们交付的是"把官方界面装进原生窗口"的那层壳，以及它带来的桌面体验（托盘、自启、通知、一键启动）。

---

## 1. 命名

- **Cetus（鲸鱼座）**：希腊神话中的鲸——刻托斯（Κῆτος / Kētos）——被波塞冬派去吞安德洛墨达的海怪，后被封入星空成为鲸鱼座。
- 三重契合：
  1. 神话**本义就是"鲸"**（不是引申义）；
  2. 中文名"鲸鱼座"自带"**座**"字——座位/底座，贴合桌面应用；
  3. 星座的"永恒定格"意象，契合"把 Harness 稳稳停靠"的定位。
- 工程名干净：`cetus` / `Cetus.Desktop` / `Cetus.Launcher`。
- **品牌红线**：
  - 不直接使用 "DeepSeek" 品牌词做主名（品牌归属问题）；
  - 不与社区同名项目混淆（agent-earth、ChisaAlter 等均叫 DeepSeek-Harness-Desktop）；
  - 素材注意区分 MIT 代码与 CC BY-NC-SA 美术素材（见 §7）。
- 待核查：GitHub 组织名 / npm 包名 / 域名 占用情况。

---

## 2. 背景与依据（已完成调研）

| # | 事实 | 来源 | 对 Cetus 的意义 |
|---|---|---|---|
| 1 | DSH = Node 服务端（`dsh web`，默认 `127.0.0.1:3080`）+ React/Vite 前端 | 官方仓库 | 后端/前端都不用改，包装"壳"即可 |
| 2 | DSH 架构**预留了嵌入式桌面壳设计**：`packages/host/webserver/src/index.ts` 注明 "Electron loads dist over file:// and carries fetch over an IPC bridge"；`client/connection` 提供进程内载体（与 HTTP/WS 同一双流抽象） | 官方仓库 `packages/host/webserver`、`packages/client/connection` | 官方本来就是按"可被桌面壳承载"设计的，file:// 离线壳（路线 B）有官方协议支撑 |
| 3 | 社区桌面端**全部是 Electron**：agent-earth/deepseek-harness-desktop（0.3.6，win/mac/linux）、ChisaAlter/Deepseek-Harness-Desktop（0.2.7，win/mac，含启动器/插件市场/托盘/自动更新）、sdkwork-ai 等 | GitHub | ① 包装这条路已被验证可行；② **.NET 桌面端目前是空白**，是差异化机会 |
| 4 | 鲸鱼启动器（HUITianYi/dsh-whale-desktop-launcher，MIT）已用 **C# 验证进程管理**：健康探针（GET 页面且含 `id="root"`）、`CreateNoWindow` 静默拉起、端口占用 15s 观察后报错、launcher.ini 用 Base64 防编码问题、`CommandLineToArgvW` 规范的参数引号规则 | GitHub 源码 | **C# 侧的进程生命周期管理已被社区趟平，可直接参考**（同为 MIT） |
| 5 | WebView2 = Chromium 内核，与官方 e2e（Playwright Chromium）同引擎 | — | WebView2 兼容性有官方测试背书，无 UI 兼容风险 |
| 6 | DSH 版本迭代极快（本机已装 0.1.0-rc.6；社区桌面端已钉 rc.8/rc.11/rc.12） | — | 桌面端必须钉版本 + 更新通道 |
| 7 | **Tauri 2 路线已被社区做到成熟**：kyorakuyk/dsh-desktop（Tauri2 + Node sidecar + `--port 0` 随机端口 + 解析 stdout URL 行，三平台 NSIS/DMG/deb/rpm）；majiayu000/dsh-desk（钉 Node 24 + `@deepseek-ai/dsh@0.1.0-rc.8`、私有 DSH_HOME、每日上游兼容检查、**零 IPC 能力授予网页**的安全姿态、macOS 已签名公证）；zhou-tao/dsh-ui（桌面 + TUI + 手机 H5 + VS Code 四端共享 wire 协议，独立 3088 端口与 `~/.dsh-ui` 数据目录） | GitHub | ① Tauri 壳方案已被验证到"安装即用、跨平台、签名发布"级别，可直接借鉴/复用；② **.NET 桌面端仍是空白**，差异化窗口仍在 |
| 8 | 生态发现入口：NoWint/Oh-My-DSH（每小时更新的 DSH 插件/项目榜单） | GitHub | 立项前先扫榜，避免重复造轮子 |
| 9 | **热度现实（2026-08-26 shields 实测）**：Tauri2 三项目 2/16/10 stars；Electron 两项目 176/141；鲸鱼启动器 7；**鲸鱼娘皮肤 dsh-deep-whale 1.7k**（桌面壳之和的 5 倍以上）；官方 198k | GitHub | **纯"包装壳"无人在意；社区真爱是鲸鱼娘主题本身**——Cetus 若只是"又一个壳"则无存在价值，差异化必须落在"鲸鱼娘桌面体验 + 完成度"上 |

---

## 3. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 语言 / 框架 | C# / **.NET 8（LTS）** ✅ 已终选 | 成熟稳定；WebView2 官方支持；与 PCL2 同一生态；实现交 AI Agent，语言门槛不作数，按平台与复用价值决策 |
| UI 框架 | **WPF**（备选 WinUI 3） | WPF 最成熟、资料最多、WebView2 集成完善；WinUI 3 更现代但坑多，作为二期评估 |
| UI 承载 | **WebView2（Evergreen 运行时）** | Win10/11 预装；Chromium 内核；免打包浏览器，安装包小、启动快 |
| 后端 | **内嵌 Node（sidecar 子进程）**：node.exe + `@deepseek-ai/dsh` + profile 随包分发 | 用户装完不需要 Node；沿用 `dsh web` 全套能力（agent、沙箱、插件） |
| 皮肤 | 经 DSH profile 插件机制安装 **dsh-deep-whale（maid-atelier / orca-link）+ skin-manager** | 社区现成、热切换、互斥兜底；跟随官方插件生命周期 |
| 数据 | **MVP 复用官方 `~/.dsh`**（与 CLI 共享会话）；后续提供独立 `--home` 数据目录开关 | 避免数据分裂；社区版（ChisaAlter）已验证独立目录模式可行 |
| 主题联动 | 跟随系统/内置 亮暗切换（官方主题按钮）；皮肤昼夜场景经主题服务联动 | 官方 `ui-theme` 服务 + 皮肤管理器 |

**壳技术栈对比（2026-08 调研后新增）**：

| 方案 | 社区现状 | 优势 | 劣势 |
|---|---|---|---|
| **Tauri 2（Rust）** | 已有 3+ 成熟项目（dsh-desktop / dsh-desk / dsh-ui） | 跨平台、安装包小、社区验证充分、可直接借鉴 | Rust 学习成本；无 .NET 系统集成优势；不做差异化就只是重复 |
| **.NET + WebView2（Cetus 原案）** | 空白 | Windows 原生集成强（注册表/凭据管理器/PowerShell）；C# 生态；与 PCL 语境一致 | 仅 Windows；需自建进程管理等全部基建 |
| **Electron** | 最全（agent-earth / ChisaAlter） | 功能最完整、可直接使用 | 体积大、内存占用高、无差异化空间 |

**无论选哪个壳，必须抄 dsh-desk 的安全基线**：不授予网页任何 IPC/Shell/FS 能力、导航锁死运行时源、外链走系统浏览器、只监管自己启动的进程组。

**路线决策**：
- **路线 A（先做）**：壳加载 `http://127.0.0.1:3080`（或 `--port 0` 随机端口），Node 作 sidecar。最简单、零协议工作；随机端口方案已被 Tauri 项目验证，可同时解决端口冲突。
- **路线 B（二期可选）**：壳加载本地 dist（file://）+ 自研 fetch/IPC 桥，复刻 DSH 为 Electron 设计的进程内载体。无端口占用、可离线，但需在壳侧实现 HTTP POST + 下行 WebSocket 协议。
- **明确不做**：C（原生客户端直连 SDK，重写 UI——等于重做产品）、D（重写 harness——不现实）。

---

## 4. 架构设计

### 4.1 进程模型

```
Cetus.exe（WPF 主进程）
 ├── Mutex 单实例互斥
 ├── 进程管理：spawn node.exe（dsh web，CreateNoWindow + 隐藏窗口）
 ├── 健康探针：GET http://127.0.0.1:3080/ → 200 且含 id="root"
 ├── WebView2 窗口（加载 http://127.0.0.1:3080，约 3 列布局原样呈现）
 ├── 托盘：显示/隐藏窗口、重启后端、打开数据目录、退出
 └── 退出：Job Object 回收 node 进程树（防止残留）
```

### 4.2 启动流程（沿用鲸鱼启动器的验证逻辑）

1. 单实例检查（Mutex）——已有实例则激活其窗口并退出
2. 健康检查：GET 目标 URL，`200 + id="root"` → 直接复用，跳到 5
3. 端口占用观察：端口被占但 15s 内不健康 → 明确报错（不抢端口）
4. 拉起 dsh：`node.exe ... dsh web`（记录 execPath + argv + cwd），最多等待 60s 就绪
5. 打开 WebView2 窗口（启动画面过渡）

### 4.3 运行期行为

- **后端崩溃检测**：WebView2 加载失败 / 轮询健康探针失败 → 故障页 → 自动重启后端（ChisaAlter 已验证此模式）
- **窗口关闭** → 进托盘（可配置）；真正退出时确保 node 子进程退出
- **会话恢复**：会话数据在 `~/.dsh/sessions`，重启后侧边栏可找回历史对话（复用官方持久化）
- **皮肤**：首次启动引导安装 skin-manager + maid-atelier；注意两套皮肤互斥（skin-manager 自动兜底回退官方默认）

### 4.4 安全边界

- 仅回环访问（127.0.0.1），不开放 0.0.0.0；不引入远程端口
- 沿用官方 loopback 特权方法集限制（settings/credentials/agentPreset 等仅 loopback 可调）
- WebView2 同源策略与浏览器一致；不注入任何页面脚本（壳不碰产品数据）

---

## 5. 品牌与视觉（鲸鱼娘）

- **图标**：鲸鱼座星图 / 鲸鱼娘主题（多分辨率 ICO，参考鲸鱼启动器的 `win32icon` 嵌入方式）
- **窗口壳**：原生标题栏（MVP）→ 自绘标题栏（二期，跟随皮肤）
- **启动画面**：品牌 splash，等待后端就绪
- **皮肤素材合规**：maid-atelier / orca-link 为 CC BY-NC-SA 4.0（非商用、需署名、衍生同许可）——**随包分发需保留署名；若走商用路线需更换或授权素材**
- 代码本体 MIT，素材与代码分开声明（参考 dsh-whale-desktop-launcher 的 ASSET_LICENSE 模式）

---

## 6. 里程碑

| 阶段 | 内容 | 预估 | 状态 |
|---|---|---|---|
| **M0 骨架验证** | WPF + WebView2 加载 `dsh web`；最小窗口 + 退出回收 node | 1–2 天 | ✅ 2026-08-26 完成：构建 0 错 0 警，冒烟 20s 存活（复用健康 3080） |
| **M1 生命周期** | 单实例、健康检查/拉起/报错、托盘、后端崩溃自动重启、皮肤引导安装 | 约 1 周 | ⏳ 下一步 |
| **M2 打包分发** | 内置 node + dsh（钉版本）、安装器（MSIX / Squirrel）、更新通道、代码签名评估 | 1–2 周 | 待定 |
| **M3 可选增强** | 路线 B（file:// 离线壳）、Windows 凭据管理器、系统通知、开机自启、自动更新、自绘标题栏 | 按需 | 待定 |

---

## 7. 风险与对策

| 风险 | 对策 |
|---|---|
| DSH 迭代快，接口/行为漂移 | 钉版本（同社区做法）+ 更新通道 + 升级前回归验证 |
| 端口 3080 冲突 | 沿用观察-报错逻辑；二期支持自定义端口 |
| WebView2 运行时缺失（老 Win10） | 引导安装 / 随包携带 fixed version（+约 100MB） |
| 皮肤互斥（设置按钮消失、侧栏错乱） | 只随包预装 skin-manager，皮肤由用户自行切换；遵循官方互斥机制 |
| 素材商用限制 | 代码 MIT / 素材 CC BY-NC-SA 分离声明；商用需替换或授权 |
| 会话数据兼容（升级迁移） | 不硬拷 profiles；提供导入导出指引（参考 ChisaAlter 的迁移注意事项） |
| 签名与分发 | Windows 代码签名证书评估；MSIX 商店 / winget / 安装器三选 |

---

## 8. 参考清单

- **鲸鱼启动器（C# 可复用逻辑）**：`github.com/HUITianYi/dsh-whale-desktop-launcher`
  - `src/DeepSeekHarnessLauncher.cs`：健康探针 `id="root"`、`EnsureServer`、`IsPortInUse`、Chromium `--app` 窗口
  - `lib/install.js`：`CommandLineToArgvW` 引号规则、launcher.ini Base64 编码、桌面路径解析
  - `scripts/build-launcher.ps1`：系统自带 csc.exe 零依赖编译（可作为 Cetus 无 SDK 构建的参照）
- **DSH 官方仓库**（`F:\deepseek-harness`）：
  - `packages/host/webserver`：file:// + IPC 桥设计说明
  - `packages/client/connection`：进程内载体 / HTTP+WS 双流协议
  - `packages/bundle/web-app/cordis.patch.yml`：web 组合构成
  - `packages/host/directory-picker-*`：原生目录选择器（桌面壳天然兼容）
- **社区桌面端（功能参照）**：
  - `github.com/agent-earth/deepseek-harness-desktop`（Electron，三平台，最小壳）
  - `github.com/ChisaAlter/Deepseek-Harness-Desktop`（Electron，启动器/插件市场/托盘/自动更新/独立 dsh-home）
- **Tauri 2 桌面端（壳方案参照，2026-08 调研）**：
  - `github.com/kyorakuyk/dsh-desktop`（Tauri2 + Node sidecar + `--port 0` 随机端口 + stdout URL 解析 + 三平台发布）
  - `github.com/majiayu000/dsh-desk`（钉版本 + 私有 DSH_HOME + 每日兼容检查 + 零 IPC 安全基线 + 签名/公证）
  - `github.com/zhou-tao/dsh-ui`（Tauri2 桌面 + Ink TUI + 手机 H5 + VS Code 四端；独立 3088 端口与 `~/.dsh-ui`）
- **生态榜单**：`github.com/NoWint/Oh-My-DSH`（每小时更新的插件/项目盘点）
- **皮肤**：`github.com/Small-tailqwq/dsh-deep-whale`（maid-atelier / orca-link / skin-manager）

---

## 9. 待办与开放问题

- [ ] GitHub 组织名 / npm 包名 / 域名 占用核查（cetus / cetus-desktop / whalecetus 等）
- [ ] **项目性质终选（2026-08 热度调研后新增）**：桌面壳品类社区热度极低（Tauri2 项目 2–16 stars）——定调三选一：① 个人练手/自用（Cetus 照做，限 M0–M1 规模，不期待用户）；② 产品化（以"鲸鱼娘品牌桌面体验"为卖点做精，壳技术栈无所谓）；③ 不造壳（浏览器 + 皮肤已是 1.7k stars 的选择）。**当前按 ① 推进：M0 骨架先行，规模受限**
- [x] **壳技术栈终选：.NET 8 + WPF + WebView2**（2026-08-26 决策：目标仅 Windows + 实现交 AI Agent + 鲸鱼启动器 C# 进程管理代码可直接复用 + .NET 桌面壳社区空白）
- [ ] **壳技术栈终选：.NET + WebView2（原案） vs Tauri 2（社区已验证） vs 直接使用社区 Tauri/Electron 成品**——若选 .NET 需明确差异化点（Windows 系统集成 / 鲸鱼娘品牌 / C# 生态）；若选 Tauri 2 则参考 dsh-desktop/dsh-desk 直接改造
- [ ] WPF vs WinUI 3 最终确认（默认 WPF；仅当终选 .NET 时适用）
- [ ] 独立数据目录（`--home`）策略与迁移指引
- [ ] 安装器形态：MSIX / Squirrel.Windows / 自定义
- [ ] 图标与启动画面素材方案（星图 or 鲸鱼娘，商用合规确认）
- [ ] 皮肤随包分发还是引导安装（默认：引导安装）
- [ ] 更新通道设计（dsh 版本 + 壳版本双轨）
