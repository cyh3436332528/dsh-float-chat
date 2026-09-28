# 推送操作手册（第 4～6 步）

本地仓库已就绪，**不需要再做任何 `git init` / `git add` / `git commit`**：

| 项 | 值 |
|---|---|
| 目录 | `C:\WorkBuddyWorkspace\projects\dsh-float-chat-release` |
| 分支 | `main` |
| 提交 | `4740caf`（`chore: 初始化 dsh-float-chat v0.4.0`） |
| 标签 | `v0.4.0`（**附注标签**，已建好，未推） |
| 文件 | 26 个 |
| 远端 | **尚未配置** |

> 下面命令里的 `<TOKEN>` 请替换成你的 GitHub PAT（只需 `repo` 权限）。
> **建议不要把带 token 的命令粘进任何会被保存的地方**，用完记得去 revoke。
> 如果只想手动推而不想用 CLI 建仓库，见第 4.0 节的「纯网页方案」。

---

## 第 4 步：建仓库 + 推送

### 4.0 先确认要建的是**空仓库**

⚠️ **绝对不要传 `license_template` 参数。**

上一次（session-purge）就是传了 `license_template: "mit"`，GitHub 自动生成了一个
只含 `LICENSE` 的 Initial commit，导致首次 `git push` 被拒，最后不得不 `git push --force` 覆盖。
本仓库的 `LICENSE` 已经在本地提交里，**不需要 GitHub 再生成一份**。

### 4.1 用 API 建仓库（推荐，与上次路径一致且实测 `api.github.com` 可用）

```bash
curl -sS -X POST https://api.github.com/user/repos \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Accept: application/vnd.github+json" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  -d '{
    "name": "dsh-float-chat",
    "description": "把 DSH 的侧边对话弹出来，变成一个能压在所有应用之上的独立置顶浮窗：自带四态外观（浅色/深色/跟随 DSH/跟随 Windows）、不透明度、窗口几何记忆、即用即焚与缓存清理。Windows 专属（WinForms + WebView2）。",
    "homepage": "https://github.com/cyh3436332528/dsh-float-chat#readme",
    "private": false,
    "has_issues": true,
    "has_wiki": false,
    "has_projects": false,
    "auto_init": false,
    "topics": ["dsh-plugin","deepseek-harness","dsh","deepseek","cordis","windows","winforms","webview2","always-on-top","desktop-window"]
  }'
```

**逐项说明**：

- `private: false` —— 公开仓库。
- `auto_init: false` —— **关键**，不要生成任何初始文件。
- **没有 `license_template`** —— 见 4.0。
- `has_issues: true` —— Issue 模板才会生效。
- `topics` —— 一次把 10 个 Topics（5 个必需 + 5 个建议）都设上，省掉第 5.3 步。
  若返回的 `topics` 为空（部分 token 权限下会忽略该字段），再跑 5.3 节的补设命令。

**成功时**会返回一大段 JSON，其中的 `"full_name": "cyh3436332528/dsh-float-chat"` 即确认。

### 4.2 配置远端并推送

```bash
cd C:/WorkBuddyWorkspace/projects/dsh-float-chat-release

git remote remove origin 2>/dev/null   # 幂等：之前没配过也不报错
git remote add origin https://github.com/cyh3436332528/dsh-float-chat.git

# 用 token 推送（token 只出现在这一条命令里）
git push -u "https://cyh3436332528:<TOKEN>@github.com/cyh3436332528/dsh-float-chat.git" main
```

> **为什么推 URL 里带 token 而不是 remote 里带**：这样 token 不会落进 `.git/config`。
> 推完后 `origin` 仍是干净的 HTTPS 地址，后续 `git fetch` / `git push` 会走凭据管理器。
>
> 若你更习惯 `gh` CLI，也可以：`gh auth login` 之后 `git push -u origin main`。

### 4.3 推送标签

```bash
git push origin v0.4.0
```

