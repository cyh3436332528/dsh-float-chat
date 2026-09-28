# 安全策略

## 报告安全问题

**请不要通过公开 Issue 报告安全问题。**

请使用 GitHub 的私密渠道：

👉 **<https://github.com/cyh3436332528/dsh-float-chat/security>** → *Report a vulnerability*

（直达链接：<https://github.com/cyh3436332528/dsh-float-chat/security/advisories/new>）

在公告中请包含：

- 问题类型（凭据泄露 / 越权访问 / 本地服务暴露 / 数据删除 / 其他）；
- 复现步骤，越小越好；
- 受影响的版本；
- 你认为的影响范围；
- 如有可行的修复思路，一并附上。

**响应预期**：我会在 7 天内确认收到，并在评估后告知处理方案。
本项目由个人维护，请理解无法承诺固定的修复时限。

## 本项目的攻击面

先说清楚这个插件做了什么，因为这决定了它的风险在哪：

1. 它在 DSH 宿主进程里注册了 **7 条 HTTP 路由**（都在 `/dsh-float-chat` 前缀下），
   其中 `POST /dsh-float-chat/open` 会**拉起一个本机进程**，
   `POST /dsh-float-chat/settings` 会**写一个文件**，`POST /dsh-float-chat/clean` 会**删目录**。
2. 它维护一个**独立的桌面窗口进程**，
   该进程持有一份带 token 的认证 URL 与渲染器能力头的值。
3. 窗口内嵌一个 **WebView2 浏览器**，其 profile 目录里存有 **DSH 的认证 cookie**。

因此以下几类问题被列为高优先级：

| 严重程度 | 问题类型 |
|---|---|
| **严重** | 非本机来源可以调用 `open` 并拿到认证 URL，或可以借插件拉起窗口进程 |
| **严重** | 任何形式的凭据泄露：把 cookie、token 或渲染器能力头值写进日志 / 文件 / 网络请求 |
| **严重** | `clean` 路由的删除范围超出既定的七个缓存目录 |
| **高** | 设置路由可被用来写出插件目录之外的文件（路径穿越），或可注入未知键 |
| **高** | 窗口进程向非预期的主机发起请求；渲染器能力头被发给了第三方域名 |
| **高** | 页面可被注入任意脚本（XSS），或被导航到攻击者控制的页面 |
| **中** | 设置面板渲染失败外溢到 DSH 设置的其他分区 |
| **中** | 清理操作会误删 Cookie 或 Local Storage（等于把用户登出） |

## 设计上的安全约束

这些是本项目刻意遵守的约束，**不接受会削弱它们的改动**：

1. **`open` 只服务本机。** 只有 `http://` / `https://` 且主机名是
   `127.0.0.1` / `localhost` / `::1` 的 origin 才会拿到认证 URL，其余一律 `400`。
2. **设置只接受白名单键。** 请求体里只挑 `SETTINGS_DEFAULTS` 中已存在的键，
   其余字段直接被丢弃，无法塞进窗口的设置文件；数值越界会被 clamp。
3. **请求体上限 64 KB。** 超过即拒，不做缓冲。
4. **清理范围是写死的七个相对路径。** 它们全部位于 `window/webview-data/` 之下，
   且**明确不包含** `Default/Network`（Cookie）与 `Default/Local Storage`。
5. **页面路由只服务一个文件。** `prefix /dsh-float-chat/page` 只接受
   `/dsh-float-chat/page` 与 `/dsh-float-chat/page/` 两个路径名，其余 `404`。
6. **不联网。** 插件自身不向任何外部主机发起请求。窗口进程只访问
   `127.0.0.1` 上的 DSH Host；渲染器能力头通过 `AddWebResourceRequestedFilter` 注入，
   过滤器是 `*`，但窗口只会导航到宿主 mint 的 `127.0.0.1` URL。
7. **页面渲染消息不拼 HTML 字符串。** 所有文本节点用 `textContent` 或 DOM API 构造。
8. **窗口侧关闭了默认右键菜单、DevTools、状态栏与缩放控制。**
9. **不读取凭据。** 除窗口内嵌浏览器自身的 cookie 之外，插件不主动读写任何凭据文件。

## 仓库里绝对不该出现的东西

`.gitignore` 已经排除，且**这些规则不要删**：

| 路径 | 里面是什么 |
|---|---|
| `window/webview-data/` | **DSH 的认证 cookie（`EBWebView/Default/Network`）与侧边对话 id（`Default/Local Storage`）** |
| `window/window-log.txt` | 窗口诊断日志，**含带 token 的认证 URL 与渲染器能力头值** |
| `window/window-state.txt` | 本机的窗口几何 |
| `window/clean-pending.txt` | 欠清标记 |
| `window/float-settings.json` | 本机设置 |

如果你发现这些文件中的任何一个**已经在仓库历史里**（无论是本仓库还是别人的 fork），
请按安全问题上报——那是一条真实的凭据泄露，不只是一个误提交。

## 上报日志时的脱敏清单

粘贴 `window-log.txt` 的内容到任何公开渠道之前，请先删掉：

- `--dsh-auth-url=` 后面 URL 里 `token=` 的值；
- `--dsh-header-value=` 后面的整串值（渲染器能力头）；
- 路径里的 Windows 用户名（例如 `C:\Users\<你的用户名>\`）；
- 侧边对话 / 会话的 id（`session-...` 之类）。

## 支持范围

只有**最新发布版本**会获得安全修复。请在报告时说明你使用的插件版本、DSH 版本与 WebView2 版本。

| 版本 | 支持状态 |
|---|---|
| 0.4.0 | ✅ 支持 |
| < 0.4.0 | ❌ 不支持（未公开发布） |

## 平台说明

本插件**仅支持 Windows 10 / 11**。窗口是一个 WinForms + WebView2 原生进程，
依赖 .NET Framework 的 `csc` 与 WebView2 Runtime。macOS / Linux 上没有实现，
也因此不受本文档所述支持范围的覆盖。
