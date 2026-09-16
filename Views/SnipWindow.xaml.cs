using System;
using System.Runtime.InteropServices;
// byte[] → IBuffer 的 AsBuffer() 扩展方法在这个命名空间下
using System.Runtime.InteropServices.WindowsRuntime;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
// Canvas.SetLeft / SetTop（放大镜和提示条的定位）
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
// 工具条/识别面板是不是事件源（IsChromeSource 要顺着可视树往上爬）
using Microsoft.UI.Xaml.Media;
// SoftwareBitmapSource —— 把裸像素喂给 Image 用的
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace CelesteGallery.Views;

/// <summary>
/// 截图覆盖窗口。铺满整个虚拟桌面（多个显示器拼起来那整块）：
/// 前半段管"拖出一块矩形"，后半段（<see cref="InitEditor"/> 那一套）管"在原地标注"。
/// 从按下热键到进剪贴板，**全程都在这一个窗口里**，中途不跳新窗口。
/// </summary>
/// <remarks>
/// ⚠️ 坐标有三套，别混：
///   · <b>XAML 坐标</b>——鼠标事件给出来的，相对窗口左上角，单位是"有效像素"；
///   · <b>屏幕坐标</b>——Win32 那套，可能有负数（副屏在主屏左边），单位是"物理像素"；
///   · <b>画布像素</b>——选区那块图自己的像素，标注存的全是它（在 SnipWindow.Editor.cs 里）。
/// 前后两者的换算收敛在 <see cref="ToScreen"/> / <see cref="FromScreen"/>，
/// 后两者的换算收敛在 <see cref="StageToCanvas"/>，别在业务代码里手算。
/// </remarks>
public sealed partial class SnipWindow : Window
{
    private const int LoupeZoom = 6;
    private bool _opticsEnabled = true;   // 放大镜开关（暂时常开，后面接设置项）
    private const double LoupeWidth = 168;
    private const double LoupeHeight = 120;

    private readonly CapturedFrame _frame;
    private readonly SoftwareBitmapSource _source = new();
    private readonly double _scale;
    private readonly CaptureRect _virt;

    private bool _dragging;
    private Point _anchor;            // 按下那一点（XAML 坐标）
    private Rect _sel;                // 当前选区（XAML 坐标）
    private bool _hasSel;
    private IntPtr _hovered = IntPtr.Zero;

    private static SnipWindow? _current;

    /// <summary>覆盖层关掉之后要通知谁（一般是"把主窗口还原回来"）。</summary>
    private readonly Action? _onClosed;

    /// <summary>
    /// 开一次截图。已经在截就什么都不做（避免叠两层覆盖层）。
    /// </summary>
    /// <param name="onClosed">
    /// 覆盖层关闭时的回调，**不管是框选完还是按 Esc 取消都会走到**。
    /// 调用方（主界面按钮、全局热键）拿它来把之前藏起来的窗口放回来。
    /// 之所以不放在"抓完屏立刻还原"：抓屏之后屏幕上还要一直盖着这层
    /// 冻结画面，主窗口这时候冒出来会挡在它上面，用户点哪都点到主窗口去了。
    /// </param>
    public static void Start(Action? onClosed = null)
    {
        if (_current is not null)
        {
            Services.StartupLog.Write("SnipWindow: 已有截图在进行中，忽略重复触发");
            return;
        }

        Services.StartupLog.Write("SnipWindow: 开始截图，准备抓屏");

        var frame = ScreenCapture.CaptureVirtualScreen(includeCursor: false);
        if (frame is null)
        {
            Services.StartupLog.Write("SnipWindow: 抓屏失败（整帧为 null），截图中止");
            onClosed?.Invoke();
            return;
        }

        Services.StartupLog.Write($"SnipWindow: 抓屏成功 {frame.Width}x{frame.Height}，准备建覆盖层");

        _current = new SnipWindow(frame, onClosed);
        _current.Activate();
        WindowForeground.BringToFront(_current.WindowHandle);
        Services.StartupLog.Write("SnipWindow: 覆盖层已创建并置顶");
    }

