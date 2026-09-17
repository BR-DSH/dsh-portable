# DSH 便携版 —— 内网离线部署说明

本包是 **完全自包含** 的 DeepSeek Harness（DSH）环境：内置 Node 运行时、pnpm 与
**完整离线依赖缓存**。目标机器 **不需要联网、不需要安装任何运行时**，解压后首启即可用。

---

## 一、这个包怎么来

由 `D:\DSH`（开发/日常使用目录）抽取"干净源码树"，再用全新 pnpm store 重新固化依赖：

| 内容 | 说明 |
| --- | --- |
| `node\` | Node v24.19.0 + pnpm 11.19.0（自带，静态链接 CRT，不依赖 VC++ 运行库） |
| `store\` | 全新构建的 pnpm 离线缓存，含 `app-npm` 与 `profiles/web` 两棵依赖树的全部包 |
| `dsh-home\runtimes\dshdoc-runtime-win32-x64\` | dsh-doc 的 CPython + Tesseract OCR 运行时（147 MB） |
| `vendor\native-fixups\` | pnpm 缓存**无法**重建的原生/下载产物（cloudflared、node-pty conpty、ssh2/cpu-features） |
| `DshDesktop.exe` | 桌面壳，自包含 .NET 9.0.19（`includedFrameworks`，目标机免装 .NET） |

版本清单：

- `@deepseek-ai/dsh` **0.1.5-rc.1**
- Web UI 全家桶 `@linxin666/dsh-web-all` **0.3.21**
- `@liustack/modsearch` **5.10.2** ／ `dsh-doc` **0.1.1**
- 本地插件 `dsh-endfield-boot` 1.0.0 ／ `dsh-pet-perlica` 0.1.0
- Node **v24.19.0** ／ pnpm **11.19.0**

---

## 二、目标机器怎么用

1. **解压**：把 zip 解到任意目录（例如 `D:\DSH`）。**必须保持目录结构**，
   路径里尽量不要有特殊字符。整个文件夹可以随意搬动、换机器。
2. **首次运行**（两种入口任选）：
   - 双击 `DshDesktop.exe` —— 桌面界面（内嵌浏览器）
   - 双击 `start-dsh.bat` —— 命令行启动，用系统浏览器打开 `http://127.0.0.1:3099`
3. **首次启动会离线展开依赖**，约 2–5 分钟（进度见窗口输出），此过程 **不访问网络**：
   - 从 `store\` 离线重建 `app-npm\node_modules` 与 `profiles\web\node_modules`
   - 由 `repair-natives.ps1` 把 `vendor\native-fixups\` 里的原生/下载产物回填
   - 校验 `dsh-home\runtimes\` 里的 OCR 运行时
4. **配置 API Key**：首次启动会自动从 `.env.example` 生成 `app-npm\.env`，
   填入 `DEEPSEEK_API_KEY=sk-xxxx` 后重启即可；也可在界面「模型设置」里配置
   多个 provider（Ollama 本地 / 超算平台 / DeepSeek 官方）。

### 哪些功能需要联网

| 不需要网络 | 需要网络 |
| --- | --- |
| 启动、会话、文件读写、代码执行、插件加载 | 调用 LLM API（DeepSeek 官方 / 超算平台） |
| 依赖重建（首启） | 联网搜索（modsearch）、X 搜索 |
| PDF/Word/Excel/PPT 解析 + OCR（全本地） | cloudflared 内网穿透隧道（如需使用） |

> 更新检查已由启动脚本强制关闭（`DSH_NO_UPDATE_CHECK=1`），离线不会卡在检查更新上。

---

## 三、WebView2 说明

`DshDesktop.exe` 的内嵌浏览器需要系统的 **Edge WebView2 运行时**。
Windows 11 与现代 Windows 10（带 Edge）通常已预装；若缺失，桌面壳会提示联网下载
（内网会失败），此时有两种办法：

- 点界面上的「浏览器打开」，改用系统浏览器访问 `http://127.0.0.1:3099`（功能完整）
- 离线安装 WebView2 Evergreen Standalone 安装包（`MicrosoftEdgeWebView2RuntimeInstallerX64.exe`，
  可一并随 Release 提供）

---

## 四、常用命令与排障

| 命令 | 作用 |
| --- | --- |
| `start-dsh.bat` / `start-dsh.ps1 [-Port N] [-NoOpen]` | 启动（含首启自检与依赖离线恢复） |
| `stop-dsh.bat` | 停止服务 |
| `repair-deps.ps1` | 只重建依赖链接，不启动 |
| `repair-natives.ps1` | 只回填 `vendor\native-fixups\` 的原生产物 |
| `dsh.cmd --profile headless "任务"` | 一次性 headless 任务 |
| `logs\dsh-web.out.log` / `.err.log` | 启动日志 |

**首启依赖恢复失败时**：确认 `store\` 与 `vendor\` 目录完整（解压时被杀软拦截、
或被增量解压工具跳过是常见原因），然后重跑 `start-dsh.bat`。

---

## 五、安全说明

- 本包 **不含** 任何 API Key、凭证、聊天记录、附件：`app-npm\.env`、
  `dsh-home\.credentials.yaml`、`dsh-home\sessions\`、`dsh-home\storages\`、
  `dsh-home\attachments\`、`dsh-home\webview2-data\` 均已剔除。
- 首次运行时会自行生成 `app-npm\.env` 模板和 `dsh-home\.anonymous-user-id`。
- 默认权限预设为 `danger-full-access`（与开发机一致），如需收紧请改
  `dsh-home\settings.yaml`。
