# 插件简介与关键词标签

用于插件市场 / 目录索引 / 仓库 About 栏的文案素材。**每一段都可直接复制**，按字数上限选用。

> 事实依据：`plugin/package.json`（name `dsh-float-chat`、version `0.4.0`、MIT、无 dependencies）、
> `plugin/host.js`（7 条路由）、`plugin/client.js`（浮窗按钮 + 悬浮窗设置页）、
> `plugin/page/float.html`、`window/src/FloatWindow.cs`（WinForms + WebView2 窗口）。
> 设置项清单见 README 的「配置项」表格。

---

## 一、仓库 About 栏（GitHub description，限 350 字符）

**中文（推荐，约 95 字）**

```
把 DSH 的侧边对话弹出来，变成一个能压在所有应用之上的独立置顶浮窗：自带四态外观（浅色/深色/跟随 DSH/跟随 Windows）、不透明度、窗口几何记忆、即用即焚与缓存清理。Windows 专属（WinForms + WebView2）。
```

**English（备选，推荐用于国际曝光）**

```
Pops the DSH side chat into a free-floating, always-on-top Windows desktop window usable over any other application, with its own settings page (appearance, always-on-top, geometry, opacity, ephemeral threads) and a cache cleanup card.
```

---

## 二、一句话简介（限 60 字 / 120 字符）

**中文**

```
把 DSH 侧边对话变成独立置顶浮窗，能压在所有应用之上，自带设置页。
```

**English**

```
Turns the DSH side chat into a standalone always-on-top window that floats over any other app.
```

---

## 三、标准简介（限 120 字 / 240 字符）

**中文**

```
DSH 的侧边对话本来只能待在主窗口里，一切走就被压在后面。本插件把它独立成一个无边框、可置顶、能覆盖任何应用的浮窗：继承当前会话上下文，有自己的标题栏按钮与完整设置（四态外观、不透明度、几何记忆、即用即焚、缓存与日志清理），还能记住窗口位置与大小。仅支持 Windows。
```

**English**

```
DSH's side chat normally lives inside the DSH window and disappears behind whatever you switch to. This plugin turns it into a frameless, always-on-top floating window that stays over any other application: it inherits the parent session's context, carries its own title-bar controls and a full settings page (four appearance modes, opacity, remembered geometry, ephemeral threads, cache and log cleanup). Windows only.
```

---

## 四、长简介（用于插件市场详情页 / awesome list 条目说明）

**中文**

```
dsh-float-chat —— 把 DSH 的侧边对话做成一个独立的桌面悬浮窗。

DSH 的侧边对话（side chat）只能待在 DSH 主窗口里：你切到浏览器、编辑器或终端，它就被压在后面；DSH 主窗口一最小化，它更是完全看不见。本插件解决的就是这件事——在输入框右侧点一下「浮窗」，侧边对话就会出现在一个无边框、可置顶、能覆盖在任何其他应用之上的独立窗口里，并且已经继承了当前会话的上下文。

它不是画在 DSH 页面里的浮层，而是一个独立的原生进程。这带来三件事：第一，它能真正压在其他应用之上，包括 DSH 主窗口最小化甚至隐藏的时候；第二，它需要 Windows——窗口是 WinForms + WebView2 程序，没有别的平台实现；第三，宿主半边必须把 Desktop 外壳的逐代渲染器能力头转发给窗口，所以它在 DSH Web 版上无法完整工作。

窗口有自己的标题栏和五颗按钮（新对话 / 置顶 / 清理缓存 / 最小化 / 关闭），图标全部用 GDI+ 自绘。窗口内的对话与 DSH 内置侧边栏是同一个引擎、同一批 side thread——同一份数据，只是多了一扇窗。支持切换与重命名历史对话（重命名是行内编辑，因为 WebView2 拒绝 window.prompt）、折叠思考过程、渲染代码块，以及一个「即用即焚」开关：打开后关闭窗口即删除本次对话记录。

设置页（设置 → 悬浮窗）分四张卡片：外观（浅色 / 深色 / 跟随 DSH / 跟随 Windows 四态，改完立即生效，原生标题栏与页面同色）、窗口（始终置顶、记住几何、默认宽高、不透明度滑块）、行为（关闭窗口时结束对话、标题栏显示会话名称、按 Esc 关闭窗口），以及缓存与日志（实时显示占用 + 一键清理）。设置原子写入 float-settings.json，窗口进程用 FileSystemWatcher 监听，改完约 50 ms 内跟上。

缓存清理会删掉七个纯缓存目录，Cookie 与 Local Storage 不在其中，所以清理不会把你登出、也不会丢对话关联；清不到的部分会在关窗后与下次启动时各补清一次。日志超过 18 MB 自动轮转，三份合计约 54 MB 封顶。

宿主半边 + 浏览器半边 + 原生窗口进程，三块拼成同一件事；无第三方运行时依赖；.exe 与 WebView2 DLL 已入库，克隆下来即可安装，不需要 .NET SDK 或 WebView2 SDK。
```

