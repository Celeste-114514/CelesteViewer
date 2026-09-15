using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CelesteGallery.Controls;

/// <summary>
/// 左右两栏之间那根**可拖拽的分隔条**。
///
/// 为什么值得单独写一个控件，而不是在页面里挂几个指针事件：
///   1. **鼠标光标**。WinUI3 里 `UIElement.ProtectedCursor` 是 protected，
///      只有派生类能设 —— 想让鼠标移到这条上变成"左右箭头"，就必须是个子类。
///      不设的话鼠标一直是普通箭头，用户根本不知道这东西能拖。
///   2. 拖拽的状态机（按下起点 / 捕获指针 / 松手 / 丢捕获）四处都要一致，
///      复制到第二个页面就是第二个 bug。
///
/// 触摸和鼠标走同一套指针事件，不用分开写。
///
/// 用法（在页面 Loaded 之后调）：
/// <code>
/// Split.Attach(LeftColumn, defaultWidth: 236);
/// Split.WidthCommitted += (_, w) => AppSettings.Set("TreePaneWidth", (int)w);
/// </code>
/// </summary>
public sealed class PaneSplitter : Grid
{
    /// <summary>分隔条本身的命中区宽度。1px 太难点中，6px 才顺手（视觉上仍是一条细线）。</summary>
    private const double HitWidth = 6;

    private readonly Rectangle _line;
    private readonly SolidColorBrush _idleBrush = new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _hotBrush = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    private ColumnDefinition? _column;
    private double _minWidth = 150;
    private double _maxWidth = 640;

    /// <summary>
    /// 最大宽度还额外受窗口宽度的这个比例限制。
    /// 只按像素设上限的话，把窗口缩到 600 宽时左栏还占 640，右边内容区就没了。
    /// </summary>
    private double _maxRatio = 0.45;

    private double _startX;
    private double _startWidth;
    private bool _dragging;

    /// <summary>拖完（或双击复位）后抛一次，参数是最终宽度 —— 用来记住它。</summary>
    public event EventHandler<double>? WidthCommitted;

    /// <summary>双击复位到这个宽度。</summary>
    public double DefaultWidth { get; set; } = 236;

    public PaneSplitter()
    {
        Width = HitWidth;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 视觉上只画中间那一条细线：拖拽区 6px，线 1px，看着就是一条分隔。
        // 悬停 / 拖动时线变白变粗，给个"抓住了"的反馈。
        _line = new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = _idleBrush,
        };
        Children.Add(_line);

        // 光标只能从子类里设（见类注释）
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        // 透明背景是**必须**的：Grid 的 Background 为 null 时不参与命中测试，
        // 那样指针事件根本到不了这里。
        // 这里显式构造颜色而不是用 Colors.Transparent —— Windows.UI 和 Microsoft.UI
        // 下各有一个 Colors，写简单名会撞 CS0104。
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        PointerEntered += (_, _) => SetHot(true);
        PointerExited += (_, _) => SetHot(_dragging);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, e) =>
        {
            // 双击 = 复位。拖过头之后不用去猜"原来是多宽"
            SetColumnWidth(DefaultWidth);
            WidthCommitted?.Invoke(this, CurrentWidth);
            e.Handled = true;
        };
    }

    /// <summary>绑定到要控制的列上。页面 Loaded 之后再调（那之前列宽还是 XAML 里的初值）。</summary>
    public void Attach(ColumnDefinition column, double defaultWidth,
                       double minWidth = 150, double maxWidth = 640)
    {
        _column = column;
        DefaultWidth = defaultWidth;
        _minWidth = minWidth;
        _maxWidth = maxWidth;
        SetColumnWidth(defaultWidth);
    }

    /// <summary>当前左栏实际宽度。</summary>
    public double CurrentWidth
        => _column is null ? DefaultWidth
           : _column.ActualWidth > 0 ? _column.ActualWidth
           : _column.Width.Value;

    /// <summary>
    /// 窗口变窄之后重量一次上限 —— 否则缩窗口时左栏会一直占着原来那个宽度，
    /// 右边内容区被挤到只剩几十像素。
    /// </summary>
    public void ReclampForWindow()
    {
        if (_column is null) return;
        double clamped = Clamp(CurrentWidth);
        if (Math.Abs(clamped - CurrentWidth) > 0.5) SetColumnWidth(clamped);
    }

    // ===== 内部 =====

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_column is null) return;

        _dragging = true;
        _startX = e.GetCurrentPoint(null).Position.X;
        // 用 ActualWidth（真实布局宽）当起点，不是 Width.Value：
        // 列宽有可能是 Auto/Star，值算出来不是像素
        _startWidth = CurrentWidth;

        CapturePointer(e.Pointer);
        SetHot(true);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        // 相对**窗口**取坐标，不是相对本控件：拖动时左栏一变宽，控件自己就往右挪了，
        // 用自身坐标会算出"越拖越慢"的怪反馈
        double x = e.GetCurrentPoint(null).Position.X;
        SetColumnWidth(Clamp(_startWidth + (x - _startX)));
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;

        _dragging = false;
        SetHot(false);
        WidthCommitted?.Invoke(this, CurrentWidth);
    }

    private void SetColumnWidth(double width)
    {
        if (_column is null) return;
        _column.Width = new GridLength(Math.Round(width));
    }

    private double Clamp(double width)
    {
        double max = _maxWidth;

        // XamlRoot.Size 和列宽都是 DIP，可以直接比
        double windowWidth = XamlRoot?.Size.Width ?? 0;
        if (windowWidth > 0) max = Math.Min(max, windowWidth * _maxRatio);

        if (max < _minWidth) max = _minWidth;
        return Math.Clamp(width, _minWidth, max);
    }

    private void SetHot(bool hot)
    {
        _line.Width = hot ? 2 : 1;
        _line.Fill = hot ? _hotBrush : _idleBrush;
    }
}
