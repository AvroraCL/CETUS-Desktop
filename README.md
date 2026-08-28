<div align="center">

<img src="docs/README 品牌横幅.png?v=3" alt="CETUS · 鲸鱼座 — 将 DeepSeek Harness 带到 Windows 桌面的原生工作台" width="100%">

[![release](https://img.shields.io/github/v/release/AvroraCL/CETUS-Desktop?style=flat-square&logo=github)](https://github.com/AvroraCL/CETUS-Desktop/releases)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?style=flat-square&logo=windows11&logoColor=white)](https://github.com/AvroraCL/CETUS-Desktop)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://github.com/AvroraCL/CETUS-Desktop)
[![Node.js](https://img.shields.io/badge/Node.js-24-339933?style=flat-square&logo=nodedotjs&logoColor=white)](https://github.com/AvroraCL/CETUS-Desktop)
[![license](https://img.shields.io/github/license/AvroraCL/CETUS-Desktop?style=flat-square)](LICENSE)

基于 DeepSeek Harness 构建的 Windows .NET 技术栈桌面客户端

</div>

CETUS 把官方 Web UI、Agent 能力和插件生态装进一个开箱即用的 Windows 应用，无需命令行，也不必一直保留浏览器标签页；启动、托盘驻留和后台进程回收都由 CETUS 负责。

> CETUS 是独立社区项目，并非 DeepSeek 官方产品。

## 面向用户

### CETUS 能做什么

- 双击启动 DeepSeek Harness，无需手动运行 `dsh web`
- 在独立原生窗口中使用官方 Web UI
- 关闭窗口后驻留系统托盘，随时重新打开
- 保证应用单实例运行，避免重复启动多个 Harness
- 退出 CETUS 时自动回收由它启动的 Node 子进程
- 启动时自动检查新版本，一键下载安装器并静默升级（GitHub Releases 更新通道）
- 完整复用 DeepSeek Harness 的 Agent 能力与插件生态
- 内置可调宽度的右侧工具栏：标签页式浏览网页、运行 PowerShell、查看本地文件，支持多开、下拉管理与最近关闭恢复

### 下载 v0.2.0

| 文件 | 说明 |
|---|---|
| [Cetus-Setup-0.2.0.exe](https://github.com/AvroraCL/CETUS-Desktop/releases/download/v0.2.0/Cetus-Setup-0.2.0.exe) | 中文安装向导，按当前用户安装，无需管理员权限 |
| [Cetus-0.2.0-win-x64-portable.zip](https://github.com/AvroraCL/CETUS-Desktop/releases/download/v0.2.0/Cetus-0.2.0-win-x64-portable.zip) | 便携版，解压后直接运行 |

历史版本与更新日志见 [Releases](https://github.com/AvroraCL/CETUS-Desktop/releases)。

### 当前状态

CETUS 目前处于早期开发阶段，**M0 桌面骨架与 M2 自包含打包已经完成并通过冒烟测试**。

当前版本可以正常启动、加载、监控和退出 DeepSeek Harness。DSH 进程异常退出或连续健康检查失败时，CETUS 会执行有限次数的自动恢复；代码签名与安全模式仍在开发中。现阶段更适合愿意参与测试和反馈的用户，不建议将它视为完全稳定的正式产品。

### 系统要求

- Windows 10 或 Windows 11（x64）
- WebView2 Runtime（Windows 10/11 通常已经预装）

正式打包版本已内嵌 .NET、Node.js 与 DeepSeek Harness，目标电脑无需另外安装开发环境。

### 安装与使用

从上方下载区或 [Releases](https://github.com/AvroraCL/CETUS-Desktop/releases) 页面获取安装包。

启动后，CETUS 会自动完成以下流程：

1. 查找已经运行的 DeepSeek Harness 服务。
2. 如果没有可用服务，启动内嵌的 Node.js 与 DSH Runtime。
3. 等待服务健康检查通过。
4. 在桌面窗口中加载 DeepSeek Harness 官方界面。

关闭主窗口时，CETUS 会继续驻留系统托盘。需要彻底退出时，请在托盘菜单中选择“退出”。

### 已知限制

- 当前版本尚未进行代码签名，Windows 可能显示安全提醒。
- 暂无安全模式；DSH 自动恢复耗尽后需要从托盘手动重试。
- 右侧终端目前是基础 PowerShell 文本会话，还没有完整的 ANSI/PTY 终端仿真。
- 右侧文件面板目前只能浏览和打开文件，暂不支持创建、重命名、移动或删除。
- 界面与 Agent 能力主要来自 DeepSeek Harness 上游，部分问题可能随上游版本变化。

### 架构约束

桌面窗口只渲染状态并转发用户操作；DSH 生命周期、WebView2 会话策略、Windows 原生集成与配置持久化由各自模块拥有。WPF 仍是原生窗口宿主，WebView 消息桥只补充 CETUS 顶部工具栏和原生侧栏入口，不复制 DeepSeek Harness 的业务界面。

---

## 面向开发者

### 设计原则

> **只做壳，不重做产品。**

CETUS 复用 DeepSeek Harness 的官方 Web UI、Agent 能力和插件生态，桌面层只负责 Windows 宿主能力：窗口、托盘、单实例、Runtime 定位、进程生命周期、健康检查与打包分发。

### 技术栈

- .NET 10
- WPF
- WebView2
- Node.js sidecar
- `@deepseek-ai/dsh`

仓库历史中曾保留一版 .NET 8 实现及其打包管线。项目于 2026-08-27 统一迁移到 .NET 10，旧实现随后移除；如有需要，可从历史提交参考便携 zip 与 Inno Setup 管线。

### 开发环境

- Windows 10/11 x64
- PowerShell 7
- .NET 10 SDK（`global.json` 固定为 10.0.400 同补丁带）
- WebView2 Runtime

无需安装系统 Node、npm 或全局 DSH。首次启动会按 `eng/runtime.json` 下载并校验固定版本 Node，再使用压缩包自带的 npm 和仓库 lockfile 构建 `.dev/runtime`；缓存完整后可离线运行。

统一开发入口：

```powershell
.\scripts\dev.ps1 doctor
.\scripts\dev.ps1 bootstrap
.\scripts\dev.ps1 run
```

常用命令：

```powershell
.\scripts\dev.ps1 run -Profile second -Port 0
.\scripts\dev.ps1 test
.\scripts\dev.ps1 check
.\scripts\dev.ps1 smoke
.\scripts\dev.ps1 reset -Profile second
```

`test` 只运行不依赖真实进程的快速测试；`check` 执行锁定还原、格式检查、Release 构建及包含 Integration 在内的全部测试。`smoke` 会真实启动 Debug 桌面窗口，并验证 DEV HWND、DSH 健康页、固定 Node/DSH 参数和退出后的进程/端口回收。

每个 profile 的设置、DSH_HOME、WebView2 数据、日志和 PID 都隔离在 `.dev/profiles/<name>`。默认端口为 3084；`-Port 0` 动态选择空闲端口。重启只会根据该 profile 的 PID 文件和完整可执行路径停止旧实例，不扫描安装版或其他 Node 进程。

`dev.bat` 是默认 `run` 的薄包装；WSL 中的 `scripts/dev-check.sh` 默认转发到 `check`。UI 改动完成后还需执行[人工 UI 回归清单](tests/manual/ui-checklist.md)。

开发缓存和 profile 默认跨运行保留。仅清理一个 profile 使用 `reset`；确认要删除整个仓库开发缓存时才使用：

```powershell
.\scripts\dev.ps1 reset -All
```

### 运行流程

预期行为：

1. 启动窗口，状态行显示“正在启动 DSH 主机…”。
2. CETUS 对 `http://127.0.0.1:3080` 发起健康检查，并确认页面包含 `id="root"`。
3. 健康检查通过后，WebView2 加载 DeepSeek Harness GUI。
4. 关闭窗口后应用进入系统托盘。
5. 从托盘退出时，CETUS 回收由它创建的 Node 子进程。

### 目录结构

```text
src/
├── Cetus.Runtime/                  # 无 WPF 依赖的运行时程序集
│   ├── Application/                # 单实例进程身份
│   ├── Configuration/              # 用户设置与环境覆盖
│   └── Hosting/                    # DSH 定位、探测、进程树、日志与健康监控
└── Cetus.Desktop/                  # WPF 适配器程序集
    ├── Browser/                    # WebView2 初始化、安全策略、主题与窗口工具栏桥
    ├── Platform/                   # HWND/DWM 与系统托盘
    ├── Runtime/                    # 启动、恢复、改端口与退出状态机
    └── MainWindow.xaml(.cs)        # 薄视图：状态渲染与用户操作

tests/Cetus.Desktop.Tests/          # Runtime、状态机与桌面策略回归测试
```

依赖方向固定为 `Cetus.Desktop → Cetus.Runtime`。`Cetus.Runtime` 不引用 WPF、WebView2 或 WinForms；生产适配器和内存测试适配器通过内部 seam 接入状态机。

### 构建与打包

运行一键发布脚本：

```powershell
scripts\publish.ps1
```

脚本需要 PowerShell 7 与 .NET 10 SDK。它复用与开发环境相同的 Runtime 清单、校验缓存和锁定 bootstrap，不依赖系统 Node/npm。首次 bootstrap 需要网络。构建产物位于 `dist\`：

- `app-0.2.0\`：自包含运行目录，目标电脑无需安装 .NET 或 Node.js
- `Cetus-0.2.0-win-x64-portable.zip`：便携版
- `Cetus-Setup-0.2.0.exe`：Inno Setup 安装程序

当前 Runtime 固定版本：

- Node.js `v24.14.0`
- `@deepseek-ai/dsh@0.1.0-rc.6`（`--omit=dev`）

固定版本与校验值只在 `eng/runtime.json` 维护；发布包内的具体版本记录在 `runtime\VERSIONS.txt`。安装程序默认安装到 `%LOCALAPPDATA%\Cetus`，WebView2 数据位于 `%LOCALAPPDATA%\Cetus\WebView2`；卸载时会一并清理。安装前会自动关闭正在运行的 CETUS 及其残留 Node 进程。

窗口使用真实的非分层 HWND：Windows 11 22H2 及以上启用系统 Desktop Acrylic，Windows 10 使用 DWM blur-behind。标题栏保持直角，并避开会让窗口拖动退回软件合成路径的透明分层窗口方案。

发布后可分别验证便携包运行时和安装程序：

```powershell
scripts\package-smoke.ps1 -ApplicationPath dist\app-0.2.0\Cetus.exe
scripts\installer-smoke.ps1 -InstallerPath dist\Cetus-Setup-0.2.0.exe -ExpectedVersion 0.2.0
```

版本信息：

- 文件版本：`0.0.2.0`
- 产品名称：`CETUS鲸鱼座`
- 产品版本：`0.2.0`
- 版权：`AvroraCL`

### 验证场景

| 场景 | 操作 | 预期结果 |
|---|---|---|
| 复用已有服务 | 保持健康的 DSH 服务运行（如端口 3080），再启动 CETUS | 直接加载已有服务，不创建 Node 子进程 |
| 自动启动服务 | 确保端口 3080 没有 DSH 服务，再启动 CETUS | 使用内嵌 Node.js 启动 DSH；托盘退出后回收进程树 |
| 隔离测试 | `$env:CETUS_PORT=3084; $env:DSH_HOME="F:\Cetus\.test-home"` | 使用独立端口与数据目录，不影响本机现有环境 |

日志位置：

- DSH sidecar：`%LOCALAPPDATA%\Cetus\logs\dsh-*.log`
- CETUS 壳层崩溃：`cetus-crash.log`

### 参与开发

欢迎通过 Issue 提交问题、建议与复现步骤。改动必须保持“只做壳，不重做产品”的定位，并维持 `Cetus.Desktop → Cetus.Runtime` 的单向依赖。
