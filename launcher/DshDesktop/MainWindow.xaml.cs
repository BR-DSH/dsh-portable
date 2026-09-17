using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DshDesktopEngine;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop;

public sealed class DsLogLine
{
    public string Text { get; }
    public Brush Brush { get; }
    public DsLogLine(string text, Brush brush) { Text = text; Brush = brush; }
}

public partial class MainWindow : Window
{
    private readonly DshCore _core;
    private readonly DispatcherTimer _statusTimer;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _stoppingForExit;
    private bool _webViewReady;
    private int _navigationRequest;
    // 外部链接去重：NewWindowRequested 与 NavigationStarting 可能对同一链接各触发一次，
    // 用短时间窗内同 URL 只开一次，避免弹两个相同标签页。
    private readonly HashSet<string> _openedExternally = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _openedExternalWindow = DateTime.MinValue;

    // ENDFIELD boot-plate loader (rail progress + sweep/fade finish)
    private readonly DispatcherTimer _loaderTimer;
    private readonly DispatcherTimer _finishTimer;
    private DateTime _loaderStartUtc;
    private DateTime _finishStartUtc;
    private bool _loaderRunning;
    private bool _finishing;
    private const double LoaderDurationMs = 2600;

    private static readonly Brush BDefault = Freeze(0xC9D1DF);
    private static readonly Brush BDim = Freeze(0x6B7484);
    private static readonly Brush BInfo = Freeze(0x7DD3FC);
    private static readonly Brush BGood = Freeze(0x34D399);
    private static readonly Brush BWarn = Freeze(0xFBBF24);
    private static readonly Brush BBad = Freeze(0xF87171);
    private static readonly Brush BAccent = Freeze(0x4D6BFE);

    private static readonly (Brush fg, Brush bg) BadgeGreen = (Freeze(0x34D399), Freeze(0x0E2B22));
    private static readonly (Brush fg, Brush bg) BadgeAmber = (Freeze(0xFBBF24), Freeze(0x2E2510));
    private static readonly (Brush fg, Brush bg) BadgeGray = (Freeze(0x8A94A6), Freeze(0x1C212B));

    private static Brush Freeze(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
        b.Freeze();
        return b;
    }

    public ObservableCollection<DsLogLine> LogItems { get; } = new();

    /// <summary>加载遮罩里的迷你实时日志</summary>
    public ObservableCollection<DsLogLine> LoadingLogLines { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _core = new DshCore();
        _core.Log = (text, kind) => DispatchLog(text, kind);
        _core.BusyChanged = busy => Dispatcher.BeginInvoke(() => SetBusyUi(busy));
        _core.StatusChanged = () => Dispatcher.BeginInvoke(RefreshStatusUi);
        _core.VersionsChanged = () => Dispatcher.BeginInvoke(ApplyVersionsUi);

        var ver = typeof(MainWindow).Assembly.GetName().Version;
        TitleVersionText.Text = "桌面版 v" + (ver == null ? "1.0.0" : ver.ToString(3));

        // Windows 主题感知图标：启动即按系统深浅色选窗口图标
        ApplyThemeIcon();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatusUi();

        _loaderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _loaderTimer.Tick += (_, _) => LoaderTick_Advance();
        _finishTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _finishTimer.Tick += (_, _) => FinishTick();

        // 日志栏绑定
        DataContext = this;
        LogItems.CollectionChanged += (_, _) =>
        {
            LogCountText.Text = $"{LogItems.Count} 行";
            if (LogPanel.Visibility == Visibility.Visible && LogItems.Count > 0)
                LogScroll.ScrollToEnd();
        };

        Loaded += async (_, _) =>
        {
            ClampWindowIntoWorkArea();
            LogLine("— DeepSeek Harness 桌面版已启动 —", BDim);
            LogLine($"安装目录：{_core.Root}", BDefault);
            _core.PrepareEnvironment();
            RefreshStatusUi();
            _statusTimer.Start();
            _ = _core.LoadVersionsAsync();
            await EnsureServerAndLoadAsync();
        };

        Closed += (_, _) => _statusTimer.Stop();
    }

    // =====================================================================
    //  Logging
    // =====================================================================

