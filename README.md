# Cetus · 鲸鱼座

**DeepSeek Harness 的 Windows .NET 桌面壳**：双击即起，无需命令行、无需浏览器标签页、无需自己装 Node。

> 核心哲学：**只做壳，不重做产品**。官方 Web UI、agent 能力、插件生态 100% 复用；我们交付的是"把官方界面装进原生窗口"的那层壳，以及它带来的桌面体验。
> 设计思路、调研与开放问题见 [`PLAN.md`](PLAN.md)。

## 仓库结构（两套并行实现）

| 目录 | 框架 | 状态 | 特点 |
|---|---|---|---|
| [`cetus-desktop/`](cetus-desktop/) | .NET 8 + WPF + WebView2 | ✅ M0 全链路验证 + 打包完成 | sidecar 生命周期（探针/拉起/回收）、trace 日志、**打包管线**：自包含发布 + 内嵌钉版本 node/dsh + 便携 zip + Inno 安装程序；构建/运行/验证见其 [README](cetus-desktop/README.md) |
| [`src/Cetus.Desktop/`](src/Cetus.Desktop/) | .NET 10 + WPF + WebView2 | ✅ M0 冒烟通过 | 单实例 Mutex、**托盘**（关窗进托盘/退出回收 node）、端口占用 15s 宽限报错、健康探针 |

## 里程碑

- [x] M0 骨架：WPF + WebView2 + dsh 进程生命周期（两套实现均构建/冒烟通过）
- [x] 打包（M2 先行版）：自包含发布 + 内嵌钉版本 node/dsh + 便携 zip + Inno 安装程序（`cetus-desktop/`）
- [ ] M1：托盘完善、崩溃自动重启、皮肤引导安装
- [ ] M2：内嵌 Node 打包、安装器、签名（`src/` 侧）

## 技术栈

C# · .NET 8 / .NET 10 · WPF · WebView2 · 内嵌 Node sidecar（`@deepseek-ai/dsh`）· MIT License
