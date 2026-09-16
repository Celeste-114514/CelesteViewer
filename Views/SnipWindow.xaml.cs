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
// SoftwareBitmapSource —— 把裸像素喂给 Image 用的
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace CelesteGallery.Views;

/// <summary>
/// 截图第一步的覆盖窗口。铺满整个虚拟桌面（多个显示器拼起来那整块），
/// 用户在上面拖出一块矩形，确认后交给 <see cref="SnipEditorWindow"/> 去标注。
/// </summary>
/// <remarks>
/// ⚠️ 坐标有两套，别混：
///   · <b>XAML 坐标</b>——鼠标事件给出来的，相对窗口左上角，单位是"有效像素"；
///   · <b>屏幕坐标</b>——Win32 那套，可能有负数（副屏在主屏左边），单位是"物理像素"。
/// 两者之间差一个 <see cref="ScreenCapture.SystemScale"/> 和一个虚拟桌面原点的偏移。
/// 换算全部收敛到 <see cref="ToScreen"/> / <see cref="FromScreen"/> 里，别在业务代码里手算。
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
    /// 之所以放在这里而不是"抓完屏立刻还原"：抓屏之后屏幕上还要一直盖着这层
    /// 冻结画面，主窗口这时候冒出来会挡在它上面，用户点哪都点到主窗口去了。
    /// </param>
    public static void Start(Action? onClosed = null)
    {
        if (_current is not null)
        {
            Services.StartupLog.Write("SnipWindow: 已有截图在进行中，忽略重复触发");
            return;
        }

        var frame = ScreenCapture.CaptureVirtualScreen(includeCursor: false);
        if (frame is null)
        {
            Services.StartupLog.Write("SnipWindow: 抓屏失败（整帧为 null），截图中止");
            onClosed?.Invoke();
            return;
        }

        _current = new SnipWindow(frame, onClosed);
        _current.Activate();
        WindowForeground.BringToFront(_current.WindowHandle);
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
        Root.KeyDown += OnKeyDown;   // Esc / Enter / W

        Loupe.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Rect(0, 0, LoupeWidth, LoupeHeight),
        };
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
            var handle = WindowHandle;
            SetWindowPos(handle, new IntPtr(-1), _virt.X, _virt.Y, _virt.Width, _virt.Height, SWP_SHOWWINDOW);

            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // WinUI 常常在窗口建好之后还要"再补一次布局"，延后啃一次更稳
                SetWindowPos(handle, new IntPtr(-1), _virt.X, _virt.Y, _virt.Width, _virt.Height, SWP_SHOWWINDOW);
                Root.Focus(FocusState.Programmatic);
            });
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 铺满桌面失败 → " + ex.Message);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _frame.Width, _frame.Height,
                                            BitmapAlphaMode.Premultiplied);
            bitmap.CopyFromBuffer(_frame.Pixels.AsBuffer());
            await _source.SetBitmapAsync(bitmap);
            DeskImage.Source = _source;
            LoupeImage.Source = _source;

            LoupeImage.Width = _frame.Width * LoupeZoom;
            LoupeImage.Height = _frame.Height * LoupeZoom;

            OuterGeom.Rect = new Rect(0, 0, Root.ActualWidth > 0 ? Root.ActualWidth : (_virt.Width / _scale),
                                          Root.ActualHeight > 0 ? Root.ActualHeight : (_virt.Height / _scale));
            MaskPath.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(96, 0, 0, 0));

            UpdateExteriorMask();
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 显示冻结帧失败 → " + ex.Message);
        }
    }

    // ===================== 鼠标 =====================

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Root.CapturePointer(e.Pointer);
        _dragging = true;
        _anchor = e.GetCurrentPoint(Root).Position;
        _hasSel = false;
        SelBorder.Visibility = Visibility.Collapsed;
        Readout.Visibility = Visibility.Collapsed;
        UpdateReadout(_anchor);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
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
        if (!_dragging) return;
        _dragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { /* 已经放开了就算了 */ }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_hovered != IntPtr.Zero)
        {
            TakeWholeWindow(_hovered);     // 双击（没拖动）时，取整窗更合大多数人的习惯
            return;
        }
        Commit();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                _current = null;
                Close();
                break;

            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                Commit();
                break;

            case Windows.System.VirtualKey.W:
                e.Handled = true;
                if (_hovered != IntPtr.Zero) TakeWholeWindow(_hovered);
                break;
        }
    }

    // ===================== 提交 =====================

    private void TakeWholeWindow(IntPtr hwnd)
    {
        try
        {
            var b = ScreenCapture.WindowBounds(hwnd);
            if (b.IsEmpty) { Commit(); return; }

            var start = FromScreen(new Point(b.X, b.Y));
            _sel = new Rect(start.X, start.Y, b.Width / _scale, b.Height / _scale);
            _hasSel = true;
            SelBorder.Visibility = Visibility.Visible;
            CanvasLike(SelBorder, _sel.X, _sel.Y, _sel.Width, _sel.Height);
            UpdateExteriorMask();
            Commit();
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 取整窗失败 → " + ex.Message);
            Commit();
        }
    }

    /// <summary>把选中的那块像素裁出来交给编辑器。没选过就取整个虚拟桌面。</summary>
    private void Commit()
    {
        try
        {
            CaptureRect physical = _hasSel
                ? new CaptureRect(
                    (int)Math.Round(_sel.X * _scale),
                    (int)Math.Round(_sel.Y * _scale),
                    (int)Math.Round(_sel.Width * _scale),
                    (int)Math.Round(_sel.Height * _scale))
                : new CaptureRect(0, 0, _frame.Width, _frame.Height);

            var piece = SnapshotEffects.Crop(_frame, physical);
            if (piece is null)
            {
                Services.StartupLog.Write("SnipWindow: 裁剪结果为空，放弃本次截图");
                _current = null;
                Close();
                return;
            }

            Services.StartupLog.Write($"SnipWindow: 选中 {piece.Width}x{piece.Height}，交给编辑器");
            _current = null;
            Close();
            SnipEditorWindow.Show(piece);
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("SnipWindow: 提交失败 → " + ex);
            _current = null;
            try { Close(); } catch { }
        }
    }

    // ===================== 辅助 =====================

    /// <summary>XAML 坐标 → 屏幕物理坐标（给需要 Win32 的场合用）。</summary>
    private Point ToScreen(Point p) => new(_virt.X + p.X * _scale, _virt.Y + p.Y * _scale);

    /// <summary>屏幕物理坐标 → XAML 坐标。</summary>
    private Point FromScreen(Point p) => new((p.X - _virt.X) / _scale, (p.Y - _virt.Y) / _scale);

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
            CanvasLike(HoverBorder, tl.X, tl.Y, b.Width / _scale, b.Height / _scale);
            HoverBorder.Visibility = Visibility.Visible;
        }
        catch { HideHover(); }
    }

    private void HideHover()
    {
        _hovered = IntPtr.Zero;
        HoverBorder.Visibility = Visibility.Collapsed;
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

        OuterGeom.Rect = new Rect(0, 0, fullW, fullH);
        InnerGeom.Rect = _hasSel ? _sel : Rect.Empty;
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

        Canvas.SetLeft(LoupeImage, -(pos.X * LoupeZoom - LoupeWidth / 2));
        Canvas.SetTop(LoupeImage, -(pos.Y * LoupeZoom - LoupeHeight / 2));
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
}
