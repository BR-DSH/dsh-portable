# DshHub / DshDesktop —— DeepSeek Harness 图形界面

> **状态**：DshHub 已存档 —— 不再随包分发 `DshHub.exe`（已从仓库删除），源码保留于
> `launcher\DshHub\` 供查阅；如需重建可运行 `build-hub.ps1`。日常使用请用 `DshDesktop.exe`。

双击包根目录下的 `DshDesktop.exe`（桌面版）即可使用。

## DshDesktop（桌面版，推荐日常使用）

把 DSH 主界面**内嵌进原生窗口**（WebView2），像一个原生桌面应用：

* **主区域**：WebView2 内嵌 `http://127.0.0.1:3099` 的 DSH 界面；用户数据在
  `dsh-home\webview2-data`（随包移动，会话/皮肤跟着走）。
* **控制条**：启动 / 停止 / 重启 / 检查更新 / 立即更新 / 版本信息 / 日志 / 浏览器打开。
* 首次启动：自动环境自检（链接修复、.env 模板）→ 自动拉起服务（`-NoOpen`，不弹浏览器）
  → 检测 WebView2 运行时并加载界面。
* **WebView2 运行时**：绝大多数 Win10/11 已内置（与 Edge 同源）。缺失时首次启动会
  提示下载 Microsoft 官方引导器（约 2MB，装到当前用户，无需管理员）。
* 完全离线场景：可预先下载 WebView2 Fixed Version 运行时（约 150MB）解压到
  `dsh-home\webview2-runtime\`，DshDesktop 会优先使用（不安装、随包移动）。

## DshHub（控制台）

纯控制面板：状态、启动/停止/重启、检查/立即更新、打开界面/日志/备份、实时日志。

两者共享同一套引擎（`launcher\DshCore\`）与同一批脚本（start/stop/update/repair-deps），
行为一致、修复同步。

## 功能（DshHub 控制台）

| 功能 | 说明 |
| --- | --- |
| 启动 / 停止 / 重启服务 | 调用包内 `start-dsh.ps1` / `stop-dsh.ps1`，行为与命令行完全一致 |
| 检查更新 | 调用 `update-dsh.ps1 -Check`，只读，不修改任何文件 |
| 立即更新 | 调用 `update-dsh.ps1 -Yes`：自动停止服务 → 备份 → 升级核心与插件 → 启动自检 |
| 打开界面 | 浏览器打开 `http://127.0.0.1:3099` |
| 打开日志 / 备份 / 安装目录 | 资源管理器直达对应目录 |
| 实时日志 | 底部控制台窗口，按内容自动着色，自动滚动，可清空 |

## 关闭行为（两者一致）

* 点 ✕（或 Alt+F4）关闭时，如果服务正在运行会弹出确认：
  - **停止并退出**：先停服务再关窗口；
  - **仅退出**：只关界面，服务继续后台跑；
  - **取消**：什么都不做。
* 内置崩溃兜底：未处理异常只记录到 `logs\dsh-gui-crash.log`，不会闪退。

## 运行要求

* Windows 10/11 x64
* **无需安装任何运行时**：两个 exe 都是**自包含**单文件（内置 .NET 运行时）。
  首次运行会自动解压内置文件到临时目录（稍慢几秒属正常）。
* DshDesktop 额外需要 WebView2 Runtime（见上，通常已内置/可自动下载）。
* 若弹 SmartScreen“已保护你的电脑”提示，点“更多信息 → 仍要运行”。

## 重新构建

在装有 .NET 9 SDK 的机器上，从本目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File build-hub.ps1       # 生成 DshHub.exe
powershell -ExecutionPolicy Bypass -File build-desktop.ps1   # 生成 DshDesktop.exe
```

脚本会：
1. 用 `IconGen`（WPF）把鲸鱼 favicon 渲染成 `DshHub.ico` / `whale-*.png`；
2. 以 **win-x64 自包含单文件** 方式发布，产出包根目录的 exe
   （DshHub ~57MB，DshDesktop ~57MB，均内置 .NET 运行时，目标电脑免安装）。
   DshDesktop 另引用 `Microsoft.Web.WebView2` NuGet（构建时联网还原）。

## 目录结构

```
launcher\
  build-hub.ps1         构建 DshHub.exe（生成图标 + 发布）
  build-desktop.ps1     构建 DshDesktop.exe（发布自包含单文件）
  IconGen\              WPF 小工具：把 whale SVG 路径渲染成多尺寸 .ico / PNG
  DshCore\              共享引擎（路径/脚本执行/环境自检/状态/版本/更新/关闭行为）
  DshHub\               WPF 控制台源码
  DshDesktop\           WPF 桌面版源码（WebView2 内嵌 DSH 界面 + 控制条）
  publish\              发布中间产物（已 gitignore）
  publish-desktop\      桌面版发布中间产物（已 gitignore）
```

## 设计说明

* GUI 只负责界面与调度，**所有实际操作仍走包内久经验证的 PowerShell 脚本**，
  因此升级流程、依赖修复、身份校验等逻辑不会与脚本分叉。
* 运行方式：GUI 通过 `powershell.exe -Command` 以 UTF-8 输出方式调用脚本，
  实时捕获 stdout/stderr 并在日志窗口按行着色。
* 图标取自已发布包 `@deepseek-ai/dsh-web-frontend/dist/favicon.svg`（鲸鱼）。
