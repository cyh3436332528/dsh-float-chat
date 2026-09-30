# 贡献指南

感谢你愿意为 `dsh-float-chat` 出力。本文件说明参与方式与代码约定。

> 本项目**跨三种运行环境**——DSH 的 Node 宿主进程、DSH 的沙箱化渲染器、以及一个独立的
> WinForms 原生窗口进程。三者之间没有共享内存，只能靠 HTTP 路由与 WebView2 消息桥通信。
> 所有改动都必须先想清楚「这一段跑在哪一边」。

## 目录

- [行为准则](#行为准则)
- [我可以贡献什么](#我可以贡献什么)
- [开发环境](#开发环境)
- [代码结构](#代码结构)
- [三边的接口约定](#三边的接口约定)
- [编码约定](#编码约定)
- [提交规范](#提交规范)
- [Pull Request 流程](#pull-request-流程)
- [兼容性声明](#兼容性声明)
- [安全](#安全)

## 行为准则

参与本项目即表示你同意遵守 [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md)。

## 我可以贡献什么

按优先级从高到低：

| 类型 | 说明 |
|---|---|
| **Bug 报告** | 用 [Bug 报告模板](./.github/ISSUE_TEMPLATE/bug_report.yml) 提交，**务必附上 DSH 版本、Windows 版本与 WebView2 版本** |
| **跨环境验证** | 在别的 DSH 版本 / 别的 Windows 版本 / 别的缩放比例上试过，无论成败都欢迎反馈 |
| **macOS / Linux 支持** | 当前**只有 Windows 实现**。这是最大的缺口，但工作量大，请先开 Issue 讨论方案 |
| **文档** | README、FAQ、注释里的错误、过时或不准确之处 |
| **测试** | 目前**没有**任何自动化测试 |
| **功能** | 请先开 Issue 讨论，避免写了不被接受的方向 |

**不予接受的方向：**

- **把宿主入口改回 `index.js`。** 改名是让 ESM 路径缓存失效、免重启激活宿主改动的唯一手段，
  见 [CHANGELOG](./CHANGELOG.md)。改回去会让「改代码 → 重启（或换名重装）」这条约定失效。
- **改动 `plugin/` 里的目录结构。** `exports` 直接指向 `host.js` / `client.js` / `page/`，
  移动它们等于破坏安装契约。
- **引入构建步骤。** 浏览器半边必须是手写的、装上即用的模块；宿主半边必须是可直接加载的 ESM。
- 在插件内引入遥测、上报或任何**非必要的**联网行为。
- 把 `window/webview-data/`、`window-log.txt` 之类**含隐私数据的运行时文件**提交进仓库。

## 开发环境

### 前置

- **Windows 10 / 11**（窗口进程是 WinForms，无法在别的平台跑）
- Node.js ≥ 20
- .NET Framework 4.x 自带的 `csc.exe`（仅修改 `FloatWindow.cs` 时需要）
- WebView2 Runtime
- 可正常运行的 **DSH Desktop**
- Git

### 本地开发（`link:` 安装，改完即时可见）

```bash
git clone https://github.com/cyh3436332528/dsh-float-chat.git
cd dsh-float-chat

# 注意：装的是 plugin/ 子目录，不是仓库根
dsh plugin --profile desktop add "$(pwd)/plugin"
```

热重载行为**四块各不相同**，这是本项目最容易踩的坑：

| 部分 | 文件 | 热重载 | 改完怎么办 |
|---|---|---|---|
| 浏览器半边 | `plugin/client.js` | ✅ 热重载 | 直接改 |
| 窗口页面 | `plugin/page/float.html` | ✅ 页面 `no-store` | 重开窗口 |
| 宿主半边 | `plugin/host.js` | ❌ **不热重载** | **重启 DSH Desktop**，或改名/换目录后重装 |
| 窗口进程 | `window/src/FloatWindow.cs` | ❌ | **重新编译 `.exe`** |

> 宿主插件的 ESM 模块按路径缓存，**重装同一路径不会重新加载**。
> 改 `host.js` 后请重启 DSH Desktop（注意：这会杀掉当前会话的宿主进程）。

### 编译窗口

**编译前必须先杀掉正在运行的 `dsh-float-window.exe`**（否则输出文件被锁），
并且**先编到 `work\` 再覆盖**（避免编译中途锁住正在用的文件）。

```cmd
cd window

C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /nologo /target:winexe /platform:x64 /out:work\dsh-float-window.exe ^
  /win32manifest:app.manifest ^
  /r:Microsoft.Web.WebView2.Core.dll ^
  /r:Microsoft.Web.WebView2.WinForms.dll ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll ^
  src\FloatWindow.cs
```

### 手工测窗口

可以自己拉起窗口进程看日志，但要知道两件事：

1. **这样启动的窗口没有宿主 mint 的认证 URL**，页面会显示 `forbidden`——属正常，不是 bug。
2. **验证页面的正确方式是点 DSH 里的「浮窗」按钮。**

想造「DSH 换了外观」的场景：在 `~/.dsh/profiles/` 下临时放一个带 `- id: ui-theme` 段的
假 profile 目录，并让它成为 mtime 最新的那个。**测完删掉，不要动真实偏好。**

## 代码结构

```
package.json                 # ⭐ 分发包清单：exports → plugin/host.js，files 纳入 plugin/ + window/
screenshots.json             # 市场卡片截图清单

plugin/                      # 插件包本体
├── package.json             # 内层清单：exports、dsh.bundle、dsh.client（本地开发用）
├── cordis.patch.yml         # bundle 层：往 profile 插入一条 host 记录
├── host.js                  # 宿主半边：5+2 条路由 + 拉起窗口进程
├── client.js                # 浏览器半边：浮窗按钮 + 悬浮窗设置页 + 导航图标
└── page/
    └── float.html           # 窗口页面：侧边聊天 UI + 与窗口进程的桥

window/                      # 原生窗口（WinForms + WebView2）
├── src/FloatWindow.cs       # 窗口源码（csc C# 5）
├── dsh-float-window.exe     # 预编译产物（**有意入库**）
├── lib/*.dll                # WebView2 的三个 DLL（**有意入库**）
├── app.manifest             # Per-Monitor V2 DPI 感知声明
└── （运行时文件不入库）      # webview-data/、window-log.txt、window-state.txt、
                             # clean-pending.txt、float-settings.json
```

> **与 `dsh-plugin-session-purge` 的结构差异**：那个插件的包是 `plugin/lib/{index.js,client.js}`
> 的两层结构；本插件是**扁平结构**（`host.js` / `client.js` 直接放在包根），
> 并额外带一个同级的 `window/` 目录。

### 为什么有两个 `package.json`

根清单是**分发包**，`plugin/package.json` 是**内层包**。这不是冗余，是被下面的约束逼出来的：

`host.js:33` 用相对路径定位窗口程序：

```js
const WINDOW_BIN = fileURLToPath(new URL('../window/dsh-float-window.exe', import.meta.url))
```

**`../window/` 要求 `window/` 是 `plugin/` 的兄弟目录。** 而 `dsh plugin add github:owner/repo`
只接受仓库根作为包——根部没有清单就直接失败，只打 `plugin/` 又会丢掉 `window/`。
所以根清单承担「把两者一起打包」这件事：

```jsonc
// package.json（根）
{
  "exports": { ".": "./plugin/host.js", "./client": "./plugin/client.js" },
  "files": ["plugin/", "window/", "README.md", "README.en.md", "LICENSE"],
  "dsh": { "bundle": { "patch": "./plugin/cordis.patch.yml" }, "client": { /* 同 plugin/ */ } }
}
```

> [!CAUTION]
> **改 `files` 时务必确认 `window/` 还在里面。** 漏掉它，安装后窗口起不来，
> 而症状只是点「浮窗」报 `浮窗打开失败` —— 排查方向容易被带偏到「exe 没编译」上去。
> 改动 `files` 或 `exports` 后请实测一次打包清单：
>
> ```bash
> npm pack --dry-run --json | grep -c '"path": "window/'
> ```
>
> 期望值 **6**（`.exe`、3 个 DLL、`src/FloatWindow.cs`、`app.manifest`）。

**根清单与 `plugin/package.json` 的 `dsh` 段必须保持一致**——这是第 5 组需要同步的地方
（另四组见下方「两份必须同步的常量」）。

### 为什么 `.exe` 和 DLL 要入库

因为**不装 .NET SDK 与 WebView2 SDK 的用户没法自己编译**，而窗口是这个插件存在的意义。
入库的代价是仓库变大（三个 DLL 约 835 KB + `.exe` 约 52 KB），收益是克隆下来就能跑。
如果你要改动 `FloatWindow.cs`，请**同时**把重新编译的 `.exe` 一起提交。

## 三边的接口约定

### 一、宿主半边 ↔ 浏览器半边 / 窗口页面（HTTP，同源）

前缀固定 `/dsh-float-chat`。全部响应为 `{ ok: boolean, ... }` 且带 `cache-control: no-store`。

| 方法 | 路径 | 请求体 | 响应 |
|---|---|---|---|
| `POST` | `/dsh-float-chat/open` | `{ origin, parent?, theme? }` | `{ ok, value: { started, pageUrl } }` |
| `GET` | `/dsh-float-chat/settings` | — | `{ ok, value, defaults }` |
| `POST` | `/dsh-float-chat/settings` | 部分设置字段 | `{ ok, value }`（归一化后的完整设置） |
| `GET` | `/dsh-float-chat/theme` | — | `{ ok, value: { preference?, resolved? } }` |
| `POST` | `/dsh-float-chat/theme` | `{ preference?, resolved? }` | `{ ok, value }` |
| `GET` | `/dsh-float-chat/stats` | — | `{ ok, value: { cacheBytes, logBytes, logCapBytes, parts } }` |
| `POST` | `/dsh-float-chat/clean` | `{}` | `{ ok, value: { freed, locked, rotated, beforeCacheBytes, after } }` |
| `GET` / `HEAD` | `/dsh-float-chat/page/`（prefix） | — | `text/html`（`page/float.html`） |

错误响应统一 `{ ok: false, error: string }`。

**改动这个接口时必须同步改三处**：`host.js`、`client.js`、`page/float.html`，并更新本表格。

### 二、窗口进程 ↔ 窗口页面（WebView2 消息桥）

**页面 → 窗口进程**（`window.chrome.webview.postMessage`）：

| `type` | 载荷 | 含义 |
|---|---|---|
| `theme-request` | — | 请求推回当前主题与设置（页面脚本先于导航完成执行，所以它主动讨一次） |
| `theme-observed` | `theme` | 页面从宿主官方接口拿到的主题，回推给标题栏 |
| `log` | `text` | 把页面侧状态写进 `window-log.txt` |
| `close-request` | — | 请求关窗（走正常清理关闭路径） |
| `clean-request` | — | 请求清缓存（窗口侧处理，等价于标题栏 `⌫`） |
| `stats-request` | — | 请求把缓存占用推回来 |

> ⚠️ **注意**：页面当前**不发送** `clean-request` / `stats-request`；
> 清缓存与看占用走的是宿主路由 `POST /clean` 与 `GET /stats`。
> 窗口进程侧的这两个分支目前没有调用方。改动时要留意这个不对称。

**窗口进程 → 页面**（`CoreWebView2.PostWebMessageAsJson`）：

| `type` | 载荷 | 含义 |
|---|---|---|
| `theme` | `theme` | 当前外观（`light` / `dark`），页面据此上色 |
| `settings` | `settings` | `{ ephemeralByDefault, showSessionTitle, closeOnEscape }` |
| `clean-result` | `text` | 清理结果，页面弹一条底部提示（`showToast`） |
| `cache-stats` | `cacheBytes`, `logBytes` | 缓存占用（**当前页面不消费**，见上方说明） |

**窗口进程注入到页面的全局**（`AddScriptToExecuteOnDocumentCreatedAsync`）：

- `window.__DSH_FLOAT_NATIVE__ = true` —— 页面据此隐藏自带标题栏
- 用 `ExecuteScriptAsync` 调用：`window.__DSH_FLOAT_CLEANUP__()`（关窗前）、`window.__DSH_FLOAT_NEW_THREAD__()`（`＋` 按钮）

### 三、运行时文件

全部位于 `window/`（相对 `.exe` 所在目录），**除了 `float-settings.json` 都由窗口进程独占**：

| 文件 | 写入方 | 读取方 | 入库 |
|---|---|---|---|
| `float-settings.json` | 宿主（tmp + rename） | 窗口进程（watcher + 轮询） | ❌ |
| `window-state.txt` | 窗口进程 | 窗口进程 | ❌ |
| `window-log.txt` | 窗口进程 | — | ❌ |
| `clean-pending.txt` | 窗口进程 / 宿主 | 窗口进程 | ❌ |
| `webview-data/` | WebView2 | WebView2 | ⛔ **绝对不行** |

### 四、两份必须同步的常量

| 常量 | 位置 A | 位置 B | 值 |
|---|---|---|---|
| 日志轮转上限 | `window/src/FloatWindow.cs` 的 `LogRotateBytes` | `host.js` 的 `LOG_CAP_BYTES` | `18 * 1024 * 1024` |
| 缓存目录清单 | `FloatWindow.cs` 的 `CacheFolders` | `host.js` 的 `CACHE_TAILS` | 七个相对路径 |
| 设置默认值 | `FloatWindow.cs` 的 `WindowSettings` | `host.js` 的 `SETTINGS_DEFAULTS` | 见 README |
| 设置取值范围 | `FloatWindow.cs` 的 `LoadSettings()` | `host.js` 的 `normalizeSettings()` | 见 README |

**改任何一处都必须同时改另一处**，否则「设置页显示的值」与「窗口实际用的值」会不一致。

### 五、浏览器半边的硬约束

- 手写的 DSH 客户端模块，格式是 `window.__ModuleLoader__.load({ id, factory })`，**不是**普通 ESM。
- 没有构建步骤：**不要**引入打包器、TypeScript 编译或任何需要 build 的依赖。
- `id` **必须**等于 `package.json` 的 `name`（`dsh-float-chat`），否则模块加载失败。
- 依赖只能通过 `require()` 取：目前用到 `react` 与 `@deepseek-ai/dsh-client-ui-primitives`。
- `dsh.client.inject` 里的 `@deepseek-ai/dsh-client-ui-conversation` 是**硬依赖**，
  它提供 `conversation.input.right` 插槽。

## 编码约定

### 通用

- **JavaScript**：缩进 2 空格；字符串统一单引号。
- **C#**：**C# 5**（服务于 .NET Framework 的 `csc`）。
  **禁止**字符串插值（`$"..."`）、**禁止** null 条件运算符（`?.`）、**禁止**表达式体成员。
  这不是古板，是目标编译器的限制。
- 注释写**为什么**，不写**是什么**。现有注释里有大量「为什么这样做」的记录（包括踩过的坑），
  请保持这个风格——它们是这个项目最有价值的部分。
- 一切对外文案用简体中文；标识符、API 名、路径保持英文原文。

### 界面相关（`client.js` / `float.html`）

- 普通按钮一律用官方 `@deepseek-ai/dsh-client-ui-primitives` 的 `Button`，**不要**自造按钮样式。
- 颜色一律走 `--dsw-alias-*` CSS 变量并带 fallback，**禁止**硬编码颜色值。
- **禁止**使用 `window.confirm` / `window.alert`（WebView2 会拒绝）。
- **禁止**整页 `location.reload()`——只刷新本组件自己的状态。
- 设置面板必须继续包在 error boundary 内。
- 组件的交互态（hover / pressed / selected）在**每一种状态**下都要**显式给出同一组 style 键**。
  只在一个分支里加键、另一个分支里不加，会让 React 的清理滞后、留下残影——
  这是本项目反复踩过的一个坑。
- 涉及中文输入法的改动请**务必实机验证**（合成期间不能抢 Enter，焦点不能丢）。

### 宿主半边（`host.js`）

- 一切影响窗口的代码都应当**失败安全**：读不到设置就用默认值，删不掉就记一笔，绝不抛到 `apply()`。
- `apply()` 里抛异常 = 一条路由都不会注册 = 界面报「浮窗打开失败」，且改文件不生效、只能重启。
  **`ctx.webServer` 这类注入服务的属性访问必须有对应的 `export const inject = [...]`**，
  否则属性是 `undefined`，`apply()` 当场就炸。（`ctx.get('name')` 反射读不需要 inject。）
- 新增设置项时，必须同步 README 的配置项表格、两份默认值、两份取值范围。

### 窗口进程（`FloatWindow.cs`）

- 所有可能抛异常的地方都要 `try/catch` 并 `Program.Log`——
  **一个不能开窗且不说话的窗口是最糟糕的失败**。
- 磁盘与网络操作不能让 UI 线程卡住。
- 主题相关的改动要**同时**验证：设置页固定深浅色、跟随 DSH、跟随 Windows 三种情形。

## 提交规范

采用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/v1.0.0/)：

```
<type>(<scope>): <描述>
```

| type | 用途 |
|---|---|
| `feat` | 新功能 |
| `fix` | 修 bug |
| `docs` | 只改文档 |
| `refactor` | 重构，不改行为 |
| `test` | 测试 |
| `chore` | 构建、依赖、杂务 |
| `style` | 格式化，不影响逻辑 |

`scope` 建议用：`host`（宿主半边）/ `client`（浏览器半边）/ `page`（窗口页面）/
`window`（原生窗口）/ `docs` / `manifest`。

示例：

```
feat(window): 支持自定义标题栏配色
fix(page): 修正输入法合成期间 Enter 被提前触发
docs: 补充 Windows 版本要求
```

**不要**在提交信息里写 emoji 前缀，也不要提交 `window/webview-data/` 下的任何东西。

## Pull Request 流程

1. **先开 Issue**（功能类改动必须先讨论），或者认领已有 Issue。
2. Fork → 从 `main` 切出特性分支，命名 `feat/xxx`、`fix/xxx`。
3. 改动。
4. **在真实的 DSH Desktop 上手动验证**，并把验证步骤写进 PR 描述。
   改了 `FloatWindow.cs` 的话，请**一并提交重新编译的 `.exe`**。
5. 提 PR，按 [PR 模板](./.github/PULL_REQUEST_TEMPLATE.md) 填写。

PR 必须附带：

- **改了什么、为什么**；
- **改动落在哪一边**（宿主 / 浏览器 / 页面 / 窗口进程）；
- **怎么验证的**（DSH 版本、Windows 版本、WebView2 版本、操作步骤、实际结果）；
- 涉及界面时**附截图**，且**浅色与深色两种主题各一张**；
- 改了接口时，说明**三边是否都同步了**。

## 兼容性声明

`plugin/package.json` 的 `dsh` 字段是插件与 DSH 之间的契约。**这两块是能安装、能注入的前提，不要动**：

```jsonc
{
  "dsh": {
    "bundle": { "patch": "./cordis.patch.yml" },   // 能 dsh plugin add 的唯一机器可读契约
    "client": {
      "platform": "web",
      "immediately": true,
      "inject": ["@deepseek-ai/dsh-client-ui-conversation"]   // 硬依赖：提供 input.right 插槽
    }
  }
}
```

<a id="about-dshcompatibility"></a>

### 关于 `dsh.compatibility` {#关于-dshcompatibility}

**这个字段不存在，不要加。** 早先的文档把它列为「待补的缺口」，那是错的。

【事实】实测 DSH Desktop 2.0.15（`Program Files/DSH Desktop/resources/app/lib/`）：

- 穷举 `lib/*.js` 里全部对 `dsh.*` 的读取，只有两处 ——
  `bundleManifest.dsh?.bundle`（`lib/profile-B8k_aHEa.js:701`）与 `dsh?.profile`（同文件 `:680`）。
  **插件清单里没有任何被消费的版本区间字段**，写了也不会被读。
- 搜索到的 `compatibility` 在另一个位置：`dsh-community-market` 包的
  **目录条目 schema**（该包 `docs/schemas/catalog-provider-page.schema.json`），
  形状为 `{ "apiVersion": string, "hosts": string[] }`。它描述的是**市场目录里的一条记录**，
  由目录提供方生成，插件作者既不该也不能在 `package.json` 里填写。

所以本插件的兼容性**只能**靠人工实测维护。`package.json` 里那两块 `dsh` 配置
是全部契约，改它们要格外谨慎。

> 如果你在别的 DSH 版本里确实见到了 `dsh.compatibility` 被消费的证据
> （比如解析它的代码路径），请开 Issue 附上文件与行号 —— 那说明上游加了新字段，
> 本文档需要更新。**不要凭印象或网上的示例片段就往 `package.json` 里加。**

## 安全

> [!CAUTION]
> **`window/webview-data/` 里存着 DSH 的认证 cookie。申请 PR 前请确认它没有被加进暂存区。**

- **绝对不要**提交 `window/webview-data/` 下的任何文件——其中的 `EBWebView/Default/Network`
  存放认证 cookie，`EBWebView/Default/Local Storage` 存放侧边对话 id。
- **绝对不要**提交 `window-log.txt`——它会记录带 token 的认证 URL 与渲染器能力头值。
- 粘贴日志到 Issue 前，请先删掉 `token=` 后面的值、`--dsh-header-value=` 后面的值，
  以及路径里的用户名。
- 报告安全问题时不要开公开 Issue，请走 [SECURITY.md](./SECURITY.md) 里的私下渠道。
- `.gitignore` 里那几行排除规则**不要删**。见该文件顶部注释。