只推 `main` **不会**带上标签，这一步不能省。

### 4.4 验证

```bash
git ls-remote --heads --tags origin
# 期望看到 refs/heads/main 与 refs/tags/v0.4.0（以及 v0.4.0^{} —— 附注标签会多这一行）

curl -sS https://api.github.com/repos/cyh3436332528/dsh-float-chat \
  -H "Authorization: Bearer <TOKEN>" \
  | grep -E '"(full_name|private|has_issues|default_branch|size)"'
```

再在浏览器里打开 <https://github.com/cyh3436332528/dsh-float-chat>，
逐项确认：

- [ ] `plugin/` 下能看到 `host.js` / `client.js` / `page/float.html` / `cordis.patch.yml` / `package.json`
- [ ] `window/` 下能看到 `src/FloatWindow.cs`、`dsh-float-window.exe`、`lib/` 三个 DLL、`app.manifest`
- [ ] **`window/webview-data/` 不存在**（这一条最重要）
- [ ] `window/window-log.txt`、`window-state.txt`、`clean-pending.txt`、`float-settings.json` **都不存在**
- [ ] `.exe` 和 DLL 显示为二进制文件，点开没有「损坏/乱码」
- [ ] LICENSE 被识别为 MIT（仓库页右侧会显示 "MIT license"）

### 4.5 若 `git push` 报 `CONNECT tunnel failed, response 502`

这是**本机已知的间歇性故障**（github.com 的 HTTPS 通道，上一次也遇到过），
**不是仓库配置问题**。处理顺序：

1. **等几分钟重试一次。** 多半就能过。
2. 仍是 502 → **改走 Contents API 单文件提交**（`api.github.com` 全程稳定，
   刚才实测 `HTTP 200 / 0.48s`）。做法：对 26 个文件逐个
   `GET /repos/{owner}/{repo}/contents/{path}` 拿 `sha`（新文件没有 `sha`），
   再 `PUT` 同一个路径，body 为 `{ message, content: <base64>, branch: "main" }`。
   注意这是个逐文件的循环，**先在浏览器里手动建一个空仓库**（或让 API 建），
   然后从 `README.md`、`LICENSE` 开始推，**`.exe` 与 DLL 放最后**（最大的三个文件）。
3. 实在不行 → 用 GitHub 网页的「Add file → Upload files」把目录拖上去，
   再把 `release/*` 与 `docs/*` 这类小文件用网页编辑器补上。
   **但这种方法对 `.exe` / `.dll` 不友好**，能走 git 就走 git。

> ⚠️ 无论用哪种兜底，**提交前都要再核对一次 `webview-data/` 不在里面**。
> 用 Contents API 或网页上传时会绕过本地 `.gitignore`。

### 4.0（备选）纯网页方案

不想碰 API 与 token 的话：

1. 打开 <https://github.com/new>
2. Repository name 填 `dsh-float-chat`
3. **不要**勾 "Add a README file"、**不要**选 "Add .gitignore"、**不要**选 License
   （三个都不要，否则会生成 Initial commit，`push` 会被拒）
4. 建完在执行：

```bash
cd C:/WorkBuddyWorkspace/projects/dsh-float-chat-release
git remote add origin https://github.com/cyh3436332528/dsh-float-chat.git
git push -u origin main
git push origin v0.4.0
```

`git push` 会提示输用户名与密码 —— 密码处填 **PAT**，不是账号密码。

---

## 第 5 步：发布 Release + 设置 Topics

### 5.1 创建 Release

**方式 A：`gh` CLI**

```bash
cd C:/WorkBuddyWorkspace/projects/dsh-float-chat-release

gh release create v0.4.0 \
  --repo cyh3436332528/dsh-float-chat \
  --title "v0.4.0 — 把 DSH 侧边对话弹成独立置顶浮窗" \
  --notes-file release/v0.4.0-release-notes.md \
  --latest
```