    private void DispatchLog(string text, DshLogKind kind)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddLog(text, kind));
            return;
        }
        AddLog(text, kind);
    }

    private void AddLog(string text, DshLogKind kind)
    {
        var brush = kind switch
        {
            DshLogKind.Dim => BDim,
            DshLogKind.Info => BInfo,
            DshLogKind.Good => BGood,
            DshLogKind.Warn => BWarn,
            DshLogKind.Bad => BBad,
            DshLogKind.Accent => BAccent,
            _ => BDefault,
        };
        LogItems.Add(new DsLogLine(string.IsNullOrEmpty(text) ? " " : text, brush));
        while (LogItems.Count > 3000) LogItems.RemoveAt(0);
        FooterText.Text = string.IsNullOrWhiteSpace(text) ? FooterText.Text : text.Length > 120 ? text[..120] : text;

        // 加载遮罩可见时，同步到迷你日志
        if (LoadingOverlay.Visibility == Visibility.Visible)
        {
            LoadingLogLines.Add(LogItems[^1]);
            while (LoadingLogLines.Count > 8) LoadingLogLines.RemoveAt(0);
            LoadingLogScroll?.ScrollToEnd();
        }
    }

    private void LogLine(string text, Brush brush)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLogRaw(text, brush)); return; }
        AddLogRaw(text, brush);
    }

    private void AddLogRaw(string text, Brush brush)
    {
        LogItems.Add(new DsLogLine(text, brush));
        while (LogItems.Count > 3000) LogItems.RemoveAt(0);
        FooterText.Text = text.Length > 120 ? text[..120] : text;

        if (LoadingOverlay.Visibility == Visibility.Visible)
        {
            LoadingLogLines.Add(LogItems[^1]);
            while (LoadingLogLines.Count > 8) LoadingLogLines.RemoveAt(0);
            LoadingLogScroll?.ScrollToEnd();
        }
    }

    // =====================================================================
    //  UI state
    // =====================================================================

    private void SetBusyUi(bool busy)
    {
        BtnStart.IsEnabled = !busy;
        BtnStop.IsEnabled = !busy;
        BtnRestart.IsEnabled = !busy;
        BtnUpdate.IsEnabled = !busy;
        BtnCheckUpdate.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void RefreshStatusUi()
    {
        // Windows 主题感知图标：每 2 秒随系统深浅色刷新一次，切主题后任务栏/Alt+Tab 自动换
        ApplyThemeIcon();

        bool running = _core.IsRunning(out var pid);
        StatusDot.Fill = running ? BGood : BAccent;
        StatusText.Text = running ? "运行中" : "未运行";
        StatusDetailText.Text = running
            ? $"http://127.0.0.1:{DshCore.Port} · PID {pid?.ToString() ?? "?"}"
            : $"http://127.0.0.1:{DshCore.Port}";
        BtnOpenExternal.IsEnabled = running;
    }

    // =====================================================================
    //  Windows 主题感知图标（Codex 式：深色底白鲸 / 浅色底蓝鲸随系统切换）
    // =====================================================================

    private static ImageSource? _iconDark;
    private static ImageSource? _iconLight;

    private static ImageSource LoadPackIcon(string name)
    {
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/{name}", UriKind.Absolute);
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void ApplyThemeIcon()
    {
        try
        {
            // HKCU\...\Themes\Personalize\AppsUseLightTheme：0=深色，1=浅色
            bool light = true;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                var v = key?.GetValue("AppsUseLightTheme");
                light = v is int iv ? iv == 1 : true;
            }
            _iconDark ??= LoadPackIcon("whale-256.png");
            _iconLight ??= LoadPackIcon("whale-light.png");
            Icon = light ? _iconLight : _iconDark;
        }
        catch
        {
            // 读取失败时保持默认（XAML 里的 whale-256.png）
        }
    }

    private void ApplyVersionsUi()
    {
        ApplyRow(RowCoreVer, RowCoreBadge, RowCoreBadgeText, _core.CoreLocal, _core.CoreLatest);

        PluginRows.Children.Clear();
        foreach (var p in _core.Plugins)
            AppendPluginRow(p);

        if (_core.OcrReady)
        {
            RowOcrText.Text = "本地 OCR 运行时就绪";
            SetBadge(RowOcrBadge, RowOcrBadgeText, "就绪", BadgeGreen);
        }
        else
        {
            RowOcrText.Text = "缺失（更新后可按提示重新下载）";
            SetBadge(RowOcrBadge, RowOcrBadgeText, "缺失", BadgeAmber);
        }

        BtnUpdateText.Text = _core.HasUpdate ? "立即更新（有新版本）" : "立即更新";
        BtnUpdate.Opacity = _core.HasUpdate ? 1.0 : 0.7;
    }

    /// <summary>按 plugin-track.json 追踪清单动态生成一行插件版本。</summary>
    private void AppendPluginRow(PluginVersionInfo p)
    {
        var row = new Grid { Height = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var metaStyle = TryFindResource("MetaText") as Style;
        var label = new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center, Style = metaStyle };
        var ver = new TextBlock { Text = p.Display, VerticalAlignment = VerticalAlignment.Center, Style = metaStyle };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = BadgeGray.bg
        };
        var badgeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = BadgeGray.fg };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(ver, 1);
        Grid.SetColumn(badge, 2);
        row.Children.Add(label);
        row.Children.Add(ver);
        badge.Child = badgeText;
        row.Children.Add(badge);

        switch (p.Badge)
        {
            case DshBadge.Green: SetBadge(badge, badgeText, "已最新", BadgeGreen); break;
            case DshBadge.Amber: SetBadge(badge, badgeText, "发现新版本", BadgeAmber); break;
            default: SetBadge(badge, badgeText, "离线", BadgeGray); break;
        }

        PluginRows.Children.Add(row);
    }

    private static void ApplyRow(TextBlock ver, Border badge, TextBlock badgeText, string? local, string? latest)
    {
        if (latest is null)
        {
            ver.Text = local ?? "—";
            SetBadge(badge, badgeText, "离线", BadgeGray);
            return;
        }
        ver.Text = $"{local}  →  {latest}";
        if (DshCore.CompareVersions(latest, local ?? "0") > 0)
            SetBadge(badge, badgeText, "发现新版本", BadgeAmber);
        else
            SetBadge(badge, badgeText, "已最新", BadgeGreen);
    }

    private static void SetBadge(Border border, TextBlock text, string label, (Brush fg, Brush bg) palette)
    {
        text.Text = label;
        text.Foreground = palette.fg;
        border.Background = palette.bg;
    }

    // =====================================================================
    //  Loading overlay + WebView2
    // =====================================================================

    private void ShowLoading(string text, string detail = "")
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowLoading(text, detail)); return; }
        LoadingText.Text = text;
        LoadingDetail.Text = detail;
        // 只盖遮罩，不隐藏 WebView：WebView2 在隐藏状态下初始化会渲染成黑屏
        LoadingOverlay.Visibility = Visibility.Visible;
        // 同步最近的日志到迷你日志框
        LoadingLogLines.Clear();
        for (int i = Math.Max(0, LogItems.Count - 6); i < LogItems.Count; i++)
            LoadingLogLines.Add(LogItems[i]);
        StartLoader();
    }

    private void HideLoading()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => HideLoading()); return; }
        LoadingOverlay.Visibility = Visibility.Collapsed;
        StopLoader();
    }

    // =====================================================================
    //  ENDFIELD boot-plate loader (rail progress + sweep/fade finish)
    // =====================================================================

    private void StartLoader()
    {
        if (_loaderRunning) return;
        _loaderRunning = true;
        _loaderStartUtc = DateTime.UtcNow;
        if (LoaderFill is not null) LoaderFill.Height = 0;
        if (LoaderWipe is not null) { LoaderWipe.Opacity = 0; LoaderWipe.Width = 10; }
        if (LoaderPct is not null) LoaderPct.Text = "0%";
        if (LoaderStatus is not null) LoaderStatus.Text = "Connecting...";
        if (LoaderMeter is not null) LoaderMeter.Margin = new Thickness(26, 0, 0, 0);
        LoadingOverlay.Opacity = 1;
        _loaderTimer.Start();
    }

    private void StopLoader()
    {
        _loaderTimer.Stop();
        _loaderRunning = false;
    }

    private void LoaderTick_Advance()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible) { StopLoader(); return; }
        double h = Math.Max(1, LoadingOverlay.ActualHeight);
        double elapsed = (DateTime.UtcNow - _loaderStartUtc).TotalMilliseconds;
        double t = Math.Min(1, elapsed / LoaderDurationMs);
        double eased = 1 - Math.Pow(1 - t, 3); // easeOutCubic
        int value = (int)Math.Round(eased * 100);
        if (LoaderFill is not null) LoaderFill.Height = eased * h;
        if (LoaderPct is not null) LoaderPct.Text = value + "%";
        if (LoaderStatus is not null)
            LoaderStatus.Text = value < 45 ? "Connecting..." : (value < 99 ? "Updating..." : "Ready");
        if (LoaderMeter is not null)
        {
            double mh = Math.Max(40, LoaderMeter.ActualHeight);
            double fillTop = (1 - eased) * h; // 进度条上沿（自下而上）
            double top = Math.Max(0, Math.Min(fillTop - mh - 10, h - mh - 64));
            LoaderMeter.Margin = new Thickness(26, top, 0, 0);
        }
    }

    /// <summary>导航成功：跳到 100%，定格后整体淡出（不扫屏），交给 Web 端启动屏接力。</summary>
    private void FinishLoading()
    {
        if (_finishing) return;
        StopLoader();
        if (LoadingOverlay.Visibility != Visibility.Visible) return;
        double h = Math.Max(1, LoadingOverlay.ActualHeight);
        if (LoaderFill is not null) LoaderFill.Height = h;
        if (LoaderPct is not null) LoaderPct.Text = "100%";
        if (LoaderStatus is not null) LoaderStatus.Text = "Ready";
        if (LoaderWipe is not null) { LoaderWipe.Opacity = 0; LoaderWipe.Width = 10; }
        _finishing = true;
        _finishStartUtc = DateTime.UtcNow;
        _finishTimer.Start();
    }

    private void FinishTick()
    {
        double elapsed = (DateTime.UtcNow - _finishStartUtc).TotalMilliseconds;
        const double HoldMs = 220;
        const double FadeMs = 380;
        if (elapsed < HoldMs) return; // 100% 定格一拍
        double f = Math.Min(1, (elapsed - HoldMs) / FadeMs);
        LoadingOverlay.Opacity = Math.Max(0, 1 - f);
        if (elapsed >= HoldMs + FadeMs)
        {
            _finishTimer.Stop();
            _finishing = false;
            HideLoading();
        }
    }

    private async Task EnsureServerAndLoadAsync()
    {
        try
        {
            if (!_core.EnvironmentReady)
            {
                ShowLoading("环境不完整", "缺少关键文件，详见日志");
                return;
            }

            bool running = _core.IsRunning(out _);
            if (!running)
            {
                ShowLoading("正在启动 DeepSeek Harness...", "首次启动会自动修复依赖，请稍候");
                await _core.StartAsync(noOpen: true);
                await _core.WaitForPortAsync(true, 12000);
            }
            else
            {
                // 服务已在运行：同样播启动屏，保证每次打开桌面版都有加载界面
                // （WebView 就绪并导航完成后由 FinishLoading 扫屏/淡出让位）
                ShowLoading("正在连接工作台...", "正在加载 DSH 界面");
            }

            if (!_core.IsRunning(out _))
            {
                ShowLoading("服务未就绪", "启动未成功，请点「日志」查看详情");
                return;
            }

            await InitWebViewAsync();
            if (_webViewReady) NavigateToApp();
        }
        catch (Exception ex)
        {
            ShowLoading("启动失败：" + ex.Message, "");
        }
    }

    private async Task InitWebViewAsync()
    {
        if (_webViewReady) return;

        // 检测系统 WebView2 运行时
        string? runtimeVersion = null;
        try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { runtimeVersion = null; }

        var userData = Path.Combine(_core.Root, "dsh-home", "webview2-data");

        if (runtimeVersion is null)
        {
            await HandleMissingRuntimeAsync();
            return;
        }

        try
        {
            // 强制软件渲染：实测本机/部分显卡驱动下 WebView2 的 GPU 合成会
            // 显示白屏/黑屏（页面已加载但内容不显示）。聊天类界面软件渲染
            // 足够流畅，且能保证任何环境都不黑屏。
            var opts = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, userData, opts);
            await WebView.EnsureCoreWebView2Async(env);
            ConfigureWebView();
            _webViewReady = true;
        }
        catch (Exception ex)
        {
            ShowLoading("WebView2 初始化失败：" + ex.Message, "");
        }
    }

    private void ConfigureWebView()
    {
        if (WebView.CoreWebView2 is null) return;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

        // 在页面任何脚本运行前注入墨黑遮罩，盖住 DSH 壳自带的原生加载画面，
        // 直到 ENDFIELD 启动屏插件挂载（z-index 更高）再撤掉——消除“原生加载界面”闪现。
        try
        {
            const string earlyScript = @"(() => {
  const cover = document.createElement('div');
  cover.style.cssText = 'position:fixed;inset:0;z-index:2147482000;background:#101110;pointer-events:none;';
  (document.head || document.documentElement).appendChild(cover);
  let mo = null;
  const remove = () => { if (cover.parentNode) cover.parentNode.removeChild(cover); if (mo) { mo.disconnect(); mo = null; } };
  const hasPlate = () => typeof document.querySelector === 'function' && !!document.querySelector('[data-endfield-loader]');
  if (hasPlate()) { remove(); return; }
  mo = new MutationObserver(() => { if (hasPlate()) remove(); });
  mo.observe(document.documentElement, { childList: true, subtree: true });
  setTimeout(remove, 12000);
})();";
            _ = WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(earlyScript);
        }
        catch { /* 注入失败不影响主流程 */ }

        // 拦截所有导航：只允许 DSH 本地界面（127.0.0.1:3099）在内嵌 WebView 中渲染；
        // 外部链接（含 target=_blank 弹窗）一律交给系统默认浏览器，避免卡在内嵌页。
        WebView.CoreWebView2.NavigationStarting += (_, e) =>
        {
            try
            {
                if (IsAppNavigation(e.Uri)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
            catch { /* ignore */ }
        };

        WebView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            try
            {
                e.Handled = true;
                if (!IsAppNavigation(e.Uri)) OpenExternal(e.Uri);
            }
            catch { /* ignore */ }
        };
    }

    /// <summary>是否属于 DSH 本地应用导航（同源 + about/blob/data）。</summary>
    private bool IsAppNavigation(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return true;
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (Uri.TryCreate(_core.Url, UriKind.Absolute, out var app)
            && Uri.TryCreate(uri, UriKind.Absolute, out var target)
            && string.Equals(target.Scheme, app.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Host, app.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == app.Port) return true;
        return false;
    }

    /// <summary>用系统默认浏览器打开外部链接（仅 http/https；5 秒内同 URL 去重）。</summary>
    private void OpenExternal(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!(uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return;
        var now = DateTime.UtcNow;
        if ((now - _openedExternalWindow).TotalSeconds > 5)
        {
            _openedExternally.Clear();
            _openedExternalWindow = now;
        }
        if (!_openedExternally.Add(uri.TrimEnd('/'))) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private async void NavigateToApp()
    {
        if (!_webViewReady) return;

        // The server begins listening before redirected stdout necessarily
        // reaches dsh-web.out.log. Wait briefly for the current process's
        // launch token instead of racing ahead to the unauthenticated origin.
        int request = ++_navigationRequest;
        string url = _core.Url;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (request != _navigationRequest || !_webViewReady) return;
            url = _core.BrowserUrl;
            if (!string.Equals(url, _core.Url, StringComparison.Ordinal)) break;
            await Task.Delay(100);
        }

        if (request == _navigationRequest && _webViewReady)
            WebView.CoreWebView2?.Navigate(url);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady) NavigateToApp();
    }

    private void WebView_InitCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webViewReady = true;
            ConfigureWebView();
            if (_core.IsRunning(out _)) NavigateToApp();
        }
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) FinishLoading();
        else ShowLoading("连接 DSH 界面失败", "请确认服务已启动，或点「浏览器打开」");
    }

    private async Task HandleMissingRuntimeAsync()
    {
        var choice = DshHub.AskDialog.Show(this, "需要 WebView2 运行时",
            "检测到本机缺少 WebView2 运行时（嵌入式浏览器内核）。\n\n是否现在下载并安装？\n（约 2MB，Microsoft 官方引导器，装到当前用户，无需管理员）",
            ("下载并安装", DshHub.AskResult.Yes, true),
            ("取消", DshHub.AskResult.Cancel, false));

        if (choice != DshHub.AskResult.Yes)
        {
            ShowLoading("缺少 WebView2 运行时", "可点「浏览器打开」用外部浏览器使用 DSH");
            return;
        }

        ShowLoading("正在下载 WebView2 运行时...", "约 2MB，请稍候");
        try
        {
            var exe = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var http = new System.Net.Http.HttpClient())
            {
                var data = await http.GetByteArrayAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                await File.WriteAllBytesAsync(exe, data);
            }
            ShowLoading("正在安装 WebView2 运行时...", "约几十秒，无需管理员");
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--silent --install" });
            if (p is not null) await Task.Run(() => p.WaitForExit(120000));
            await InitWebViewAsync();
            if (_webViewReady) NavigateToApp();
        }
        catch (Exception ex)
        {
            ShowLoading("WebView2 安装失败：" + ex.Message, "可点「浏览器打开」使用外部浏览器");
        }
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.StartAsync(noOpen: true);
        if (_core.IsRunning(out _))
        {
            if (_webViewReady) NavigateToApp();
            else await EnsureServerAndLoadAsync();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.StopAsync();
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.RestartAsync(noOpen: true);
        if (_core.IsRunning(out _) && _webViewReady) NavigateToApp();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.CheckUpdateAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        bool running = _core.IsRunning(out _);
        string msg = running
            ? "更新前会先停止当前服务（自动备份 → 升级 → 自检），\n完成后需要手动重新启动服务。\n\n确定现在开始更新吗？"
            : "将自动备份当前版本并升级到最新版（含自检）。\n\n确定现在开始更新吗？";
        var choice = DshHub.AskDialog.Show(this, "立即更新", msg,
            ("开始更新", DshHub.AskResult.Yes, true),
            ("取消", DshHub.AskResult.Cancel, false));
        if (choice != DshHub.AskResult.Yes) return;

        ShowLoading("正在更新...", "更新可能耗时数分钟，完成后请重新启动服务");
        await _core.UpdateAsync();
        HideLoading();
        if (_core.IsRunning(out _) && _webViewReady) NavigateToApp();
    }

    private void Versions_Click(object sender, RoutedEventArgs e)
        => VersionsPanel.Visibility = VersionsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // 「日志」按钮：展开/收起底部日志栏
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ToggleLogPanel();

    private void ToggleLogPanel_Click(object sender, RoutedEventArgs e) => ToggleLogPanel();

    private void ToggleLogPanel()
    {
        bool show = LogPanel.Visibility != Visibility.Visible;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && LogItems.Count > 0) LogScroll.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogItems.Clear();
        LogCountText.Text = "";
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_core.BrowserUrl) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_core.Root}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    // =====================================================================
    //  Close behavior
    // =====================================================================

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        bool running = _core.IsRunning(out _);
        if (!running)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closePromptOpen || _stoppingForExit) return;

        _closePromptOpen = true;
        try
        {
            var choice = DshHub.AskDialog.Show(this, "关闭桌面版",
                "DeepSeek Harness 服务正在运行。\n关闭窗口时是否同时停止服务？\n\n选择「仅退出」后服务会继续在后台运行。",
                ("停止并退出", DshHub.AskResult.Yes, true),
                ("仅退出", DshHub.AskResult.No, false),
                ("取消", DshHub.AskResult.Cancel, false));

            if (choice == DshHub.AskResult.Yes)
            {
                _stoppingForExit = true;
                _ = StopThenCloseAsync();
            }
            else if (choice == DshHub.AskResult.No)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        catch (Exception ex)
        {
            LogLine($"[错误] 关闭确认框异常：{ex.Message}", BBad);
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private async Task StopThenCloseAsync()
    {
        try
        {
            await _core.StopThenCloseAsync();
        }
        finally
        {
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    // =====================================================================
    //  Window chrome
    // =====================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && e.ClickCount == 1)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // 确保标题栏/控制条在屏幕内（多显示器/远程桌面 CenterScreen 可能顶出屏幕）
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // 确保窗口完整落在所在显示器工作区内：
    // 尺寸过大时收缩（避免 150% DPI 下窗口比屏幕还大、底部内容被顶出屏幕
    // 看不到，例如加载遮罩的状态/日志区），位置越界时拉回。
    private void ClampWindowIntoWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return;

            // 工作区转成 DIP（Left/Top/Width/Height 是 DIP）
            double scale = 1.0;
            try { scale = GetDpiForWindow(hwnd) / 96.0; } catch { }
            double waLeft = mi.rcWork.Left / scale;
            double waTop = mi.rcWork.Top / scale;
            double waW = (mi.rcWork.Right - mi.rcWork.Left) / scale;
            double waH = (mi.rcWork.Bottom - mi.rcWork.Top) / scale;

            // 尺寸过大 → 收缩到工作区（保留最小尺寸）
            double w = Width, h = Height;
            double maxW = waW - 24, maxH = waH - 24;
            if (maxW >= MinWidth && w > maxW) w = maxW;
            if (maxH >= MinHeight && h > maxH) h = maxH;
            if (w != Width) Width = w;
            if (h != Height) Height = h;

            // 位置钳位
            double x = Left, y = Top;
            double minX = waLeft, minY = waTop;
            double maxX = waLeft + waW - Math.Min(80, w);
            double maxY = waTop + waH - Math.Min(80, h);
            if (maxX < minX || maxY < minY) return;
            double nx = Math.Clamp(x, minX, maxX);
            double ny = Math.Clamp(y, minY, maxY);
            if (nx != x) Left = nx;
            if (ny != y) Top = ny;
        }
        catch { /* ignore */ }
    }
}
