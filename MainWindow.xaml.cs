using System;
using System.IO;
using CelesteViewer.Helpers;
using CelesteViewer.Services;
using CelesteViewer.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CelesteViewer;

/// <summary>
/// 主窗口 —— 缩略图墙。只管窗口级别的事：
///   1. 接住"双击图片文件打开程序"传进来的路径
///   2. 双击某张图时弹出**独立的看图窗口**（看图页不再塞在这个窗口里）
///   3. 把标题栏交给缩略图墙的顶部区域，让窗口能拖
///   4. 全屏切换
///
/// 看图页为什么搬出去另开窗口：看图时要全屏、要拖来拖去看细节，
/// 和图墙挤在一个窗口里两边都施展不开。另开窗口之后，
/// 关掉看图窗口就直接回到图墙，连"返回"都不用点。
///
/// 界面本身全在 Views/ 下，这一层保持薄。
/// </summary>
public sealed partial class MainWindow : Window, IViewerHost
{
    private readonly AppWindow? _appWindow;
    private bool _fullScreen;

    public MainWindow()
    {
        StartupLog.Write("MainWindow: 开始构造");
        InitializeComponent();
        StartupLog.Write("MainWindow: XAML 加载完成");

        App.Instance = this;
        _appWindow = AppWindow;
        StartupLog.Write($"MainWindow: AppWindow 获取{(_appWindow is null ? "失败（全屏功能会不可用）" : "成功")}");

        // 窗口图标（标题栏左上角 / 任务栏 / Alt+Tab）。
        // 非打包程序不设这个的话，任务栏上会是个白板图标 ——
        // exe 自己的图标是另一回事，那个由 csproj 的 ApplicationIcon 在编译期写死。
        AppIcon.ApplyToWindow(_appWindow);

        // 在图墙上双击一张图 → 弹独立看图窗口
        Browser.OpenRequested += ShowViewer;

        // 默认先显示缩略图墙。标题栏这里设一次（此时页面还没布局完，可能失败），
        // 页面自己的 Loaded 里会再设一次兜住。
        ShowBrowser();

        // 双击图片文件时，Windows 会把文件路径作为命令行参数传进来。
        // 非打包的 WinUI3 程序没有 OnFileActivated 那套，直接从命令行取最可靠。
        string[] args = Environment.GetCommandLineArgs();
        StartupLog.Write($"MainWindow: 命令行参数 {args.Length} 个");

        string? target = ExtractPath(args);
        if (target is not null)
        {
            App.StartFilePath = target;
            StartupLog.Write($"MainWindow: 准备打开 {target}");
            ShowViewer(target);
        }
        else
        {
            StartupLog.Write("MainWindow: 无参数启动，显示缩略图墙");
        }
    }

    /// <summary>从命令行里挑出第一个像路径的参数（跳过 -- 开头的开关）。</summary>
    private static string? ExtractPath(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;

            if (File.Exists(args[i])) return args[i];

            StartupLog.Write($"MainWindow: 参数指向的文件不存在，忽略 → {args[i]}");
            return null;
        }

        return null;
    }

    /// <summary>
    /// 打开看图窗口。
    ///
    /// 复用同一个窗口：已经开着就换图并提到最前，不另开一个 ——
    /// 否则在图墙上点十张图就叠出十个窗口，任务栏直接没法看。
    /// </summary>
    private static void ShowViewer(string path) => PhotoWindow.Show(path);

    /// <summary>切回缩略图墙（看图窗口关掉时其实什么都不用做，这里留着给接口用）。</summary>
    private void ShowBrowser()
    {
        if (_fullScreen) ToggleFullScreen();

        Browser.Visibility = Visibility.Visible;
        SetCustomTitleBar(Browser.TitleBarElement);
        Browser.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 把页面里的一块区域设成标题栏：整块区域都能拖动窗口，
    /// 右上角的最小化/最大化/关闭仍然由系统画。
    ///
    /// 名字刻意不叫 SetTitleBar —— 基类 Window 已经有同名方法了，
    /// 叫一样的名字会把自己递归进去。
    /// </summary>
    public void SetCustomTitleBar(UIElement titleBar)
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBar);
            StartupLog.Write("标题栏已接管（自定义标题栏生效）");
        }
        catch (Exception ex)
        {
            // 标题栏接管失败不该让程序挂掉，退化成系统标题栏也能用
            StartupLog.Write("接管标题栏失败，退回系统标题栏", ex);
            try { ExtendsContentIntoTitleBar = false; } catch { }
        }
    }

    public void ToggleFullScreen()
    {
        if (_appWindow is null) return;

        _fullScreen = !_fullScreen;
        _appWindow.SetPresenter(
            _fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        StartupLog.Write($"全屏切换 → {(_fullScreen ? "全屏" : "窗口")}");
    }

    /// <summary>
    /// 当前是不是全屏。页面里按 Esc 时要知道"该先退全屏还是先退页面"，
    /// 所以得把状态暴露出去 —— 之前页面自己记了个 _fullScreen，
    /// 但那个值从来没被更新过，导致 Esc 退不出全屏。
    /// </summary>
    public bool IsFullScreen => _fullScreen;

    // ===== IViewerHost =====
    // 主窗口不再是看图页的宿主了（看图页搬去了 PhotoWindow），
    // 但接口还是实现着：将来要是想在窗口里直接看图，这套是现成的。

    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    void IViewerHost.SetTitleBar(UIElement element) => SetCustomTitleBar(element);

    public void RequestBack() => ShowBrowser();
}