> ⚠️ **不要这样用**：`--notes-file` 指向的是本运维手册（含六节发布流程），
> 而 Release 正文应该只是第 1 节那段。
> 所以更推荐**方式 B**：把下面「Release 正文」整段复制进网页表单。

**方式 B：网页**

1. 打开 <https://github.com/cyh3436332528/dsh-float-chat/releases/new>
2. **Choose a tag** 选 `v0.4.0`（已推上去的附注标签）
3. **Release title** 填：

```
v0.4.0 — 把 DSH 侧边对话弹成独立置顶浮窗
```

4. **Describe this release** 粘贴：

```markdown
把 DSH 的侧边对话弹出来，变成一个能压在所有应用之上的独立置顶浮窗。

> ⚠️ **这是 `0.4.0`，按 SemVer 属于尚未到 1.0 的早期版本。**
> 它是这个插件的**首次公开开源**，但**不是**首个正式版本——接口与行为仍可能在不发大版本的情况下调整。

## 功能

- **独立置顶浮窗**：不是画在 DSH 页面里的浮层，而是一个**独立的原生进程**（WinForms + WebView2），
  因此能真正压在其他应用之上，也能在 DSH 主窗口最小化甚至隐藏时继续存在。
- **无边框 + 圆角 + 拖角缩放**，标题栏整条可拖动。
- **五颗标题栏按钮**：新对话 / 置顶（图标反映状态）/ 清理缓存（点击先闪红）/ 最小化 / 关闭。
  图标全部 GDI+ 自绘，不用字体符号。
- **继承当前会话上下文**：窗口里的对话与 DSH 内置侧边栏是**同一个引擎、同一批 side thread**，
  只是多了一扇窗。
- **多对话管理**：切换、重命名（行内编辑）、删除单条记录，窗口索引记 20 条。
- **即用即焚**：打开后关闭窗口即删除本次对话记录（含落盘文件）。
- **折叠思考过程、渲染代码块、回到底部按钮、中文输入法焦点处理**。
- **四态外观**：浅色 / 深色 / 跟随 DSH / 跟随 Windows。改完**立即生效**，原生标题栏与页面同色。
- **完整设置页**（设置 → 悬浮窗）：外观 / 窗口 / 行为 / 缓存与日志四张卡片。
- **缓存与日志清理**：标题栏按钮或设置页里的「清理」，两个入口做同一件事。
  清不到的部分在**关窗后**与**下次启动时**各补清一次。日志超 18 MB 自动轮转，三份约 54 MB 封顶。

## 安装

```bash
git clone https://github.com/cyh3436332528/dsh-float-chat.git
cd dsh-float-chat
dsh plugin --profile desktop add ./plugin
# 然后重启 DSH Desktop
```

要点：

- **仅支持 Windows 10 / 11**，且需要 **DSH Desktop**（Web 版不能完整工作）。
- `.exe` 与 WebView2 DLL 已入库，**不需要** .NET SDK 或 WebView2 SDK。
- 装完**必须重启 DSH Desktop** 一次（宿主半边不热重载）。

## ⚠️ 三条要知道的事

1. **仅 Windows。** 窗口是 WinForms + WebView2 程序，macOS / Linux 上没有实现。
2. **`window/webview-data/` 里存着你的 DSH 认证 cookie。** 它已被 `.gitignore` 排除——
   提交任何东西之前请确认它不在暂存区；也**不要整个删掉它**（会丢登录态与对话关联）。
3. **`window/window-log.txt` 会记录带 token 的认证 URL 与渲染器能力头值。**
   上报问题前请先删掉这些值。详见 README 的「风险提示」。

## 已知限制

- 仅 Windows；DSH Web 版不能完整工作。
- 宿主半边**不热重载**，改 `host.js` 必须重启 DSH Desktop。
- 同一个插件**只有一个浮窗进程**，重复点「浮窗」不会开第二个窗口。
- 没有机器可读的 DSH 版本约束（DSH 插件清单里不存在这样的字段，非本项目疏漏）。
- **无自动化测试。**
- `dsh.client.inject` 依赖 `@deepseek-ai/dsh-client-ui-conversation`，
  该包版本变化可能影响「浮窗」按钮的挂载。

## 完整变更

见 [CHANGELOG.md](https://github.com/cyh3436332528/dsh-float-chat/blob/main/CHANGELOG.md)。
```

