using System;
using System.Collections.Generic;
using CelesteGallery.Helpers;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace CelesteGallery.Views;

/// <summary>
/// 独立看图窗口。
///
/// 出现的时机有两个：
///   1. 在缩略图墙上双击一张图
///   2. 在资源管理器里双击图片文件（程序带路径启动）
///
/// 两种都弹这个窗口，主窗口的缩略图墙留在原地不动 ——
/// 关掉看图窗口就直接回到图墙，不用再点一次"返回"。
/// </summary>
public sealed partial class PhotoWindow : Window, IViewerHost
{
    /// <summary>
    /// 把所有打开的看图窗口记下来。
    /// 不记的话窗口对象可能被当成垃圾回收掉，表现是"窗口闪一下就没了"。
    /// </summary>
    private static readonly List<PhotoWindow> _open = new();

    /// <summary>当前那个看图窗口。同一时刻只留一个，换图就在它里面换。</summary>
    private static PhotoWindow? _current;

    private readonly AppWindow? _appWindow;
    private bool _fullScreen;
    private bool _closed;
    private readonly ViewerPage _viewer;

    /// <summary>
    /// 打开看图窗口。已经开着就换图并提到最前，不另开一个。
    /// </summary>
    public static void Show(string? path)
    {
        if (_current is not null)
        {
            Services.StartupLog.Write($"PhotoWindow: 复用已有窗口 → {path ?? "(无路径)"}");
            _current._viewer.OpenPath(path);
            _current.BringToFront();
            return;
        }

        _current = new PhotoWindow(path);
    }

    public PhotoWindow(string? path)
    {
        InitializeComponent();

        _appWindow = AppWindow;
        AppIcon.ApplyToWindow(_appWindow);

        _viewer = new ViewerPage { Host = this };
        Host.Children.Add(_viewer);

        Closed += OnClosed;
        _open.Add(this);

        ResizeToDefault();
        ApplyTitleBar(_viewer.TitleBarElement);

        _viewer.OpenPath(path);

        BringToFront();
        Services.StartupLog.Write($"PhotoWindow: 已打开 → {path ?? "(无路径)"}");
    }

    /// <summary>
    /// 把自己弄到最前。
    ///
    /// ⚠️ 光调 <c>Activate()</c> 是不够的 —— 这个窗口是在主窗口的按键 / 双击事件里
    /// 建出来的，WinUI 的 Activate 在这种时机下经常**只显示、不抬升**，
    /// 结果窗口明明开了却躲在主窗口后面（用户报的就是这个）。
    /// 所以两步都做：
    ///   1. <c>Activate()</c> —— 走 WinUI 正规那套（显示窗口、装输入焦点）；
    ///   2. <see cref="WindowForeground.BringToFront"/> —— 按 Win32 的规则硬抬一次 z 序。
    ///
    /// 再补一次**延后抬升**：当前这条输入消息（双击 / 回车）还没处理完的时候，
    /// 系统的前台切换有可能被压住；等这一轮消息循环走完再抬一次最稳。
    /// 多抬这一次不会有副作用 —— 那时用户手还没离开鼠标。
    /// </summary>
    private void BringToFront()
    {
        Activate();
        WindowForeground.BringToFront(WindowHandle);

        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closed) WindowForeground.BringToFront(WindowHandle);
        });
    }

    /// <summary>默认开成屏幕的八成，太大了压迫感强，太小了看不清。</summary>
    private void ResizeToDefault()
    {
        if (_appWindow is null) return;

        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;

            int w = (int)(work.Width * 0.8);
            int h = (int)(work.Height * 0.85);

            _appWindow.Resize(new SizeInt32(w, h));
            _appWindow.Move(new PointInt32(
                work.X + (work.Width - w) / 2,
                work.Y + (work.Height - h) / 2));
        }
        catch
        {
            // 拿不到显示器信息就算了，系统给的默认大小也能用
        }
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;      // 让那次"延后抬升"知道别再动这个窗口了
        _open.Remove(this);
        _viewer.ReleaseImage();
        if (ReferenceEquals(_current, this)) _current = null;
    }

    // ===== IViewerHost =====

    public IntPtr WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(this);

    public bool IsFullScreen => _fullScreen;

    public void ToggleFullScreen()
    {
        if (_appWindow is null) return;

        _fullScreen = !_fullScreen;
        _appWindow.SetPresenter(
            _fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
    }

    /// <summary>独立窗口里按返回 = 关掉自己。</summary>
    public void RequestBack() => Close();

    /// <summary>
    /// 从图库的"幻灯片放映"按钮进来：已经开着就直接在这个窗口里开始放映，
    /// 没开着（_current 为空）就什么都不做 —— 调用方会先 <see cref="Show"/> 把窗口开起来。
    /// </summary>
    public static void StartSlideshow() => _current?._viewer.StartSlideshow();

    /// <summary>
    /// 接管标题栏。
    ///
    /// ⚠️ 名字**不能**叫 SetTitleBar —— 基类 Window 已经有同名的公开方法了。
    /// 叫一样的名字，里面那句 SetTitleBar(element) 会绑到自己身上，
    /// 于是无限递归直到栈溢出（程序秒退，连日志都来不及写）。
    /// 这个坑 MainWindow 那边早就踩过，搬代码时一定要连注释一起搬。
    /// </summary>
    public void ApplyTitleBar(UIElement element)
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(element);          // 这里调的是基类 Window 的那个
        }
        catch
        {
            try { ExtendsContentIntoTitleBar = false; } catch { }
        }
    }

    void IViewerHost.SetTitleBar(UIElement element) => ApplyTitleBar(element);
}
