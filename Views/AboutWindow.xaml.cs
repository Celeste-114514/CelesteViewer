using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CelesteGallery.Views;

/// <summary>
/// 「关于」窗口。
///
/// 从主界面左下角的「关于」按钮弹出，只显示一屏信息，
/// 关掉它的方式有三种（都不需要找标题栏的 X，那玩意儿已经被关掉了）：
///   1. 按窗口里的「确定」
///   2. 点一下窗口以外的地方（本窗口失焦就自己关）
///   3. 按 Esc
///
/// 之所以做成独立窗口而不是内容对话框（ContentDialog）：
/// 内容对话框会强行盖住整个主窗口并抢走输入，看个版本号不需要这种重量级待遇。
/// </summary>
public sealed partial class AboutWindow : Window
{
    /// <summary>
    /// 持有当前窗口的引用。
    ///
    /// 不持有会被垃圾回收 —— 表现是"窗口闪一下就没了"。
    /// 这条 WinUI 的坑在 PhotoWindow 那边踩过一次，这里照抄同样的写法。
    /// </summary>
    private static AboutWindow? _current;

    private readonly AppWindow? _appWindow;

    /// <summary>
    /// 是否已经真正激活过一次。
    ///
    /// Activated 事件在窗口刚建好时会先来一发 Deactivated，
    /// 不认这个开关的话窗口会在弹出的瞬间自己关掉。
    /// </summary>
    private bool _everActivated;

    /// <summary>关掉之后就别再抬了（"延后抬升"那一步要用）。</summary>
    private bool _closed;

    /// <summary>
    /// 正在联网（检查 / 下载）时为 true。
    ///
    /// 这个窗口默认是"点一下别处就自己关"，但下载到一半被关掉就前功尽弃 ——
    /// 用户可能只是切去看一眼浏览器，回来发现进度没了。
    /// 所以联网期间挂起"失焦即关"，只有真闲着才允许被点掉。
    /// </summary>
    private bool _busy;

    /// <summary>最近一次检查到的新版本 tag；没检查到就是 null。</summary>
    private string? _latestTag;

    /// <summary>新版本安装包地址；这次检查没找到安装包就是 null。</summary>
    private string? _latestSetupUrl;

    /// <summary>所有"程序主动关窗"都走这里，不直接调 Close()（方便以后统一加日志/动画）。</summary>
    private void CloseByDesign() => Close();

    public static void Show()
    {
        if (_current is not null)
        {
            _current.BringToFront();
            return;
        }

        _current = new AboutWindow();
    }

