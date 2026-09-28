/**
 * dsh-float-chat — browser half.
 *
 * Three registrations, all additive:
 *
 *  - one compact control beside the composer's submit action, which asks the Host
 *    half (`POST /dsh-float-chat/open`) to raise the floating side-chat window;
 *  - one settings page (`settings.section`), the same seat `dsh-plugin-session-purge`
 *    uses for 会话管理 — appearance and behaviour of that window;
 *  - one nav glyph: `settings.section` projects only `id`/`order`/`label`, so a
 *    third-party entry wears the shell's gear unless it claims its own row. That
 *    is exactly what `dshmarket` / `dsh-better-sidebar` do (see the comment on
 *    `installSettingsNavIcon`), and this half does the same with a window mark.
 *
 * It also keeps DSH's own appearance reported to the Host's theme channel, so the
 * window can look like DSH without anyone guessing.
 *
 * The window itself is a separate process: the DSH renderer is sandboxed and cannot
 * open native windows, and Electron ships no Document Picture-in-Picture
 * implementation, so an always-on-top window over other applications has to be its
 * own program.
 */
window.__ModuleLoader__.load({
  id: 'dsh-float-chat',
  factory(require) {
    const React = require('react')
    const primitives = require('@deepseek-ai/dsh-client-ui-primitives')
    const Button = primitives.Button
    const h = React.createElement

    const SETTINGS_API = '/dsh-float-chat/settings'
    /** The section label, used both by the slot and by the nav-glyph claimer. */
    const SECTION_LABEL = '悬浮窗'

    const BUTTON = {
      display: 'inline-flex',
      alignItems: 'center',
      gap: '4px',
      height: '26px',
      padding: '0 8px',
      borderRadius: '8px',
      border: '1px solid var(--dsw-alias-border-l1, rgba(127,127,127,.3))',
      background: 'transparent',
      color: 'var(--dsw-alias-label-secondary, #9a9aa4)',
      font: '12px/1 system-ui, "Microsoft YaHei", sans-serif',
      cursor: 'pointer',
      WebkitAppRegion: 'no-drag',
    }

    const NOTE = {
      marginLeft: '6px',
      font: '11px/1.3 system-ui, "Microsoft YaHei", sans-serif',
      color: 'var(--dsw-alias-state-error-primary, #e5534b)',
      maxWidth: '220px',
    }

    /* ── DSH's own appearance, read the official way ──────────────────────── */

    /**
     * `ctx.get('theme')` is the registry `dsh-client-ui-theme` provides; its
     * snapshot carries `preference` (what the user picked, possibly `system`)
     * and `active.id` (the scheme it resolves to now). Only when that service is
     * absent do we fall back to reading the rendered background.
     */
    let themeService
    let lastTheme = { preference: undefined, resolved: undefined }

    function findThemeService(ctx) {
      if (themeService !== undefined) return themeService
      try {
        const service = ctx.get('theme')
        if (service !== undefined && typeof service.getTheme === 'function') themeService = service
      } catch (error) {
        themeService = undefined
      }
      return themeService
    }

    function resolvedTheme() {
      const snapshot = themeService !== undefined ? themeService.getTheme() : undefined
      const active = snapshot !== undefined ? snapshot.active : undefined
      const id = active !== undefined ? active.id : undefined
      if (id === 'light' || id === 'dark') return id
      const preference = snapshot !== undefined ? snapshot.preference : undefined
      if (preference === 'light' || preference === 'dark') return preference
      return undefined
    }

    function backgroundColorTheme() {
      try {
        const value = getComputedStyle(document.body).backgroundColor
        const parts = /rgba?\(([^)]+)\)/.exec(value)
        if (parts === null) return 'dark'
        const [r, g, b] = parts[1].split(',').map((piece) => Number.parseFloat(piece))
        const luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255
        return Number.isFinite(luminance) && luminance > 0.5 ? 'light' : 'dark'
      } catch (error) {
        return 'dark'
      }
    }

    /** Re-read the appearance and hand it to the Host's theme channel. */
    function reportTheme(ctx) {
      const snapshot = themeService !== undefined ? themeService.getTheme() : undefined
      const preference = snapshot !== undefined ? snapshot.preference : undefined
      lastTheme = {
        preference: preference === 'light' || preference === 'dark' || preference === 'system' ? preference : undefined,
        resolved: resolvedTheme(),
      }
      if (lastTheme.resolved === undefined) lastTheme.resolved = backgroundColorTheme()
      // A Host that predates the theme channel answers 404; that is not an error.
      fetch('/dsh-float-chat/theme', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(lastTheme),
      }).catch(() => {})
    }

    /* ── the nav glyph ────────────────────────────────────────────────────── */

    /**
     * The settings shell picks nav glyphs from a closed list of section ids
     * (`models`, `agent-presets`, `plugins`) and falls back to its own gear for
     * every other id: `settings.section` projects only `id` / `order` / `label`,
     * so a registrant has no icon to pass. Every third-party section therefore
     * wears the gear — `dshmarket` claims its own row after the dialog mounts
     * and swaps the gear for its mark, and `dsh-better-sidebar` /
     * `dsh-skill-mcp-panel` do the same. This is that same trick, with a window
     * mark: the row whose visible text equals our own label gets a marker, and a
     * stylesheet hides the shell's gear and paints our SVG through a
     * `mask-image` in `currentColor` (so it follows the theme for free).
     */
    const NAV_ICON_MARKER = 'data-dsh-float-nav-icon'
    const NAV_ROW_SELECTOR = '[role="dialog"] nav button'
    /** A window frame plus a detached panel in front of it: "floats over". */
    const NAV_MARK_SVG = [
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="#000">',
      '<rect x="1.7" y="1.7" width="9.4" height="9.4" rx="2.6" fill="none" stroke="#000" stroke-width="1.7"/>',
      '<rect x="7.6" y="7.6" width="6.8" height="6.8" rx="2"/>',
      '</svg>',
    ].join('')

    function navIconCss(maskUrl) {
      return [
        `[${NAV_ICON_MARKER}] > svg { display: none; }`,
        `[${NAV_ICON_MARKER}]::before {`,
        `  content: '';`,
        `  flex: none;`,
        `  width: 16px;`,
        `  height: 16px;`,
        `  background-color: currentColor;`,
        `  -webkit-mask-image: url("${maskUrl}");`,
        `  mask-image: url("${maskUrl}");`,
        `  -webkit-mask-repeat: no-repeat;`,
        `  mask-repeat: no-repeat;`,
        `  -webkit-mask-position: center;`,
        `  mask-position: center;`,
        `  -webkit-mask-size: 16px 16px;`,
        `  mask-size: 16px 16px;`,
        `}`,
      ].join('\n')
    }

    /** Mark the one nav row this section owns; removed with the fiber. */
    function installSettingsNavIcon(ctx) {
      if (typeof document === 'undefined' || typeof MutationObserver === 'undefined') return
      ctx.effect(() => {
        const tag = document.createElement('style')
        tag.dataset.plugin = 'dsh-float-chat'
        tag.dataset.pluginCss = 'dsh-float-chat/settings-nav-icon'
        tag.textContent = navIconCss(`data:image/svg+xml,${encodeURIComponent(NAV_MARK_SVG)}`)
        document.head.appendChild(tag)
        let disposed = false
        let scheduled = false
        const sync = () => {
          scheduled = false
          if (disposed) return
          for (const row of document.querySelectorAll(NAV_ROW_SELECTOR)) {
            if (String(row.textContent ?? '').trim() === SECTION_LABEL) row.setAttribute(NAV_ICON_MARKER, '')
            else row.removeAttribute(NAV_ICON_MARKER)
          }
        }
        const schedule = () => {
          if (scheduled || disposed) return
          scheduled = true
          queueMicrotask(sync)
        }
        sync()
        const observer = new MutationObserver(schedule)
        observer.observe(document.body, { childList: true, subtree: true, characterData: true })
        return () => {
          disposed = true
          observer.disconnect()
          for (const row of document.querySelectorAll(`[${NAV_ICON_MARKER}]`)) row.removeAttribute(NAV_ICON_MARKER)
          tag.remove()
        }
      }, 'dsh-float-chat: settings nav icon')
    }

    /* ── the composer control ─────────────────────────────────────────────── */

    function FloatChatButton(props) {
      const [state, setState] = React.useState('idle')
      const [note, setNote] = React.useState('')
      const [hover, setHover] = React.useState(false)
      const [pressed, setPressed] = React.useState(false)
      const sessionId = typeof props.sessionId === 'string' ? props.sessionId : ''

      const open = React.useCallback(() => {
        if (state === 'opening') return
        setState('opening')
        setNote('')
        fetch('/dsh-float-chat/open', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            origin: location.origin,
            parent: sessionId,
            theme: lastTheme.resolved !== undefined ? lastTheme.resolved : backgroundColorTheme(),
          }),
        }).then(async (response) => {
          const parsed = await response.json().catch(() => null)
          if (!response.ok || parsed === null || parsed.ok !== true) {
            const message = parsed !== null && parsed.error !== undefined ? String(parsed.error) : 'HTTP ' + String(response.status)
            throw new Error(message)
          }
          setState('idle')
        }).catch((error) => {
          setState('error')
          setNote('打开浮窗失败：' + String(error !== null && error !== undefined && error.message ? error.message : error))
        })
      }, [sessionId, state])

      const visual = Object.assign({}, BUTTON)
      if (hover) {
        visual.background = 'var(--dsw-alias-interactive-bg-hover, rgba(127, 127, 127, .16))'
        visual.color = 'var(--dsw-alias-label-primary, #e8e8ea)'
      }
      if (pressed) visual.background = 'var(--dsw-alias-interactive-bg-pressed, rgba(127, 127, 127, .24))'
      if (state === 'error') visual.color = 'var(--dsw-alias-state-error-primary, #e5534b)'

      return h('span', { style: { display: 'inline-flex', alignItems: 'center' } }, [
        h('button', {
          key: 'button',
          type: 'button',
          style: visual,
          title: '在独立置顶窗口中打开侧边对话',
          'data-dsh-float-chat': 'open',
          onClick: open,
          onMouseEnter: () => { setHover(true) },
          onMouseLeave: () => { setHover(false); setPressed(false) },
          onMouseDown: () => { setPressed(true) },
          onMouseUp: () => { setPressed(false) },
        }, state === 'opening' ? '打开中…' : '浮窗'),
        note === '' ? null : h('span', { key: 'note', style: NOTE }, note),
      ])
    }

    /* ── the 悬浮窗 settings page ─────────────────────────────────────────── */

    /**
     * Layout follows what the shipped sections do (`dsh-client-ui-settings-shell`,
     * the market's section): a page title with a one-line description, then
     * rounded cards per topic, each row ≥44px with the label and its description
     * stacked on the left and the control right-aligned — spacing carries the
     * grouping instead of hairlines, which is what keeps it from reading cramped.
     */
    /** 缓存大小这类数字：B / KB / MB / GB，一位小数就够看。 */
    function fmtBytes(bytes) {
      const n = typeof bytes === 'number' && Number.isFinite(bytes) ? bytes : 0
      if (n < 1024) return n + ' B'
      if (n < 1048576) return (n / 1024).toFixed(1) + ' KB'
      if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB'
      return (n / 1073741824).toFixed(2) + ' GB'
    }

    const S = {
      root: { font: 'inherit', color: 'inherit', display: 'flex', flexDirection: 'column', gap: '20px', paddingBottom: '32px' },
      head: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '2px 2px 0' },
      title: { margin: 0, fontSize: '16px', fontWeight: 500, lineHeight: '24px' },
      sub: { margin: 0, fontSize: '12px', lineHeight: '18px', color: 'var(--dsw-alias-label-tertiary, #8b93a1)' },
      card: {
        display: 'flex',
        flexDirection: 'column',
        gap: '4px',
        padding: '14px 16px 16px',
        borderRadius: '16px',
        border: '0.5px solid var(--dsw-alias-border-l1, rgba(127,127,127,.22))',
        background: 'var(--dsw-alias-bg-layer-1, rgba(127,127,127,.035))',
      },
      cardTitle: {
        fontSize: '13px',
        fontWeight: 600,
        lineHeight: '20px',
        padding: '2px 0 8px',
        color: 'var(--dsw-alias-label-secondary, #6b7280)',
      },
      row: { display: 'flex', gap: '16px', alignItems: 'center', minHeight: '44px', padding: '4px 0' },
      labels: { display: 'flex', flexDirection: 'column', gap: '2px', flex: '1 1 auto', minWidth: 0 },
      name: { fontSize: '13px', lineHeight: '20px' },
      hint: { fontSize: '12px', lineHeight: '18px', color: 'var(--dsw-alias-label-tertiary, #8b93a1)' },
      controls: { flex: '0 0 auto', display: 'flex', gap: '6px', alignItems: 'center' },
      footer: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
      note: { margin: 0, fontSize: '12px', lineHeight: '18px', whiteSpace: 'pre-wrap', color: 'var(--dsw-alias-label-secondary, #6b7280)' },
      input: {
        width: '84px',
        height: '30px',
        boxSizing: 'border-box',
        padding: '0 8px',
        borderRadius: '8px',
        border: '1px solid var(--dsw-alias-border-l1, rgba(127,127,127,.3))',
        background: 'transparent',
        color: 'inherit',
        font: 'inherit',
        fontSize: '13px',
      },
      value: { minWidth: '52px', textAlign: 'center', fontSize: '13px', fontVariantNumeric: 'tabular-nums', color: 'var(--dsw-alias-label-secondary, #6b7280)' },
      slider: { width: '190px', accentColor: 'var(--dsw-alias-state-business-primary, #4176e6)', cursor: 'pointer' },
      tiles: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: '8px', paddingTop: '4px' },
      tile: {
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: '7px',
        padding: '12px 6px',
        borderRadius: '12px',
        borderStyle: 'solid',
        borderWidth: '1px',
        // Explicit in BOTH states: a style key that disappears from the object is the
        // one React clears late, which is how a "leftover border" shows up.
        borderColor: 'var(--dsw-alias-border-l1, rgba(127,127,127,.28))',
        outline: 'none',
        boxShadow: 'none',
        WebkitTapHighlightColor: 'transparent',
        background: 'transparent',
        color: 'var(--dsw-alias-label-secondary, #6b7280)',
        font: 'inherit',
        fontSize: '12px',
        lineHeight: '16px',
        cursor: 'pointer',
        transition: 'border-color 120ms ease-out, background-color 120ms ease-out, color 120ms ease-out, transform 100ms ease-out',
      },
      tileOn: {
        borderColor: 'var(--dsw-alias-state-business-primary, #4176e6)',
        background: 'var(--dsw-alias-interactive-bg-hover, rgba(127,127,127,.08))',
        color: 'var(--dsw-alias-label-primary, #1f2328)',
      },
    }

    /** Thin JSON client for the Host half's settings route. */
    async function settingsApi(pathname, init) {
      const response = await fetch(SETTINGS_API + pathname, Object.assign(
        { headers: { 'content-type': 'application/json' } },
        init,
      ))
      if (response.status === 404) {
        const missing = new Error('not served')
        missing.unavailable = true
        throw missing
      }
      let body = null
      try { body = await response.json() } catch (error) { body = null }
      if (body === null) throw new Error('HTTP ' + response.status + '（返回不是 JSON）')
      if (!response.ok || body.ok === false) throw new Error(body.error || ('HTTP ' + response.status))
      // The whole envelope: GET also carries the defaults ("恢复默认" needs them).
      return body
    }

    /** One labelled row: stacked label + description on the left, control right. */
    function Row(props) {
      return h('div', { style: S.row }, [
        h('span', { key: 'labels', style: S.labels }, [
          h('span', { key: 'name', style: S.name }, props.label),
          props.hint === undefined ? null : h('span', { key: 'hint', style: S.hint }, props.hint),
        ]),
        h('span', { key: 'controls', style: S.controls }, props.children),
      ])
    }

    /** One on/off row: the two states are the button's own label, nothing hidden. */
    function ToggleRow(props) {
      const on = props.value === true
      return h(Row, { label: props.label, hint: props.hint },
        h(Button, {
          size: 'sm',
          variant: on ? 'primary' : 'outline',
          disabled: props.disabled === true,
          onClick: () => { props.onChange(!on) },
        }, on ? '开' : '关'),
      )
    }

    /** One number row: a small input, committed on blur or Enter. */
    function NumberRow(props) {
      const [draft, setDraft] = React.useState(String(props.value))
      React.useEffect(() => { setDraft(String(props.value)) }, [props.value])
      const commit = () => {
        const parsed = Number.parseFloat(draft)
        if (!Number.isFinite(parsed)) {
          setDraft(String(props.value))
          return
        }
        if (parsed !== props.value) props.onChange(parsed)
      }
      return h(Row, { label: props.label, hint: props.hint }, [
        h('input', {
          key: 'input',
          style: S.input,
          type: 'number',
          value: draft,
          min: props.min,
          max: props.max,
          step: props.step,
          disabled: props.disabled === true,
          onChange: (event) => { setDraft(event.target.value) },
          onBlur: commit,
          onKeyDown: (event) => { if (event.key === 'Enter') commit() },
        }),
        props.unit === undefined ? null : h('span', { key: 'unit', style: { fontSize: '12px', color: 'var(--dsw-alias-label-tertiary, #8b93a1)' } }, props.unit),
      ])
    }

    /**
     * One slider row. The window has to follow the thumb while it moves: the window
     * process watches the settings file (FileSystemWatcher), so a write lands there
     * in a few milliseconds. Writes are therefore throttled (~110 ms, leading +
     * trailing) instead of one per input event — a drag costs a handful of writes
     * rather than sixty — and the value is committed once more on release. The local
     * draft wins until the pointer is released, so the echo from the server cannot
     * pull the thumb back mid-drag.
     */
    function SliderRow(props) {
      const [draft, setDraft] = React.useState(props.value)
      const dragging = React.useRef(false)
      const lastSent = React.useRef(0)
      const pending = React.useRef(undefined)
      const timer = React.useRef(undefined)
      React.useEffect(() => {
        if (!dragging.current) setDraft(props.value)
      }, [props.value])
      React.useEffect(() => () => { if (timer.current !== undefined) clearTimeout(timer.current) }, [])
      /** Leading + trailing throttle: the window moves while the thumb does. */
      const live = (value) => {
        pending.current = value
        const now = Date.now()
        if (now - lastSent.current >= 110) {
          lastSent.current = now
          props.onLive(value)
          return
        }
        if (timer.current !== undefined) return
        timer.current = setTimeout(() => {
          timer.current = undefined
          if (pending.current === undefined) return
          lastSent.current = Date.now()
          props.onLive(pending.current)
        }, 110)
      }
      const finish = (value) => {
        dragging.current = false
        pending.current = undefined
        if (timer.current !== undefined) {
          clearTimeout(timer.current)
          timer.current = undefined
        }
        props.onChange(value)
      }
      const valueOf = (event) => Number.parseFloat(event.target.value)
      return h(Row, { label: props.label, hint: props.hint }, [
        h('input', {
          key: 'slider',
          style: S.slider,
          type: 'range',
          min: props.min,
          max: props.max,
          step: props.step,
          value: draft,
          disabled: props.disabled === true,
          'aria-label': props.label,
          onPointerDown: () => { dragging.current = true },
          onInput: (event) => {
            const value = valueOf(event)
            setDraft(value)
            live(value)
          },
          onPointerUp: (event) => { finish(valueOf(event)) },
          onKeyUp: (event) => {
            const keys = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown']
            if (keys.indexOf(event.key) >= 0) finish(valueOf(event))
          },
        }),
        h('span', { key: 'value', style: S.value }, Math.round(draft * 100) + '%'),
      ])
    }

    /** "浅色 / 深色" as it reads in the hint under 配色方案. */
    const THEME_NAMES = { light: '浅色', dark: '深色' }

    /**
     * One appearance option carries its own mark, the way every sidebar card
     * carries one: inline SVG in `currentColor`, so it follows the theme for free.
     */
    const MARK_LINE = { fill: 'none', stroke: 'currentColor', strokeWidth: 1.5, strokeLinecap: 'round', strokeLinejoin: 'round' }
    const markBox = (children) => h('svg', { width: 18, height: 18, viewBox: '0 0 16 16', 'aria-hidden': 'true' }, children)

    const markSun = () => markBox([
      h('circle', { key: 'c', cx: 8, cy: 8, r: 3.1, ...MARK_LINE }),
      h('path', { key: 'r', d: 'M8 1.3v1.5M8 13.2v1.5M1.3 8h1.5M13.2 8h1.5M3.4 3.4l1.1 1.1M11.5 11.5l1.1 1.1M12.6 3.4l-1.1 1.1M4.5 11.5l-1.1 1.1', ...MARK_LINE }),
    ])

    const markMoon = () => markBox(
      h('path', { d: 'M12.9 9.7A5.3 5.3 0 0 1 6.3 3.1a5.3 5.3 0 1 0 6.6 6.6z', ...MARK_LINE }),
    )

    const markApp = () => markBox([
      h('rect', { key: 'w', x: 1.7, y: 2.6, width: 12.6, height: 10.8, rx: 2.4, ...MARK_LINE }),
      h('path', { key: 'b', d: 'M1.7 6h12.6', ...MARK_LINE }),
      h('circle', { key: 'd', cx: 4.2, cy: 4.3, r: 0.6, fill: 'currentColor' }),
    ])

    const markMonitor = () => markBox([
      h('rect', { key: 'w', x: 1.4, y: 2.4, width: 13.2, height: 8.8, rx: 1.8, ...MARK_LINE }),
      h('path', { key: 's', d: 'M8 11.2v2.2M5.4 13.4h5.2', ...MARK_LINE }),
    ])

    /**
     * One appearance tile. It owns its own hover / press state so a click reads as
     * instant: the border settles on hover, the tile dips while the pointer is down,
     * and both states carry the same style keys, so nothing lingers from the
     * previous selection.
     */
    function AppearanceTile(props) {
      const [hover, setHover] = React.useState(false)
      const [pressed, setPressed] = React.useState(false)
      const style = Object.assign({}, S.tile)
      if (props.selected) Object.assign(style, S.tileOn)
      else if (hover) style.borderColor = 'var(--dsw-alias-border-l3, rgba(127,127,127,.5))'
      if (pressed) style.transform = 'scale(0.97)'
      return h('button', {
        type: 'button',
        'data-dsh-float-appearance': props.id,
        'aria-pressed': props.selected,
        style,
        onClick: props.onSelect,
        onMouseEnter: () => { setHover(true) },
        onMouseLeave: () => { setHover(false); setPressed(false) },
        onMouseDown: () => { setPressed(true) },
        onMouseUp: () => { setPressed(false) },
      }, [
        h(props.mark, { key: 'mark' }),
        h('span', { key: 'label' }, props.label),
      ])
    }

    const APPEARANCE_OPTIONS = [
      { id: 'light', label: '浅色', mark: markSun },
      { id: 'dark', label: '深色', mark: markMoon },
      { id: 'dsh', label: '跟随 DSH', mark: markApp },
      { id: 'system', label: '跟随 Windows', mark: markMonitor },
    ]

    /** The settings page the section slot renders. */
    function FloatSettingsPanel() {
      const [state, setState] = React.useState({ status: 'loading', settings: null, defaults: undefined, note: '' })

      const load = React.useCallback(() => {
        setState((current) => Object.assign({}, current, { status: 'loading', note: '' }))
        settingsApi('').then(
          (body) => setState({ status: 'ready', settings: body.value, defaults: body.defaults, note: '' }),
          (error) => setState({
            status: error !== null && error !== undefined && error.unavailable === true ? 'unavailable' : 'error',
            settings: null,
            defaults: undefined,
            note: error !== null && error !== undefined && error.unavailable === true
              ? ''
              : String((error && error.message) || error),
          }),
        )
      }, [])

      React.useEffect(() => { load() }, [load])

      const save = React.useCallback((patch, quiet) => {
        setState((current) => {
          if (current.settings === null) return current
          // Optimistic: the window's watcher picks the file up in milliseconds, and a
          // failed write is reported underneath rather than bouncing the control.
          // `quiet` is the drag path — it must not make the note flicker per step.
          return Object.assign({}, current, {
            settings: Object.assign({}, current.settings, patch),
            status: 'ready',
            note: quiet === true ? current.note : '保存中…',
          })
        })
        settingsApi('', { method: 'POST', body: JSON.stringify(patch) }).then(
          (body) => setState((current) => Object.assign({}, current, {
            status: 'ready',
            settings: body.value,
            note: quiet === true ? current.note : '已保存',
          })),
          (error) => setState((current) => Object.assign({}, current, {
            status: 'error',
            note: '保存失败：' + String((error && error.message) || error),
          })),
        )
      }, [])

      const [garbage, setGarbage] = React.useState(null)
      const [cleanNote, setCleanNote] = React.useState('')
      const [cleaning, setCleaning] = React.useState(false)

      const loadGarbage = React.useCallback(() => {
        // Served by the Host half; a Host from before this feature answers 404 and
        // the card just says the numbers are unavailable.
        settingsApi('/stats').then(
          (body) => setGarbage(body.value),
          () => setGarbage(null),
        )
      }, [])

      React.useEffect(() => { loadGarbage() }, [loadGarbage])

      /** 清理：和悬浮窗标题栏上那个按钮做的是同一件事，只是从设置页发起。 */
      const cleanNow = () => {
        setCleaning(true)
        setCleanNote('')
        settingsApi('/clean', { method: 'POST', body: JSON.stringify({ source: 'settings' }) }).then(
          (body) => {
            const value = body.value || {}
            const freed = typeof value.freed === 'number' ? value.freed : 0
            setCleaning(false)
            setCleanNote((freed === 0 && value.locked === 0 && value.rotated !== true
              ? '没有需要清理的内容'
              : '已清理 ' + fmtBytes(freed)
                + (value.rotated === true ? '，日志已轮转' : '')
                + (value.locked > 0 ? '；另有 ' + value.locked + ' 项将在浮窗关闭后清理' : '')))
            loadGarbage()
          },
          (error) => { setCleaning(false); setCleanNote('清理失败：' + String((error && error.message) || error)) },
        )
      }

      if (state.status === 'loading' && state.settings === null) {
        return h('div', { style: S.root }, h('p', { style: S.sub }, '正在加载设置…'))
      }

      if (state.status === 'unavailable') {
        return h('div', { style: S.root }, [
          h('div', { key: 'head', style: S.head }, [
            h('p', { key: 't', style: S.title }, SECTION_LABEL),
            h('p', { key: 's', style: S.sub }, '设置暂不可用。'),
          ]),
          h('div', { key: 'card', style: S.card }, [
            h('div', { key: 'h', style: S.cardTitle }, '需要重启 DSH Desktop'),
            h('p', { key: 'p', style: S.note }, '设置服务尚未就绪。重启一次 DSH Desktop 后即可使用。'),
            h('div', { key: 'actions', style: Object.assign({}, S.footer, { paddingTop: '10px' }) },
              h(Button, { size: 'sm', onClick: load }, '重试')),
          ]),
        ])
      }

      const settings = state.settings
      if (settings === null) {
        return h('div', { style: S.root }, [
          h('p', { key: 't', style: S.title }, SECTION_LABEL),
          h('p', { key: 'e', style: S.note }, state.note === '' ? '设置加载失败。' : state.note),
          h('div', { key: 'actions', style: S.footer }, h(Button, { size: 'sm', onClick: load }, '重试')),
        ])
      }

      // What the window is actually using right now: the forced value when one is
      // set, otherwise DSH's own resolved scheme (the window follows it).
      const currentTheme = THEME_NAMES[lastTheme.resolved] ?? '未知'
      const appearanceHint = settings.appearance === 'dsh'
        ? `跟随 DSH 的浅色 / 深色（当前：${currentTheme}）`
        : settings.appearance === 'system'
          ? '跟随 Windows 的浅色 / 深色'
          : `始终使用${THEME_NAMES[settings.appearance] ?? settings.appearance}，不随 DSH 与 Windows 切换`

      const blocks = [
        h('div', { key: 'head', style: S.head }, [
          h('p', { key: 't', style: S.title }, SECTION_LABEL),
          h('p', { key: 's', style: S.sub }, '独立浮窗的外观与行为。修改立即生效；窗口尺寸与位置在下次打开时应用。'),
        ]),
        h('div', { key: 'appearance', style: S.card }, [
          h('div', { key: 'h', style: S.cardTitle }, '外观'),
          h('div', { key: 'label', style: S.labels }, [
            h('span', { key: 'name', style: S.name }, '配色方案'),
            h('span', { key: 'hint', style: S.hint }, appearanceHint),
          ]),
          h('div', { key: 'tiles', style: S.tiles }, APPEARANCE_OPTIONS.map((option) => h(AppearanceTile, {
            key: option.id,
            id: option.id,
            label: option.label,
            mark: option.mark,
            selected: settings.appearance === option.id,
            onSelect: () => { save({ appearance: option.id }) },
          }))),
        ]),
        h('div', { key: 'window', style: S.card }, [
          h('div', { key: 'h', style: S.cardTitle }, '窗口'),
          h(ToggleRow, {
            key: 'pin',
            label: '始终置顶',
            hint: '打开后保持在其它窗口上方；标题栏的图钉按钮可随时切换',
            value: settings.alwaysOnTop,
            onChange: (value) => { save({ alwaysOnTop: value }) },
          }),
          h(ToggleRow, {
            key: 'remember',
            label: '记住窗口位置与大小',
            hint: '下次打开时恢复上次的位置与大小',
            value: settings.rememberGeometry,
            onChange: (value) => { save({ rememberGeometry: value }) },
          }),
          h(NumberRow, {
            key: 'width',
            label: '默认宽度',
            hint: '未记住位置与大小时使用',
            value: settings.defaultWidth,
            min: 320,
            max: 1600,
            step: 10,
            unit: 'px',
            onChange: (value) => { save({ defaultWidth: value }) },
          }),
          h(NumberRow, {
            key: 'height',
            label: '默认高度',
            value: settings.defaultHeight,
            min: 260,
            max: 1600,
            step: 10,
            unit: 'px',
            onChange: (value) => { save({ defaultHeight: value }) },
          }),
          h(SliderRow, {
            key: 'opacity',
            label: '不透明度',
            hint: '低于 100% 时窗口略微透明',
            value: settings.opacity,
            min: 0.6,
            max: 1,
            step: 0.05,
            // Dragging writes straight through (throttled) so the window follows the
            // thumb; releasing commits the final value and updates the note.
            onLive: (value) => { save({ opacity: Math.min(1, Math.max(0.6, Math.round(value * 100) / 100)) }, true) },
            onChange: (value) => { save({ opacity: Math.min(1, Math.max(0.6, Math.round(value * 100) / 100)) }) },
          }),
        ]),
        h('div', { key: 'behaviour', style: S.card }, [
          h('div', { key: 'h', style: S.cardTitle }, '行为'),
          h(ToggleRow, {
            key: 'ephemeral',
            label: '关闭窗口时结束对话',
            hint: '即用即焚：关闭浮窗即删除本次侧边对话的记录',
            value: settings.ephemeralByDefault,
            onChange: (value) => { save({ ephemeralByDefault: value }) },
          }),
          h(ToggleRow, {
            key: 'title',
            label: '标题栏显示会话名称',
            hint: '关闭后标题栏只显示「悬浮窗」',
            value: settings.showSessionTitle,
            onChange: (value) => { save({ showSessionTitle: value }) },
          }),
          h(ToggleRow, {
            key: 'esc',
            label: '按 Esc 关闭窗口',
            hint: '窗口内没有打开菜单时生效',
            value: settings.closeOnEscape,
            onChange: (value) => { save({ closeOnEscape: value }) },
          }),
        ]),
      ]

      // 缓存与垃圾：数字来自宿主 fs（GET /stats），清理走 POST /clean。跟悬浮窗
      // 标题栏上的「清理」是同一件事，两边都能发起。
      blocks.push(h('div', { key: 'garbage', style: S.card }, [
        h('div', { key: 't', style: S.cardTitle }, '缓存与日志'),
        h('div', { key: 'row', style: S.row }, [
          h('span', { key: 'labels', style: S.labels }, [
            h('span', { key: 'name', style: S.name }, garbage === null
              ? '浏览器缓存与日志'
              : '浏览器缓存 ' + fmtBytes(garbage.cacheBytes) + ' · 日志 ' + fmtBytes(garbage.logBytes)),
            h('span', { key: 'hint', style: S.hint }, garbage === null
              ? '宿主尚未提供大小（重启一次 DSH 后可用）'
              : '缓存来自窗口内嵌的浏览器（网页资源与编译结果），日志是本窗口的诊断记录；'
                + '两者都不含 Cookie 与对话数据，日志超过 ' + fmtBytes(garbage.logCapBytes) + ' 自动轮转。'),
          ]),
          h('span', { key: 'controls', style: S.controls },
            h(Button, {
              key: 'clean',
              size: 'sm',
              variant: 'outline',
              disabled: cleaning || garbage === null,
              onClick: cleanNow,
            }, cleaning ? '清理中…' : '清理')),
        ]),
        cleanNote === '' ? null : h('p', { key: 'note', style: S.note }, cleanNote),
      ]))

      if (state.note !== '') {
        blocks.push(h('p', { key: 'note', style: S.note }, state.note))
      }
      blocks.push(h('div', { key: 'actions', style: S.footer }, [
        h(Button, {
          key: 'reset',
          size: 'sm',
          variant: 'outline',
          disabled: state.defaults === undefined,
          onClick: () => { if (state.defaults !== undefined) save(state.defaults) },
        }, '恢复默认设置'),
        h(Button, { key: 'reload', size: 'sm', onClick: load }, '刷新'),
      ]))

      return h('div', { style: S.root }, blocks)
    }

    /** A render failure inside this panel must not reach the settings shell. */
    class Boundary extends React.Component {
      constructor(props) {
        super(props)
        this.state = { error: '' }
      }
      static getDerivedStateFromError(error) {
        return { error: String((error && error.message) || error) }
      }
      render() {
        if (this.state.error) {
          return h('p', { style: { font: 'inherit', opacity: 0.8 } },
            '悬浮窗设置加载出错，不影响其他设置：' + this.state.error)
        }
        return this.props.children
      }
    }

    function FloatSettingsSection() {
      return h(Boundary, null, h(FloatSettingsPanel, null))
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        /*
         * Track DSH's appearance the official way: read the ui-theme service now
         * and re-read it on every `theme/change`, reporting each answer to the
         * Host's theme channel (which the floating window polls). The window then
         * looks like DSH — the same source the Appearance row itself uses.
         */
        ctx.effect(() => {
          findThemeService(ctx)
          reportTheme(ctx)
          let off
          try {
            off = ctx.on('theme/change', () => { reportTheme(ctx) })
          } catch (error) {
            off = undefined
          }
          return () => {
            if (typeof off === 'function') off()
          }
        }, 'dsh-float-chat: appearance channel')

        installSettingsNavIcon(ctx)

        ctx.slots.inject('conversation.input.right', () => ctx.slots.register({
          name: 'conversation.input.right',
          id: 'dsh-float-chat',
          order: 40,
        }, FloatChatButton))

        // Same seat 会话管理 uses: one settings page of its own in the nav.
        ctx.slots.inject('settings.section', () => ctx.slots.register({
          name: 'settings.section',
          id: 'dsh-float-chat',
          order: 95,
          label: SECTION_LABEL,
        }, FloatSettingsSection))
      },
    }
  },
})
