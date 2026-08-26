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
├── MainWindow.xaml(.cs)    # 窗口 + WebView2 + 托盘
└── Hosting/
    ├── DshLocator.cs       # 定位 node.exe 与 dsh 入口（env 覆盖 → PATH 扫描 → npm 全局探测）
    └── DshHost.cs          # 拉起/健康检查/端口占用观察/退出回收
```

## 里程碑

- [x] M0 骨架：WPF + WebView2 + dsh 进程生命周期（构建通过 + 冒烟通过）
- [ ] M1：托盘完善、崩溃自动重启、皮肤引导安装
- [ ] M2：内嵌 Node 打包、安装器、签名
