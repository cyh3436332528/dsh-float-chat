# Changelog

本文件记录 `dsh-float-chat` 的所有重要变更。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

> **关于版本号**：当前是 **`0.4.0`**，按 SemVer 属于**尚未到 1.0 的早期版本**——
> 它**不是**首个正式版本，接口与行为仍可能在不发大版本的情况下调整。
>
> **关于历史条目缺失**：本次开源发布只包含 `[0.4.0]` 一条记录。
> 版本号走到 `0.4.0` 说明作者在本地迭代过若干轮，但那些中间版本**没有留下变更记录**，
> 因此**不在此处追述**——不编造 `0.1.0`～`0.3.0` 的内容。

## [Unreleased]

### TODO

- 为宿主侧路由补自动化测试（当前**没有**任何自动化测试）。
- 为 macOS / Linux 提供窗口实现或明确的替代方案（当前**仅 Windows**）。

### 注

- 截图已收齐 5 张（触发入口 / 浮窗整屏 / 浮窗近景 / 设置页上下半），无待补。
- ~~补充 `dsh.compatibility` 字段~~ —— **作废**。该字段不存在于 DSH 的插件清单中，
  写了也不会被读；详见 [CONTRIBUTING.md](./CONTRIBUTING.md#关于-dshcompatibility)。

---

## [0.4.0] - 2026-09-28

**首次公开开源的版本**（早期版本，非 1.0）。

> 日期依据：`plugin/` 与 `window/` 目录内文件的文件系统时间戳集中在 2026-09-28。
> **TODO**：若实际发布日期不同，请以正式发布日为准修改本节标题日期。

### Added

**宿主半边（`host.js`）**

- 三条核心路由（`ctx.webServer.register`，全部 `no-store`）：

  | 方法 | 路径 | 用途 |
  |---|---|---|
  | `POST` | `/dsh-float-chat/open` | 开窗：校验 loopback origin → mint 认证 URL → 转发渲染器能力头 → `spawn` 窗口进程 |
  | `GET` | `/dsh-float-chat/settings` | 读窗口设置（附带默认值，供「恢复默认设置」使用） |
  | `POST` | `/dsh-float-chat/settings` | 局部更新窗口设置（只接受已知键，tmp + rename 原子落盘） |

- 两条通道路由：

  | 方法 | 路径 | 用途 |
  |---|---|---|
  | `GET` / `POST` | `/dsh-float-chat/theme` | 外观通道：浏览器半边上报 DSH 的 ui-theme 状态（`preference` + `resolved`），窗口页面来问 |
  | `GET` | `/dsh-float-chat/stats` | 缓存与日志的实时大小 |
  | `POST` | `/dsh-float-chat/clean` | 清理浏览器缓存 + 按 18 MB 上限轮转日志 |

- **页面路由** `prefix /dsh-float-chat/page`：把 `page/float.html` 以 `no-store` 提供给窗口，
  使窗口与 DSH Host **同源**，从而复用其已认证的会话访问侧边对话 JSON 接口。
- **设置归一化**：`normalizeSettings()` 丢弃未知键、把越界数字 clamp 到合法区间，
  手改配置文件也不会让窗口崩。
- **开窗时的外观判定**：调用方给的主题只是提示；实际以主题通道（渲染器上报 → 宿主 settings）为准，
  最后才用设置页里被固定的方案种子首帧。
- **窗口进程生命周期**：同时只保留一个窗口进程；`ctx.effect` 在卸载时撤销所有路由并 kill 子进程；
  `spawn` 前显式 `delete env.ELECTRON_RUN_AS_NODE`。

**浏览器半边（`client.js`）**

- **输入框右侧的「浮窗」按钮**（`conversation.input.right`，`order: 40`）：
  悬浮 / 按下 / 出错三态；点击后 `POST /dsh-float-chat/open`，失败时按钮变红并在旁边显示原因。
- **「悬浮窗」设置页**（`settings.section`，`order: 95`），四张卡片：
  - **外观**：四态「配色方案」方块（浅色 / 深色 / 跟随 DSH / 跟随 Windows），各带一枚 `currentColor` 内联 SVG 标记。
  - **窗口**：始终置顶、记住窗口位置与大小、默认宽度、默认高度、不透明度滑块。
  - **行为**：关闭窗口时结束对话、标题栏显示会话名称、按 Esc 关闭窗口。
  - **缓存与日志**：实时显示缓存 / 日志大小 + 一个 **清理** 按钮。
  - 底部：**恢复默认设置** / **刷新**。
- **外观上报**：读 `theme` 服务的 `getTheme()`（`preference` + `active.id`），
  在 `theme/change` 时重新上报，并在服务缺失时回落到读取渲染后的 `body` 背景亮度。
- **设置页导航图标**：`settings.section` 只投影 `id` / `order` / `label`，没有 icon 字段；
  本插件在弹层挂载后按可见文字认领自己那一行，用 `MutationObserver` + `mask-image` 换成自己的窗口标记。
- **不透明度滑块**：`onInput` 节流约 110 ms（领跑 + 尾随）直写到宿主，松手再提交最终值；
  拖动期间本地 draft 优先，避免服务端回值把滑块拽回去。
- 整个设置面板包在 React error boundary 内，渲染失败不会外溢到设置面板的其他分区。

**窗口页面（`page/float.html`）**

- 侧边对话 UI：消息气泡、``` 围栏代码块与 `` ` `` 行内代码、可折叠的 `思考` disclosure、
  `正在思考…` 忙碌行、工具调用与结果的 `· name` / `✓` / `✕` 单行 chrome。
- **交胶囊式输入框**：随内容长高（上限 132 px 后内部滚动）、圆形强调色发送键（自绘 SVG 箭头）、
  空输入时收起强调色、`Enter` 发送 / `Shift+Enter` 换行。
- **中文输入法处理**：合成期间（`isComposing` / `keyCode === 229`）不抢 Enter；
  点日志区或直接敲字都会把焦点交回输入框，避免候选框跑到屏幕左上角。
- **对话历史**：标题栏下拉菜单，切换 / 重命名（行内编辑，因为 WebView2 拒绝 `window.prompt`）/ 删除；
  悬停或键盘聚焦时弹出完整标题与时间的提示卡。
- **即用即焚**：右上角开关；关闭窗口时通过 `window.__DSH_FLOAT_CLEANUP__` 删除本次对话记录。
- **与窗口进程的桥**：页面发 `theme-request` / `theme-observed` / `log` / `close-request`；
  接收 `theme` / `settings` / `clean-result`。
- **继承上下文**：开窗时注入边界提示（`BOUNDARY_PROMPT`），
  并在渲染时把边界文本与 `Mode:` 段落过滤掉，只保留用户自己的话。
- 消息进场淡入上移、`prefers-reduced-motion` 下全部动效自动关闭。
- 页面被原生窗口托管时（`window.__DSH_FLOAT_NATIVE__ === true`）隐藏自带标题栏，
  把窗口 chrome 让给原生标题栏。

**原生窗口（`window/src/FloatWindow.cs`）**

- WinForms + WebView2 的无边框窗口：自绘标题栏 + 五颗 GDI+ 图标按钮、圆角区域、
  两个原生缩放手柄、拖角缩放、0.35 → 目标透明度的淡入。
- **始终置顶**：独立进程，因此能在 DSH 主窗口最小化 / 隐藏时继续压在其他应用之上。
- **Per-Monitor V2 DPI 感知**（`app.manifest` + 运行时 API 双保险），
  否则 150% 缩放下窗口会被位图拉伸而发虚。
- **主题三层来源**：设置文件里的 `appearance` → 直接读 profile 的
  `~/.dsh/profiles/<profile>/cordis.patch.yml` 里 `- id: ui-theme` 段（1 秒一次），
  仅当偏好是 `system` 时才看 Windows 的 `AppsUseLightTheme`；URL 里的 `theme=` 只作兜底。
- **设置热更新**：`FileSystemWatcher` 盯 `float-settings.json`（Changed / Created / Renamed 都接，
  事件丢回 UI 线程）+ 1 秒轮询兜底。
- **认证桥接**：先导航到宿主 mint 的认证 URL 拿浏览器信任 cookie，再导航到页面 URL；
  并为窗口的**每一个**请求注入渲染器能力头（`AddWebResourceRequestedFilter("*")`）。
- **渲染器能力头 / 标题**：页面 `document.title` 的改动（侧边对话标题、主会话标题）反映到原生标题栏。
- **缓存清理**：标题栏 `⌫` 按钮，清理七个缓存目录 + 按 18 MB 轮转日志；
  被浏览器占用删不掉时写 `clean-pending.txt`，**关窗后**与**下次启动时**各补清一次。
- **页面侧状态回传**：页面可以通过桥把状态写进 `window-log.txt`，卡住的窗口可以「读」而不只是「描述」。
- 关窗路径：先让页面执行清理（`__DSH_FLOAT_CLEANUP__`）再真正关闭，避免即用即焚的记录残留。

### Changed

- **宿主入口由 `index.js` 改名为 `host.js`（`package.json` 的 `exports["."]`）。**
  原因：DSH 的宿主插件 ESM **按路径缓存**，改文件内容不会重新加载。
  改名（或换目录）是让缓存失效、在不重启 DSH 的前提下激活宿主改动的**唯一**手段。
  这是本项目一个刻意保留的约定，**请勿改回 `index.js`**。
- 设置里的 `appearance` 取值 `auto` 更名为 `dsh`（语义不变，仍是「跟随 DSH」）。
  两半都会把旧文件里的 `auto` 读作 `dsh`，**老配置文件继续可用**。
- 页面**不再**在原生窗口存在时轮询宿主的外观接口。
  早期实现里页面每 2 秒查一次（只反映 DSH 的外观），
  结果用户把浮窗固定成浅色后，2 秒又被拉回深色。**现在原生窗口才是权威。**
- 不透明度滑块的写入策略：从「每步都写盘」（日志刷屏）→「只有松手才提交」（拖动时窗口不动、手感差）
  → **现在的节流直写（约 110 ms，领跑 + 尾随），松手再补一次最终值**。
- 「即用即焚」的默认值只作用于**首次推送**。
  早期实现里，页面开窗会写一次 `localStorage`，而「是否被手动设置过」就是「localStorage 里有没有这个键」，
  于是从第二次开窗起默认值永久失效。现在改为记账「上一次推送的值」：
  首帧只当默认值，**之后值真的变了才应用**。
- 设置页的选中态在**两种状态**下都显式给 `borderColor`，并关掉 `outline` / `box-shadow`。
  早期只在选中时加 `borderColor`，取消选中时该键从 style 对象消失会让 React 清理滞后、留下残影。
- 圆角窗口在**缩放期间**先撤掉 `SetWindowRgn`（`SetWindowRgn(hwnd, NULL, true)`）再装回。
  早期只是「停止更新区域」，导致放大出来的部分被旧区域整块剪掉，只看得见手柄括号。
- WebView2 的浏览器参数不再做任何覆盖：
  微信输入法是 TSF 文本服务，强行让它走旧的 IMM32 路径会让输入法不可用。

### Security

- **`open` 路由只接受 loopback origin**（`127.0.0.1` / `localhost` / `::1`，且限 http/https），
  其余一律 `400`。认证 URL 不会交给非本机来源。
- **设置接口只接受白名单键**：请求体里只挑 `SETTINGS_DEFAULTS` 中存在的键，
  无法把额外字段塞进窗口的设置文件（`PATCH_KEYS`）。
- **请求体上限 64 KB**（`MAX_BODY_BYTES`），超出即拒。
- **页面路由 `no-store`**，且只服务 `page/float.html` 一个文件，不接受任意路径。
- 窗口侧关闭了 WebView2 的默认右键菜单、状态栏、DevTools 与缩放控制。
- 页面渲染消息**只用 DOM API 构造节点**，不拼 HTML 字符串。
- 设置面板包在 React error boundary 内；**不使用** `window.confirm` / `window.alert`；
  **不做**整页 `location.reload()`。
- 普通按钮使用官方 `@deepseek-ai/dsh-client-ui-primitives` 的 `Button`，
  配色一律走 `--dsw-alias-*` CSS 变量。

### Known Issues

- **仅 Windows。** 窗口是 WinForms + WebView2 程序，macOS / Linux 上没有实现。
- **DSH Web 版不能完整工作**：开窗依赖 Desktop 外壳的逐代渲染器能力头。
- **宿主半边不热重载**，改 `host.js` 必须重启 DSH Desktop。
- **同一个插件只会有一个浮窗进程**，重复点「浮窗」不会开第二个窗口。
- **清理清理不到正被浏览器占用的缓存目录**，只能延后到关窗后 / 下次启动时补清。
- **`window-log.txt` 会记录带 token 的认证 URL 与渲染器能力头值**——
  上报问题前请先删掉，详见 README 的「风险提示」。
- **没有机器可读的 DSH 版本约束**——DSH 插件清单只认 `dsh.bundle` 与 `dsh.profile`，
  不存在可声明版本区间的字段，兼容性只能靠人工实测。
- **无自动化测试。** 全部结论来自手工实测。
- `dsh.client.inject` 依赖 `@deepseek-ai/dsh-client-ui-conversation`，
  该包版本变化可能影响「浮窗」按钮的挂载。

---

## 版本对照

| 插件版本 | 日期 | 说明 |
|---|---|---|
| 0.4.0 | 2026-09-28 | 首次公开开源的版本（早期版本，非 1.0） |

> `0.1.0`～`0.3.0` 没有任何变更记录留存，**不在此追述**。
