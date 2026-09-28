## 这个 PR 做了什么

关联 Issue：

## 为什么

## 改动类型

- [ ] Bug 修复
- [ ] 新功能
- [ ] 重构（不改行为）
- [ ] 文档
- [ ] 测试
- [ ] 构建 / 依赖 / 杂务

## 改动落在哪一边

本插件横跨三种运行环境，请明确勾选。

- [ ] 宿主半边 `plugin/host.js`
- [ ] 浏览器半边 `plugin/client.js`
- [ ] 窗口页面 `plugin/page/float.html`
- [ ] 原生窗口 `window/src/FloatWindow.cs`（**若勾选，请一并提交重新编译的 `dsh-float-window.exe`**）
- [ ] `plugin/package.json` 或 `plugin/cordis.patch.yml`
- [ ] 文档 / 模板 / 打包

## ⚠️ 契约自检

**请如实回答，不要跳过。**

- [ ] 本 PR **没有**改任何 HTTP 路由的路径、方法、请求体或响应结构
- [ ] 本 PR 改了 HTTP 接口，**并且**已同步修改 `host.js` / `client.js` / `page/float.html` 三处
      以及 `CONTRIBUTING.md` 的接口约定表
- [ ] 本 PR **没有**触碰下面这四组必须成对同步的常量
- [ ] 本 PR 触碰了成对常量，且**两边都改了**：

  | 常量 | 位置 A | 位置 B | 已同步 |
  |---|---|---|---|
  | 日志轮转上限 18 MB | `FloatWindow.cs` `LogRotateBytes` | `host.js` `LOG_CAP_BYTES` | [ ] |
  | 缓存目录清单（7 个） | `FloatWindow.cs` `CacheFolders` | `host.js` `CACHE_TAILS` | [ ] |
  | 设置默认值 | `FloatWindow.cs` `WindowSettings` | `host.js` `SETTINGS_DEFAULTS` | [ ] |
  | 设置取值范围 | `FloatWindow.cs` `LoadSettings()` | `host.js` `normalizeSettings()` | [ ] |

- [ ] 本 PR **没有**把宿主入口改回 `index.js`
- [ ] 本 PR **没有**移动 `plugin/` 下的 `host.js` / `client.js` / `page/`
- [ ] 本 PR **没有**引入任何构建步骤

## 🔒 安全自检

- [ ] 本 PR **没有**扩大 `POST /dsh-float-chat/clean` 的删除范围
- [ ] 本 PR 扩大了清理范围（若勾选，请说明新增了哪些目录、为什么必须清，并确认**不含 Cookie 与 Local Storage**）
- [ ] 本 PR **没有**放宽 `open` 的 loopback origin 校验
- [ ] 本 PR **没有**放宽设置接口的键白名单
- [ ] 本 PR **没有**新增任何指向外部主机的网络请求
- [ ] 本 PR **没有**提交 `window/webview-data/`、`window-log.txt`、`float-settings.json` 等运行时文件
- [ ] 我确认暂存区里没有 cookie、token、渲染器能力头值或本机用户名

**若扩大了清理范围，请逐项列出：**

| 新增清理目标 | 相对路径（相对 `window/webview-data/`） | 为什么必须清 | 不含 Cookie / Local Storage |
| ------ | ---- | ---- | ---- |
|        |      |      |      |

## 验证方式

**环境**

| 项目 | 值 |
| ----------- | - |
| 插件版本 | |
| DSH 版本 / 形态 | |
| Windows 版本 / 显示缩放 | |
| WebView2 Runtime 版本 | |

**步骤与结果**

```
1.
2.
3.
```

- [ ] 已在**真实的 DSH Desktop** 上手动验证（而不是只跑了静态检查）
- [ ] 改动涉及窗口进程时，已重新编译 `.exe` 并确认它能正常开窗

## 界面改动

若本 PR 不涉及界面，可整节留空。

- [x] 已**在真实的 DSH 上**目视确认
- [x] 已确认**浅色**主题下显示正常
- [x] 已确认**深色**主题下显示正常
- [x] 已确认「跟随 DSH」与「跟随 Windows」两种模式下正常
- [x] 交互态（hover / pressed / selected）在**每一种状态**下都显式给出了同一组 style 键
- [ ] 未使用 `window.confirm` / `window.alert`
- [ ] 未使用整页 `location.reload()`
- [ ] 普通按钮仍使用官方 `@deepseek-ai/dsh-client-ui-primitives`
- [ ] 设置面板仍包在 error boundary 内
- [ ] 涉及中文输入法的改动，已实机验证过合成期间不抢 Enter、焦点不丢

**截图 / 屏录**（浅色 + 深色各一张）

## 检查清单

- [ ] 我读过 [CONTRIBUTING.md](../CONTRIBUTING.md)
- [ ] 我已自测过改动，而不只是「应该能跑」
- [ ] 提交信息遵循 Conventional Commits
- [ ] 没有提交任何密钥、token 或 `window/` 下的运行时数据
- [ ] 没有提交 `node_modules`
- [ ] 如果这是破坏性变更，我在下方写明了迁移方式

## 破坏性变更

## 备注

> 提醒：改 `plugin/host.js` 后宿主半边**不会热重载**，需要重启 DSH Desktop；
> 如果你是通过改名/换目录来激活改动的，请在下面说明。