    public AboutWindow()
    {
        InitializeComponent();

        _appWindow = AppWindow;
        Title = "关于 CelesteGallery";
        AppIcon.ApplyToWindow(_appWindow);

        ConfigureWindowChrome();
        FillInfo();
        _ = LoadIconAsync();

        Activated += OnActivated;
        Closed += OnClosed;

        BringToFront();

        // 让「确定」拿到焦点：这样 Esc 和回车都落在窗口内容里，不会打空
        OkButton.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 把自己弄到最前。和 PhotoWindow 那边同款问题、同款修法（见那里的注释）：
    /// 程序已经在跑的时候新建窗口，光靠 <c>Activate()</c> 会落在主窗口后面。
    ///
    /// 这个窗口尤其不能躲在后面 —— 它设计成"点别处就关"，
    /// 一旦开在主窗口后面，用户看不到它，随手一点主窗口就把自己关了，
    /// 表现就是"点了关于没反应"。
    /// </summary>
    private void BringToFront()
    {
        Activate();
        WindowForeground.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));

        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closed)
                WindowForeground.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));
        });
    }

    /// <summary>
    /// 窗口外观：右上角那三个按钮一个都不画，也不给最小化和最大化。
    ///
    /// 关于"去掉 ×"这件事踩过的坑，按顺序记一下（省得下次再走一遍）：
    ///   1. <c>OverlappedPresenter.IsClosable</c> —— 这套 WinAppSDK（2.4）里
    ///      **真没有**这个属性（编译期直接 CS1061），网上很多说法是更新版本才加的。
    ///   2. Win32 摘 <c>WS_SYSMENU</c> —— ExtendsContentIntoTitleBar = true 之后，
    ///      那三个按钮是 AppWindow 自己画的，根本不看这个窗口样式
    ///      （2026-09-14 端到端实测：样式改了，× 照画、点了照样关）。
    ///   3. 只拦 <c>AppWindow.Closing</c> —— 能做到"点了关不掉"，但按钮还杵在那儿，
    ///      看着像坏了、实际上就是坏了（用户就是这么反馈的）。
    ///
    /// 最后能用的办法：把标题栏按钮的底色和前景色**全部设成透明**，
    /// 系统照常画、但画出来是空的，视觉上等于没有。
    ///
    /// 配套地，Closing 事件不再拦截 —— 万一有人正好戳到那个看不见的位置，
    /// 或者按了 Alt+F4，窗口就正常关掉。宁可多一条退路，也不要一个点不动的死按钮。
    /// 「确定 / 点窗口外面 / Esc」三条主路依然都在，各自独立。
    /// </summary>
    private void ConfigureWindowChrome()
    {
        try
        {
            // 内容顶到窗口边框，不要一条系统画的白标题栏
            ExtendsContentIntoTitleBar = true;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }

            // 三个按钮（最小化 / 最大化 / 关闭）全部画成透明。
            //
            // 注意 IsCustomizationSupported：极少数的系统/远程桌面环境不支持改标题栏颜色，
            // 硬设会抛异常。那种情况下按钮会照常显示 —— 但 Closing 也没被拦，点了照样能关，
            // 不会退化成"死按钮"。
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindowTitleBar bar = AppWindow.TitleBar;
                Windows.UI.Color none = Windows.UI.Color.FromArgb(0, 0, 0, 0);

                bar.ButtonBackgroundColor = none;
                bar.ButtonInactiveBackgroundColor = none;
                bar.ButtonHoverBackgroundColor = none;
                bar.ButtonPressedBackgroundColor = none;
                bar.ButtonForegroundColor = none;
                bar.ButtonInactiveForegroundColor = none;
                bar.ButtonHoverForegroundColor = none;
                bar.ButtonPressedForegroundColor = none;
            }

            // 三块卡片叠起来比原来的"技术参数表"高，装不下会挤出一根滚动条。
            // 730 在 1080p 上还留有余量（工作区一般 1000 上下），再高就该考虑换布局了。
            //
            // 居中改成相对主窗口：主窗口常常不是全屏、也不在屏幕正中，
            // 按屏幕居中的话弹出的小窗会离它老远，这个"点一下就关"的窗口尤其别扭。
            WindowPlacement.ResizeAndCenterOnOwner(AppWindow, 500, 730);
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("AboutWindow: 设置窗口外观失败", ex);
        }
    }

    private void FillInfo()
    {
        Assembly asm = Assembly.GetExecutingAssembly();

        string version = ReadVersion(asm);

        VersionText.Text = "版本 " + version;
        UpdateVersionText.Text = "当前版本 " + version;
        BuildTimeText.Text = ReadBuildTime(asm);
        CopyrightText.Text = "CelesteGallery · 本地图片查看器\n与 CelesteMusicPlayer 同一套设计语言";

        // 启动期如果已经自动检查出新版，打开关于页直接显示，不用用户再点一次。
        UpdateChecker.UpdateInfo? cached = UpdateChecker.LatestAvailable;
        if (cached is not null)
        {
            _latestTag = cached.Tag;
            _latestSetupUrl = cached.SetupUrl;
            ShowUpdateFound(cached.Tag, cached.SetupUrl, cached.Notes);
        }
    }

    /// <summary>
    /// 读版本号。优先读文件版本（csproj 里的 &lt;Version&gt; 会写进这里），
    /// 读不到就退回程序集版本，再不行才显示"未知" —— 关于窗口不该因为读不到版本而崩。
    /// </summary>
    private static string ReadVersion(Assembly asm)
    {
        try
        {
            string path = asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion!;
            }
        }
        catch { }

        return asm.GetName().Version?.ToString() ?? "未知";
    }

    /// <summary>构建时间取 exe 的最后修改时间，比维护一个手写日期可靠。</summary>
    private static string ReadBuildTime(Assembly asm)
    {
        try
        {
            string path = asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch { }

        return "未知";
    }

    private async System.Threading.Tasks.Task LoadIconAsync()
    {
        var source = await AppIcon.LoadPngAsync();
        if (source is not null) AboutIcon.Source = source;
    }

    /// <summary>失焦即关。点窗口以外的任何地方都会走到这里。</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            // 联网期间不关：下载到一半被点掉，用户会以为程序崩了
            if (_everActivated && !_busy) CloseByDesign();
            return;
        }

        _everActivated = true;
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    // ==================== 软件更新 ====================

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        _ = CheckUpdateAsync();
    }

    private async System.Threading.Tasks.Task CheckUpdateAsync()
    {
        _busy = true;
        try
        {
            UpdateChecker.UpdateInfo? found = await UpdateChecker.CheckForUpdateAsync();

            // 顺序不能反：先看有没有报错，再看有没有新版。
            // 网络不通时 CheckForUpdateAsync 也返回 null，
            // 要是先判断"没有新版"就显示"已是最新"，等于拿"查不到"骗用户说"你是最新的"。
            if (found is null && !string.IsNullOrEmpty(UpdateChecker.LastError))
            {
                UpdateStatusText.Text = "检查失败：网络不通或接口异常，可到下方 Releases 页面手动查看。";
                DownloadUpdateButton.Visibility = Visibility.Collapsed;
                NotesBox.Visibility = Visibility.Collapsed;
                return;
            }

            if (found is null)
            {
                UpdateStatusText.Text = $"已是最新版本（{UpdateChecker.CurrentVersionText()}）。";
                DownloadUpdateButton.Visibility = Visibility.Collapsed;
                NotesBox.Visibility = Visibility.Collapsed;
                return;
            }

            _latestTag = found.Tag;
            _latestSetupUrl = found.SetupUrl;
            ShowUpdateFound(found.Tag, found.SetupUrl, found.Notes);
        }
        finally
        {
            _busy = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>把"发现新版本"这件事显示到卡片上（手动检查和启动期缓存共用这一段）。</summary>
    private void ShowUpdateFound(string tag, string? setupUrl, string? notes)
    {
        string current = UpdateChecker.CurrentVersionText();
        UpdateStatusText.Text = string.IsNullOrEmpty(setupUrl)
            ? $"发现新版本 {tag}（当前 {current}）。该版本没挂安装包，请到下方 Releases 页面手动下载。"
            : $"发现新版本 {tag}（当前 {current}）。点「下载更新」自动安装。";

        DownloadUpdateButton.Visibility =
            string.IsNullOrEmpty(setupUrl) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrWhiteSpace(notes))
        {
            NotesBox.Visibility = Visibility.Collapsed;
            return;
        }

        // Release 正文可能是很长一大篇，截个头就行 —— 想看全文有 Releases 链接
        string text = notes!.Trim();
        if (text.Length > 600) text = text[..600] + "…";
        ReleaseNotesText.Text = text;
        NotesBox.Visibility = Visibility.Visible;
    }

    private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_latestSetupUrl) || string.IsNullOrEmpty(_latestTag)) return;
        _ = DownloadAndLaunchInstallerAsync(_latestSetupUrl!, _latestTag!);
    }

    /// <summary>
    /// 下载安装包到临时目录（带进度），下完拉起安装向导覆盖安装。
    ///
    /// 走的是和 CelesteMusicPlayer 一样的路子：安装包自己会读注册表里的安装位置覆盖安装，
    /// 前提是这个程序当初是**用安装包装的**，而不是现在的本地构建 exe。
    /// </summary>
    private async System.Threading.Tasks.Task DownloadAndLaunchInstallerAsync(string url, string tag)
    {
        _busy = true;
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;

        string fileName = System.IO.Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "CelesteGallery-Setup.exe";
        string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CelesteGalleryUpdate");
        string target = System.IO.Path.Combine(tmpDir, fileName);

        try
        {
            System.IO.Directory.CreateDirectory(tmpDir);
            // 清掉同名残留，不然重复下载时覆盖会失败
            if (System.IO.File.Exists(target)) System.IO.File.Delete(target);

            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
            UpdateStatusText.Text = $"正在下载 {fileName} …";

            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromMinutes(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteGallery/" + UpdateChecker.CurrentVersionText());

            using var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = new System.IO.FileStream(
                target, System.IO.FileMode.Create, System.IO.FileAccess.Write,
                System.IO.FileShare.None, 81920, useAsync: true);

            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await file.WriteAsync(buffer, 0, read);
                done += read;
                UpdateStatusText.Text = total.HasValue && total.Value > 0
                    ? $"正在下载 {fileName} … {done * 100 / total.Value}%（{FormatBytes(done)} / {FormatBytes(total.Value)}）"
                    : $"正在下载 {fileName} … 已下载 {FormatBytes(done)}";
                DownloadProgress.Value = total.HasValue && total.Value > 0
                    ? Math.Clamp(done * 100 / total.Value, 0, 100) : 0;
            }

            DownloadProgress.Value = 100;
            UpdateStatusText.Text = $"下载完成，正在启动安装向导（{tag}）…";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            };

            if (System.Diagnostics.Process.Start(psi) is not null)
            {
                UpdateStatusText.Text = "安装向导已启动，本程序即将退出以完成更新…";
                // 等安装向导真起来再退，退早了文件还占着，NSIS 覆盖会失败
                await System.Threading.Tasks.Task.Delay(800);
                Application.Current?.Exit();
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("AboutWindow.DownloadUpdate", ex);
            UpdateStatusText.Text = "下载失败：网络中断或地址失效，可到下方 Releases 页面手动下载。";
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _busy = false;
            CheckUpdateButton.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => CloseByDesign();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseByDesign();
            e.Handled = true;
        }
    }
}
