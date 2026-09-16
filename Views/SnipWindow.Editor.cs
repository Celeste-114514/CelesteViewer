using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
// Microsoft.UI.Colors 的常量表（Windows.UI 那边只有 Color 结构体本身）
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

// Windows.Storage.Streams 里也有个 Buffer，和 System.Buffer 撞名，
// 同时 using 会让 `Buffer.BlockCopy` 报 CS0104。显式指向托管那个。
using Buffer = System.Buffer;

namespace CelesteGallery.Views;

/// <summary>
/// <see cref="SnipWindow"/> 的后半截：框选结束之后的**原地标注**。
///
/// 这一部分原本是一个独立的窗口（SnipEditorWindow，居中弹出来的那种），
/// 后来整个搬进了覆盖层 —— 用户在哪儿框的就在哪儿改，视线不用重新找一个新窗口。
/// 搬过来之后标注引擎一行没改：当年编辑器的 Stage 是"一张图 + 一层预览"，
/// 现在覆盖层里的 Stage 还是这两个东西，只是位置被摆到了选区上、尺寸等于选区，
/// 所以「Stage 局部坐标 ÷ 视图缩放 = 画布像素」这条换算原封不动成立。
///
/// <para>
/// 拆成单独一个文件纯粹是为了别让 SnipWindow.xaml.cs 长得没边：
/// 那个文件管"框选 + 窗口外形 + 生命周期"，这个文件管"标注 + 工具条 + 收尾"。
/// 两边靠几个字段通信（<c>_editing</c> / <c>_sel</c> / <c>_canvas</c>），
/// 谁也别去动对方的方法。
/// </para>
/// </summary>
public sealed partial class SnipWindow
{
    /// <summary>常用色板。第一个是默认色。</summary>
    private static readonly (string Name, byte R, byte G, byte B)[] Palette =
    {
        ("红", 255, 59, 48),
        ("橙", 255, 149, 0),
        ("黄", 255, 214, 10),
        ("绿", 52, 199, 89),
        ("青", 50, 173, 230),
        ("蓝", 0, 122, 255),
        ("紫", 175, 82, 222),
        ("黑", 0, 0, 0),
        ("白", 255, 255, 255),
    };

    private static readonly (SnipTool Tool, string Label, string Tip)[] ToolDefs =
    {
        (SnipTool.Pen,     "画笔", "按住拖动自由画"),
        (SnipTool.Rect,    "矩形", "框出一块重点"),
        (SnipTool.Ellipse, "椭圆", "圈出重点"),
        (SnipTool.Arrow,   "箭头", "指哪儿看哪儿"),
        (SnipTool.Line,    "直线", "拉一条直线"),
        (SnipTool.Number,  "序号", "点一下放一个带数字的圆点，自动递增"),
        (SnipTool.Text,    "文字", "点一下开始打字，回车确认"),
        (SnipTool.Mosaic,  "马赛克", "框一块打码，遮隐私信息"),
        (SnipTool.Blur,    "模糊", "框一块模糊，比马赛克柔和"),
    };

    /// <summary>「完成后存档」这个开关在 settings.txt 里的键。</summary>
    private const string ArchiveOnDoneKey = "SnipArchiveOnDone";

    // ===================== 状态 =====================

    /// <summary>是不是已经框完、正在选区上原地标注。</summary>
    private bool _editing;

    /// <summary>是不是真的在拖动绘制（不要和框选的 <c>_dragging</c> 搞混）。</summary>
    private bool _drawing;

    private byte[]? _origin;      // 选区刚裁下来、一个字都没改的像素
    private byte[]? _canvas;      // 工作画布（标注直接画在上面）
    private int _pw;              // 画布宽（= 选区的物理像素宽，也是导出尺寸）
    private int _ph;
    private double _viewScale = 1.0;   // 画布像素 → XAML 单位（正常情况下 = 1/MapX）
    private WriteableBitmap? _bmp;

    private readonly List<SnipAnnotation> _anns = new();
    private long _crc;

    private SnipTool _tool = SnipTool.Rect;
    private byte _cr = 255, _cg = 59, _cb = 48;
    private double _lineWidth = 3;
    private int _numberSeq = 1;
    private bool _pickColorMode;
    private bool _saving;
    private bool _finishing;
    private bool _archiveHooked;

    private Point _dragStart;
    private Point _dragNow;
    private Point _textAnchor;
    private FrameworkElement? _preview;
    private SnipAnnotation? _penLive;
    private MarkStroke? _penStroke;

    private readonly Button[] _toolButtons = new Button[ToolDefs.Length];

    /// <summary>工具条最右边那条短消息（"已复制""存档失败"这类），下一次操作时清掉。</summary>
    private string _message = "";

    /// <summary>外面（<see cref="SnipWindow"/> 的键盘处理）问"现在是不是在标注"。</summary>
    internal bool IsEditing => _editing;