5. **不要**勾 "Set as a pre-release"（`0.4.0` 不是 pre-release，它是正常的早期版本号）。
6. 点 **Publish release**。

### 5.2 顺带确认仓库设置

在 <https://github.com/cyh3436332528/dsh-float-chat/settings> 确认：

- [ ] **Issues** 已勾选（`has_issues`）
- [ ] 若想让别人提 PR，**Pull requests** 也保持勾选
- [ ] **Features → Discussions** 可选开（个人项目不开也行）
- [ ] 若想收紧：**Settings → Security → Advisories** 默认开启，
      这正是 `SECURITY.md` 里那个私密上报入口

### 5.3 设置 Topics

在仓库首页右上角 **⚙️ About → Topics** 里逐条添加，或直接跑命令：

```bash
curl -sS -X PUT https://api.github.com/repos/cyh3436332528/dsh-float-chat/topics \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Accept: application/vnd.github+json" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  -d '{"names":["dsh-plugin","deepseek-harness","dsh","deepseek","cordis","windows","winforms","webview2","always-on-top","desktop-window"]}'
```

**必需项（缺一不可）**：

```
dsh-plugin
deepseek-harness
dsh
deepseek
cordis
```

**建议补充项**：

```
windows
winforms
webview2
always-on-top
desktop-window
```

> **`dsh-plugin` 是关键**：社区插件发现机制依赖它（官方 `deepseek-ai/deepseek-harness`
> 仓库按该 topic 检索插件，`dsh-find-plugin`、`dshmarket` 也都依赖它）。
> 缺了它等于不会被任何目录收录，`awesome-dsh-plugin` 的收录检查也会不通过。

**About 栏 Description**（可在同一处填，限 350 字符）：

```
把 DSH 的侧边对话弹出来，变成一个能压在所有应用之上的独立置顶浮窗：自带四态外观（浅色/深色/跟随 DSH/跟随 Windows）、不透明度、窗口几何记忆、即用即焚与缓存清理。Windows 专属（WinForms + WebView2）。
```

**Website** 可填 `https://github.com/cyh3436332528/dsh-float-chat#readme`。

---

## 第 6 步：向插件市场提交收录

### ⏳ 时间条件

**仓库必须创建满 1 天**（CI 自动检查）。所以：

- 09-28 建库 → **09-29 之后**才能提 PR。

不要提前提，会被 CI 直接打回。

### 提交方式：一个文件 + 一个 PR

1. Fork `awesome-dsh-plugin/awesome-dsh-plugin`
2. 新增 **一个文件**：`data/plugins/cyh3436332528__dsh-float-chat.yml`
   （内容 = 本仓库的 `release/awesome-dsh-plugin-entry.yml`）
3. 提 PR。**不要手工编辑那两个 README** —— 它们由 `data/plugins/*.yml` 自动生成

### 条目文件内容（已校验）

```yaml
url: https://github.com/cyh3436332528/dsh-float-chat
name: cyh3436332528/dsh-float-chat
category: ui
description:
  en: 'Pops the DSH side chat into a free-floating, always-on-top Windows desktop window usable over any other application, with its own 悬浮窗 settings page (appearance light / dark / follow-DSH / follow-Windows, always-on-top, remembered geometry and default size, opacity, close-window-ends-the-thread, session title in the title bar, Esc to close) and a cache-and-log cleanup card.'
  zh: '把 DSH 的侧边对话弹成一个可覆盖在任何应用之上的独立置顶浮窗（Windows 专属），自带「悬浮窗」设置页：四态外观（浅色 / 深色 / 跟随 DSH / 跟随 Windows）、始终置顶、记住窗口尺寸与位置、默认宽高、不透明度、关闭窗口即结束对话、标题栏显示会话名称、Esc 关闭，以及缓存与日志清理卡片。'
```

