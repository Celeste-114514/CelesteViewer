using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
// Microsoft.UI.Colors 的常量表（Windows.UI 那边只有 Color 结构体本身）
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

// Windows.Storage.Streams 里也有个 Buffer，和 System.Buffer 撞名，
// 同时 using 会让 `Buffer.BlockCopy` 报 CS0104。显式指向托管那个。
using Buffer = System.Buffer;

namespace CelesteGallery.Views;

/// <summary>
/// 截图第二步：标注编辑器。
///
/// 拿到的已经是用户框选好的那一块像素，在这里画箭头/框/文字、打码、认字，
/// 然后存文件或直接进剪贴板。
/// </summary>
/// <remarks>
/// ⚠️ 本窗口里有三套坐标，改代码时别弄混：
///   · <b>图像像素</b>——所有标注存的都是它，也是画像素时用的；canvas 的尺寸就是它；
///   · <b>有效像素（DIP）</b>——XAML 布局用的，Stage 的宽高是它；
///   · <b>屏幕物理像素</b>——只有摆窗口位置时才用到。
/// 换算只有 <see cref="ToImage"/> 一个入口，别在事件处理里手算。
/// </remarks>
public sealed partial class SnipEditorWindow : Window
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

    /// <summary>窗口四周给工具条和状态条留的高度（有效像素），算窗口尺寸时用。</summary>
    private const double ChromeHeight = 88;

    private static SnipEditorWindow? _current;

    private readonly byte[] _origin;    // 刚截下来、一个字都没改的像素
    private readonly byte[] _canvas;    // 工作画布（标注直接画在上面）
    private readonly int _w;
    private readonly int _h;
    private readonly double _scale;     // 图像像素 ÷ 有效像素

    private readonly List<SnipAnnotation> _anns = new();
    private WriteableBitmap? _bmp;

    private double _viewScale = 1.0;    // 图像像素 → 有效像素（Stage 上一格等于几个图像像素的倒数）
    private long _crc;

    private SnipTool _tool = SnipTool.Rect;
    private byte _cr = 255, _cg = 59, _cb = 48;
    private double _lineWidth = 3;
    private int _numberSeq = 1;
    private bool _pickColorMode;
    private bool _saving;

    // 拖拽中的状态（坐标一律是图像像素）
    private bool _dragging;
    private Point _dragStart;
    private Point _dragNow;
    private FrameworkElement? _preview;
    private SnipAnnotation? _penLive;
    private MarkStroke? _penStroke;

    private readonly Button[] _toolButtons = new Button[ToolDefs.Length];

    /// <summary>打开编辑器。已经在编辑就忽略重复触发。</summary>
    public static void Show(CapturedFrame frame)
    {
        if (_current is not null)
        {
            StartupLog.Write("截图编辑器：已经开着一个了，忽略重复触发");
            return;
        }

        try
        {
            _current = new SnipEditorWindow(frame);
            _current.Activate();
            WindowForeground.BringToFront(_current.WindowHandle);
        }
        catch (Exception ex)
        {
            _current = null;
            StartupLog.Write("截图编辑器：打开失败", ex);
        }
    }

    private SnipEditorWindow(CapturedFrame frame)
    {
        InitializeComponent();

        _w = frame.Width;
        _h = frame.Height;
        _scale = frame.Scale is > 0 ? frame.Scale : 1.0;

        // 拷两份出来：origin 当"撤销/重放的底"，canvas 是干活的那份。
        // 不能直接改 frame.Pixels —— 那是 SnipWindow 交过来的，还要留着做对照。
        _origin = new byte[_w * _h * 4];
        _canvas = new byte[_w * _h * 4];
        Buffer.BlockCopy(frame.Pixels, 0, _origin, 0, Math.Min(frame.Pixels.Length, _origin.Length));
        Buffer.BlockCopy(_origin, 0, _canvas, 0, _canvas.Length);

        AppIcon.ApplyToWindow(AppWindow);

        BuildTools();
        BuildPalette();
        LayoutWindow();

        // ⚠️ 挂 Root 的 Loaded，不是 Window 的 ——
        // WinUI3 的 Window 本身没有 Loaded 事件（它不是 FrameworkElement），
        // 位图要等界面挂上去、控件尺寸算出来之后再塞。
        Root.Loaded += OnLoaded;
        Closed += (_, _) => _current = null;

        // 键盘快捷键挂在根上；Root 要能拿到焦点才收得到键
        Root.KeyDown += OnKeyDown;
        Root.IsTabStop = true;

        Stage.PointerPressed += OnStagePressed;
        Stage.PointerMoved += OnStageMoved;
        Stage.PointerReleased += OnStageReleased;
        Stage.PointerExited += (_, _) => HidePickerTip();

        WidthSlider.Value = _lineWidth;
        RefreshStatus();
    }

    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // ===================== 窗口外形 =====================

    /// <summary>
    /// 按图的大小定窗口尺寸：图小就原尺寸贴出来（1:1 看最舒服），
    /// 图大到超过屏幕就等比缩到能装下。
    /// </summary>
    private void LayoutWindow()
    {
        try
        {
            // 截图在哪个显示器上，编辑窗口就开在哪个显示器上 ——
            // 否则双屏用户会看到"我在副屏截的图，编辑窗跑到主屏去了"。
            // 判据用鼠标当前位置：用户刚拖完框，鼠标必然还在那台显示器上。
            var area = DisplayArea.Primary;
            if (GetCursorPos(out POINT pt))
            {
                var picked = DisplayArea.GetFromPoint(
                    new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary);
                if (picked is not null) area = picked;
            }

            var work = area.WorkArea;

            double naturalW = _w / _scale;    // 换成有效像素
            double naturalH = _h / _scale;

            // 留 8% 边距，别顶到任务栏和屏幕边
            double availW = work.Width / _scale * 0.92;
            double availH = (work.Height / _scale - ChromeHeight) * 0.94;

            double k = 1.0;
            if (naturalW > availW) k = Math.Min(k, availW / naturalW);
            if (naturalH > availH) k = Math.Min(k, availH / naturalH);
            if (k <= 0 || double.IsNaN(k)) k = 1.0;

            double dispW = Math.Max(120, Math.Round(naturalW * k));
            double dispH = Math.Max(80, Math.Round(naturalH * k));

            Stage.Width = dispW;
            Stage.Height = dispH;
            _viewScale = dispW / _w;

            int winW = (int)Math.Round(dispW + 24);
            int winH = (int)Math.Round(dispH + ChromeHeight + 10);
            AppWindow.Resize(new SizeInt32(winW, winH));

            int x = work.X + (int)Math.Max(0, (work.Width - winW * _scale) / 2);
            int y = work.Y + (int)Math.Max(0, (work.Height - winH * _scale) / 2);
            AppWindow.Move(new PointInt32((int)Math.Round(x / _scale), (int)Math.Round(y / _scale)));
        }
        catch (Exception ex)
        {
            StartupLog.Write("截图编辑器：摆窗口失败（不影响使用）", ex);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _bmp = new WriteableBitmap(_w, _h);
            PushPixels();
            ViewImage.Source = _bmp;
            ViewImage.Width = Stage.Width;
            ViewImage.Height = Stage.Height;

            Root.Focus(FocusState.Programmatic);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            StartupLog.Write("截图编辑器：显示画面失败", ex);
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
                Padding = new Thickness(10, 5, 10, 5),
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
        CancelTextInput();
        HidePreview();

        for (int i = 0; i < ToolDefs.Length; i++)
        {
            bool on = ToolDefs[i].Tool == tool;
            _toolButtons[i].Background = new SolidColorBrush(
                on ? Color.FromArgb(255, 58, 110, 165) : Colors.Transparent);
        }

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
    }

    private void WidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WidthText is null) return;
        _lineWidth = Math.Round(e.NewValue);
        WidthText.Text = ((int)_lineWidth).ToString();
    }

    // ===================== 鼠标 =====================

    /// <summary>Stage 上的坐标 → 图像像素。整个窗口只有这一处换算，别在别处手算。</summary>
    private Point ToImage(Point stagePoint)
    {
        double k = _viewScale > 0 ? _viewScale : 1.0;
        return new Point(stagePoint.X / k, stagePoint.Y / k);
    }

    private void OnStagePressed(object sender, PointerRoutedEventArgs e)
    {
        CancelTextInput();

        var p = ToImage(e.GetCurrentPoint(Stage).Position);

        if (_pickColorMode)
        {
            PickColorAt((int)p.X, (int)p.Y);
            return;
        }

        Stage.CapturePointer(e.Pointer);
        _dragging = true;
        _dragStart = p;
        _dragNow = p;

        switch (_tool)
        {
            case SnipTool.Number:
                // 序号是"点一下就放"，不用拖
                _dragging = false;
                try { Stage.ReleasePointerCapture(e.Pointer); } catch { }
                PlaceNumber(p);
                break;

            case SnipTool.Text:
                _dragging = false;
                try { Stage.ReleasePointerCapture(e.Pointer); } catch { }
                BeginTextInput(p);
                break;

            case SnipTool.Pen:
                BeginPen(p, e);
                break;
        }
    }

    private void OnStageMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Stage).Position;
        var p = ToImage(pos);

        if (_pickColorMode)
        {
            ShowPickerTip(pos, (int)p.X, (int)p.Y);
            return;
        }

        if (!_dragging) return;
        _dragNow = p;

        if (_tool == SnipTool.Pen) { ExtendPen(p); return; }

        ShowPreview(_dragStart, p);
    }

    private void OnStageReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
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

    // ===================== 各支笔的实时行为 =====================

    /// <summary>
    /// 拖拽过程中的预览。
    ///
    /// 预览是**另画在 XAML 覆盖层上**的，不是直接画进像素：拖动时鼠标一秒能来
    /// 上百个位置，每次都真去改一遍像素（还连带重传一遍整张位图）会明显卡手。
    /// 松手之后才走引擎正式烙进像素 —— 那时候只画一次。
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

    private void BeginPen(Point p, PointerRoutedEventArgs e)
    {
        _penLive = new SnipAnnotation { Tool = SnipTool.Pen, R = _cr, G = _cg, B = _cb, Width = _lineWidth };
        _penLive.Xs.Add((float)p.X);
        _penLive.Ys.Add((float)p.Y);

        _penStroke = new MarkStroke { Tool = MarkTool.Pen, Width = _lineWidth, R = _cr, G = _cg, B = _cb };
        _penStroke.Add(p.X, p.Y);

        PhotoMark.BeginStroke(_canvas, _w, _h, 1);
        Refresh();
    }

    private void ExtendPen(Point p)
    {
        if (_penLive is null || _penStroke is null) return;

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
        PhotoMark.AddPoint(_canvas, _origin, _w, _h, _penStroke, idx);

        Refresh();
    }

    private void EndPen()
    {
        if (_penLive is not null && _penStroke is not null)
        {
            PhotoMark.FinishStroke(_canvas, _origin, _w, _h, _penStroke);
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
            X0 = Math.Max(r, Math.Min(_w - r, p.X)),
            Y0 = Math.Max(r, Math.Min(_h - r, p.Y)),
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
        TextInput.Margin = new Thickness(p.X * _viewScale, p.Y * _viewScale, 0, 0);
        TextInput.FontSize = 16;
        _textAnchor = p;

        TextInput.KeyDown -= TextInput_KeyDown;
        TextInput.KeyDown += TextInput_KeyDown;
        TextInput.LostFocus -= TextInput_LostFocus;
        TextInput.LostFocus += TextInput_LostFocus;

        TextInput.Focus(FocusState.Programmatic);
    }

    private Point _textAnchor;

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
        }
    }

    private void TextInput_LostFocus(object sender, RoutedEventArgs e) => CommitTextInput();

    private void CommitTextInput()
    {
        if (TextInput.Visibility != Visibility.Visible) return;

        string text = TextInput.Text;
        TextInput.Visibility = Visibility.Collapsed;
        TextInput.Text = "";
        Root.Focus(FocusState.Programmatic);

        if (string.IsNullOrWhiteSpace(text)) return;

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
        Redraw();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_anns.Count == 0) return;
        _anns.Clear();
        Redraw();
    }

    /// <summary>从原图重放所有标注。撤销、清除、新加一条之后都走这里。</summary>
    private void Redraw()
    {
        SnapshotDraw.Replay(_canvas, _origin, _w, _h, _anns, _scale);
        Refresh();
        RefreshStatus();
    }

    /// <summary>把画布内容推到屏幕。用 CRC 挡掉没变化时的无效刷新。</summary>
    private void Refresh()
    {
        PushPixels();
        RefreshStatus();
    }

    private void PushPixels()
    {
        if (_bmp is null) return;
        try
        {
            long crc = Checksum(_canvas);
            if (crc == _crc) return;
            _crc = crc;

            using var stream = _bmp.PixelBuffer.AsStream();
            stream.Write(_canvas, 0, _w * _h * 4);
            _bmp.Invalidate();
        }
        catch (Exception ex)
        {
            StartupLog.Write("截图编辑器：刷新画面失败", ex);
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
        StatusText.Text = $"{_w} × {_h} 像素　·　标注 {_anns.Count} 条　·　当前：{ToolLabel(_tool)}";
        HintText.Text = "Ctrl+Z 撤销　Ctrl+S 保存　Ctrl+C 复制　Esc 关闭";
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
        StatusText.Text = _pickColorMode ? "取色：在图上点一下取那个点的颜色" : "";
        if (!_pickColorMode) RefreshStatus();
    }

    private void ShowPickerTip(Point stagePoint, int imgX, int imgY)
    {
        var c = PixelAt(imgX, imgY);
        PickerSwatch.Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
        PickerHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        double x = stagePoint.X + 16, y = stagePoint.Y + 16;
        if (x + 74 > Stage.Width) x = stagePoint.X - 74 - 6;
        if (y + 30 > Stage.Height) y = stagePoint.Y - 30 - 6;

        PickerTip.Margin = new Thickness(Math.Max(0, x), Math.Max(0, y), 0, 0);
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
        RefreshStatus();
    }

    private (byte R, byte G, byte B) PixelAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _w || y >= _h) return (0, 0, 0);
        int i = (y * _w + x) * 4;
        return (_canvas[i + 2], _canvas[i + 1], _canvas[i]);
    }

    // ===================== 保存 / 复制 / 识别 =====================

    /// <summary>把当前画布压成 PNG 字节。存文件和进剪贴板都走它。</summary>
    private byte[] Encode() => ImageEditService.FromPixels(_canvas, _w, _h);

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
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
                StatusText.Text = "编码失败，没能生成图片数据";
                return;
            }

            if (!ImageEditService.Save(png, file.Path))
            {
                StatusText.Text = "写入失败，换个位置再试";
                return;
            }

            StartupLog.Write($"截图已保存：{file.Path}");
            StatusText.Text = "已保存：" + file.Path;
            Close();
        }
        catch (Exception ex)
        {
            StartupLog.Write("截图编辑器：保存失败", ex);
            StatusText.Text = "保存失败：" + ex.Message;
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        try
        {
            byte[] png = Encode();
            if (png.Length == 0) { StatusText.Text = "编码失败"; return; }

            // 剪贴板位图要的是"流"，不是文件。走内存流，
            // 免得为了放进剪贴板先往磁盘上写一个临时文件。
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

            StartupLog.Write($"截图已复制到剪贴板（{_w}x{_h}）");
            StatusText.Text = "已复制到剪贴板，去聊天窗口 Ctrl+V 就行";
        }
        catch (Exception ex)
        {
            StartupLog.Write("截图编辑器：复制失败", ex);
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
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
            StartupLog.Write("截图编辑器：识别文字失败", ex);
            OcrText.Text = "识别失败：" + ex.Message;
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
            StatusText.Text = "文字已复制";
        }
        catch (Exception ex)
        {
            StartupLog.Write("截图编辑器：复制文字失败", ex);
        }
    }

    // ===================== 键盘 =====================

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                _current = null;
                Close();
                break;

            case Windows.System.VirtualKey.Z when ctrl:
                e.Handled = true;
                Undo();
                break;

            case Windows.System.VirtualKey.S when ctrl:
                e.Handled = true;
                SaveButton_Click(this, new RoutedEventArgs());
                break;

            case Windows.System.VirtualKey.C when ctrl:
                e.Handled = true;
                _ = CopyAsync();
                break;

            case Windows.System.VirtualKey.Enter when ctrl:
                e.Handled = true;
                _ = CopyAsync();
                break;
        }
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

    // ---- Win32：只用来问"鼠标在哪台显示器上" ----

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);
}
