/**
 * Host half of dsh-float-chat.
 *
 * Owns the pieces the browser half cannot own itself:
 *
 * - `POST /dsh-float-chat/open` — the composer control asks for the floating
 *   window. This side mints the browser-trust URL (`ctx.connection
 *   .authenticatedUrl`) and forwards the Desktop shell's per-generation
 *   renderer capability header, which the window needs to be admitted at all.
 * - `GET /dsh-float-chat/page/` — the compact side-chat page the window shows,
 *   served from this project so the window stays same-origin with the DSH Host
 *   and reuses its authenticated session for the Side Chat JSON API.
 * - `GET|POST /dsh-float-chat/settings` — the floating window's own preferences
 *   (appearance override, always-on-top, geometry policy, opacity, …), stored as
 *   `../window/float-settings.json`. The window process reads that same file, so
 *   a change made in the settings page reaches the window in about a second
 *   without any IPC.
 * - `GET|POST /dsh-float-chat/theme` — the appearance channel: the browser half
 *   reports DSH's own ui-theme state, the window page asks.
 *
 * The window itself is a separate process (`../window/dsh-float-window.exe`, a
 * WinForms + WebView2 app): the DSH renderer is sandboxed and cannot open native
 * windows, and Electron ships no Document Picture-in-Picture implementation, so
 * an always-on-top window over other applications has to be its own program.
 */
