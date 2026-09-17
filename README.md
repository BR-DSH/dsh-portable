# DeepSeek Harness 自包含便携版（DSH）

一个**完全自包含**的 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 环境：
内置 Node.js 运行时、pnpm 与离线依赖缓存，目标电脑**无需安装任何运行时**；
所有用户数据、配置、插件都收在包内，**整个文件夹拷到任意机器即可用**；
新机器首次运行会自动展开依赖，有网自动补齐、离线用内置缓存恢复，**自主运行**。

> 本项目同时用局域网 Git（Gitea）做多机分发：`git pull` 拉新代码与插件清单，
> 各机器首启自动装依赖，实现"一处维护、处处同步"。

---

## 设计目标：自包含（Self-Contained）

| 原则 | 实现 |
| --- | --- |
| 免安装运行时 | 内置 `node\`（Node v24.19）+ `tools\pnpm.cmd` + `store\` 离线 pnpm 缓存 |
| 数据随包走 | 一切用户数据在 `dsh-home\`（会话/设置/凭证/插件/OCR 运行时），不依赖 `~/.dsh` |
| 新机器首启自动展开 | `start-dsh.ps1` 自动修复 pnpm 链接；缺依赖时**先离线用 `store\` 恢复，不够再联网补齐** |
| 更新闭环 | `update-dsh.ps1` 升级核心 + 按 `plugin-track.json` 清单升级插件，升级前自动备份、可回滚 |
| 单一数据源 | 插件追踪清单 `plugin-track.json` —— 更新脚本与 DshDesktop 版本面板共用，加一行即三处生效 |
| 密钥安全 | `.env` / `.credentials.yaml` / 会话等全部 gitignore，绝不上 git 服务器 |

---

## 快速开始

**方式一：桌面版（推荐）**
双击根目录 `DshDesktop.exe`（自包含单文件，免装 .NET / WebView2 缺时会自动引导）。
界面内嵌 WebView2 运行 Web UI（`http://127.0.0.1:3099`），控制条提供
启动 / 停止 / 重启 / 检查更新 / 立即更新 / 版本信息 / 刷新界面 / 浏览器打开 / 日志。

**方式二：命令行**
```bat
start-dsh.bat        :: 首次运行自动展开依赖并启动，浏览器打开 http://127.0.0.1:3099
stop-dsh.bat         :: 停止
```

**配置 API Key**
首次启动若没有 `app-npm\.env` 会自动从 `.env.example` 生成模板，填入：
```
DEEPSEEK_API_KEY=sk-xxxx
OLLAMA_API_KEY=ollama        # Ollama 本地模型用（可选）
```
也可在 Web 界面「模型设置」里配置多个 provider（Ollama 本地 / 超算平台 / DeepSeek 官方）。

> 迁移提示：旧版端口是 3080，现统一为 **3099**（避开开发目录常驻的 3080）。

---

## 常用命令

| 命令 | 作用 |
| --- | --- |
| `start-dsh.bat` / `start-dsh.ps1 [-Port N] [-NoOpen]` | 启动（含首启自检与依赖恢复） |
| `stop-dsh.bat` / `stop-dsh.ps1` | 停止服务 |
| `dsh.cmd --profile headless "任务"` | 一次性 headless 任务 |
| `update-dsh.ps1 -Check` | 只检查更新（不改动） |
| `update-dsh.ps1 -Yes` | 立即升级（自动停服务→备份→升级核心+清单插件→自检） |
| `repair-deps.ps1` | 只重建 pnpm 依赖链接，不启动 |
| `rollback-dsh.ps1 -List` / `-Restore <备份名>` | 查看 / 恢复升级前备份 |

---

## 插件体系