---

## 五、一句话卖点（用于 awesome list 的一行条目）

> 收录格式参考 awesome-dsh-plugin：「`- [名称](链接) — 一句话描述`」

```
- [dsh-float-chat](https://github.com/cyh3436332528/dsh-float-chat) — 把 DSH 侧边对话做成独立置顶浮窗，可压在任何应用之上，自带四态外观、几何记忆与即用即焚（仅 Windows）。
```

---

## 六、关键词标签

### package.json `keywords`（已写入，可直接校对）

```json
"keywords": [
  "deepseek",
  "deepseek-harness",
  "dsh",
  "dsh-plugin",
  "float-window",
  "always-on-top",
  "chat"
]
```

### GitHub Topics

**必需项（逐条添加，缺一不可）**

```
dsh-plugin
deepseek-harness
dsh
deepseek
cordis
```

> `dsh-plugin` 与 `deepseek-harness` 是社区发现机制的必需项——官方 `deepseek-ai/deepseek-harness`
> 仓库即使用 `dsh-plugin` topic 做插件检索，`dsh-find-plugin`、`dshmarket` 也都依赖它。

**建议补充项**

```
windows
winforms
webview2
always-on-top
desktop-window
```

> ⚠️ `awesome-dsh-plugin` 的收录检查会核对「仓库是否已打 `dsh-plugin` topic」。
> 建议同时开启 Settings → Features → **Issues**（Issue 模板才会生效）。

---

## 七、徽章（README 顶部，已用于本仓库 README）

```markdown
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-0.4.0-blue.svg)](./CHANGELOG.md)
[![Topic](https://img.shields.io/badge/topic-dsh--plugin-1f6feb.svg)](https://github.com/topics/dsh-plugin)
[![Platform](https://img.shields.io/badge/platform-Windows-0078d4.svg)](#兼容性)
[![Awesome DSH Plugin](https://awesome-dsh-plugin.com/badge.svg)](https://awesome-dsh-plugin.com)
```

> awesome-dsh-plugin 官方提供的收录徽章地址为
> `[![Awesome DSH Plugin](https://awesome-dsh-plugin.com/badge.svg)](https://awesome-dsh-plugin.com)`，
> 收录成功后即可挂上。
>
> **版本徽章是静态的**（`version-0.4.0`），升级版本时记得同步改这一行。
> 也可以换成 shields.io 的动态徽章：
> `https://img.shields.io/github/v/tag/cyh3436332528/dsh-float-chat?label=version`

---

## 八、供 README 引用的「一句话差异说明」

如果要在一段里说清它和「网页弹窗」的区别：

```
它不是把页面缩小塞进一个弹窗——那是网页能做到的事。它是一个独立的原生进程：
DSH 的渲染器是沙箱化的，开不了原生窗口；Electron 也没有实现 Document Picture-in-Picture。
要做「压在其他应用之上的置顶窗口」，就只能自己写一个程序。
```