**为什么 `category: ui`**：本插件的全部价值就是那个浮动窗口界面。
其他候选（如 `tools`）也可以，但 `ui` 最贴切。

**合法 `category` 取值**（备查）：

```
agi  ui  usage  theme  model  identity  session  memory  tools
wsl  browser  vision  voice  docs  skill  workflow  git  notify  dev
security  remote  market  fun
```

> ⚠️ **描述里含 `: `（冒号 + 空格）必须加引号**，否则 YAML 解析失败。
> 上文的 `en` / `zh` 都已加单引号，并已用 `yaml.safe_load` 实测通过。
> 你自己改文案时**保留引号**。

### PR 标题

```
Add dsh-float-chat to UI
```

### PR 正文

```markdown
## 插件

- **名称**：dsh-float-chat
- **仓库**：https://github.com/cyh3436332528/dsh-float-chat
- **分类**：UI
- **License**：MIT
- **当前版本**：0.4.0（早期版本，非 1.0）

## 一句话描述

把 DSH 的侧边对话弹成一个可覆盖在任何应用之上的独立置顶浮窗，自带完整设置页（外观 / 窗口 / 行为 / 缓存）。
Pops the DSH side chat into a free-floating, always-on-top Windows desktop window usable over any other application, with its own settings page.

## 收录检查

- [x] 声明了 `dsh.bundle` manifest（`plugin/package.json` 的 `dsh.bundle.patch` → `cordis.patch.yml`）
- [x] 可用 `dsh plugin add` 安装
- [x] 功能与上面的描述一致——README 的「核心特性」与「配置项」逐项对应源码
- [x] 放在正确的分类下（UI）
- [x] 处于维护状态（我会跟进 issue 与 PR）
- [x] 仓库已添加 `dsh-plugin` topic

## 已在 README 中说明的事

- **仅支持 Windows**（窗口是 WinForms + WebView2 程序），且依赖 **DSH Desktop**
  （需要 Desktop 外壳的逐代渲染器能力头）；
- 「已知限制」一节如实列出了当前的全部不足，包括没有机器可读的 DSH 版本约束、
  宿主半边不热重载、无自动化测试；
- 「风险提示」一节写明了清理会删哪些目录、`webview-data/` 里存有什么。

## 验证

已在 Windows 11 + DSH Desktop 2.0.15（WebView2 Runtime 153.0.4234.48）上实测：
安装、输入框「浮窗」按钮开窗、窗口内的多轮对话、历史对话切换与重命名、
即用即焚、四态外观切换、几何记忆、以及缓存清理的两个入口。

## 备注

本插件依赖 Windows 专属的原生窗口进程，**不适用于 macOS / Linux**——这一点已在
README 的「兼容性」一节明确标注，避免用户装上后发现窗口打不开。
```

---

## 发布后建议做的三件事

1. **去 revoke 用过的 PAT。** 若 token 出现在对话记录里，务必作废重建。
   <https://github.com/settings/tokens>
2. **补真实截图**（你已选择先留占位）。
   README 的截图区整段包在 HTML 注释里，补图步骤：
   - 把 4 张图放进 `docs/assets/`，文件名与 README 里写的一致：
     `01-composer-button.png`、`02-float-window.png`、`03-settings-page.png`、`04-title-bar.png`
   - 删掉 README 里 `<!--` 与 `-->` 这两行注释标记（其余内容已经写好，不用改）
   - 重新提交
3. **（已作废）~~补 `dsh.compatibility`~~** —— 该字段不存在于 DSH 的插件清单中，
   写了也不会被读。详见 `CONTRIBUTING.md` 的「关于 `dsh.compatibility`」一节。