插件由 pnpm 管理在 `dsh-home\profiles\web\`（package.json + pnpm-lock.yaml），
**追踪清单 `plugin-track.json` 是单一数据源**（update-dsh.ps1 与 DshDesktop 版本面板共用）：

```json
{ "plugins": [
  { "name": "@linxin666/dsh-web-all", "label": "Web UI 全家桶" },
  { "name": "dsh-doc",                 "label": "dsh-doc 文档/OCR" },
  { "name": "@liustack/modsearch",     "label": "modsearch 联网搜索" }
] }
```

- **Web UI 全家桶**（`@linxin666/dsh-web-all`）：任务看板、Git 图谱、皮肤中心、
  右侧面板、令牌统计、远程运维等（全家桶全量启用）。
- **dsh-doc**：PDF/Word/Excel/PPT 与扫描件全本地解析 + OCR（CPython + Tesseract），
  运行时在 `dsh-home\runtimes\dshdoc-runtime-win32-x64\`，随包移动。
- **modsearch**：联网搜索插件。提供 `web_search` / `x_search` / `read_page`，
  免 Key 用 Firecrawl 免费额度；若遇 IP 风控可配自己的 key：
  ```
  node dsh-home\profiles\web\node_modules\@liustack\modsearch\dist\main.js config set firecrawl.apiKey <key>
  ```
  搜索 X 需另装 Grok CLI（`grok` 登录）。key 存在 `~/.modsearch\config.json`（用户目录，
  不入 git）；也可在 `app-npm\.env` 设 `FIRECRAWL_API_KEY` 随包带（`start-dsh.ps1` 会读 .env 注入环境）。
- **佩丽卡桌面宠物**（`dsh-pet-perlica`，本地插件在 `plugins\`）：浮动 Q 版精灵，
  随 agent 活动切换动画，可拖动记忆位置。
- **本地插件**（`plugins\dsh-endfield-boot` 等）以 `link:` 依赖挂载，
  `start-dsh.ps1` 首启自动重建 junction 链接。

> 新增追踪插件：改 `plugin-track.json` 加一行 + `dsh.cmd plugin --profile web add <pkg>`，
> 更新、版本显示、更新提示三处自动同步。

---

## 自包含 / 首启自动展开原理

`start-dsh.ps1` 每次启动做：

1. **核心依赖自检**：`app-npm\node_modules\@deepseek-ai\dsh\lib\bin.js` 缺失则
   用 `tools\pnpm.cmd` + `store\` **离线重建**，离线不足自动联网补齐。
2. **Web 插件依赖自检**：按 `dsh-home\profiles\web\package.json` 逐项核对 `node_modules`，
   缺失即从锁文件 + `store\` 离线恢复（`--frozen-lockfile`），不足联网补齐。
3. **本地插件链接修复**：重建 `plugins\` 下 `link:` 插件的 junction。
4. **可选运行时自检**：dsh-doc 的 OCR 运行时缺失时自动下载校验；失败仅临时禁用该插件，不影响启动。
5. 启动服务（端口 3099），等待就绪。

> 所以把整个文件夹拷到新机器（或 git clone 后），首次启动即可**自动展开、自主运行**，
> 有网自动装、离线用内置缓存兜底。

---

## 更新与回滚

- 启动时可自动检查更新（`DSH_NO_UPDATE_CHECK=1` 关闭；离线自动跳过）。
- `update-dsh.ps1 -Check` 检查核心 + 清单插件版本；`-Yes` 执行：停服务 → 备份 → 
  升级核心与插件（经 `dsh plugin --profile web add`）→ 启动自检。
- 升级前自动备份到 `backups\app-<版本>-<时间>\`（含 app-npm 与插件配置）；
  `rollback-dsh.ps1 -Restore <备份名>` 一键回滚。
- 提示：DshDesktop 版本信息面板按 `plugin-track.json` 动态显示各插件本地/最新版本，
  有新版显示琥珀色徽章，可直接点「立即更新」。

---

## 目录结构

```
dsh\                      （整个文件夹 = 便携包）
├─ DshDesktop.exe         桌面版 GUI（自包含单文件，内置 WebView2 逻辑）
├─ start/stop-dsh.bat     命令行启停入口
├─ dsh.cmd                便携 CLI（--profile headless 等）
├─ update-dsh.ps1         更新脚本（核心 + 清单插件）
├─ rollback-dsh.ps1       回滚脚本
├─ plugin-track.json      插件追踪清单（单一数据源）
├─ app-npm\               程序本体（@deepseek-ai/dsh，可自动升级）+ .env（API Key）
├─ node\                 内置 Node.js 运行时（v24）
├─ tools\pnpm.cmd        内置 pnpm
├─ store\                 pnpm 离线依赖缓存（首启用）
├─ dsh-home\              用户数据目录（随包移动）
│  ├─ profiles\web\        Web 插件（node_modules + lockfile + cordis.patch.yml）
│  ├─ sessions\            会话记录
│  ├─ settings.yaml        用户设置 / 模型 provider
│  ├─ .credentials.yaml    API 密钥（gitignore）
│  ├─ runtimes\            dsh-doc 的 OCR 运行时
│  └─ webview2-data\       内嵌浏览器用户数据
├─ plugins\               本地插件源码（pet-perlica / endfield-boot）
├─ launcher\              DshDesktop 源码 + 构建脚本（build-desktop.ps1）
├─ backups\               升级前备份
└─ logs\                  运行日志
```

---

## 数据与密钥说明（重要）

- 所有用户数据在 `dsh-home\`：设置、会话历史、API Key、插件配置全部随包走，
  **不依赖 `~/.dsh`**。整个文件夹可拷贝/移动到任意机器。
- **不要删除 `dsh-home\`**（= 清空所有设置和会话）。备份只需打包 `dsh-home\`（可选加 `app-npm\.env`）。
- 密钥从不入库：`app-npm/.env`、`dsh-home/.credentials.yaml`、会话、`store\`、日志全部 gitignore。
- 本机与 git 的边界：git 只传代码/配置骨架（脚本、插件清单、profile 清单），
  各机器的本地数据（会话、宠物状态、皮肤、WebView 缓存）各自独立。

---

## 通过 Git 分发（局域网 Gitea）

仓库 `.gitignore` 已按"多机同步 + 各机独立本地数据"设计：

```
git clone https://<gitea>/admin/dsh.git      # 新机器拿到骨架
start-dsh.bat                                 # 首启自动展开依赖（有网自动装）
git pull                                      # 后续拉新代码 / 新 exe / 插件清单
```

新机器拿到后运行 `start-dsh.bat` 即可自主跑起来；重编译的 `DshDesktop.exe`、
更新后的脚本、`plugin-track.json` 都随 git 同步。

---

## 常见问题

- **启动失败**：看 `logs\dsh-web.err.log` 最后几行。
- **端口被占用**：`start-dsh.ps1 -Port 1234`。
- **换电脑**：整个文件夹拷贝/解压即可，无需重装；先确保 `store\` 完整可离线恢复。
- **联网搜索被 IP 风控**：配 Firecrawl key（见上「modsearch」）。
- **搜不到 X**：需装 Grok CLI 并登录（modsearch 的 X 源只走 grok）。
- **内嵌浏览器点了链接卡住**：外部链接已改为交给系统默认浏览器；
  万一界面异常点控制条「刷新界面」恢复。

---

## 免责声明

开发者预览版（0.1.x），DeepSeek 官方标注后续可能有破坏性变更；
本包为自包含封装 + 本地魔改插件，非官方发行物。