import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { readFile, readdir, rename, rm, stat, writeFile } from 'node:fs/promises'
import { dirname, join, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

/** The floating window app shipped beside this package. */
const WINDOW_BIN = fileURLToPath(new URL('../window/dsh-float-window.exe', import.meta.url))

/** Route paths (the page route is a prefix: the shell matches `prefix` or `prefix/…`). */
const OPEN_ROUTE = '/dsh-float-chat/open'
const PAGE_ROUTE = '/dsh-float-chat/page'
/** The window's own preferences (read and written by the settings page). */
const SETTINGS_ROUTE = '/dsh-float-chat/settings'
/** Appearance channel: the renderer reports itself here, the window page asks. */
const THEME_ROUTE = '/dsh-float-chat/theme'
/** Cache/garbage channel: the settings page reads the sizes here and asks for a clean. */
const STATS_ROUTE = '/dsh-float-chat/stats'
const CLEAN_ROUTE = '/dsh-float-chat/clean'
const PAGE_TAIL = join('page', 'float.html')
const WINDOW_TAIL = join('window', 'dsh-float-window.exe')
const SETTINGS_TAIL = join('window', 'float-settings.json')

/* ── 缓存与垃圾 ────────────────────────────────────────────────────────────
 * The window's WebView2 profile sits beside the exe; the folders below are pure
 * cache (regenerated on demand). Cookies and Local Storage are NOT among them, so
 * a clean never logs the window out and never loses the side-thread ids.
 */
const WINDOW_DIR = dirname(WINDOW_BIN)
const CACHE_TAILS = [
  join('EBWebView', 'Default', 'Cache'),
  join('EBWebView', 'Default', 'Code Cache'),
  join('EBWebView', 'Default', 'GPUCache'),
  join('EBWebView', 'Default', 'DawnGraphiteCache'),
  join('EBWebView', 'Default', 'DawnWebGPUCache'),
  join('EBWebView', 'GrShaderCache'),
  join('EBWebView', 'ShaderCache'),
]
/** Keep in step with `LogRotateBytes` in window/src/FloatWindow.cs (3 × 18 MB). */
const LOG_CAP_BYTES = 18 * 1024 * 1024
const LOG_NAME = 'window-log.txt'
/** Left behind when a clean could not finish because the window holds the files:
 *  the window deletes the rest when it closes, or on its next start. */
const PENDING_CLEAN = 'clean-pending.txt'

/** Bytes inside a folder; a missing folder counts as zero. */
async function folderBytes(path) {
  let entries = []
  try { entries = await readdir(path, { withFileTypes: true }) } catch { return 0 }
  let total = 0
  for (const entry of entries) {
    const full = join(path, entry.name)
    if (entry.isDirectory()) {
      total += await folderBytes(full)
      continue
    }
    try { total += (await stat(full)).size } catch { /* vanished mid-walk */ }
  }
  return total
}

/** What the settings page shows: cache size, log size, and the log cap. */
async function cacheStats() {
  const profile = join(WINDOW_DIR, 'webview-data')
  let cacheBytes = 0
  const parts = []
  for (const tail of CACHE_TAILS) {
    const size = await folderBytes(join(profile, tail))
    if (size > 0) parts.push({ name: tail.split(sep).pop(), bytes: size })
    cacheBytes += size
  }
  let logBytes = 0
  try { logBytes = (await stat(join(WINDOW_DIR, LOG_NAME))).size } catch { logBytes = 0 }
  return { cacheBytes, logBytes, logCapBytes: LOG_CAP_BYTES, parts }
}

/**
 * Delete the browser cache and rotate an oversized log. What the running window
 * still holds cannot be deleted from here — `clean-pending.txt` then tells the
 * window to finish the job when it closes (see CleanPendingCache in FloatWindow.cs).
 */
async function cleanCacheAndLog() {
  const before = await cacheStats()
  let freed = 0
  let locked = 0
  const profile = join(WINDOW_DIR, 'webview-data')
  for (const tail of CACHE_TAILS) {
    const full = join(profile, tail)
    const size = await folderBytes(full)
    if (size === 0) continue
    try { await rm(full, { recursive: true, force: true }) } catch { locked += 1 }
    freed += Math.max(0, size - (await folderBytes(full)))
  }
  let rotated = false
  try {
    const logPath = join(WINDOW_DIR, LOG_NAME)
    if ((await stat(logPath)).size >= LOG_CAP_BYTES) {
      const first = join(WINDOW_DIR, 'window-log.1.txt')
      const second = join(WINDOW_DIR, 'window-log.2.txt')
      await rm(second, { force: true })
      try { await rename(first, second) } catch { /* no previous log yet */ }
      await rename(logPath, first)
      rotated = true
    }
  } catch { rotated = false }
  try {
    const marker = join(WINDOW_DIR, PENDING_CLEAN)
    if (locked > 0) await writeFile(marker, 'pending', 'utf8')
    else await rm(marker, { force: true })
  } catch { /* best effort */ }
  return { freed, locked, rotated, beforeCacheBytes: before.cacheBytes, after: await cacheStats() }
}

/** Header the Desktop shell attaches to its own renderer's requests. */
const RENDERER_HEADER = 'x-dsh-desktop-renderer'

/** A request body larger than this is not a window request. */
const MAX_BODY_BYTES = 64 * 1024

/**
 * The floating window's preferences. Every field has to mean something in the
 * window process (`window/src/FloatWindow.cs`) or the page (`page/float.html`):
 * a setting that changes nothing is worse than no setting.
 */
const SETTINGS_DEFAULTS = Object.freeze({
  /** `dsh` follows DSH's own ui-theme preference, `system` follows Windows, `light`/`dark` force one. */
  appearance: 'dsh',
  /** Start pinned over other applications. */
  alwaysOnTop: true,
  /** Remember and restore the window's size and position. */
  rememberGeometry: true,
  /** Window opacity, 0.6 – 1. */
  opacity: 1,
  /** Size used when geometry is not remembered (or nothing was saved yet). */
  defaultWidth: 620,
  defaultHeight: 900,
  /** Start a side thread that is deleted when the window closes. */
  ephemeralByDefault: false,
  /** Put the conversation's title in the window title bar. */
  showSessionTitle: true,
  /** Esc closes the window (running the page's own cleanup first). */
  closeOnEscape: false,
})

/** One accepted appearance: a forced scheme, "follow DSH", or "follow Windows". */
const APPEARANCES = ['light', 'dark', 'dsh', 'system']

/** `auto` was v0.4.0's name for "follow DSH": old files keep working. */
const appearanceOf = (value) => {
  const wanted = value === 'auto' ? 'dsh' : value
  return APPEARANCES.includes(wanted) ? wanted : SETTINGS_DEFAULTS.appearance
}

const clampNumber = (value, fallback, min, max) => {
  const number = typeof value === 'number' && Number.isFinite(value) ? value : fallback
  return Math.min(max, Math.max(min, number))
}

/**
 * Fold whatever is on disk (or came in a request) into a complete, trusted
 * settings object. Unknown keys are dropped and out-of-range numbers are
 * clamped, so a hand-edited file can never crash the window.
 */
function normalizeSettings(raw) {
  const source = raw !== null && typeof raw === 'object' ? raw : {}
  return {
    appearance: appearanceOf(source.appearance),
    alwaysOnTop: typeof source.alwaysOnTop === 'boolean' ? source.alwaysOnTop : SETTINGS_DEFAULTS.alwaysOnTop,
    rememberGeometry: typeof source.rememberGeometry === 'boolean' ? source.rememberGeometry : SETTINGS_DEFAULTS.rememberGeometry,
    opacity: clampNumber(source.opacity, SETTINGS_DEFAULTS.opacity, 0.6, 1),
    defaultWidth: Math.round(clampNumber(source.defaultWidth, SETTINGS_DEFAULTS.defaultWidth, 320, 1600)),
    defaultHeight: Math.round(clampNumber(source.defaultHeight, SETTINGS_DEFAULTS.defaultHeight, 260, 1600)),
    ephemeralByDefault: typeof source.ephemeralByDefault === 'boolean' ? source.ephemeralByDefault : SETTINGS_DEFAULTS.ephemeralByDefault,
    showSessionTitle: typeof source.showSessionTitle === 'boolean' ? source.showSessionTitle : SETTINGS_DEFAULTS.showSessionTitle,
    closeOnEscape: typeof source.closeOnEscape === 'boolean' ? source.closeOnEscape : SETTINGS_DEFAULTS.closeOnEscape,
  }
}

/** Patch shape accepted by POST: only the keys the caller actually sent. */
const PATCH_KEYS = Object.freeze(Object.keys(SETTINGS_DEFAULTS))

/**
 * Locate a file that ships beside this package. Plugin packages are loaded
 * through the profile's `node_modules` junction, so the module's own directory
 * may be either the project directory or the junction — walking up from the
 * module finds the project in both worlds without depending on which one it is.
 */
function findBeside(relative) {
  let dir = dirname(fileURLToPath(import.meta.url))
  for (let depth = 0; depth < 8; depth += 1) {
    const candidate = join(dir, relative)
    if (existsSync(candidate)) return candidate
    const parent = dirname(dir)
    if (parent === dir) break
    dir = parent
  }
  return undefined
}

const PAGE_FILE = findBeside(PAGE_TAIL)
const WINDOW_EXE = existsSync(WINDOW_BIN) ? WINDOW_BIN : findBeside(WINDOW_TAIL)
const SETTINGS_FILE = findBeside(SETTINGS_TAIL) ?? join(dirname(WINDOW_BIN), 'float-settings.json')

/** Read a small JSON request body (the routes only ever receive one). */
async function readJsonBody(req) {
  const chunks = []
  let size = 0
  for await (const chunk of req) {
    size += chunk.length
    if (size > MAX_BODY_BYTES) return undefined
    chunks.push(chunk)
  }
  if (size === 0) return {}
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'))
  } catch (error) {
    return undefined
  }
}

