// dsh-float-chat floating window.
//
// A frameless, always-on-top WinForms window that hosts the side-chat page in
// WebView2. It is a separate process on purpose: the DSH renderer cannot open
// native windows, and the window has to keep floating over other applications
// while the DSH window is behind, minimized or hidden.
//
// Usage: dsh-float-window.exe --dsh-auth-url=<token url> --dsh-page-url=<page url> [--dsh-title=<text>]
//
// Compiled with the .NET Framework csc (C# 5): no string interpolation, no
// null-conditional operators, no expression-bodied members.

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace DshFloatChat
{
    internal static class Program
    {
        /**
         * The display runs at a scale factor (150% here). An unaware process is
         * rendered at 100% and then bitmap-stretched by the compositor — which is
         * exactly why this window looked blurry next to the DSH page. Per-monitor
         * DPI awareness makes the browser surface render at the real scale, so
         * text is as crisp as the rest of the desktop.
         */
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private static readonly string LogPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "window-log.txt");

        /// <summary>Startup diagnostics: a window that cannot open must say why.</summary>
        internal static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        /**
         * 日志上限：`window-log.txt` 超过 18 MB 就在启动时轮转——当前的改名成
         * `window-log.1.txt`，原来的 `.1` 再退成 `.2`，`.2` 丢掉。三份合计约 54 MB 封顶，
         * 按现在的用量够装很久，又不像"只留一份"那样一超就把上一次的记录冲掉。
         * 轮转失败（文件被占用等）只会被记一笔，日志继续追加，绝不影响窗口启动。
         */
        private const long LogRotateBytes = 18 * 1024 * 1024;

        private static void RotateLogIfHuge()
        {
            try
            {
                if (!File.Exists(LogPath)) return;
                if (new FileInfo(LogPath).Length < LogRotateBytes) return;
                string folder = Path.GetDirectoryName(LogPath) ?? ".";
                string oldest = Path.Combine(folder, "window-log.2.txt");
                string previous = Path.Combine(folder, "window-log.1.txt");
                if (File.Exists(oldest)) File.Delete(oldest);
                if (File.Exists(previous)) File.Move(previous, oldest);
                File.Move(LogPath, previous);
            }
            catch (Exception error)
            {
                Log("log rotation skipped: " + error.Message);
            }
        }

        /**
         * 浏览器缓存目录（相对 `webview-data`）：全都是纯缓存，删掉只是下次打开时重新
         * 生成、稍慢一点；Cookie 与 Local Storage 不在其中，登录与对话关联不受影响。
         */
        private static readonly string[] CacheFolders = new string[]
        {
            @"EBWebView\Default\Cache",
            @"EBWebView\Default\Code Cache",
            @"EBWebView\Default\GPUCache",
            @"EBWebView\Default\DawnGraphiteCache",
            @"EBWebView\Default\DawnWebGPUCache",
            @"EBWebView\GrShaderCache",
            @"EBWebView\ShaderCache",
        };

        private static string AppFolder
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "."; }
        }

        /** 上次没清完的缓存（浏览器还在跑时删不掉）：关窗后或下次启动时补清。 */
        private static string PendingCleanPath
        {
            get { return Path.Combine(AppFolder, "clean-pending.txt"); }
        }

        private static long FolderBytes(string path)
        {
            long total = 0;
            try
            {
                string[] files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                for (int index = 0; index < files.Length; index++)
                {
                    try { total += new FileInfo(files[index]).Length; }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return total;
        }

        /** 缓存当前占用（字节）。 */
        internal static long CacheBytes()
        {
            long total = 0;
            string root = Path.Combine(AppFolder, "webview-data");
            for (int index = 0; index < CacheFolders.Length; index++)
            {
                total += FolderBytes(Path.Combine(root, CacheFolders[index]));
            }
            return total;
        }

        internal static long LogBytes()
        {
            try { return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0; }
            catch (Exception) { return 0; }
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        /**
         * 清浏览器缓存 + 轮转超限的日志，返回一句可以直接显示给用户的结果。
         * 浏览器正在跑的时候 `Cache` / `Code Cache` 里的文件被它占着删不掉——那就留一张
         * `clean-pending.txt`，等窗口关闭（浏览器已退出）或下次启动（浏览器还没起来）
         * 时补清；这两个时机都会调 CleanPendingCache。
         */
        internal static string CleanCacheAndLog()
        {
            long freed = 0;
            int locked = 0;
            string root = Path.Combine(AppFolder, "webview-data");
            for (int index = 0; index < CacheFolders.Length; index++)
            {
                string folder = Path.Combine(root, CacheFolders[index]);
                if (!Directory.Exists(folder)) continue;
                long size = FolderBytes(folder);
                try
                {
                    Directory.Delete(folder, true);
                    freed += size;
                }
                catch (Exception)
                {
                    // 被浏览器占着：留给关窗后的补清。
                    locked += 1;
                }
            }
            bool rotated = false;
            try
            {
                if (LogBytes() >= LogRotateBytes)
                {
                    RotateLogIfHuge();
                    rotated = true;
                }
            }
            catch (Exception) { }
            try
            {
                if (locked > 0) File.WriteAllText(PendingCleanPath, "pending");
                else if (File.Exists(PendingCleanPath)) File.Delete(PendingCleanPath);
            }
            catch (Exception) { }
            if (freed <= 0 && !rotated)
            {
                return locked > 0 ? "缓存正被浮窗占用，关闭窗口后自动清理" : "没有需要清理的内容";
            }
            string text = "已清理 " + Mb(freed);
            if (rotated) text += "，日志已轮转";
            if (locked > 0) text += "；另有 " + locked + " 项将在关闭窗口后清理";
            return text;
        }

        /** 关窗后 / 下次启动时把欠下的清理补上。 */
        private static void CleanPendingCache()
        {
            try
            {
                if (!File.Exists(PendingCleanPath)) return;
                Program.Log("pending clean: " + CleanCacheAndLog());
            }
            catch (Exception error)
            {
                Program.Log("pending clean failed: " + error.Message);
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string authUrl = null;
            string pageUrl = null;
            string title = "\u60ac\u6d6e\u7a97";
            string headerName = null;
            string headerValue = null;

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log("unhandled: " + Convert.ToString(e.ExceptionObject));
            };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Log("thread exception: " + e.Exception);
            };
            RotateLogIfHuge();
            // 上次关窗时没清完的缓存：此刻浏览器还没起来，文件没被占，能删干净。
            CleanPendingCache();
            Log("start args=" + string.Join(" | ", args));
            // Belt and braces: the manifest declares PerMonitorV2, this call keeps
            // the runtime API path too, and either way the result is logged.
            try
            {
                bool applied = SetProcessDpiAwarenessContext(new IntPtr(-4));
                Log("dpi awareness api applied=" + applied);
            }
            catch (Exception error)
            {
                Log("dpi awareness api unavailable: " + error.Message);
            }

            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (arg.StartsWith("--dsh-auth-url=", StringComparison.Ordinal))
                {
                    authUrl = arg.Substring("--dsh-auth-url=".Length);
                }
                else if (arg.StartsWith("--dsh-page-url=", StringComparison.Ordinal))
                {
                    pageUrl = arg.Substring("--dsh-page-url=".Length);
                }
                else if (arg.StartsWith("--dsh-title=", StringComparison.Ordinal))
                {
                    title = arg.Substring("--dsh-title=".Length);
                }
                else if (arg.StartsWith("--dsh-header-name=", StringComparison.Ordinal))
                {
                    headerName = arg.Substring("--dsh-header-name=".Length);
                }
                else if (arg.StartsWith("--dsh-header-value=", StringComparison.Ordinal))
                {
                    headerValue = arg.Substring("--dsh-header-value=".Length);
                }
            }

            if (string.IsNullOrEmpty(pageUrl)) return;

            /*
             * 主题：深浅两套外观，**以 DSH 自己的外观偏好为准**（用户在 DSH 设置里选的
             * 浅色 / 深色 / 跟随系统，落在 profile 的 cordis.patch.yml 里）。窗口活着的时候
             * 每秒核对一次这个文件，用户在 DSH 里一换，标题栏、手柄和页面当场一起换色；
             * 偏好是“跟随系统”时才看 Windows 的 AppsUseLightTheme（WM_SETTINGCHANGE）。
             * 桥放在页面 URL 里的 theme= 只是读不到设置文件时的兜底。
             */
            string theme = ParseTheme(pageUrl);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FloatWindowForm(authUrl, pageUrl, title, headerName, headerValue, theme));
            // 窗口关掉、浏览器进程退出之后，欠下的缓存清理这时才删得动。
            System.Threading.Thread.Sleep(600);
            CleanPendingCache();
        }

        /** 桥放在页面 URL 里的 theme=light|dark，仅当读不到 DSH 设置文件时的兜底。 */
        private static string ParseTheme(string pageUrl)
        {
            Match match = Regex.Match(pageUrl, "[?&]theme=(light|dark)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            return match.Groups[1].Value.ToLowerInvariant();
        }
    }

    internal sealed class FloatWindowForm : Form
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        /** 系统改了设置就广播 WM_SETTINGCHANGE（换深浅色时 lParam 是 ImmersiveColorSet）。 */
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_THEMECHANGED = 0x031A;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly string authUrl;
        private readonly string pageUrl;
        private readonly string rendererHeaderName;
        private readonly string rendererHeaderValue;
        private readonly string statePath;
        /** Windows 11 corner rounding for this frameless window. */
        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /** Custom corner radius: the system's own rounding is smaller than requested. */
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        private const int CORNER_RADIUS = 26;

        private readonly Timer saveTimer;
        /** 每秒核对 DSH 外观偏好的看门定时器。 */
        private readonly Timer themeTimer;
        /** 设置文件的监视器：设置页一改就立刻生效，不用等下一次轮询。 */
        private FileSystemWatcher settingsWatcher;
        private WebView2 webView;
        private Label status;
        private IconButton pinButton;
        private IconButton cleanButton;
        private IconButton newThreadButton;
        private IconButton closeButton;
        private IconButton minimizeButton;
        private Label captionLabel;
        private Panel headerPanel;
        private Panel controlsPanel;
        private Panel gripRight;
        private Panel gripLeft;
        /** 当前生效的调色板：窗口里每一个有色像素都从这里取。 */
        private ThemePalette palette;
        /** 打开时 URL 带来的外观；只在读不到 DSH 设置文件时当兜底。 */
        private string themeOverride;
        /** DSH 设置文件的位置与上次看到的修改时间。 */
        private string cachedThemePath;
        private DateTime themeFileStamp = DateTime.MinValue;
        /** 窗口自己的设置（宿主半写的 float-settings.json）与它的位置/时间戳。 */
        private WindowSettings prefs = new WindowSettings();
        private string settingsPath;
        private DateTime settingsFileStamp = DateTime.MinValue;
        private bool lightTheme;
        private Color themePageFill;
        private Color themeHeaderFill;
        private bool resizing;
        private bool resizeFromLeft;
        private bool resizeFromTop;
        private const int GRIP_SIZE = 58;
        private Point resizeOrigin = Point.Empty;
        private Point boundsStart = Point.Empty;
        private Size resizeStart = Size.Empty;
        private bool started;
        private bool closingAfterCleanup;

        /**
         * 一套主题的全部颜色。深色和浅色各一份，取值与 page/float.html 里的
         * --bg / --layer / --hover / --hairline / --accent / --danger 一一对应，
         * 这样原生标题栏和页面内容在同一个主题下不会出现色差。
         */
        private sealed class ThemePalette
        {
            internal Color PageFill;        // 页面底色
            internal Color HeaderFill;      // 标题栏底色
            internal Color FormBack;        // 窗口本体（圆角以外那一圈）
            internal Color CaptionText;     // 标题文字
            internal Color ButtonText;      // ＋ 号
            internal Color DimText;         // — ✕ 与未置顶的 ◉
            internal Color Accent;          // 置顶 / 按下高亮
            internal Color ButtonHover;
            internal Color ButtonDown;
            internal Color Danger;          // 状态行报错
            internal Color GripIdleHeader;  // 缩放手柄：压在标题栏上的那部分
            internal Color GripHoverHeader;
            internal Color GripIdlePage;    // 缩放手柄：压在页面上的那部分
            internal Color GripHoverPage;
            internal Color GripArc;         // 手柄的 L 形括号
        }

        /** 深色档：与原观感逐色一致。 */
        private static ThemePalette DarkPalette()
        {
            ThemePalette palette = new ThemePalette();
            palette.PageFill = Color.FromArgb(15, 17, 21);
            palette.HeaderFill = Color.FromArgb(30, 30, 33);
            palette.FormBack = Color.FromArgb(22, 22, 24);
            palette.CaptionText = Color.FromArgb(233, 233, 236);
            palette.ButtonText = Color.FromArgb(170, 176, 186);
            palette.DimText = Color.FromArgb(154, 154, 164);
            palette.Accent = Color.FromArgb(75, 139, 245);
            palette.ButtonHover = Color.FromArgb(52, 52, 58);
            palette.ButtonDown = Color.FromArgb(64, 64, 70);
            palette.Danger = Color.FromArgb(229, 83, 75);
            palette.GripIdleHeader = Color.FromArgb(30, 30, 33);
            palette.GripHoverHeader = Color.FromArgb(44, 44, 51);
            palette.GripIdlePage = Color.FromArgb(15, 17, 21);
            palette.GripHoverPage = Color.FromArgb(30, 32, 38);
            palette.GripArc = Color.FromArgb(232, 236, 244);
            return palette;
        }

        /** 浅色档：白页面 + 浅灰标题栏，文字转深；手柄括号也必须转深，否则白底上看不见。 */
        private static ThemePalette LightPalette()
        {
            ThemePalette palette = new ThemePalette();
            palette.PageFill = Color.FromArgb(255, 255, 255);
            palette.HeaderFill = Color.FromArgb(245, 246, 247);
            palette.FormBack = Color.FromArgb(245, 246, 247);
            palette.CaptionText = Color.FromArgb(15, 17, 21);
            palette.ButtonText = Color.FromArgb(88, 94, 102);
            palette.DimText = Color.FromArgb(110, 116, 124);
            palette.Accent = Color.FromArgb(65, 118, 230);
            palette.ButtonHover = Color.FromArgb(233, 235, 238);
            palette.ButtonDown = Color.FromArgb(221, 224, 229);
            palette.Danger = Color.FromArgb(217, 45, 32);
            palette.GripIdleHeader = Color.FromArgb(245, 246, 247);
            palette.GripHoverHeader = Color.FromArgb(230, 233, 238);
            palette.GripIdlePage = Color.FromArgb(255, 255, 255);
            palette.GripHoverPage = Color.FromArgb(240, 242, 245);
            palette.GripArc = Color.FromArgb(92, 99, 110);
            return palette;
        }

        /** 系统“应用”深浅色：AppsUseLightTheme 为 0 表示深色（读不到也按深色）。 */
        private static bool SystemUsesLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object value = key.GetValue("AppsUseLightTheme");
                    if (value is int) return ((int)value) != 0;
                }
            }
            catch (Exception error)
            {
                Program.Log("system theme unreadable: " + error.Message);
            }
            return false;
        }

        /**
         * 找到 DSH 存外观偏好的那个文件。
         *
         * DSH 的用户设置按 profile 落盘成 patch 层（`~/.dsh/profiles/<profile>/cordis.patch.yml`），
         * 里面 `- id: ui-theme` 那一段就是浅色 / 深色 / 跟随系统；用户在 DSH 设置里一改，
         * 这个文件当场被重写（实测：改成浅色后文件 mtime 立即更新）。取最近改过的、且带
         * ui-theme 段的那个 profile，多 profile 下也就是当前在用的那个；都没有就退回
         * 旧的 `~/.dsh/settings.yaml`。
         */
        private string FindDshSettingsFile()
        {
            if (cachedThemePath != null && File.Exists(cachedThemePath)) return cachedThemePath;
            string best = null;
            DateTime newest = DateTime.MinValue;
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dsh = Path.Combine(home, ".dsh");
                string profiles = Path.Combine(dsh, "profiles");
                if (Directory.Exists(profiles))
                {
                    string[] dirs = Directory.GetDirectories(profiles);
                    for (int index = 0; index < dirs.Length; index++)
                    {
                        string candidate = Path.Combine(dirs[index], "cordis.patch.yml");
                        if (!File.Exists(candidate)) continue;
                        string text;
                        try { text = File.ReadAllText(candidate); }
                        catch (Exception) { continue; }
                        if (text.IndexOf("ui-theme", StringComparison.Ordinal) < 0) continue;
                        DateTime written = File.GetLastWriteTimeUtc(candidate);
                        if (best == null || written > newest)
                        {
                            best = candidate;
                            newest = written;
                        }
                    }
                }
                if (best == null)
                {
                    string legacy = Path.Combine(dsh, "settings.yaml");
                    if (File.Exists(legacy)) best = legacy;
                }
            }
            catch (Exception error)
            {
                Program.Log("dsh settings lookup failed: " + error.Message);
            }
            cachedThemePath = best;
            if (best != null) Program.Log("dsh settings file: " + best);
            return best;
        }

        /**
         * DSH 当前的外观偏好："light" / "dark" / "system"；读不到就是 null。
         * 只认 ui-theme 那一段的 `preference:`，不碰文件里别人的设置。
         */
        private string ReadDshThemePreference()
        {
            string path = FindDshSettingsFile();
            if (path == null) return null;
            try
            {
                themeFileStamp = File.GetLastWriteTimeUtc(path);
                string[] lines = File.ReadAllLines(path);
                int headerIndent = -1;
                for (int index = 0; index < lines.Length; index++)
                {
                    string raw = lines[index];
                    string trimmed = raw.Trim();
                    int indent = raw.Length - raw.TrimStart(' ').Length;
                    if (headerIndent < 0)
                    {
                        // 两种写法都认：profile patch 层的 `- id: ui-theme`，
                        // 以及旧 settings.yaml 的 `ui-theme:`。
                        if (trimmed == "- id: ui-theme" || trimmed == "ui-theme:") headerIndent = indent;
                        continue;
                    }
                    if (trimmed.Length == 0) continue;
                    if (indent <= headerIndent) return "system";   // 这一段结束了，没写 preference
                    if (trimmed.StartsWith("preference:", StringComparison.Ordinal))
                    {
                        string value = trimmed.Substring("preference:".Length).Trim();
                        return value.Trim('"', '\'').ToLowerInvariant();
                    }
                }
                return "system";
            }
            catch (Exception error)
            {
                Program.Log("dsh theme read failed: " + error.Message);
                return null;
            }
        }

        /**
         * 主题解析顺序：**DSH 的外观偏好是权威**——它就是用户正在看的那套界面；
         * 偏好是 `system` 时才看 Windows 的“应用”深浅色；设置文件读不到才退回打开时
         * URL 带来的值，再不行才用 Windows。
         */
        private bool ResolveLightTheme()
        {
            // 设置页里的「配色方案」最优先：固定浅/深色、或明确跟随 Windows 时，都不再看 DSH。
            if (prefs.Appearance == "light") return true;
            if (prefs.Appearance == "dark") return false;
            if (prefs.Appearance == "system") return SystemUsesLightTheme();
            string preference = ReadDshThemePreference();
            if (preference == "light") return true;
            if (preference == "dark") return false;
            if (preference == null && themeOverride != null) return themeOverride == "light";
            return SystemUsesLightTheme();
        }

        /** 按当前解析结果换色（设置文件 / 打开时的 URL / Windows，见 ResolveLightTheme）。 */
        private void SyncTheme(string reason)
        {
            ApplyTheme(ResolveLightTheme(), reason);
        }

        /** 真正换色的地方：只有值变了才重铺，所以可以随便反复调。 */
        private void ApplyTheme(bool light, string reason)
        {
            if (palette != null && light == lightTheme) return;
            lightTheme = light;
            palette = light ? LightPalette() : DarkPalette();
            Program.Log("theme -> " + (light ? "light" : "dark") + " (" + reason + ")");
            ApplyPalette();
        }

        /**
         * 每秒看一眼 DSH 的设置文件：用户在 DSH 里换外观，这个窗口当场跟着换——
         * 不用重开窗口，也不用把 Windows 一起切换。文件没动就什么都不做。
         */
        private void PollThemeFile()
        {
            string path = FindDshSettingsFile();
            if (path == null)
            {
                SyncTheme("dsh settings missing");
                return;
            }
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return; }
            if (stamp == themeFileStamp) return;
            Program.Log("dsh settings changed; re-reading theme");
            SyncTheme("dsh settings changed");
        }

        /* ── 窗口自己的设置：宿主半写的 float-settings.json ────────────────────── */

        /**
         * 一份设置的形状，与宿主半 host.js 里的 SETTINGS_DEFAULTS 一一对应。
         * 文件是扁平 JSON，这里按 key 取值；读不到或读坏了就整份退回默认值——
         * 一份坏文件绝不能让窗口起不来。
         */
        private sealed class WindowSettings
        {
            internal string Appearance = "dsh";       // dsh = 跟随 DSH；system = 跟随 Windows；light/dark = 固定
            internal bool AlwaysOnTop = true;
            internal bool RememberGeometry = true;
            internal double Opacity = 1;
            internal int DefaultWidth = 620;
            internal int DefaultHeight = 900;
            internal bool EphemeralByDefault = false;
            internal bool ShowSessionTitle = true;
            internal bool CloseOnEscape = false;
        }

        private static string JsonString(string text, string key)
        {
            Match match = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static bool JsonBool(string text, string key, bool fallback)
        {
            Match match = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(true|false)");
            if (!match.Success) return fallback;
            return match.Groups[1].Value == "true";
        }

        private static double JsonNumber(string text, string key, double fallback)
        {
            Match match = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!match.Success) return fallback;
            double parsed;
            return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        /** 读设置文件；文件不存在（还没在设置页里改过任何东西）就是默认值。 */
        private void LoadSettings()
        {
            WindowSettings next = new WindowSettings();
            try
            {
                if (settingsPath != null && File.Exists(settingsPath))
                {
                    settingsFileStamp = File.GetLastWriteTimeUtc(settingsPath);
                    string text = File.ReadAllText(settingsPath);
                    string appearance = JsonString(text, "appearance");
                    if (appearance == "auto") appearance = "dsh";   // v0.4.0 的旧名字
                    if (appearance == "light" || appearance == "dark" || appearance == "dsh" || appearance == "system") next.Appearance = appearance;
                    next.AlwaysOnTop = JsonBool(text, "alwaysOnTop", next.AlwaysOnTop);
                    next.RememberGeometry = JsonBool(text, "rememberGeometry", next.RememberGeometry);
                    next.Opacity = Math.Min(1.0, Math.Max(0.6, JsonNumber(text, "opacity", next.Opacity)));
                    next.DefaultWidth = (int)Math.Round(Math.Min(1600.0, Math.Max(320.0, JsonNumber(text, "defaultWidth", next.DefaultWidth))));
                    next.DefaultHeight = (int)Math.Round(Math.Min(1600.0, Math.Max(260.0, JsonNumber(text, "defaultHeight", next.DefaultHeight))));
                    next.EphemeralByDefault = JsonBool(text, "ephemeralByDefault", next.EphemeralByDefault);
                    next.ShowSessionTitle = JsonBool(text, "showSessionTitle", next.ShowSessionTitle);
                    next.CloseOnEscape = JsonBool(text, "closeOnEscape", next.CloseOnEscape);
                }
            }
            catch (Exception error)
            {
                Program.Log("float settings unreadable: " + error.Message);
            }
            prefs = next;
        }

        /** 每秒核对一次设置文件：设置页里一改，窗口当场跟着变。 */
        private void PollSettingsFile()
        {
            if (settingsPath == null || !File.Exists(settingsPath)) return;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(settingsPath); }
            catch (Exception) { return; }
            if (stamp == settingsFileStamp) return;
            LoadSettings();
            Program.Log("float settings changed; appearance=" + prefs.Appearance
                + " pin=" + prefs.AlwaysOnTop
                + " opacity=" + prefs.Opacity.ToString(CultureInfo.InvariantCulture));
            ApplyLiveSettings();
            SyncTheme("float settings changed");
            PushPageSettings();
        }

        /**
         * 设置文件被改的瞬间就重读：宿主半用 tmp+rename 落盘，所以盯目录 + 过滤文件名，
         * Changed / Created / Renamed 都要接。事件来自线程池线程，得丢回 UI 线程再动控件；
         * 每秒一次的轮询留着当兜底（监视器漏事件时最多晚一秒）。
         */
        private void NudgeThemeNow()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(delegate { PollSettingsFile(); PollThemeFile(); }));
            }
            catch (Exception)
            {
                // 关窗过程中事件到达是正常的，丢掉即可。
            }
        }

        /** 装上监视器；装不上就只靠轮询，功能不受影响。 */
        private void ArmSettingsWatcher()
        {
            try
            {
                string folder = Path.GetDirectoryName(settingsPath);
                if (folder == null || !Directory.Exists(folder)) return;
                settingsWatcher = new FileSystemWatcher(folder, "float-settings.json");
                settingsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                FileSystemEventHandler changed = delegate { NudgeThemeNow(); };
                settingsWatcher.Changed += changed;
                settingsWatcher.Created += changed;
                settingsWatcher.Renamed += delegate(object sender, RenamedEventArgs args) { NudgeThemeNow(); };
                settingsWatcher.EnableRaisingEvents = true;
                Program.Log("settings watcher armed on " + folder);
            }
            catch (Exception error)
            {
                Program.Log("settings watcher unavailable: " + error.Message);
            }
        }

        /** 运行期能立刻改的那几项（几何与初始尺寸只在下一次开窗时生效）。 */
        private void ApplyLiveSettings()
        {
            TopMost = prefs.AlwaysOnTop;
            if (pinButton != null)
            {
                pinButton.Text = TopMost ? "\u25C9" : "\u25CB";
                pinButton.ForeColor = TopMost ? palette.Accent : palette.DimText;
            }
            Opacity = prefs.Opacity;
        }

        /** 把页面要用的那几项推给它：即用即焚默认值、标题栏策略、Esc 行为。 */
        private void PushPageSettings()
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.PostWebMessageAsJson(
                        "{\"type\":\"settings\",\"settings\":{"
                        + "\"ephemeralByDefault\":" + (prefs.EphemeralByDefault ? "true" : "false")
                        + ",\"showSessionTitle\":" + (prefs.ShowSessionTitle ? "true" : "false")
                        + ",\"closeOnEscape\":" + (prefs.CloseOnEscape ? "true" : "false")
                        + "}}");
                }
            }
            catch (Exception error)
            {
                Program.Log("settings push failed: " + error.Message);
            }
        }

        /** 把一条 JSON 推给页面；失败只记日志。 */
        private void PostToPage(string json)
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null) webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception error)
            {
                Program.Log("page push failed: " + error.Message);
            }
        }

        /** 标题栏「清理」按钮 / 页面请求：清完把结果推回页面显示一条提示。 */
        private void CleanFromWindow()
        {
            // 点击反馈：图标先闪一下红再做事——这是唯一会删东西的按钮，值得一个红色信号。
            // 立刻红（不渐入）、停 DangerHoldMs、再按 dangerFadeMs 渐变淡回。
            cleanButton.FlashDanger(IconButton.DangerHoldMs);
            string summary = Program.CleanCacheAndLog();
            Program.Log("clean: " + summary);
            PostToPage("{\"type\":\"clean-result\",\"text\":" + JsonString(summary) + "}");
        }

        /** 把缓存占用推给页面（提示条要显示"现在多大"）。 */
        private void PushCacheStats()
        {
            PostToPage("{\"type\":\"cache-stats\",\"cacheBytes\":" + Program.CacheBytes().ToString(CultureInfo.InvariantCulture)
                + ",\"logBytes\":" + Program.LogBytes().ToString(CultureInfo.InvariantCulture)
                + "}");
        }

        /** 最小 JSON 字符串转义：我们造的那几句话里不会有换行。 */
        private static string JsonString(string text)
        {
            string safe = (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + safe + "\"";
        }

        /** 把调色板铺到标题栏、按钮、手柄、WebView 底色上，并让页面同步换色。 */
        private void ApplyPalette()
        {
            if (palette == null) return;
            themePageFill = palette.PageFill;
            themeHeaderFill = palette.HeaderFill;
            BackColor = palette.FormBack;
            if (headerPanel != null) headerPanel.BackColor = palette.HeaderFill;
            if (controlsPanel != null) controlsPanel.BackColor = palette.HeaderFill;
            if (captionLabel != null) captionLabel.ForeColor = palette.CaptionText;
            // 五颗自绘按钮：前景色（含置顶的状态色）与动画用的三种底色一起更新。
            Restyle(newThreadButton, palette.ButtonText);
            Restyle(closeButton, palette.DimText);
            Restyle(minimizeButton, palette.DimText);
            Restyle(pinButton, TopMost ? palette.Accent : palette.DimText);
            Restyle(cleanButton, palette.DimText);
            if (webView != null) webView.DefaultBackgroundColor = palette.PageFill;
            if (status != null) status.ForeColor = palette.Danger;
            if (gripLeft != null)
            {
                gripLeft.BackColor = palette.GripIdleHeader;
                gripLeft.Invalidate();
            }
            if (gripRight != null)
            {
                gripRight.BackColor = palette.GripIdlePage;
                gripRight.Invalidate();
            }
            ApplyWebViewScheme();
            PushPageTheme();
            Invalidate();
        }

        /** WebView2 自己的 prefers-color-scheme：页面里的滚动条、表单控件跟着同色。 */
        private void ApplyWebViewScheme()
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.Profile.PreferredColorScheme =
                        lightTheme ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
                }
            }
            catch (Exception error)
            {
                Program.Log("preferred color scheme unavailable: " + error.Message);
            }
        }

        /** 页面的那一半：把当前主题推给 float.html（它一直在监听桥消息）。 */
        private void PushPageTheme()
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.PostWebMessageAsJson(
                        "{\"type\":\"theme\",\"theme\":\"" + (lightTheme ? "light" : "dark") + "\"}");
                }
            }
            catch (Exception error)
            {
                Program.Log("theme push failed: " + error.Message);
            }
        }

        /**
         * 页面推上来的主题：它读的是宿主官方接口（DSH 的 ui-theme 设置，宿主没这个路由时
         * 根本不会推）。只认 light/dark，认不出来就保持现状——畸形消息不许把颜色搅乱。
         */
        private void ApplyObservedTheme(string payload)
        {
            Match match = Regex.Match(payload, "\"theme\"\\s*:\\s*\"(light|dark)\"");
            if (!match.Success) return;
            ApplyTheme(match.Groups[1].Value == "light", "page (dsh theme)");
        }

        /**
         * 标题用 DSH 自己那套界面字体的中文档位。DSH 的字体栈是
         * `-apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Hiragino Sans GB",
         * "Microsoft YaHei", …`；标题里是中文会话名，用 "Segoe UI" 会掉进 GDI 的字体回退，
         * 字形与字重都不是设计值（这就是"看着丑"的来源），所以直接取微软雅黑 UI 这一档。
         */
        private static Font UiFont(float size, FontStyle style)
        {
            string[] preferred = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            for (int index = 0; index < preferred.Length; index++)
            {
                try
                {
                    FontFamily[] families = FontFamily.Families;
                    for (int scan = 0; scan < families.Length; scan++)
                    {
                        if (families[scan].Name == preferred[index]) return new Font(families[scan], size, style);
                    }
                }
                catch (Exception)
                {
                    // 字体探测失败就继续往下退，不能让窗口起不来。
                }
            }
            return new Font("Segoe UI", size, style);
        }

        /** 双缓冲的 Panel：缩放时标题栏不能闪。 */
        private sealed class BufferedPanel : Panel
        {
            internal BufferedPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }

        /** 双缓冲的 Label：标题文字同理。 */
        private sealed class BufferedLabel : Label
        {
            internal BufferedLabel()
            {
                DoubleBuffered = true;
            }
        }

        internal FloatWindowForm(string authUrl, string pageUrl, string title, string headerName, string headerValue, string theme)
        {
            this.authUrl = authUrl;
            this.pageUrl = pageUrl;
            this.rendererHeaderName = headerName;
            this.rendererHeaderValue = headerValue;
            this.statePath = Path.Combine(AppDir, "window-state.txt");
            this.settingsPath = Path.Combine(AppDir, "float-settings.json");
            this.themeOverride = theme;
            // 设置先读：调色板、置顶、透明度、几何策略都由它决定。
            LoadSettings();
            // 先定调色板，后面每一块控件都按它上色。
            SyncTheme("startup");

            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            // 置顶与不透明度来自设置页；几何策略同理（见 ApplySavedBounds）。
            TopMost = prefs.AlwaysOnTop;
            Opacity = prefs.Opacity;
            MinimumSize = new Size(320, 260);
            BackColor = palette.FormBack;
            StartPosition = FormStartPosition.Manual;
            ApplySavedBounds();

            Color headerFill = palette.HeaderFill;
            Color textColor = palette.CaptionText;
            Color dimColor = palette.DimText;
            Font uiFont = UiFont(9.75F, FontStyle.Regular);

            // 整窗双缓冲 + 下面各子控件也双缓冲：拖角缩放时标题栏不再一闪一闪。
            DoubleBuffered = true;

            // ---- header (drag handle + window controls) ----
            headerPanel = new BufferedPanel();
            Panel header = headerPanel;
            header.Dock = DockStyle.Fill;
            header.BackColor = headerFill;
            header.MouseDown += OnHeaderMouseDown;
            header.Cursor = Cursors.SizeAll;

            Label caption = new BufferedLabel();
            captionLabel = caption;
            caption.Text = title;
            caption.ForeColor = textColor;
            // 跟 DSH 自己的界面字体一致：微软雅黑 UI、常规字重（不再用合成粗体）。
            caption.Font = UiFont(9.75F, FontStyle.Regular);
            caption.AutoSize = false;
            caption.Dock = DockStyle.Fill;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Padding = new Padding(42, 0, 0, 0);
            caption.MouseDown += OnHeaderMouseDown;
            // 标题栏可以拖动：光标直接说明这一点，不用试。
            caption.Cursor = Cursors.SizeAll;

            // 五颗按钮共用同一套「自绘图标 + 过渡动画」：字形不再由字体决定，
            // 悬停/按下是渐变而不是跳变。
            IconButton newThread = MakeIconButton(PaintPlus, palette.ButtonText);
            newThreadButton = newThread;
            newThread.Click += delegate { StartNewThread(); };
            IconButton close = MakeIconButton(PaintCross, dimColor);
            closeButton = close;
            close.Click += delegate { Close(); };
            close.DangerOnHover = true;
            IconButton minimize = MakeIconButton(PaintMinus, dimColor);
            minimizeButton = minimize;
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            pinButton = MakeIconButton(PaintPinOff, TopMost ? palette.Accent : dimColor);
            if (TopMost) pinButton.SetPainter(PaintPinOn);
            pinButton.Click += delegate { TogglePin(); };
            IconButton clean = MakeIconButton(PaintBin, dimColor);
            cleanButton = clean;
            clean.Click += delegate { CleanFromWindow(); };

            // 悬停提示：原生按钮的作用一眼可见
            ToolTip tips = new ToolTip();
            // 默认要等半秒才冒提示，太慢；首现与重现都缩短一点更跟手。
            tips.InitialDelay = 220;
            tips.ReshowDelay = 100;
            tips.AutoPopDelay = 9000;
            tips.SetToolTip(newThread, "新对话（继承当前会话的上下文）");
            tips.SetToolTip(pinButton, "始终置顶：开 / 关");
            tips.SetToolTip(minimize, "最小化");
            tips.SetToolTip(close, "关闭（即用即焚开启时会删除本次记录）");
            tips.SetToolTip(clean, "清理浏览器缓存与日志");
            tips.SetToolTip(caption, title);

            controlsPanel = new BufferedPanel();
            Panel controls = controlsPanel;
            controls.Dock = DockStyle.Right;
            controls.Width = 5 * 48;
            controls.Height = 46;
            controls.BackColor = headerFill;
            close.Dock = DockStyle.Right;
            minimize.Dock = DockStyle.Right;
            pinButton.Dock = DockStyle.Right;
            newThread.Dock = DockStyle.Right;
            controls.Controls.Add(clean);
            controls.Controls.Add(newThread);
            controls.Controls.Add(pinButton);
            controls.Controls.Add(minimize);
            controls.Controls.Add(close);

            header.Controls.Add(caption);
            header.Controls.Add(controls);

            // ---- browser surface ----
            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.DefaultBackgroundColor = palette.PageFill;
            /*
             * WinForms attaches its own IME context to controls by default; on a
             * control that hosts a browser engine that fights the engine's own TSF
             * text service (that is how the composition caret ends up unanchored and
             * the candidate window jumps to the screen corner). NoControl hands IME
             * handling entirely to WebView2.
             */
            webView.ImeMode = ImeMode.NoControl;

            status = new BufferedLabel();
            status.Dock = DockStyle.Bottom;
            status.Height = 0;
            status.ForeColor = palette.Danger;
            status.Font = uiFont;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(webView, 0, 1);
            layout.Controls.Add(status, 0, 2);
            Controls.Add(layout);

            // ---- resize grips (native, drawn over both bottom corners) ----
            // Windows 自己的无边框缩放就是四角/四边热区；这里的 WebView2 盖住了客户区、
            // 拿不到 WM_NCHITTEST，所以按标准做法在角上放两个 WinForms 手柄。
            gripRight = MakeGrip(false);
            gripLeft = MakeGrip(true);
            Controls.Add(gripRight);
            Controls.Add(gripLeft);
            gripRight.BringToFront();
            gripLeft.BringToFront();
            PlaceGrips();
            Resize += delegate
            {
                PlaceGrips();
                // 拖角缩放时不要每移动一下就重建窗口区域：SetWindowRgn 会整窗重绘，
                // 标题栏那种"一闪一闪"就是它。缩放期间先用直角，松手（MouseUp / ResizeEnd）再恢复圆角。
                if (!resizing) ApplyRoundedRegion();
            };
            ClientSizeChanged += delegate { PlaceGrips(); };

            saveTimer = new Timer();
            saveTimer.Interval = 400;
            saveTimer.Tick += delegate
            {
                saveTimer.Stop();
                SaveBounds();
            };

            ResizeEnd += delegate { QueueSave(); ApplyRoundedRegion(); };
            Move += delegate { QueueSave(); };
            FormClosing += delegate { SaveBounds(); };
            FormClosing += async delegate(object sender, FormClosingEventArgs args)
            {
                if (closingAfterCleanup) return;
                closingAfterCleanup = true;
                // An ephemeral thread is deleted by the page before the window goes away.
                args.Cancel = true;
                try
                {
                    await RunPageCleanupAsync();
                }
                catch (Exception error)
                {
                    Program.Log("cleanup failed: " + error.Message);
                }
                Close();
            };
            // Activating the window must put the cursor in the page, otherwise the
            // first keystrokes after clicking the window go nowhere.
            Activated += delegate
            {
                if (webView != null && webView.CoreWebView2 != null) webView.Focus();
            };
            Shown += OnShown;
            // 用户在 DSH 里换外观（浅色 / 深色 / 跟随系统）时这个窗口当场跟着换。
            themeTimer = new Timer();
            themeTimer.Interval = 1000;
            themeTimer.Tick += delegate { PollThemeFile(); PollSettingsFile(); };
            themeTimer.Start();
            // 设置页里点一下，窗口当场变（监视器），轮询只做兜底。
            ArmSettingsWatcher();
            // 控件齐了再铺一遍，确保每一块都拿到当前调色板里的颜色。
            ApplyPalette();
            // 置顶 / 透明度 / 置顶按钮状态按设置来。
            ApplyLiveSettings();
        }

        private static string AppDir
        {
            get
            {
                string location = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(location);
                return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
            }
        }

        /**
         * One shared shape for every header control: identical size, identical
         * type size and identical hover, so ＋ / 置顶 / — / ✕ read as one row
         * instead of four differently sized buttons.
         */
        /**
         * 标题栏按钮，图标自己用 GDI+ 画：字体符号（⌫ / ⌦ 之类）的字形由字体厂商决定，
         * 笔画粗细、留白、基线都不受控，小字号下就是"劣质"的来源。
         *
         * 交互动画跟手柄同一套做法（15ms 定时器 + 指数逼近）：底色从常态渐变到悬停/按下色，
         * 图标整体微微放大（悬停 5%）或缩小（按下 8%），**盖子额外抬起一点**——像是准备
         * 把东西扔进去，这一点点动效就是"手感"的来源。画笔颜色取 `ForeColor`，悬停时渐变到
         * `IconHoverColor`（调色板里的亮色），所以换主题时跟着一起变。
         * `ControlStyles.UserPaint` 之后 WinForms 不再画按钮本体，底色与反馈全部由这里负责。
         */
        private sealed class IconButton : Button
        {
            private Action<Graphics, Rectangle, Color, float, float> painter;
            private readonly Timer animator;
            private readonly Timer dangerTimer;
            private bool hovering;
            private bool pressing;
            private float hover;    // 0 → 1
            private float press;    // 0 → 1
            private float danger;   // 0 → 1：点击后立刻变红，停一下再淡回
            private float dangerTarget;
            private int dangerFadeMs = 120;   // 红 → 常态的淡出时长（还要更快就继续调小）
            /** 全红的停留时长：只做"点到了"的信号，别占着不走。 */
            internal const int DangerHoldMs = 150;

            internal IconButton(Action<Graphics, Rectangle, Color, float, float> painter)
            {
                this.painter = painter;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                TabStop = false;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                animator = new Timer();
                animator.Interval = 15;
                animator.Tick += delegate { Step(); };
                dangerTimer = new Timer();
                dangerTimer.Tick += delegate
                {
                    dangerTimer.Stop();
                    dangerTarget = 0F;
                    if (!animator.Enabled) animator.Start();
                };
            }

            internal Color HoverColor { get; set; }
            internal Color DownColor { get; set; }
            internal Color IconHoverColor { get; set; }
            internal Color DangerColor { get; set; }
            /** 悬停即泛红（关闭按钮）：会删东西的按钮，指针压上来时就该有提示。 */
            internal bool DangerOnHover { get; set; }

            /** 换图标：置顶这种有状态的按钮用（状态不同就换一支画笔）。 */
            internal void SetPainter(Action<Graphics, Rectangle, Color, float, float> next)
            {
                painter = next;
                Invalidate();
            }

            /** 点击反馈：图标**立刻**变红（不渐入），保持 holdMs 后平滑淡回常态。 */
            internal void FlashDanger(int holdMs)
            {
                danger = 1F;
                dangerTarget = 1F;
                Invalidate();
                dangerTimer.Stop();
                dangerTimer.Interval = holdMs > 0 ? holdMs : 320;
                dangerTimer.Start();
            }

            private static float Approach(float value, float target, ref bool moving)
            {
                float delta = target - value;
                if (Math.Abs(delta) < 0.012F) return target;
                moving = true;
                return value + delta * 0.34F;
            }

            private void Step()
            {
                bool moving = false;
                hover = Approach(hover, hovering ? 1F : 0F, ref moving);
                press = Approach(press, pressing ? 1F : 0F, ref moving);
                if (dangerTarget >= 1F)
                {
                    // 点下去立刻红：这一段刻意不做渐入。
                    if (danger < 1F) { danger = 1F; moving = true; }
                }
                else if (danger > 0F)
                {
                    // 按固定时长线性淡回：红色"恢复"是一段看得见的渐变，不是一帧跳回。
                    danger -= (float)animator.Interval / (float)dangerFadeMs;
                    if (danger < 0F) danger = 0F;
                    moving = true;
                }
                Invalidate();
                if (!moving) animator.Stop();
            }

            private static Color Blend(Color from, Color to, float amount)
            {
                if (amount <= 0F) return from;
                if (amount >= 1F) return to;
                return Color.FromArgb(
                    from.A + (int)((to.A - from.A) * amount),
                    from.R + (int)((to.R - from.R) * amount),
                    from.G + (int)((to.G - from.G) * amount),
                    from.B + (int)((to.B - from.B) * amount));
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovering = true;
                base.OnMouseEnter(e);
                if (!animator.Enabled) animator.Start();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovering = false;
                pressing = false;
                base.OnMouseLeave(e);
                if (!animator.Enabled) animator.Start();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                pressing = true;
                base.OnMouseDown(e);
                if (!animator.Enabled) animator.Start();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                pressing = false;
                base.OnMouseUp(e);
                if (!animator.Enabled) animator.Start();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                float lift = Math.Max(hover, press);
                Color background = Blend(BackColor, pressing ? DownColor : HoverColor, lift);
                using (SolidBrush fill = new SolidBrush(background)) g.FillRectangle(fill, ClientRectangle);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (painter == null) return;
                float scale = 1F + 0.05F * hover - 0.08F * press;
                // smoothstep：淡出挑两头慢、中间快，比线性更像"渐变"。
                float dangerShown = danger * danger * (3F - 2F * danger);
                if (DangerOnHover) dangerShown = Math.Max(dangerShown, hover * 0.85F);
                Color icon = Blend(Blend(ForeColor, IconHoverColor, lift), DangerColor, dangerShown);
                painter(g, ClientRectangle, icon, scale, hover);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (animator != null) { animator.Stop(); animator.Dispose(); }
                    if (dangerTimer != null) { dangerTimer.Stop(); dangerTimer.Dispose(); }
                }
                base.Dispose(disposing);
            }
        }

        /**
         * 垃圾桶：清理的通用符号，和设置页里的 mark 用同一套 16×16 描边语言
         * （盖子 + 提手 + 桶身 + 两道竖肋），笔画细、留白均匀，缩小到 18px 也不糊。
         */
        private static void PaintBin(Graphics g, Rectangle bounds, Color color, float iconScale, float lidLift)
        {
            float scale = Math.Min(bounds.Width, bounds.Height) / 26F * iconScale;
            float ox = bounds.Left + (bounds.Width - 16F * scale) / 2F;
            float oy = bounds.Top + (bounds.Height - 16F * scale) / 2F;
            // 悬停时盖子（连提手）轻轻抬起：像准备往里扔东西，这一点动效就是手感。
            float lid = lidLift * 1.2F * scale;
            using (Pen pen = new Pen(color, Math.Max(1.1F, 1.5F * scale)))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                // 盖子
                g.DrawLine(pen, ox + 2.6F * scale, oy + 4.4F * scale - lid, ox + 13.4F * scale, oy + 4.4F * scale - lid);
                // 提手（跟着盖子一起抬）
                g.DrawLines(pen, new PointF[] {
                    new PointF(ox + 6.1F * scale, oy + 4.4F * scale - lid),
                    new PointF(ox + 6.1F * scale, oy + 3.1F * scale - lid),
                    new PointF(ox + 9.9F * scale, oy + 3.1F * scale - lid),
                    new PointF(ox + 9.9F * scale, oy + 4.4F * scale - lid),
                });
                // 桶身
                g.DrawLines(pen, new PointF[] {
                    new PointF(ox + 4.2F * scale, oy + 4.4F * scale),
                    new PointF(ox + 4.9F * scale, oy + 12.5F * scale),
                    new PointF(ox + 11.1F * scale, oy + 12.5F * scale),
                    new PointF(ox + 11.8F * scale, oy + 4.4F * scale),
                });
                // 两道竖肋
                g.DrawLine(pen, ox + 6.9F * scale, oy + 7.0F * scale, ox + 6.9F * scale, oy + 10.8F * scale);
                g.DrawLine(pen, ox + 9.1F * scale, oy + 7.0F * scale, ox + 9.1F * scale, oy + 10.8F * scale);
            }
        }

        /** 16×16 设计网格 → 控件坐标：返回画笔，并给出原点与缩放。 */
        private static Pen IconPen(Rectangle bounds, Color color, float iconScale, out float ox, out float oy, out float scale)
        {
            scale = Math.Min(bounds.Width, bounds.Height) / 26F * iconScale;
            ox = bounds.Left + (bounds.Width - 16F * scale) / 2F;
            oy = bounds.Top + (bounds.Height - 16F * scale) / 2F;
            Pen pen = new Pen(color, Math.Max(1.1F, 1.5F * scale));
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            return pen;
        }

        /** ＋：新对话。 */
        private static void PaintPlus(Graphics g, Rectangle bounds, Color color, float iconScale, float lidLift)
        {
            float ox, oy, scale;
            using (Pen pen = IconPen(bounds, color, iconScale, out ox, out oy, out scale))
            {
                g.DrawLine(pen, ox + 8F * scale, oy + 3.9F * scale, ox + 8F * scale, oy + 12.1F * scale);
                g.DrawLine(pen, ox + 3.9F * scale, oy + 8F * scale, ox + 12.1F * scale, oy + 8F * scale);
            }
        }

        /** —：最小化。 */
        private static void PaintMinus(Graphics g, Rectangle bounds, Color color, float iconScale, float lidLift)
        {
            float ox, oy, scale;
            using (Pen pen = IconPen(bounds, color, iconScale, out ox, out oy, out scale))
            {
                g.DrawLine(pen, ox + 4F * scale, oy + 8F * scale, ox + 12F * scale, oy + 8F * scale);
            }
        }

        /** ✕：关闭。 */
        private static void PaintCross(Graphics g, Rectangle bounds, Color color, float iconScale, float lidLift)
        {
            float ox, oy, scale;
            using (Pen pen = IconPen(bounds, color, iconScale, out ox, out oy, out scale))
            {
                g.DrawLine(pen, ox + 4.4F * scale, oy + 4.4F * scale, ox + 11.6F * scale, oy + 11.6F * scale);
                g.DrawLine(pen, ox + 11.6F * scale, oy + 4.4F * scale, ox + 4.4F * scale, oy + 11.6F * scale);
            }
        }

        /** 置顶：未开启是空心圈，开启时圆心填实——状态本身就在图标里。 */
        private static void PaintPinOff(Graphics g, Rectangle bounds, Color color, float iconScale, float lidLift)
        {
            float ox, oy, scale;
            using (Pen pen = IconPen(bounds, color, iconScale, out ox, out oy, out scale))
            {
                g.DrawEllipse(pen, ox + 4F * scale, oy + 4F * scale, 8F * scale, 8F * scale);
            }
        }

        private static void PaintPinOn(Graphics g, Rectangle bounds, Color color, float iconScale, float lidLift)
        {
            float ox, oy, scale;
            using (Pen pen = IconPen(bounds, color, iconScale, out ox, out oy, out scale))
            {
                g.DrawEllipse(pen, ox + 4F * scale, oy + 4F * scale, 8F * scale, 8F * scale);
            }
            using (SolidBrush fill = new SolidBrush(color))
            {
                float dot = 3.6F * scale;
                g.FillEllipse(fill, ox + 8F * scale - dot / 2F, oy + 8F * scale - dot / 2F, dot, dot);
            }
        }

        /** 造一颗自绘图标按钮：尺寸、颜色与动画参数一次配齐。 */
        private IconButton MakeIconButton(Action<Graphics, Rectangle, Color, float, float> painter, Color foreground)
        {
            IconButton button = new IconButton(painter);
            button.Size = new Size(48, 46);
            button.BackColor = palette.HeaderFill;
            button.ForeColor = foreground;
            button.HoverColor = palette.ButtonHover;
            button.DownColor = palette.ButtonDown;
            button.IconHoverColor = palette.ButtonText;
            button.DangerColor = palette.Danger;
            button.Dock = DockStyle.Right;
            return button;
        }

        /** 一颗自绘按钮跟随调色板：底色、悬停/按下色、危险色、前景色。 */
        private void Restyle(IconButton button, Color foreground)
        {
            if (button == null) return;
            button.BackColor = palette.HeaderFill;
            button.ForeColor = foreground;
            button.HoverColor = palette.ButtonHover;
            button.DownColor = palette.ButtonDown;
            button.IconHoverColor = palette.ButtonText;
            button.DangerColor = palette.Danger;
            button.Invalidate();
        }

        private void OnHeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private void TogglePin()
        {
            TopMost = !TopMost;
            // 状态画进图标里（圆心填实 = 已置顶），颜色同时跟着调色板走。
            pinButton.SetPainter(TopMost ? (Action<Graphics, Rectangle, Color, float, float>)PaintPinOn : PaintPinOff);
            pinButton.ForeColor = TopMost ? palette.Accent : palette.DimText;
        }

        private void QueueSave()
        {
            if (!started) return;
            saveTimer.Stop();
            saveTimer.Start();
        }

        /// <summary>Remembered geometry, kept inside a connected screen's work area.</summary>
        private void ApplySavedBounds()
        {
            int width = prefs.DefaultWidth;
            int height = prefs.DefaultHeight;
            int x = int.MinValue;
            int y = int.MinValue;

            try
            {
                // 设置页里关掉「记住大小与位置」就只用默认尺寸，连旧状态都不看。
                if (prefs.RememberGeometry && File.Exists(statePath))
                {
                    string[] parts = File.ReadAllText(statePath).Split(',');
                    if (parts.Length == 4)
                    {
                        int parsed;
                        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) x = parsed;
                        if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) y = parsed;
                        if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) width = Math.Max(320, parsed);
                        if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) height = Math.Max(260, parsed);
                    }
                }
            }
            catch (Exception)
            {
                // Geometry is a convenience; a broken file falls back to the default.
            }

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            if (x == int.MinValue || y == int.MinValue || !IsOnSomeScreen(x, y, width, height))
            {
                x = work.Right - width - 24;
                y = work.Top + Math.Max(0, (work.Height - height) / 2);
            }

            Size = new Size(width, height);
            Location = new Point(x, y);
        }

        private static bool IsOnSomeScreen(int x, int y, int width, int height)
        {
            Rectangle wanted = new Rectangle(x, y, width, height);
            Screen[] screens = Screen.AllScreens;
            for (int index = 0; index < screens.Length; index++)
            {
                Rectangle area = screens[index].WorkingArea;
                if (wanted.Right > area.Left + 60 && wanted.Left < area.Right - 40
                    && wanted.Bottom > area.Top + 40 && wanted.Top < area.Bottom - 40) return true;
            }
            return false;
        }

        private void SaveBounds()
        {
            // 设置页里关掉「记住大小与位置」之后就不再写回状态，下次照样从默认尺寸开。
            if (!prefs.RememberGeometry) return;
            try
            {
                Rectangle bounds = RestoreBounds;
                string text = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                    bounds.X, bounds.Y, bounds.Width, bounds.Height);
                File.WriteAllText(statePath, text);
            }
            catch (Exception)
            {
                // A geometry write must never break the window.
            }
        }

        /** Temporary probe: IME messages reaching the form prove an interception. */
        private int imeMessages;
        protected override void WndProc(ref Message message)
        {
            int id = message.Msg;
            bool ime = (id >= 0x0281 && id <= 0x0289) || id == 0x010D || id == 0x010E || id == 0x010F || id == 0x0110;
            if (ime && imeMessages < 20)
            {
                imeMessages++;
                Program.Log("WM_IME message on the form: 0x" + id.ToString("X4") + " (count " + imeMessages + ")");
            }
            // Windows 换了深浅色：偏好是 `system` 时才跟着变（显式浅/深色时这里什么都不做）。
            if (id == WM_SETTINGCHANGE || id == WM_THEMECHANGED)
            {
                SyncTheme("system change");
            }
            base.WndProc(ref message);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch (Exception error)
            {
                Program.Log("rounded corners unavailable: " + error.Message);
            }
            ApplyRoundedRegion();
        }

        /// <summary>
        /// One corner resize handle. Both handles share one visual language — a
        /// rounded corner bracket with two dots on the diagonal — but each is drawn
        /// to face its own corner (top-left vs bottom-right), not as a mirror copy.
        /// Live feedback: an eased highlight while the pointer is over it, accent
        /// colour while dragging, so the affordance is obvious before you grab it.
        /// </summary>
        private Panel MakeGrip(bool topLeft)
        {
            Panel grip = new BufferedPanel();
            grip.Size = new Size(GRIP_SIZE, GRIP_SIZE);
            // 鼠标移上来必须变成双向缩放光标（左上/右下都在 ↘↖ 对角线上，用同一个光标）
            grip.Cursor = Cursors.SizeNWSE;
            /*
             * 两个角用同一套几何：把面板裁成"贴着窗口角的 L 形缝"（缝宽 = 卡片到窗口边的
             * 距离 8 CSS px，内边界是半径 16 CSS px 的圆角）。右下角那条缝正好避开输入卡片，
             * 左上角同理（只是那里背后是标题栏，本来也看不见）。
             */
            double scale = DeviceDpi / 96.0;
            int margin = (int)Math.Round(8 * scale);
            int innerRadius = (int)Math.Round(16 * scale);
            int side = grip.Width;
            using (System.Drawing.Drawing2D.GraphicsPath sliver = new System.Drawing.Drawing2D.GraphicsPath())
            {
                // 凹角必须反向扫弧，奇偶(Alternate)填充会把区域算成空 -> 用非零环绕
                sliver.FillMode = System.Drawing.Drawing2D.FillMode.Winding;
                if (topLeft)
                {
                    sliver.AddLine(side, 0, 0, 0);
                    sliver.AddLine(0, 0, 0, side);
                    sliver.AddLine(0, side, margin, side);
                    sliver.AddLine(margin, side, margin, margin + innerRadius);
                    sliver.AddArc(margin, margin, innerRadius * 2, innerRadius * 2, 180F, -90F);
                    sliver.AddLine(margin + innerRadius, margin, side, margin);
                    sliver.CloseFigure();
                }
                else
                {
                    sliver.AddLine(side, 0, side, side);
                    sliver.AddLine(side, side, 0, side);
                    sliver.AddLine(0, side, 0, side - margin);
                    sliver.AddLine(0, side - margin, side - margin - innerRadius, side - margin);
                    sliver.AddArc(side - margin - innerRadius * 2, side - margin - innerRadius * 2, innerRadius * 2, innerRadius * 2, 90F, -90F);
                    sliver.AddLine(side - margin, side - margin - innerRadius, side - margin, 0);
                    sliver.CloseFigure();
                }
                grip.Region = new Region(sliver);
            }
            float glow = 0F;          // 0 = idle, 1 = fully highlighted
            float target = 0F;
            bool pressed = false;
            Timer ease = new Timer();
            ease.Interval = 15;
            ease.Tick += delegate
            {
                float step = 0.18F;
                if (Math.Abs(target - glow) <= step)
                {
                    glow = target;
                    ease.Stop();
                }
                else
                {
                    glow += target > glow ? step : -step;
                }
                grip.Invalidate();
            };

            grip.Paint += delegate(object sender, PaintEventArgs args)
            {
                /*
                 * 1) 铺底：先画页面色，再把“标题栏高度以内”的部分盖成标题栏色。
                 *    手柄的 L 形缝会跨过标题栏下沿，只用一种底色就会露出一块色差。
                 * 2) 括号：贴边、端头圆、转角很小（不再圆润），两端各延长一段。
                 */
                Graphics graphics = args.Graphics;
                RectangleF area = new RectangleF(0F, 0F, grip.Width, grip.Height);
                using (SolidBrush pageBrush = new SolidBrush(themePageFill)) graphics.FillRectangle(pageBrush, area);
                // 只有“真的压在标题栏上”的那部分才铺标题栏色：
                // 用绝对位置算（局部 y<46 对右下角手柄是错的，会在页面区域露出亮带，
                // 白线压上去就显得比另一个角细）。
                int headerHeight = headerPanel != null ? headerPanel.Height : 0;
                int headerOverlap = headerHeight - grip.Top;
                if (headerOverlap > 0)
                {
                    float bandHeight = Math.Min(headerOverlap, grip.Height);
                    using (SolidBrush headerBrush = new SolidBrush(themeHeaderFill))
                    {
                        graphics.FillRectangle(headerBrush, new RectangleF(0F, 0F, grip.Width, bandHeight));
                    }
                }

                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int radius = 12;
                int extend = 20;                 // 两端各自延长的长度
                float inset = 4F;
                float far = inset + radius;

                using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    // 只有一条路径：右下角用 180° 旋转画同一个字形，
                    // 这样粗细、长度、转角在两角之间不可能出现差异。
                    System.Drawing.Drawing2D.GraphicsState state = graphics.Save();
                    if (!topLeft)
                    {
                        graphics.TranslateTransform(grip.Width, grip.Height);
                        graphics.RotateTransform(180F);
                    }
                    path.AddLine(inset, far + extend, inset, far);
                    path.AddArc(inset, inset, radius * 2, radius * 2, 180F, 90F);
                    path.AddLine(far, inset, far + extend, inset);
                    int alpha = pressed ? 255 : (int)Math.Round(225 + 30 * glow);
                    Color arc = pressed ? palette.Accent : Color.FromArgb(alpha, palette.GripArc);
                    using (Pen pen = new Pen(arc, 5F))
                    {
                        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        graphics.DrawPath(pen, path);
                    }
                    graphics.Restore(state);
                }
            };
            grip.MouseEnter += delegate
            {
                target = 1F;
                grip.BackColor = topLeft ? palette.GripHoverHeader : palette.GripHoverPage;
                ease.Start();
            };
            grip.MouseLeave += delegate
            {
                // 无论是否在拖拽都要清掉“按下”外观，否则指针快速移出时弧线会一直停在 accent 蓝
                pressed = false;
                if (!resizing) target = 0F;
                grip.BackColor = topLeft ? palette.GripIdleHeader : palette.GripIdlePage;
                ease.Start();
            };
            // 拖拽中丢失鼠标捕获（指针移出控件并松开）时也要复位，避免 resizing 卡住
            grip.MouseCaptureChanged += delegate
            {
                if (grip.Capture) return;
                if (!resizing && !pressed) return;
                bool wasResizing = resizing;
                resizing = false;
                pressed = false;
                target = 0F;
                ease.Start();
                if (wasResizing) { QueueSave(); ApplyRoundedRegion(); }
            };
            grip.MouseDown += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Left) return;
                pressed = true;
                target = 1F;
                resizing = true;
                // 先撤区域，否则旧区域会把放大后的区域剪掉（见 ClearRoundedRegion）。
                ClearRoundedRegion();
                resizeFromLeft = topLeft;
                resizeFromTop = topLeft;
                resizeOrigin = Cursor.Position;
                resizeStart = Size;
                boundsStart = Location;
                ease.Start();
                grip.Invalidate();
            };
            grip.MouseMove += delegate
            {
                if (resizing) ApplyResize();
            };
            grip.MouseUp += delegate
            {
                if (!resizing) return;
                resizing = false;
                pressed = false;
                // 松手后指针通常还停在这里：保持点亮，移开才渐隐
                target = grip.ClientRectangle.Contains(grip.PointToClient(Cursor.Position)) ? 1F : 0F;
                ease.Start();
                QueueSave();
                ApplyRoundedRegion();
            };
            return grip;
        }

        /// <summary>Linear colour blend used for the handle's eased hover feedback.</summary>
        private static Color Blend(Color from, Color to, float amount)
        {
            float k = Math.Max(0F, Math.Min(1F, amount));
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * k),
                (int)Math.Round(from.G + (to.G - from.G) * k),
                (int)Math.Round(from.B + (to.B - from.B) * k));
        }

        /// <summary>
        /// Drag maths shared by both handles: the bottom-right handle moves the right
        /// and bottom edges, the top-left handle moves the left and top edges, with
        /// the opposite edges pinned — the same behaviour as a Windows sizing border.
        /// </summary>
        private void ApplyResize()
        {
            Point now = Cursor.Position;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int dx = now.X - resizeOrigin.X;
            int dy = now.Y - resizeOrigin.Y;
            int width = resizeFromLeft ? resizeStart.Width - dx : resizeStart.Width + dx;
            int height = resizeFromTop ? resizeStart.Height - dy : resizeStart.Height + dy;
            int maxWidth = Math.Max(MinimumSize.Width, work.Width - 8);
            int maxHeight = Math.Max(MinimumSize.Height, work.Height - 8);
            width = Math.Min(Math.Max(MinimumSize.Width, width), maxWidth);
            height = Math.Min(Math.Max(MinimumSize.Height, height), maxHeight);
            int x = resizeFromLeft ? boundsStart.X + (resizeStart.Width - width) : boundsStart.X;
            int y = resizeFromTop ? boundsStart.Y + (resizeStart.Height - height) : boundsStart.Y;
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            SetBounds(x, y, width, height);
        }

        private void PlaceGrips()
        {
            if (gripRight != null)
            {
                gripRight.Location = new Point(
                    Math.Max(0, ClientSize.Width - gripRight.Width),
                    Math.Max(0, ClientSize.Height - gripRight.Height));
            }
            if (gripLeft != null)
            {
                gripLeft.Location = new Point(0, 0);
            }
        }

        /// <summary>Round every edge of the frame; redrawn whenever the size changes.</summary>
        private void ApplyRoundedRegion()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            try
            {
                IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, CORNER_RADIUS, CORNER_RADIUS);
                // SetWindowRgn hands ownership to the system: the handle must not be deleted here.
                SetWindowRgn(Handle, region, true);
            }
            catch (Exception error)
            {
                Program.Log("rounded region unavailable: " + error.Message);
            }
        }

        /**
         * 拖角缩放开始时先撤掉窗口区域：区域永远记着"上一次的尺寸"，不撤掉就会把新扩出来的
         * 那一块整块剪掉（只看得见手柄的括号）。空区域 = 直角窗口，这正是缩放期间想要的；
         * 松手后再用 ApplyRoundedRegion 把圆角装回来。
         */
        private void ClearRoundedRegion()
        {
            if (!IsHandleCreated) return;
            try
            {
                SetWindowRgn(Handle, IntPtr.Zero, true);
            }
            catch (Exception error)
            {
                Program.Log("clearing rounded region failed: " + error.Message);
            }
        }

        /// <summary>Close path: let the page delete an ephemeral thread first.</summary>
        private async Task RunPageCleanupAsync()
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(
                        "(function(){ return (window.__DSH_FLOAT_CLEANUP__ ? window.__DSH_FLOAT_CLEANUP__() : true); })()");
                }
            }
            catch (Exception error)
            {
                Program.Log("cleanup skipped: " + error.Message);
            }
        }

        /** The header's ＋ button: ask the page to drop its thread and start over. */
        private async void StartNewThread()
        {
            try
            {
                if (webView == null || webView.CoreWebView2 == null) return;
                await webView.CoreWebView2.ExecuteScriptAsync(
                    "window.__DSH_FLOAT_NEW_THREAD__ ? window.__DSH_FLOAT_NEW_THREAD__() : null");
                webView.Focus();
            }
            catch (Exception error)
            {
                Program.Log("new thread failed: " + error.Message);
            }
        }

        /** 开窗淡入：从七成不透明度爬到用户设定的值，避免"啪"地弹出（也盖住首帧白闪）。 */
        private void FadeIn()
        {
            double target = prefs.Opacity;
            Opacity = Math.Max(0.35, target * 0.7);
            Timer fade = new Timer();
            fade.Interval = 16;
            fade.Tick += delegate
            {
                double next = Opacity + (target - Opacity) * 0.28;
                if (Math.Abs(target - next) < 0.012)
                {
                    Opacity = target;
                    fade.Stop();
                    fade.Dispose();
                    return;
                }
                Opacity = next;
            };
            fade.Start();
        }

        private async void OnShown(object sender, EventArgs e)
        {
            started = true;
            Program.Log("shown; bounds=" + RestoreBounds + " dpi=" + GetDpiForWindow(Handle));
            FadeIn();
            try
            {
                string dataFolder = Path.Combine(AppDir, "webview-data");
                // No browser-argument overrides: 微信输入法 is a TSF text service, and
                // forcing Chromium onto the legacy IMM32 path made it unusable.
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, dataFolder, null);
                Program.Log("webview environment created: " + environment.BrowserVersionString);
                await webView.EnsureCoreWebView2Async(environment);
                Program.Log("webview ready");
                // 浏览器控件自己也带一份深浅色：滚动条和表单控件靠它。
                ApplyWebViewScheme();
                // The page reports its own state/trouble here, so a stuck window can
                // be diagnosed from this log instead of from screenshots.
                webView.CoreWebView2.WebMessageReceived += delegate(object source, CoreWebView2WebMessageReceivedEventArgs args)
                {
                    string payload = "";
                    try { payload = args.WebMessageAsJson; }
                    catch (Exception error) { Program.Log("page message unreadable: " + error.Message); }
                    Program.Log("page: " + payload);
                    // 页面脚本先于导航完成事件执行，所以它自己主动讨一次主题与设置：
                    // 不留深色闪现，也不让「即用即焚默认值」晚一拍生效。
                    if (payload.IndexOf("\"theme-request\"", StringComparison.Ordinal) >= 0)
                    {
                        PushPageTheme();
                        PushPageSettings();
                    }
                    // 页面从宿主官方接口（DSH 的 ui-theme 设置）拿到的主题：标题栏也照它上色。
                    if (payload.IndexOf("\"theme-observed\"", StringComparison.Ordinal) >= 0) ApplyObservedTheme(payload);
                    // 页面请求关窗（设置里打开了 Esc 关闭）：走正常的清理关闭路径。
                    if (payload.IndexOf("\"close-request\"", StringComparison.Ordinal) >= 0) BeginInvoke(new Action(Close));
                    // 标题栏「清理」按钮与页面都可能请求清缓存：清完把结果推回页面显示一条提示。
                    if (payload.IndexOf("\"clean-request\"", StringComparison.Ordinal) >= 0) BeginInvoke(new Action(CleanFromWindow));
                    if (payload.IndexOf("\"stats-request\"", StringComparison.Ordinal) >= 0) BeginInvoke(new Action(PushCacheStats));
                };
                // Tell the page it runs inside the native window before its own
                // scripts do, so it leaves the window chrome to this header.
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.__DSH_FLOAT_NATIVE__ = true;");
                CoreWebView2Settings settings = webView.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsZoomControlEnabled = false;
                // The page sets document.title from the side thread's own title; the
                // settings page can turn that off (then the plain title stays).
                webView.CoreWebView2.DocumentTitleChanged += delegate
                {
                    string pageTitle = webView.CoreWebView2.DocumentTitle;
                    if (captionLabel != null && !string.IsNullOrEmpty(pageTitle) && prefs.ShowSessionTitle) captionLabel.Text = pageTitle;
                    else if (captionLabel != null && !prefs.ShowSessionTitle && !string.IsNullOrEmpty(Text)) captionLabel.Text = Text;
                };

                // Every navigation's outcome lands in the log: a refused token URL
                // or a rejected page is otherwise invisible in this window.
                webView.CoreWebView2.NavigationCompleted += delegate(object source, CoreWebView2NavigationCompletedEventArgs completed)
                {
                    if (!completed.IsSuccess || completed.HttpStatusCode >= 400)
                    {
                        Program.Log("navigation failed status=" + completed.HttpStatusCode
                            + " error=" + completed.WebErrorStatus
                            + " url=" + webView.CoreWebView2.Source);
                    }
                };

                /*
                 * The Desktop shell admits only requests carrying its per-generation
                 * renderer capability header, and this window is an ordinary browser
                 * to it. The Host passed the value on, so every request this window
                 * makes carries it.
                 */
                if (!string.IsNullOrEmpty(rendererHeaderName) && !string.IsNullOrEmpty(rendererHeaderValue))
                {
                    string name = rendererHeaderName;
                    string value = rendererHeaderValue;
                    webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    webView.CoreWebView2.WebResourceRequested += delegate(object source, CoreWebView2WebResourceRequestedEventArgs args)
                    {
                        try
                        {
                            args.Request.Headers.SetHeader(name, value);
                        }
                        catch (Exception)
                        {
                            // A request that refuses the header is not worth failing the window over.
                        }
                    };
                    Program.Log("renderer capability header attached");
                }

                if (!string.IsNullOrEmpty(authUrl))
                {
                    // The token URL answers with the browser-trust cookie; the page
                    // that follows it is irrelevant, so one navigation is enough.
                    TaskCompletionSource<bool> minted = new TaskCompletionSource<bool>();
                    EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
                    handler = delegate
                    {
                        webView.CoreWebView2.NavigationCompleted -= handler;
                        minted.TrySetResult(true);
                    };
                    webView.CoreWebView2.NavigationCompleted += handler;
                    webView.CoreWebView2.Navigate(authUrl);
                    await Task.WhenAny(minted.Task, Task.Delay(8000));
                }

                webView.CoreWebView2.Navigate(pageUrl);
                Program.Log("navigated to page");
                // Hand the keyboard to the page: the window is usually raised by a
                // click in the DSH composer, so the first thing typed must land in
                // this page's composer rather than nowhere.
                webView.CoreWebView2.NavigationCompleted += delegate
                {
                    webView.Focus();
                    // 页面已经跑起来了：把当前主题与设置补推一次（它自己也讨过，这里是双保险）。
                    PushPageTheme();
                    PushPageSettings();
                    ApplyWebViewScheme();
                    webView.CoreWebView2.ExecuteScriptAsync(
                        "(function(){var i=document.getElementById('input'); if (i) i.focus(); return true;})()");
                };
            }
            catch (Exception error)
            {
                Program.Log("webview failure: " + error);
                status.Height = 26;
                status.Text = "  \u5185\u7f6e\u6d4f\u89c8\u5668\u542f\u52a8\u5931\u8d25\uff1a" + error.Message;
            }
        }
    }
}
