# Cetus.Desktop — M0 骨架验证

DeepSeek Harness 的 Windows .NET 桌面壳（路线 A：WebView2 加载 `http://127.0.0.1:3080`，node 作 sidecar）。

## M0 范围

- [x] WPF + WebView2 最小窗口，加载本机 `dsh --profile web`
- [x] 后端生命周期：健康探针（200 + `id="root"`）→ 复用或隐藏拉起 node → 轮询就绪
- [x] 退出回收 node 进程树（仅回收自己拉起的；复用已有服务则不杀）
- [ ] 单实例 / 托盘 / 崩溃自愈（M1）
- [ ] 打包分发（M2）

## 构建

```powershell
# 需要 .NET 8 SDK（本机装在 %LOCALAPPDATA%\Microsoft\dotnet）
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build F:\Cetus\cetus-desktop\src\Cetus.Desktop\Cetus.Desktop.csproj -c Debug
```

## 运行与验证

| 场景 | 做法 | 预期 |
|---|---|---|
| 复用路径 | 先启动任意 `dsh web`（如 3080 已有实例），再运行 Cetus.Desktop.exe | 直接加载，不 spawn node，退出不杀已有服务 |
| 拉起路径 | 单独运行（无 3080 服务） | 隐藏拉起 node，窗口加载 UI；关闭窗口后 node 进程树消失 |
| 隔离测试 | `$env:CETUS_PORT=3081; $env:DSH_HOME="F:\Cetus\.test-home"; .\Cetus.Desktop.exe` | 在独立端口/数据目录拉起，不影响 3080 实机 |

## 环境变量（M0 阶段配置）

| 变量 | 含义 | 默认 |
|---|---|---|
| `CETUS_HOST` / `CETUS_PORT` | sidecar 地址 | `127.0.0.1` / `3080` |
| `CETUS_NODE_EXE` | node.exe 路径覆盖 | 打包内 runtime → 该变量 → Program Files/PATH 自动探测 |
| `CETUS_DSH_CLI_JS` | dsh `lib/bin.js` 路径覆盖 | 打包内 runtime → 该变量 → `%APPDATA%\npm` 等自动探测 |
| `DSH_HOME` | 传给 sidecar 的数据目录 | 不传（沿用默认 `~/.dsh`） |

日志：sidecar 输出 → `%LOCALAPPDATA%\Cetus\logs\dsh-*.log`；壳层 trace → `cetus-shell.log`；崩溃 → `cetus-crash.log`。

## 代码结构

```
src\Cetus.Desktop\
  App.xaml(.cs)          应用入口（M0 单窗口）
  MainWindow.xaml(.cs)   窗口 + WebView2 + 启动编排（splash/失败重试）
  Core\CetusConfig.cs    环境变量驱动的配置
  Core\DshServerProcess.cs   sidecar 生命周期（探针/拉起/轮询/回收）
```

## 打包（M2 先行版）

一键发布：`scripts\publish.ps1`（.NET 8 SDK + npm 需要网络）。

产物（`cetus-desktop\dist\`）：

- `app\` — 可运行目录：**自包含**（目标机无需 .NET）、内嵌钉版本的 `runtime\node.exe` + `runtime\dsh\`（npm `--omit=dev` 安装的 `@deepseek-ai/dsh@0.1.0-rc.6` 及其依赖），双击 `Cetus.Desktop.exe` 即起，不依赖全局 Node/npm/dsh
- `Cetus-0.1.0-win-x64-portable.zip` — 便携包
- `Cetus-Setup-0.1.0.exe` — **安装程序**（Inno Setup，约 91 MB，中文向导）：
  - 按用户安装到 `%LOCALAPPDATA%\Cetus`，**无需管理员权限**
  - 开始菜单 + 可选桌面快捷方式；控制面板/设置可卸载（含开始菜单快捷方式清理）
  - 安装前若 Cetus 在运行会自动询问并关闭（含残留的后端 node 进程，仅匹配 `runtime\node.exe` 命令行，不影响其他 node）
  - 卸载保留用户数据（`%LOCALAPPDATA%\Cetus\logs` 与 WebView2 缓存），属有意设计

运行时解析顺序：**打包内 `runtime\` → 环境变量覆盖（CETUS_NODE_EXE / CETUS_DSH_CLI_JS）→ 系统探测**。钉版本记录在 `app\runtime\VERSIONS.txt`（cetus / node / dsh / built）。

日志（打包版同样适用）：`%LOCALAPPDATA%\Cetus\logs\`（cetus-shell.log / dsh-*.log / cetus-crash.log）；WebView2 数据：`%LOCALAPPDATA%\Cetus\WebView2`。

尚未做（PLAN.md M2 其余项）：代码签名、更新通道、图标资源；安装器形态目前为 Inno（MSIX/Squirrel 仍可评估）。

## 已知限制（M0 有意省略，见 PLAN.md）

- 端口被占但 15s 内不健康 → 目前直接报"未就绪"（M1 补观察-报错逻辑）
- 后端崩溃后的自动重启 → M1
- 进程树回收用 `Process.Kill(entireProcessTree)`，Job Object 加固 → M1