function sendJson(res, status, value) {
  const body = JSON.stringify(value)
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'content-length': String(Buffer.byteLength(body)),
  })
  res.end(body)
}

/** Only loopback origins may be handed a token URL. */
function loopbackOrigin(value) {
  if (typeof value !== 'string' || value === '') return undefined
  try {
    const url = new URL(value)
    const host = url.hostname.toLowerCase()
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return undefined
    if (host !== '127.0.0.1' && host !== 'localhost' && host !== '[::1]' && host !== '::1') return undefined
    return url.origin
  } catch (error) {
    return undefined
  }
}

/**
 * The Host services this half drives. `webServer` has to be declared: cordis
 * only exposes an injected service as a property, and an undeclared
 * `ctx.webServer` is undefined — which fails `apply()` before a single route is
 * registered, and the composer button then reports 浮窗打开失败.
 */
export const inject = ['webServer']

export function apply(ctx) {
  /** The one companion window process, or null. */
  let child = null

  /** Appearance the renderer last reported about itself: { preference, resolved }. */
  let reportedTheme = undefined

  /** The settings as last read or written, so GET never needs the disk. */
  let cachedSettings = undefined

  /**
   * The official read of DSH's appearance: the Host `settings` service owns the
   * `ui-theme` namespace — the same entry the shell's own Appearance row writes —
   * so ask it instead of guessing. Service, namespace and value are all optional;
   * anything missing simply means "unknown".
   */
  const settingsTheme = () => {
    try {
      const settings = ctx.get('settings')
      if (settings === undefined || typeof settings.describe !== 'function') return undefined
      const entry = settings.describe().find((item) => String(item.ns) === 'ui-theme')
      const value = entry !== undefined ? entry.value : undefined
      const preference = value !== undefined ? value.preference : undefined
      if (preference !== 'light' && preference !== 'dark' && preference !== 'system') return undefined
      return { preference }
    } catch (error) {
      ctx.logger?.warn?.(`dsh-float-chat: settings read failed: ${error instanceof Error ? error.message : String(error)}`)
      return undefined
    }
  }

  /** What the window page should paint: the renderer's word first, settings second. */
  const currentTheme = () => {
    if (reportedTheme !== undefined) return reportedTheme
    const fromSettings = settingsTheme()
    return fromSettings === undefined ? {} : fromSettings
  }

  /** Read the window's own settings file; a missing or broken file means defaults. */
  const loadSettings = async () => {
    if (cachedSettings !== undefined) return cachedSettings
    try {
      const text = await readFile(SETTINGS_FILE, 'utf8')
      cachedSettings = normalizeSettings(JSON.parse(text))
    } catch (error) {
      cachedSettings = normalizeSettings(undefined)
    }
    return cachedSettings
  }

  /** Write them back through a temp file, so the window never reads a half file. */
  const saveSettings = async (patch) => {
    const current = await loadSettings()
    const next = normalizeSettings(Object.assign({}, current, patch))
    const temp = `${SETTINGS_FILE}.tmp`
    await writeFile(temp, `${JSON.stringify(next, null, 2)}\n`, 'utf8')
    await rename(temp, SETTINGS_FILE)
    cachedSettings = next
    return next
  }

  const launch = (authUrl, pageUrl, rendererHeader) => {
    if (child !== null && child.exitCode === null && child.signalCode === null) return false
    const env = { ...process.env }
    // The DSH Host runs inside Electron: an inherited ELECTRON_RUN_AS_NODE would
    // turn a child Electron process into a Node one instead of a window.
    delete env.ELECTRON_RUN_AS_NODE
    const args = [
      `--dsh-auth-url=${authUrl}`,
      `--dsh-page-url=${pageUrl}`,
      '--dsh-title=\u60ac\u6d6e\u7a97',
    ]
    if (rendererHeader !== '') {
      args.push(`--dsh-header-name=${RENDERER_HEADER}`, `--dsh-header-value=${rendererHeader}`)
    }
    child = spawn(WINDOW_EXE ?? WINDOW_BIN, args, {
      env,
      detached: true,
      stdio: ['ignore', 'inherit', 'inherit'],
      windowsHide: false,
    })
    child.on('exit', () => {
      child = null
    })
    child.on('error', (error) => {
      ctx.logger?.warn?.(`dsh-float-chat: window spawn failed: ${error instanceof Error ? error.message : String(error)}`)
      child = null
    })
    child.unref()
    return true
  }

  const disposeRoutes = [
    ctx.webServer.register({
      kind: 'exact',
      path: OPEN_ROUTE,
      handler: async (req, res) => {
        if (req.method !== 'POST') {
          sendJson(res, 405, { ok: false, error: 'method not allowed' })
          return
        }
        const body = await readJsonBody(req)
        if (body === undefined) {
          sendJson(res, 400, { ok: false, error: 'invalid body' })
          return
        }
        const origin = loopbackOrigin(body.origin)
        if (origin === undefined) {
          sendJson(res, 400, { ok: false, error: 'loopback origin required' })
          return
        }
        const connection = ctx.get('connection')
        if (connection === undefined || typeof connection.authenticatedUrl !== 'function') {
          sendJson(res, 500, { ok: false, error: 'connection service unavailable' })
          return
        }
        const parent = typeof body.parent === 'string' ? body.parent : ''
        // The caller's own reading is a hint; what the theme channel knows (the
        // renderer's report, else the Host settings entry) decides, so the window
        // is born in the appearance DSH actually has. A forced appearance in the
        // window's own settings wins in the window process itself.
        const settings = await loadSettings()
        const reported = currentTheme()
        let opening = body.theme === 'light' || body.theme === 'dark' ? body.theme : undefined
        if (opening === undefined) opening = reported.resolved
        if (opening === undefined && (reported.preference === 'light' || reported.preference === 'dark')) {
          opening = reported.preference
        }
        // A forced scheme can seed the first paint; `dsh`/`system` are resolved by
        // the window process, which sees both DSH's preference and Windows.
        if (opening === undefined && (settings.appearance === 'light' || settings.appearance === 'dark')) {
          opening = settings.appearance
        }
        const pageUrl = new URL(`${PAGE_ROUTE}/`, origin)
        if (parent !== '') pageUrl.searchParams.set('parent', parent)
        if (opening !== undefined) pageUrl.searchParams.set('theme', opening)
        const forwarded = req.headers[RENDERER_HEADER]
        try {
          const started = launch(
            connection.authenticatedUrl(`${origin}/`),
            pageUrl.href,
            typeof forwarded === 'string' ? forwarded : '',
          )
          sendJson(res, 200, { ok: true, value: { started, pageUrl: pageUrl.href } })
        } catch (error) {
          ctx.logger?.warn?.(`dsh-float-chat: window launch threw: ${error instanceof Error ? error.message : String(error)}`)
          sendJson(res, 500, { ok: false, error: 'launch failed' })
        }
      },
    }),
    ctx.webServer.register({
      kind: 'exact',
      path: SETTINGS_ROUTE,
      handler: async (req, res) => {
        if (req.method === 'GET') {
          sendJson(res, 200, { ok: true, value: await loadSettings(), defaults: SETTINGS_DEFAULTS })
          return
        }
        if (req.method !== 'POST') {
          sendJson(res, 405, { ok: false, error: 'method not allowed' })
          return
        }
        const body = await readJsonBody(req)
        if (body === undefined) {
          sendJson(res, 400, { ok: false, error: 'invalid body' })
          return
        }
        // Only the keys this plugin owns: a request can never smuggle in extra
        // fields that later end up in the window's file.
        const patch = {}
        let touched = 0
        for (const key of PATCH_KEYS) {
          if (Object.prototype.hasOwnProperty.call(body, key) && body[key] !== undefined) {
            patch[key] = body[key]
            touched += 1
          }
        }
        if (touched === 0) {
          sendJson(res, 400, { ok: false, error: 'no known setting in body' })
          return
        }
        try {
          const saved = await saveSettings(patch)
          sendJson(res, 200, { ok: true, value: saved })
        } catch (error) {
          ctx.logger?.warn?.(`dsh-float-chat: settings write failed: ${error instanceof Error ? error.message : String(error)}`)
          sendJson(res, 500, { ok: false, error: 'settings write failed' })
        }
      },
    }),
    ctx.webServer.register({
      kind: 'exact',
      path: THEME_ROUTE,
      handler: async (req, res) => {
        if (req.method === 'GET') {
          // The window page polls this: one small object, no guessing on its side.
          sendJson(res, 200, { ok: true, value: currentTheme() })
          return
        }
        if (req.method !== 'POST') {
          sendJson(res, 405, { ok: false, error: 'method not allowed' })
          return
        }
        const body = await readJsonBody(req)
        if (body === undefined) {
          sendJson(res, 400, { ok: false, error: 'invalid body' })
          return
        }
        // The browser half reports straight from the ui-theme service: the
        // preference the user picked plus the scheme it resolves to right now.
        const preference = body.preference === 'light' || body.preference === 'dark' || body.preference === 'system'
          ? body.preference
          : undefined
        const resolved = body.resolved === 'light' || body.resolved === 'dark' ? body.resolved : undefined
        if (preference === undefined && resolved === undefined) {
          sendJson(res, 400, { ok: false, error: 'theme required' })
          return
        }
        const next = {}
        if (preference !== undefined) next.preference = preference
        if (resolved !== undefined) next.resolved = resolved
        reportedTheme = next
        sendJson(res, 200, { ok: true, value: reportedTheme })
      },
    }),
    ctx.webServer.register({
      kind: 'exact',
      path: STATS_ROUTE,
      handler: async (req, res) => {
        if (req.method !== 'GET') {
          sendJson(res, 405, { ok: false, error: 'method not allowed' })
          return
        }
        try {
          sendJson(res, 200, { ok: true, value: await cacheStats() })
        } catch (error) {
          sendJson(res, 500, { ok: false, error: String((error && error.message) || error) })
        }
      },
    }),
    ctx.webServer.register({
      kind: 'exact',
      path: CLEAN_ROUTE,
      handler: async (req, res) => {
        if (req.method !== 'POST') {
          sendJson(res, 405, { ok: false, error: 'method not allowed' })
          return
        }
        const body = await readJsonBody(req)
        if (body === undefined) {
          sendJson(res, 400, { ok: false, error: 'invalid body' })
          return
        }
        try {
          sendJson(res, 200, { ok: true, value: await cleanCacheAndLog() })
        } catch (error) {
          sendJson(res, 500, { ok: false, error: String((error && error.message) || error) })
        }
      },
    }),
    ctx.webServer.register({
      kind: 'prefix',
      path: PAGE_ROUTE,
      handler: async (req, res) => {
        const path = new URL(req.url ?? '/', 'http://127.0.0.1').pathname
        if (PAGE_FILE === undefined || (path !== PAGE_ROUTE && path !== `${PAGE_ROUTE}/`)) {
          res.writeHead(404, { 'cache-control': 'no-store' }).end('not found')
          return
        }
        try {
          const bytes = await readFile(PAGE_FILE)
          res.writeHead(200, {
            'content-type': 'text/html; charset=utf-8',
            'cache-control': 'no-store',
            'content-length': String(bytes.byteLength),
          })
          res.end(req.method === 'HEAD' ? undefined : bytes)
        } catch (error) {
          res.writeHead(500, { 'cache-control': 'no-store' }).end('page unavailable')
        }
      },
    }),
  ]

  ctx.effect(() => () => {
    for (const dispose of disposeRoutes) dispose()
    if (child !== null && child.exitCode === null) child.kill()
    child = null
  }, 'dsh-float-chat routes and window process')
}