    /// <summary>
    /// 把工具条、色板、指针订阅全部装好。构造函数里调一次。
    ///
    /// 必须在构造期一次挂完，不能在"进入编辑"时挂 ——
    /// 每进一次编辑挂一遍，第二次就变成同一次拖动触发两回，画出来的东西翻倍。
    /// </summary>
    private void InitEditor()
    {
        BuildTools();
        BuildPalette();

        Stage.PointerPressed += OnStagePressed;
        Stage.PointerMoved += OnStageMoved;
        Stage.PointerReleased += OnStageReleased;
        Stage.PointerExited += (_, _) => HidePickerTip();

        WidthSlider.Value = _lineWidth;
    }

    // ===================== 进 / 出原地编辑 =====================

    /// <summary>
    /// 框选完了：把裁下来的那块像素架到选区上，进入原地标注。
    /// 失败返回 false（调用方按"这次截图没成"处理）。
    /// </summary>
    private bool BeginEditing(CapturedFrame piece)
    {
        try
        {
            _pw = piece.Width;
            _ph = piece.Height;
            if (_pw <= 0 || _ph <= 0) return false;

            // 拷两份：origin 当"撤销/重放的底"，canvas 是干活的那份。
            // 不能直接改 piece.Pixels —— 那是抓屏结果，双屏/多次框选还要留着做对照。
            _origin = new byte[_pw * _ph * 4];
            _canvas = new byte[_pw * _ph * 4];
            Buffer.BlockCopy(piece.Pixels, 0, _origin, 0, Math.Min(piece.Pixels.Length, _origin.Length));
            Buffer.BlockCopy(_origin, 0, _canvas, 0, _canvas.Length);

            _anns.Clear();
            _numberSeq = 1;
            _crc = 0;
            _message = "";
            _pickColorMode = false;
            PickColorButton.Background = new SolidColorBrush(Colors.Transparent);

            _bmp = new WriteableBitmap(_pw, _ph);
            PieceImage.Source = _bmp;
            PushPixels(force: true);

            // Stage 严丝合缝盖在选区上：位置 = 选区左上，尺寸 = 选区大小。
            // 于是"屏幕上一个点"和"画布上一个像素"之间只差一个缩放。
            double w = _sel.Width, h = _sel.Height;
            CanvasLike(Stage, _sel.X, _sel.Y, w, h);
            _viewScale = w > 0 ? w / _pw : 1.0;
            if (!(_viewScale > 0)) _viewScale = 1.0;

            // 预览画到选区外会显得很脏（比如箭头甩出去一截），裁掉
            Stage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };

            _editing = true;
            Stage.Visibility = Visibility.Visible;
            SelBorder.Visibility = Visibility.Visible;

            HookArchiveSwitch();

            // 进编辑之后放大镜和坐标读数就该让位了 ——
            // 它们服务的是"选得准不准"，现在用户关心的是"画得对不对"。
            Loupe.Visibility = Visibility.Collapsed;
            Readout.Visibility = Visibility.Collapsed;

            // ⚠️ 顺序不能反：先填状态文字、再摆工具条。
            // 工具条宽度是"跟着内容走"的，而尺寸那串字（"900 × 600"）要到这里才写进去；
            // 先摆后填的话，工具条会在摆好之后又宽出去一截，右边缘直接顶出屏幕。
            RefreshStatus();
            LayoutToolPanel();

            StartupLog.Write($"SnipWindow: 进入原地标注（画布 {_pw}x{_ph}，缩放 {_viewScale:0.####}）");
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 进入原地标注失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 退出原地标注。
    /// </summary>
    /// <param name="resetSelection">
    /// true = 连同选区一起清掉，回到"什么都没选"的初始状态（用户按 Esc 就是这条）；
    /// false = 保留选区（一般用不上，留个口子）。
    /// </param>
    private void EndEditing(bool resetSelection)
    {
        _editing = false;
        _drawing = false;
        _bmp = null;

        try
        {
            CancelTextInput();
            HidePreview();
            HidePickerTip();

            Stage.Visibility = Visibility.Collapsed;
            Stage.Clip = null;
            PieceImage.Source = null;
            ToolPanel.Visibility = Visibility.Collapsed;
            OcrPanel.Visibility = Visibility.Collapsed;

            _anns.Clear();
            _origin = null;
            _canvas = null;
            _pw = _ph = 0;
            _message = "";

            if (resetSelection)
            {
                _hasSel = false;
                SelBorder.Visibility = Visibility.Collapsed;
                UpdateExteriorMask();
            }

            ModeText.Text = "矩形选区";
            ModeText.Foreground = new SolidColorBrush(Colors.White);
            HintText.Text = "拖拽框选，松手即进标注；双击取整屏；W 取当前窗口；Esc 取消";
            Root.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 退出原地标注时出错", ex);
        }
    }

    // ===================== 工具条 =====================

    private void BuildTools()
    {
        for (int i = 0; i < ToolDefs.Length; i++)
        {
            var def = ToolDefs[i];
            var btn = new Button
            {
                Padding = new Thickness(9, 4, 9, 4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new TextBlock { Text = def.Label, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromArgb(220, 226, 226, 226)) },
            };
            ToolTipService.SetToolTip(btn, def.Tip);

            var tool = def.Tool;
            btn.Click += (_, _) => SelectTool(tool);
            _toolButtons[i] = btn;
            ToolBar.Children.Add(btn);
        }

        SelectTool(SnipTool.Rect);
    }

    private void SelectTool(SnipTool tool)
    {
        _tool = tool;
        _pickColorMode = false;
        _message = "";
        PickColorButton.Background = new SolidColorBrush(Colors.Transparent);
        CancelTextInput();
        HidePreview();

        for (int i = 0; i < ToolDefs.Length; i++)
        {
            bool on = ToolDefs[i].Tool == tool;
            _toolButtons[i].Background = new SolidColorBrush(
                on ? Color.FromArgb(255, 58, 110, 165) : Colors.Transparent);
        }

        // 点完按钮焦点会跑到按钮上，键盘快捷键就收不到了。拿回来。
        Root.Focus(FocusState.Programmatic);
        RefreshStatus();
    }

    private void BuildPalette()
    {
        foreach (var (name, r, g, b) in Palette)
        {
            var chip = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(255, r, g, b)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(chip, name);
            byte cr = r, cg = g, cb = b;
            chip.Click += (_, _) => SetColor(cr, cg, cb);
            ColorBar.Children.Add(chip);
        }

        SetColor(_cr, _cg, _cb);
    }

    private void SetColor(byte r, byte g, byte b)
    {
        _cr = r; _cg = g; _cb = b;
        CurrentColorChip.Background = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        Root.Focus(FocusState.Programmatic);
    }

    private void WidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WidthText is null) return;
        _lineWidth = Math.Round(e.NewValue);
        WidthText.Text = ((int)_lineWidth).ToString();
    }

    /// <summary>
    /// 把工具条摆到选区旁边。
    ///
    /// 规矩：优先贴选区**下方**、右边缘对齐 —— 跟 QQ 截图一致，也是眼睛最自然的位置
    /// （刚框完的时候视线就停在选区右下角）。下方塞不下就翻到上方；
    /// 实在都塞不下（比如选区几乎是整屏）就夹在屏幕里，宁可压着图也不能跑到屏幕外。
    /// </summary>
    private void LayoutToolPanel()
    {
        try
        {
            double fullW = Root.ActualWidth > 0 ? Root.ActualWidth : _virt.Width / _scale;
            double fullH = Root.ActualHeight > 0 ? Root.ActualHeight : _virt.Height / _scale;
            const double Pad = 10;

            // 先透明地显示出来量一次真实尺寸 ——
            // 工具条宽度取决于里面那堆按钮的实际排版，猜不得（改了字号/系统缩放就变）。
            // 设成透明再量，用户看不到"先出现在左上角再跳过去"那一下。
            //
            // ⚠️ 量之前必须先把对齐设成"左上 + 宽高自适应"。
            // Grid 里子元素的默认对齐是 Stretch，先量的话量到的是**整块屏幕**
            // （实测被量成 2540x1440），尺寸和位置就全算错了 ——
            // 现场表现是工具条变成一张盖住整个屏幕的深色板，一个按钮都看不见。
            CanvasLike(ToolPanel, 0, 0, double.NaN, double.NaN);
            ToolPanel.Opacity = 0;
            ToolPanel.MaxWidth = Math.Max(320, fullW - Pad * 2);
            ToolPanel.Visibility = Visibility.Visible;
            Root.UpdateLayout();

            double tw = ToolPanel.ActualWidth > 0 ? ToolPanel.ActualWidth : 900;
            double th = ToolPanel.ActualHeight > 0 ? ToolPanel.ActualHeight : 48;

            // 兜底：万一还是量成了"整块屏幕"（布局没来得及跑完），
            // 就按可用宽度铺满 —— 丑一点，但至少按钮点得到，不至于把用户堵死。
            double limit = fullW - Pad * 2;
            if (tw > limit - 1) tw = limit;
            if (th > fullH / 2) th = 48;
            tw = Math.Min(tw, limit);

            double selLeft = _sel.X, selTop = _sel.Y;
            double selRight = _sel.X + _sel.Width, selBottom = _sel.Y + _sel.Height;

            // 横向：右边缘对齐选区右边缘，但不许探出屏幕
            double x = selRight - tw;
            x = Math.Max(Pad, Math.Min(x, fullW - tw - Pad));

            // 纵向：下方 → 上方 → 硬夹
            double y = selBottom + Pad;
            if (y + th > fullH - Pad) y = selTop - th - Pad;
            if (y < Pad) y = Math.Max(Pad, Math.Min(fullH - th - Pad, selBottom - Pad - th));
            if (y < Pad) y = Pad;

            CanvasLike(ToolPanel, x, y, double.NaN, double.NaN);
            ToolPanel.Opacity = 1;

            StartupLog.Write($"SnipWindow: 工具条摆到 ({x:0},{y:0})，尺寸 {tw:0}x{th:0}（选区 {selLeft:0},{selTop:0} {_sel.Width:0}x{_sel.Height:0}，屏幕 {fullW:0}x{fullH:0}）");
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 摆工具条失败", ex);
            ToolPanel.Opacity = 1;
        }
    }

    /// <summary>
    /// 「存档」勾选框：取上次的状态，并记住这次的选择。
    ///
    /// 为什么要记：这是个人习惯问题 —— 有人截图就是为了往本地攒一份，
    /// 有人从来不留（发的都是些看一眼就删的东西）。每次都按默认值来，
    /// 就要每次都抬手改一遍，很烦。
    ///
    /// 注意用的是 <c>Click</c> 而不是 <c>Checked/Unchecked</c>：
    /// 上面那行 <c>IsChecked = ...</c> 一赋值就可能触发 Checked，
    /// 那时候订阅还没挂上，倒也没什么；但用 Click 语义更准 ——
    /// 只有"用户真的动手点了"才写盘。
    /// </summary>
    private void HookArchiveSwitch()
    {
        if (_archiveHooked) return;
        _archiveHooked = true;

        ArchiveSwitch.IsChecked = AppSettings.GetBool(ArchiveOnDoneKey, true);
        ArchiveSwitch.Click += (_, _) =>
            AppSettings.Set(ArchiveOnDoneKey, ArchiveSwitch.IsChecked == true);
    }

    // ===================== 指针（都挂在 Stage 上）=====================

    /// <summary>Stage 坐标 → 画布像素。原地标注里只有这一处换算，别在别处手算。</summary>
    private Point StageToCanvas(Point stagePoint)
    {
        double k = _viewScale > 0 ? _viewScale : 1.0;
        return new Point(stagePoint.X / k, stagePoint.Y / k);
    }

    private void OnStagePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;   // 别让覆盖层把它当成"在选区外按下了，重新框选"
        if (!_editing) return;

        CancelTextInput();
        var p = StageToCanvas(e.GetCurrentPoint(Stage).Position);

        if (_pickColorMode)
        {
            PickColorAt((int)p.X, (int)p.Y);
            return;
        }

        Stage.CapturePointer(e.Pointer);
        _drawing = true;
        _dragStart = p;
        _dragNow = p;
        _message = "";

        switch (_tool)
        {
            case SnipTool.Number:
                // 序号是"点一下就放"，不用拖
                _drawing = false;
                try { Stage.ReleasePointerCapture(e.Pointer); } catch { }
                PlaceNumber(p);
                break;

            case SnipTool.Text:
                _drawing = false;
                try { Stage.ReleasePointerCapture(e.Pointer); } catch { }
                BeginTextInput(p);
                break;

            case SnipTool.Pen:
                BeginPen(p);
                break;
        }
    }

    private void OnStageMoved(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_editing) return;

        var pos = e.GetCurrentPoint(Stage).Position;
        var p = StageToCanvas(pos);

        if (_pickColorMode)
        {
            ShowPickerTip(pos, (int)p.X, (int)p.Y);
            return;
        }

        if (!_drawing) return;
        _dragNow = p;

        if (_tool == SnipTool.Pen) { ExtendPen(p); return; }

        ShowPreview(_dragStart, p);
    }

    private void OnStageReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_drawing) return;
        _drawing = false;
        try { Stage.ReleasePointerCapture(e.Pointer); } catch { }

        if (_tool == SnipTool.Pen) { EndPen(); return; }

        HidePreview();

        double dx = Math.Abs(_dragNow.X - _dragStart.X);
        double dy = Math.Abs(_dragNow.Y - _dragStart.Y);
        if (dx < 3 && dy < 3) return;   // 手抖点一下，不当成一次绘制

        var a = new SnipAnnotation
        {
            Tool = _tool,
            X0 = _dragStart.X, Y0 = _dragStart.Y,
            X1 = _dragNow.X, Y1 = _dragNow.Y,
            R = _cr, G = _cg, B = _cb,
            Width = _lineWidth,
            Block = SnipTool.Mosaic == _tool ? (int)Math.Max(4, _lineWidth * 2.5) : (int)(12 + _lineWidth),
        };

        _anns.Add(a);
        Redraw();
    }

    /// <summary>
    /// 拖拽过程中的预览。
    ///
    /// 预览是**另画在 XAML 覆盖层上**的，不是直接画进像素：拖动时鼠标一秒能来
    /// 上百个位置，每次都真去改一遍像素（还连带重传一遍整张位图）会明显卡手。
    /// 松手之后才走引擎正式烙进像素 —— 那时候只画一次。
    ///
    /// ⚠️ 覆盖层是"半透明的整块屏幕 + 透明窗口"，所以这里的图形必须是**实色**的，
    /// 不能靠透明度区分层次：半透明会透出后面的冻结帧，看着像鬼影。
    /// </summary>
    private void ShowPreview(Point a, Point b)
    {
        HidePreview();

        double x = Math.Min(a.X, b.X) * _viewScale;
        double y = Math.Min(a.Y, b.Y) * _viewScale;
        double w = Math.Abs(b.X - a.X) * _viewScale;
        double h = Math.Abs(b.Y - a.Y) * _viewScale;
        double stroke = Math.Max(1, _lineWidth * _viewScale);
        var brush = new SolidColorBrush(Color.FromArgb(255, _cr, _cg, _cb));

        switch (_tool)
        {
            case SnipTool.Rect:
            case SnipTool.Mosaic:
            case SnipTool.Blur:
            {
                bool obfuscate = _tool != SnipTool.Rect;
                var rect = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = stroke,
                    Width = w,
                    Height = h,
                    // 打码/模糊的预览给一层淡色底，明确告诉用户"这一块会被处理掉"
                    Fill = obfuscate ? new SolidColorBrush(Color.FromArgb(40, _cr, _cg, _cb)) : null,
                    StrokeDashArray = obfuscate ? new DoubleCollection { 4, 3 } : null,
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                _preview = rect;
                break;
            }

            case SnipTool.Ellipse:
            {
                var el = new Ellipse
                {
                    Stroke = brush,
                    StrokeThickness = stroke,
                    Width = w,
                    Height = h,
                };
                Canvas.SetLeft(el, x);
                Canvas.SetTop(el, y);
                _preview = el;
                break;
            }

            case SnipTool.Line:
            case SnipTool.Arrow:
            {
                double ax = a.X * _viewScale, ay = a.Y * _viewScale;
                double bx = b.X * _viewScale, by = b.Y * _viewScale;

                Overlay.Children.Add(new Line
                {
                    X1 = ax, Y1 = ay, X2 = bx, Y2 = by,
                    Stroke = brush,
                    StrokeThickness = stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                });

                if (_tool == SnipTool.Arrow)
                {
                    // 箭头的头用一个多边形示意；真正落到像素上的是引擎里那套三角形
                    double dx = bx - ax, dy = by - ay;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len > 1)
                    {
                        double ux = dx / len, uy = dy / len;
                        double head = Math.Max(8, stroke * 3.2);
                        double half = head * 0.42;
                        Overlay.Children.Add(new Polygon
                        {
                            Fill = brush,
                            Points = new PointCollection
                            {
                                new(bx, by),
                                new(bx - ux * head - uy * half, by - uy * head + ux * half),
                                new(bx - ux * head + uy * half, by - uy * head - ux * half),
                            },
                        });
                    }
                }
                break;
            }
        }

        // 直线/箭头在上面已经自己加过了（它们不止一个图形），别再加第二遍
        if (_tool is SnipTool.Line or SnipTool.Arrow) return;
        if (_preview is not null) Overlay.Children.Add(_preview);
    }

    private void HidePreview()
    {
        Overlay.Children.Clear();
        _preview = null;
    }

    // ---- 画笔：走 PhotoMark 的增量接口，拖动时只重算受影响的那一小块 ----

    private void BeginPen(Point p)
    {
        if (_canvas is null) return;

        _penLive = new SnipAnnotation { Tool = SnipTool.Pen, R = _cr, G = _cg, B = _cb, Width = _lineWidth };
        _penLive.Xs.Add((float)p.X);
        _penLive.Ys.Add((float)p.Y);

        _penStroke = new MarkStroke { Tool = MarkTool.Pen, Width = _lineWidth, R = _cr, G = _cg, B = _cb };
        _penStroke.Add(p.X, p.Y);

        PhotoMark.BeginStroke(_canvas, _pw, _ph, 1);
        Refresh();
    }

    private void ExtendPen(Point p)
    {
        if (_penLive is null || _penStroke is null || _canvas is null || _origin is null) return;

        // 采样间隔太密的话点列会长得飞快，一两像素一个点够了
        int n = _penStroke.Count;
        if (n > 0)
        {
            double dx = p.X - _penStroke.Xs[n - 1];
            double dy = p.Y - _penStroke.Ys[n - 1];
            if (dx * dx + dy * dy < 4) return;
        }

        _penLive.Xs.Add((float)p.X);
        _penLive.Ys.Add((float)p.Y);

        int idx = _penStroke.Count;
        _penStroke.Add(p.X, p.Y);
        PhotoMark.AddPoint(_canvas, _origin, _pw, _ph, _penStroke, idx);

        Refresh();
    }

    private void EndPen()
    {
        if (_penLive is not null && _penStroke is not null && _canvas is not null && _origin is not null)
        {
            PhotoMark.FinishStroke(_canvas, _origin, _pw, _ph, _penStroke);
            _anns.Add(_penLive);
            Redraw();
        }
        _penLive = null;
        _penStroke = null;
    }

    // ---- 序号 ----

    private void PlaceNumber(Point p)
    {
        double r = SnapshotDraw.BadgeSize * _scale / 2.0;
        var a = new SnipAnnotation
        {
            Tool = SnipTool.Number,
            // 徽章以点击点为圆心，所以起止点都记圆心
            X0 = Math.Max(r, Math.Min(_pw - r, p.X)),
            Y0 = Math.Max(r, Math.Min(_ph - r, p.Y)),
            X1 = 0, Y1 = 0,
            Text = _numberSeq.ToString(),
            R = _cr, G = _cg, B = _cb,
            Width = _lineWidth,
        };
        _numberSeq++;
        _anns.Add(a);
        Redraw();
    }

    // ---- 文字 ----

    private void BeginTextInput(Point p)
    {
        TextInput.Visibility = Visibility.Visible;
        TextInput.Text = "";
        // 输入框挂在 Root 上，所以位置 = 选区左上 + 画布坐标换算过来的偏移
        TextInput.Margin = new Thickness(
            _sel.X + p.X * _viewScale,
            _sel.Y + p.Y * _viewScale,
            0, 0);
        TextInput.FontSize = 16;
        _textAnchor = p;

        TextInput.KeyDown -= TextInput_KeyDown;
        TextInput.KeyDown += TextInput_KeyDown;
        TextInput.LostFocus -= TextInput_LostFocus;
        TextInput.LostFocus += TextInput_LostFocus;

        TextInput.Focus(FocusState.Programmatic);
    }

    private void TextInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitTextInput();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelTextInput();
            Root.Focus(FocusState.Programmatic);
        }
    }

    private void TextInput_LostFocus(object sender, RoutedEventArgs e) => CommitTextInput();

    private void CommitTextInput()
    {
        if (TextInput.Visibility != Visibility.Visible) return;

        string text = TextInput.Text;
        TextInput.Visibility = Visibility.Collapsed;
        TextInput.Text = "";

        if (string.IsNullOrWhiteSpace(text)) return;

        if (!_editing || _canvas is null) return;

        _anns.Add(new SnipAnnotation
        {
            Tool = SnipTool.Text,
            X0 = _textAnchor.X,
            Y0 = _textAnchor.Y,
            Text = text,
            R = _cr, G = _cg, B = _cb,
            FontSize = 16,
            Width = _lineWidth,
        });
        Redraw();
    }

    private void CancelTextInput()
    {
        if (TextInput.Visibility == Visibility.Visible)
        {
            TextInput.Visibility = Visibility.Collapsed;
            TextInput.Text = "";
        }
    }

    // ===================== 撤销 / 重画 =====================

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_anns.Count == 0) return;
        _anns.RemoveAt(_anns.Count - 1);
        _message = "";
        Redraw();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_anns.Count == 0) return;
        _anns.Clear();
        _message = "";
        Redraw();
    }

    /// <summary>从原图重放所有标注。撤销、清除、新加一条之后都走这里。</summary>
    private void Redraw()
    {
        if (_canvas is null || _origin is null) return;
        SnapshotDraw.Replay(_canvas, _origin, _pw, _ph, _anns, _scale);
        PushPixels();
        RefreshStatus();
    }

    /// <summary>把画布内容推到屏幕。用 CRC 挡掉没变化时的无效刷新。</summary>
    private void Refresh() => PushPixels();

    private void PushPixels(bool force = false)
    {
        if (_bmp is null || _canvas is null) return;
        try
        {
            long crc = Checksum(_canvas);
            if (!force && crc == _crc) return;
            _crc = crc;

            using var stream = _bmp.PixelBuffer.AsStream();
            stream.Write(_canvas, 0, _pw * _ph * 4);
            _bmp.Invalidate();
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 刷新画布失败", ex);
        }
    }

    /// <summary>抽样算个校验和当"变没变"的判据。全量算太慢，抽样足够。</summary>
    private static long Checksum(byte[] px)
    {
        long h = 17;
        int step = Math.Max(4, (px.Length / 4096 / 4) * 4);
        for (int i = 0; i + 3 < px.Length; i += step)
        {
            h = h * 31 + px[i];
            h = h * 31 + px[i + 1];
            h = h * 31 + px[i + 2];
        }
        return h;
    }

    private void RefreshStatus()
    {
        SizeText.Text = $"{_pw} × {_ph}";

        string core = $"{ToolLabel(_tool)}　·　标注 {_anns.Count} 条";
        ModeText.Text = string.IsNullOrEmpty(_message) ? core : core + "　|　" + _message;
        HintText.Text = "拖拽绘制　Ctrl+Z 撤销　Ctrl+S 存档　Ctrl+C 复制　Enter 完成　Esc 重选";
    }

    private static string ToolLabel(SnipTool t)
    {
        foreach (var (tool, label, _) in ToolDefs)
            if (tool == t) return label;
        return "";
    }

    // ===================== 取色 =====================

    private void PickColorButton_Click(object sender, RoutedEventArgs e)
    {
        _pickColorMode = !_pickColorMode;
        HidePickerTip();
        PickColorButton.Background = new SolidColorBrush(
            _pickColorMode ? Color.FromArgb(255, 58, 110, 165) : Colors.Transparent);
        _message = _pickColorMode ? "在图上点一下取那个点的颜色" : "";
        Root.Focus(FocusState.Programmatic);
        RefreshStatus();
    }

    private void ShowPickerTip(Point stagePoint, int imgX, int imgY)
    {
        var c = PixelAt(imgX, imgY);
        PickerSwatch.Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
        PickerHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        double x = stagePoint.X + 16, y = stagePoint.Y + 16;
        if (x + 74 > Stage.Width) x = stagePoint.X - 74 - 6;
        if (y + 30 > Stage.Height) y = stagePoint.Y - 30 - 6;

        // PickerTip 挂在 Root 上，所以还要加上选区原点
        PickerTip.Margin = new Thickness(
            Math.Max(0, _sel.X + x), Math.Max(0, _sel.Y + y), 0, 0);
        PickerTip.Visibility = Visibility.Visible;
    }

    private void HidePickerTip() => PickerTip.Visibility = Visibility.Collapsed;

    private void PickColorAt(int x, int y)
    {
        var c = PixelAt(x, y);
        SetColor(c.R, c.G, c.B);
        _pickColorMode = false;
        PickColorButton.Background = new SolidColorBrush(Colors.Transparent);
        HidePickerTip();
        _message = $"取到颜色 #{c.R:X2}{c.G:X2}{c.B:X2}";
        RefreshStatus();
    }

    private (byte R, byte G, byte B) PixelAt(int x, int y)
    {
        if (_canvas is null) return (0, 0, 0);
        if (x < 0 || y < 0 || x >= _pw || y >= _ph) return (0, 0, 0);
        int i = (y * _pw + x) * 4;
        return (_canvas[i + 2], _canvas[i + 1], _canvas[i]);
    }

    // ===================== 保存 / 复制 / 识别 =====================

    /// <summary>把当前画布压成 PNG 字节。存文件和进剪贴板都走它。</summary>
    private byte[] Encode()
        => _canvas is null ? Array.Empty<byte>() : ImageEditService.FromPixels(_canvas, _pw, _ph);

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || !_editing) return;
        _saving = true;
        SaveAsButton.IsEnabled = false;
        try
        {
            var types = new List<(string Name, string Extension)>
            {
                ("PNG 图片", ".png"),
                ("JPEG 图片", ".jpg"),
            };

            var file = await StoragePicker.PickSaveFileAsync(
                WindowHandle, $"截图_{DateTime.Now:yyyyMMdd_HHmmss}", types);
            if (file is null) return;   // 用户取消了

            byte[] png = Encode();
            if (png.Length == 0)
            {
                _message = "编码失败，没能生成图片数据";
                RefreshStatus();
                return;
            }

            if (!ImageEditService.Save(png, file.Path))
            {
                _message = "写入失败，换个位置再试";
                RefreshStatus();
                return;
            }

            StartupLog.Write($"截图已另存为：{file.Path}");
            CloseSnip();
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 另存为失败", ex);
            _message = "保存失败：" + ex.Message;
            RefreshStatus();
        }
        finally
        {
            _saving = false;
            SaveAsButton.IsEnabled = true;
        }
    }

    /// <summary>Ctrl+S：直接把当前画布存进截图文件夹（不弹选位置，走"默认位置"）。</summary>
    private void SaveToLibrary()
    {
        if (!_editing) return;
        try
        {
            byte[] png = Encode();
            if (png.Length == 0) { _message = "编码失败"; RefreshStatus(); return; }

            string? path = SnipStore.Save(png);
            // ⚠️ 这里必须写全 System.IO.Path —— 本文件同时 using 了
            // Microsoft.UI.Xaml.Shapes（要 Rectangle/Ellipse/Line/Polygon），
            // 那里面也有个 Path，短名 `Path` 会报 CS0104。
            _message = path is null ? "存档失败，试试「另存为…」" : "已存进截图文件夹：" + System.IO.Path.GetFileName(path);
            if (path is not null) StartupLog.Write($"截图已存档：{path}");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 存档失败", ex);
            _message = "存档失败：" + ex.Message;
            RefreshStatus();
        }
    }

    private async Task<bool> CopyToClipboardAsync()
    {
        byte[] png = Encode();
        if (png.Length == 0) { _message = "编码失败"; RefreshStatus(); return false; }
        return await PutOnClipboardAsync(png);
    }

    /// <summary>
    /// 把 PNG 字节放进剪贴板。
    ///
    /// 剪贴板位图要的是"流"，不是文件 —— 走内存流，
    /// 免得为了放进剪贴板先往磁盘上写一个临时文件。
    /// </summary>
    private async Task<bool> PutOnClipboardAsync(byte[] png)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var outStream = stream.GetOutputStreamAt(0))
            {
                using var writer = new DataWriter(outStream);
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);

            StartupLog.Write($"SnipWindow: 截图已复制到剪贴板（{_pw}x{_ph}）");
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 复制到剪贴板失败", ex);
            _message = "复制失败：" + ex.Message;
            RefreshStatus();
            return false;
        }
    }

    // ===================== 完成（主路径）=====================

    private void DoneButton_Click(object sender, RoutedEventArgs e) => _ = FinishAsync();

    /// <summary>
    /// 「完成」——整条截图流水线真正的出口，也是绝大多数人唯一会按的按钮。
    ///
    /// 做三件事：进剪贴板 → （勾了存档就）落一份到截图文件夹 → 关掉覆盖层。
    /// 顺序不能换：剪贴板在前，因为"复制"这一下万一失败（剪贴板被别的软件占着，
    /// 这种情况真会发生），我们还来得及**不关窗**、让用户走「另存为…」把图救出来。
    /// 反过来先关窗再复制，失败就等于把这张图吞了。
    ///
    /// 一个刻意的取舍：**不弹"已复制"的提示**。QQ / 微信截图也是静默关掉的，
    /// 弹一个还要再点一下的确认框纯属添堵。
    /// </summary>
    private async Task FinishAsync()
    {
        if (_finishing || !_editing) return;
        _finishing = true;
        DoneButton.IsEnabled = false;

        try
        {
            byte[] png = Encode();
            if (png.Length == 0)
            {
                _message = "编码失败，没能生成图片数据";
                RefreshStatus();
                return;
            }

            bool copied = await PutOnClipboardAsync(png);

            string? archived = null;
            if (ArchiveSwitch.IsChecked == true) archived = SnipStore.Save(png);

            // 两件都没成：**别关窗**。关掉等于把用户刚标的这张图弄丢了。
            if (!copied && archived is null)
            {
                _message = "复制到剪贴板失败（可能被别的软件占着），也没能存档。图还在，点「另存为…」救一下。";
                RefreshStatus();
                return;
            }

            StartupLog.Write($"截图完成：{_pw}x{_ph}，剪贴板={(copied ? "好" : "失败")}，存档={archived ?? "没存"}");
            // 关窗之前先把工具条撤掉，免得关的过程中它闪一下
            ToolPanel.Visibility = Visibility.Collapsed;
            CloseSnip();
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 完成时出错", ex);
            _message = "出错了：" + ex.Message;
            RefreshStatus();
        }
        finally
        {
            _finishing = false;
            // 窗口可能在上面那段里已经关掉了，动控件要兜一下 ——
            // finally 里抛异常会把真正的结果盖掉，比原来那个问题更难查。
            try { DoneButton.IsEnabled = true; } catch { }
        }
    }

    // ===================== 识别文字 =====================

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editing) return;

        LayoutOcrPanel();
        OcrPanel.Visibility = Visibility.Visible;
        OcrText.Text = "识别中…";

        try
        {
            byte[] png = Encode();
            if (png.Length == 0) { OcrText.Text = "编码失败，没法识别。"; return; }

            var result = await PhotoOcr.RecognizeAsync(png);
            OcrText.Text = result.Ok
                ? (string.IsNullOrWhiteSpace(result.Text)
                    ? "这张图里没认出文字。"
                    : result.Text.Trim())
                : (result.Message ?? "识别失败。");
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 识别文字失败", ex);
            OcrText.Text = "识别失败：" + ex.Message;
        }
    }

    /// <summary>识别结果面板浮在选区右边；右边放不下就翻到左边，纵向跟选区对齐。</summary>
    private void LayoutOcrPanel()
    {
        try
        {
            double fullW = Root.ActualWidth > 0 ? Root.ActualWidth : _virt.Width / _scale;
            double fullH = Root.ActualHeight > 0 ? Root.ActualHeight : _virt.Height / _scale;
            const double Pad = 10;
            const double PanelW = 320;

            double h = Math.Max(160, Math.Min(420, _sel.Height));
            double x = _sel.X + _sel.Width + Pad;
            if (x + PanelW > fullW - Pad) x = _sel.X - PanelW - Pad;
            x = Math.Max(Pad, Math.Min(x, fullW - PanelW - Pad));

            double y = Math.Max(Pad, Math.Min(_sel.Y, fullH - h - Pad));

            CanvasLike(OcrPanel, x, y, PanelW, h);
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 摆识别面板失败", ex);
        }
    }

    private void OcrClose_Click(object sender, RoutedEventArgs e)
        => OcrPanel.Visibility = Visibility.Collapsed;

    private void OcrCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(OcrText.Text ?? "");
            Clipboard.SetContent(package);
            _message = "文字已复制";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StartupLog.Write("SnipWindow: 复制文字失败", ex);
        }
    }

    // ===================== 键盘（标注这几条快捷键）=====================

    /// <summary>
    /// 标注状态下认的键。返回 true 表示"吃掉了"，覆盖层的框选逻辑别再管。
    /// Esc 不在这里 —— "退出标注"和"取消整次截图"是同一段状态机的事，
    /// 放在 <see cref="SnipWindow"/> 那边一起判断。
    /// </summary>
    private bool EditorKeyDown(KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();

        switch (e.Key)
        {
            // 标注完直接回车就走，不用按 Ctrl —— QQ 截图就是这个手感
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                _ = FinishAsync();
                return true;

            case Windows.System.VirtualKey.Z when ctrl:
                e.Handled = true;
                Undo();
                return true;

            case Windows.System.VirtualKey.S when ctrl:
                e.Handled = true;
                SaveToLibrary();
                return true;

            case Windows.System.VirtualKey.C when ctrl:
                e.Handled = true;
                _ = CopyToClipboardAsync();
                return true;
        }

        return false;
    }

    private static bool IsCtrlDown()
    {
        try
        {
            return Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch { return false; }
    }
}
