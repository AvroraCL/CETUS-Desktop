# Cetus · 鲸鱼座

**DeepSeek Harness 的 Windows .NET 桌面壳**：双击即起，无需命令行、无需浏览器标签页、无需自己装 Node。

> 核心哲学：**只做壳，不重做产品**。官方 Web UI、agent 能力、插件生态 100% 复用；我们交付的是"把官方界面装进原生窗口"的那层壳，以及它带来的桌面体验（托盘、单实例、一键启动）。
> 设计思路、调研与开放问题见 [`PLAN.md`](PLAN.md)。

## 状态

**M0 骨架 ✅ 已构建并冒烟通过**（.NET 10 版）。技术栈：**.NET 10 + WPF + WebView2**（Node 以 sidecar 子进程形式内嵌运行 `dsh web`）。

> 注：仓库历史中保留过一版 .NET 8 实现及其打包管线（便携 zip + Inno 安装程序），2026-08-27 决定统一为 .NET 10 后已移除；打包管线如需可参照历史提交重建。

## 运行前提

- Windows 10/11
- Node.js ≥ 22.19（M0 阶段直接复用本机 dsh；打包阶段再内嵌）
- 已安装 `@deepseek-ai/dsh`（`npm i -g @deepseek-ai/dsh` 或本机已可用 `dsh web`）
- .NET 10 SDK（已装于 `%USERPROFILE%\.dotnet`，用户 PATH 已含；新开终端生效）

## 运行

```powershell
dotnet run --project src/Cetus.Desktop
```

或直接运行构建产物：`src\Cetus.Desktop\bin\Debug\net10.0-windows\Cetus.exe`

预期行为：启动窗口 → 状态行显示"正在启动 DSH 主机…" → 健康检查通过（GET http://127.0.0.1:3080 且含 `id="root"`）→ WebView2 加载 GUI → 关窗进托盘 → 托盘"退出"时回收 node 子进程。

## 目录结构

```
src/Cetus.Desktop/
├── App.xaml(.cs)           # 单实例互斥（Mutex）
├── MainWindow.xaml(.cs)    # 窗口 + WebView2（显式用户数据目录）+ 托盘
└── Hosting/
    ├── DshLocator.cs       # 运行时定位：打包内 runtime → env 覆盖 → PATH → npm 全局 → dsh shim
    └── DshHost.cs          # 拉起/健康检查/端口占用观察/退出回收；sidecar 日志落盘
```

## 打包（M2）

一键发布：`scripts\publish.ps1`（.NET 10 SDK + npm，需网络）。产物（`dist\`）：

- `app\` — 自包含运行目录（目标机无需 .NET/Node），内嵌钉版本 `runtime\node.exe`（v24.14.0）+ `runtime\dsh\`（`@deepseek-ai/dsh@0.1.0-rc.6`，`--omit=dev`）；版本清单 `runtime\VERSIONS.txt`
- `Cetus-0.1.1-win-x64-portable.zip` — 便携包
- `Cetus-Setup-0.1.1.exe` — Inno 安装程序（中文向导，按用户安装 `%LOCALAPPDATA%\Cetus`，无需管理员；安装前自动关闭运行中的 Cetus 及其残留 node；卸载零残留——WebView2 数据在 `%LOCALAPPDATA%\Cetus\WebView2`；版本信息：文件版本 0.0.1.1 / 产品名称 CETUS鲸鱼座 / 产品版本 0.1.1 / 版权 AvroraCL）

运行验证场景：

| 场景 | 做法 | 预期 |
|---|---|---|
| 复用路径 | 已有健康 dsh（如 3080）再启动 | 直接加载，不 spawn node |
| 拉起路径 | 无 3080 服务 | 隐藏拉起打包内 node，加载 UI；托盘"退出"回收 node 树 |
| 隔离测试 | `$env:CETUS_PORT=3084; $env:DSH_HOME="F:\Cetus\.test-home"` | 独立端口/数据目录，不影响实机 |

日志：sidecar → `%LOCALAPPDATA%\Cetus\logs\dsh-*.log`；壳层崩溃 → `cetus-crash.log`。

## 里程碑

- [x] M0 骨架：WPF + WebView2 + dsh 进程生命周期（构建通过 + 冒烟通过）
- [x] M2 打包：自包含发布 + 内嵌钉版本 node/dsh + 便携 zip + Inno 安装程序
- [ ] M1：崩溃自动重启、皮肤引导安装、Job Object 加固
- [ ] M2 余项：代码签名、更新通道、图标资源