    public SnipWindow(CapturedFrame frame, Action? onClosed = null)
    {
        InitializeComponent();

        _onClosed = onClosed;
        _frame = frame;
        _scale = frame.Scale is > 0 ? frame.Scale : 1.0;
        _virt = ScreenCapture.VirtualScreen;

        DeskImage.IsHitTestVisible = false;   // 鼠标事件统一由 Root 接管子项不挡路

        AppIcon.ApplyToWindow(AppWindow);
        CoverVirtualScreen();

        // ⚠️ 挂 Root 的 Loaded，不是 Window 的 ——
        // WinUI3 的 Window 本身没有 Loaded 事件（它不是 FrameworkElement），
        // 要先等界面挂上去才能把冻结帧塞进 Image。
        Root.Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _current = null;
            try { _onClosed?.Invoke(); }
            catch (Exception ex) { Services.StartupLog.Write("SnipWindow: 关闭回调出错", ex); }
        };

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.DoubleTapped += OnDoubleTapped;
        Root.IsTabStop = true;
        Root.KeyDown += OnKeyDown;   // Esc / Enter / W / 标注那几条

        Loupe.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, LoupeWidth, LoupeHeight),
        };

        // 标注那半截（工具条、色板、Stage 的指针处理）在构造期一次装好。
        // 不能"进编辑时再挂"：指针事件重复订阅会让一次拖动画两遍。
        InitEditor();

        Services.StartupLog.Write("SnipWindow: 构造函数走完");
    }

    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // ===================== 窗口外形 =====================

    private void CoverVirtualScreen()
    {
        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);   // 没边框没标题栏，客户区就是全部可用面积
                p.IsResizable = false;
                p.IsMinimizable = false;
            }

            // 边框去掉了，窗口外框尺寸基本等于客户区，直接按虚拟桌面铺满。
            // 用 Win32 的 SetWindowPos 而不是 AppWindow.Move/Resize：
            // 前者明确就是物理像素，后者那套"display units"遇上多屏高清会偏。
            FitToDesktop();

            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // WinUI 常常在窗口建好之后还要"再补一次布局"，延后啃一次更稳
                FitToDesktop();
                Root.Focus(FocusState.Programmatic);
                Services.StartupLog.Write($"SnipWindow: 覆盖层尺寸校准完成（客户区 {Root.ActualWidth:0}x{Root.ActualHeight:0}，虚拟桌面 {_virt.Width}x{_virt.Height}）");
            });
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 铺满桌面失败 → " + ex.Message);
        }
    }

    /// <summary>
    /// 让<b>客户区</b>（不是窗口外框）精确等于整个虚拟桌面。
    /// </summary>
    /// <remarks>
    /// 为什么不能直接 SetWindowPos(..., _virt.Width, _virt.Height) 完事：
    /// 就算调了 SetBorderAndTitleBar(false,false)，WinUI3 的窗口外框仍然比客户区
    /// 大一圈（实测 100% 缩放下左右各 3px、上下各 3px）。照虚拟桌面尺寸摆下去，
    /// 客户区只有 2554x1434 —— 右边和下边各漏一条 6px 的活桌面露在外面没被盖住，
    /// 而且冻结帧还得拉伸 1.0023 倍去凑，画面会糊一点点。
    ///
    /// 这里不去猜那圈边框有多厚（不同 DPI / 系统版本会变），而是直接向系统要数字：
    /// 摆一次 → 量 GetWindowRect 和 GetClientRect → 差值就是边框厚度 → 按差值
    /// 把窗口撑大并把原点往外挪。收敛很快，一次就准。
    /// </remarks>
    private void FitToDesktop()
    {
        var handle = WindowHandle;

        // 第一刀：先按"外框 = 虚拟桌面"摆下去，此时拿到的是这块尺寸下的真实边框厚度
        SetWindowPos(handle, new IntPtr(-1), _virt.X, _virt.Y, _virt.Width, _virt.Height, SWP_SHOWWINDOW);

        if (!GetWindowRect(handle, out var win) || !GetClientRect(handle, out var client))
            return;

        int frameW = win.Width - client.Width;
        int frameH = win.Height - client.Height;
        if (frameW < 0 || frameH < 0) return;      // 拿到了怪数据就别动，保持第一刀的结果

        // 第二刀：把边框那圈补回来，让客户区正好盖满
        SetWindowPos(handle, new IntPtr(-1),
                     _virt.X - frameW / 2, _virt.Y - frameH / 2,
                     _virt.Width + frameW, _virt.Height + frameH,
                     SWP_SHOWWINDOW);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.StartupLog.Write($"SnipWindow: 界面就绪，铺冻结帧（{_frame.Width}x{_frame.Height}，缩放 {_scale:0.##}）");

            var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _frame.Width, _frame.Height,
                                            BitmapAlphaMode.Premultiplied);
            bitmap.CopyFromBuffer(_frame.Pixels.AsBuffer());
            await _source.SetBitmapAsync(bitmap);
            DeskImage.Source = _source;
            LoupeImage.Source = _source;

            LoupeImage.Width = _frame.Width * LoupeZoom;
            LoupeImage.Height = _frame.Height * LoupeZoom;

            UpdateExteriorMask();
            Services.StartupLog.Write($"SnipWindow: 冻结帧已铺好（Root {Root.ActualWidth:0}x{Root.ActualHeight:0}，" +
                                      $"映射 {MapX:0.####}/{MapY:0.####}）");
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 显示冻结帧失败 → " + ex.Message);
        }
    }

    // ===================== 鼠标 =====================

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 点在工具条/识别面板上的时候，别把它当成"在选区外按下了，重新框选"。
        // 按钮自己会把点击吃掉，但滑块和横向滚动条拖起来事件会冒上来。
        if (_editing && IsChromeSource(e.OriginalSource)) return;

        // 标注状态下点选区外 = 这张不要了，重新框一块。
        // （点在选区**里面**根本到不了这里 —— 那些事件由 Stage 接走了。）
        if (_editing)
        {
            Services.StartupLog.Write("SnipWindow: 在选区外按下，退出标注、重新框选");
            EndEditing(resetSelection: true);
        }

        Root.CapturePointer(e.Pointer);
        _dragging = true;
        _anchor = e.GetCurrentPoint(Root).Position;
        _hasSel = false;
        SelBorder.Visibility = Visibility.Collapsed;
        Readout.Visibility = Visibility.Collapsed;
        UpdateReadout(_anchor);
        Services.StartupLog.Write($"SnipWindow: 按下 ({_anchor.X:0},{_anchor.Y:0})，开始框选");
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 标注状态下鼠标基本都在选区里（那边由 Stage 接走），漏到这里的只有
        // "在选区外晃"——不需要放大镜，也不需要窗口高亮，那两样都是为"选得准"服务的。
        if (_editing) return;

        var pos = e.GetCurrentPoint(Root).Position;

        UpdateLoupe(pos);

        if (_dragging)
        {
            double x = Math.Min(_anchor.X, pos.X);
            double y = Math.Min(_anchor.Y, pos.Y);
            double w = Math.Abs(pos.X - _anchor.X);
            double h = Math.Abs(pos.Y - _anchor.Y);
            _sel = new Rect(x, y, w, h);
            _hasSel = w > 2 && h > 2;

            SelBorder.Visibility = _hasSel ? Visibility.Visible : Visibility.Collapsed;
            CanvasLike(SelBorder, x, y, w, h);
            UpdateExteriorMask();
            UpdateReadout(pos, true);
        }
        else
        {
            // 没在拖的时候，顺手提示鼠标底下是哪个窗口（后面按 W 可以直接取整窗）
            HighlightWindowUnder(pos);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Services.StartupLog.Write($"SnipWindow: 松开（dragging={_dragging}，选区 {_sel.Width:0}x{_sel.Height:0}，hasSel={_hasSel}）");
        if (!_dragging) return;
        _dragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { /* 已经放开了就算了 */ }

        // 松手就**就地**进标注 —— 这是 QQ / 微信截图的手感，也是大多数人下意识的预期。
        // 之前要再按一下回车或双击才走，会让人以为"卡住了"。
        //
        // 门槛 8 像素：手抖点一下（按住又松开）不该为了一块 3x3 的图进标注模式，
        // 那种情况原地不动、继续等用户重新框就是了。
        if (_hasSel && _sel.Width >= 8 && _sel.Height >= 8)
            EnterEditing();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 标注状态下双击当没看见：那多半是用户手快点了两下，
        // 不该被解释成"取整窗 / 提交"。
        if (_editing) return;

        if (_hovered != IntPtr.Zero)
        {
            TakeWholeWindow(_hovered);     // 双击（没拖动）时，取整窗更合大多数人的习惯
            return;
        }
        EnterEditing();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 标注状态下先让标注那套快捷键挑一遍（回车完成、Ctrl+Z / S / C）
        if (_editing && EditorKeyDown(e)) return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                if (_editing)
                {
                    // 两段式：第一次 Esc 只是放弃标注、回到"重新框选"，
                    // 再按一次才真的退出截图。手滑碰一下不至于把整张图弄没。
                    Services.StartupLog.Write("SnipWindow: Esc 退出标注，回到框选");
                    EndEditing(resetSelection: true);
                    break;
                }
                CloseSnip();
                break;

            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                // 标注状态下的回车被 EditorKeyDown 吃掉了，能走到这里只有
                // "框完还没松手就敲了回车"这种情况，等同于松手。
                if (!_editing && _hasSel) EnterEditing();
                break;

            case Windows.System.VirtualKey.W:
                e.Handled = true;
                if (!_editing && _hovered != IntPtr.Zero) TakeWholeWindow(_hovered);
                break;
        }
    }

    // ===================== 提交 =====================

    private void TakeWholeWindow(IntPtr hwnd)
    {
        try
        {
            var b = ScreenCapture.WindowBounds(hwnd);
            if (b.IsEmpty) { EnterEditing(); return; }

            var start = FromScreen(new Point(b.X, b.Y));
            _sel = new Rect(start.X, start.Y, b.Width / MapX, b.Height / MapY);
            _hasSel = true;
            SelBorder.Visibility = Visibility.Visible;
            CanvasLike(SelBorder, _sel.X, _sel.Y, _sel.Width, _sel.Height);
            UpdateExteriorMask();
            EnterEditing();
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 取整窗失败 → " + ex.Message);
            EnterEditing();
        }
    }

    /// <summary>
    /// 框选结束：把选中那块像素裁出来架到选区上，就地进标注。
    /// 没选过（双击/回车那条兜底路径）就取整个虚拟桌面。
    ///
    /// 跟老版本最大的区别：**这里不关窗**。以前是裁完把自己的覆盖层关掉、
    /// 另开一个居中的编辑器窗口，用户视线得重新找一个地方；
    /// 现在选区和画布是同一个位置、同一份尺寸 —— 改的地方就是刚框的地方。
    /// </summary>
    private void EnterEditing()
    {
        try
        {
            CaptureRect physical = _hasSel
                ? new CaptureRect(
                    (int)Math.Round(_sel.X * MapX),
                    (int)Math.Round(_sel.Y * MapY),
                    (int)Math.Round(_sel.Width * MapX),
                    (int)Math.Round(_sel.Height * MapY))
                : new CaptureRect(0, 0, _frame.Width, _frame.Height);

            var piece = SnapshotEffects.Crop(_frame, physical);
            if (piece is null)
            {
                Services.StartupLog.Write("SnipWindow: 裁剪结果为空，放弃本次截图");
                CloseSnip();
                return;
            }

            Services.StartupLog.Write($"SnipWindow: 选中 {piece.Width}x{piece.Height}，进入原地标注");

            if (BeginEditing(piece)) return;

            // 画布架不起来（比如内存不够）也不能把用户晾在一层点不动的覆盖层上
            Services.StartupLog.Write("SnipWindow: 进入标注失败，收工");
            CloseSnip();
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 提交失败 → " + ex);
            CloseSnip();
        }
    }

    /// <summary>关掉覆盖层。所有出口（完成 / Esc / 出错）都走它，省得有人忘了清 <see cref="_current"/>。</summary>
    private void CloseSnip()
    {
        _current = null;
        try { Close(); } catch { /* 已经关了就算了 */ }
    }

    // ===================== 辅助 =====================

    /// <summary>
    /// 界面坐标 → 图像像素的倍率。
    ///
    /// ⚠️ 为什么不直接用 <c>frame.Scale</c>（系统缩放）：
    /// 冻结帧是按 Stretch=Fill 铺在**客户区**上的，所以"屏幕上一个点"对应"帧上一像素"
    /// 的真实倍率是 帧宽 ÷ 客户区宽，不是系统 DPI 缩放。
    ///
    /// 正常情况下这个比值就是 1 —— <see cref="FitToDesktop"/> 会把客户区校准到
    /// 和虚拟桌面一模一样大。但校准有可能失败（拿不到边框厚度、或者布局晚一帧），
    /// 那时这里算出来的倍率会自动兜住，选区和存出来的图仍然对得上。
    /// 不这么写的话，客户区比桌面小一圈时选区就会整体偏移 —— 分辨率低时看不出来，
    /// 4K 屏上很明显。
    /// </summary>
    private double MapX => Root.ActualWidth > 0 ? _virt.Width / Root.ActualWidth : _scale;

    private double MapY => Root.ActualHeight > 0 ? _virt.Height / Root.ActualHeight : _scale;

    /// <summary>XAML 坐标 → 屏幕物理坐标（给需要 Win32 的场合用）。</summary>
    private Point ToScreen(Point p) => new(_virt.X + p.X * MapX, _virt.Y + p.Y * MapY);

    /// <summary>屏幕物理坐标 → XAML 坐标。</summary>
    private Point FromScreen(Point p) => new((p.X - _virt.X) / MapX, (p.Y - _virt.Y) / MapY);

    private void HighlightWindowUnder(Point p)
    {
        try
        {
            var s = ToScreen(p);
            var hwnd = ScreenCapture.WindowAt((int)s.X, (int)s.Y);
            if (hwnd == IntPtr.Zero || hwnd == WindowHandle)
            {
                HideHover();
                return;
            }
            _hovered = hwnd;

            var b = ScreenCapture.WindowBounds(hwnd);
            if (b.IsEmpty) { HideHover(); return; }

            var tl = FromScreen(new Point(b.X, b.Y));
            CanvasLike(HoverBorder, tl.X, tl.Y, b.Width / MapX, b.Height / MapY);
            HoverBorder.Visibility = Visibility.Visible;
        }
        catch { HideHover(); }
    }

    private void HideHover()
    {
        _hovered = IntPtr.Zero;
        HoverBorder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 这个事件源是不是工具条/识别面板里的东西。
    ///
    /// 用途只有一个：覆盖层的"按下 = 重新框选"很霸道，得把浮层从它手底下摘出去。
    /// 直接比 <c>e.OriginalSource</c> 不够 —— 用户点到的往往是按钮里那个 TextBlock，
    /// 得顺着可视树往上找一圈。
    /// </summary>
    private bool IsChromeSource(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, ToolPanel)
                || ReferenceEquals(node, OcrPanel)
                // 文字输入框也算一个。它挂在 Root 上（不在 Stage 里，见 XAML 的说明），
                // 用户在框里点一下要是不拦着，就会被当成"点了选区外"——
                // 一整张标注当场被清空，还想打字呢，很难受。
                || ReferenceEquals(node, TextInput)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>把 "左边/上边/宽高" 落到（水平）对齐和行为上——抽出来是因为 Border 挂在 Grid 里，
    /// 直接塞 Canvas 的坐标属性是无效的。<b>Grid 里靠 Margin + 左上对齐。</b></summary>
    private void CanvasLike(FrameworkElement el, double x, double y, double w, double h)
    {
        el.HorizontalAlignment = HorizontalAlignment.Left;
        el.VerticalAlignment = VerticalAlignment.Top;
        el.Margin = new Thickness(x, y, 0, 0);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    private void UpdateExteriorMask()
    {
        double fullW = Root.ActualWidth > 0 ? Root.ActualWidth : _virt.Width / _scale;
        double fullH = Root.ActualHeight > 0 ? Root.ActualHeight : _virt.Height / _scale;

        // 没选区：四条里只留最上面那条铺满整屏，其余收成 0 ——
        // 效果就是"整屏压暗"
        if (!_hasSel)
        {
            CanvasLike(MaskTop, 0, 0, fullW, fullH);
            CanvasLike(MaskBottom, 0, 0, 0, 0);
            CanvasLike(MaskLeft, 0, 0, 0, 0);
            CanvasLike(MaskRight, 0, 0, 0, 0);
            return;
        }

        double x = _sel.X, y = _sel.Y, w = _sel.Width, h = _sel.Height;
        double right = x + w, bottom = y + h;

        // 四条严格不重叠，接缝处不会出现"压暗两次"的深边
        CanvasLike(MaskTop, 0, 0, fullW, y);                       // 选区以上
        CanvasLike(MaskBottom, 0, bottom, fullW, Math.Max(0, fullH - bottom));  // 选区以下
        CanvasLike(MaskLeft, 0, y, x, h);                          // 选区左侧
        CanvasLike(MaskRight, right, y, Math.Max(0, fullW - right), h);         // 选区右侧
    }

    private void UpdateReadout(Point pos, bool withSize = false)
    {
        var s = ToScreen(pos);
        string text = withSize
            ? $"{(int)Math.Round(_sel.Width)} × {(int)Math.Round(_sel.Height)}    屏幕 {(int)s.X}, {(int)s.Y}"
            : $"屏幕 {(int)s.X}, {(int)s.Y}";

        ReadoutText.Text = text;

        // 读数跟着鼠标飘，靠近右上角时翻到左边来，别被屏幕边缘切掉
        double x = pos.X + 14, y = pos.Y + 16;
        if (x + 240 > Root.ActualWidth) x = pos.X - 240;
        if (y + 30 > Root.ActualHeight) y = pos.Y - 30;
        CanvasLike(Readout, x, y, double.NaN, double.NaN);
        Readout.Visibility = Visibility.Visible;
    }

    private void UpdateLoupe(Point pos)
    {
        if (!_opticsEnabled) { Loupe.Visibility = Visibility.Collapsed; return; }

        double x = pos.X + 18, y = pos.Y + 18;
        if (x + LoupeWidth > Root.ActualWidth) x = pos.X - LoupeWidth - 18;
        if (y + LoupeHeight > Root.ActualHeight) y = pos.Y - LoupeHeight - 18;

        CanvasLike(Loupe, x, y, LoupeWidth, LoupeHeight);

        Canvas.SetLeft(LoupeImage, -(pos.X * MapX * LoupeZoom - LoupeWidth / 2));
        Canvas.SetTop(LoupeImage, -(pos.Y * MapY * LoupeZoom - LoupeHeight / 2));
        Canvas.SetLeft(LoupeCrossV, LoupeWidth / 2);
        Canvas.SetTop(LoupeCrossV, 0);
        Canvas.SetLeft(LoupeCrossH, 0);
        Canvas.SetTop(LoupeCrossH, LoupeHeight / 2);

        Loupe.Visibility = Visibility.Visible;
    }

    // ---- Win32 ----
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
}
