using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
// Slider 的 ValueChanged 事件参数（RangeBaseValueChangedEventArgs）在这个命名空间下
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
// 直方图是拿 Polygon / Polyline 一笔一笔画出来的（见 RenderHistogram）
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
// Windows.UI.Color —— Color 结构体在这个命名空间下（Microsoft.UI 那边只有 Colors 常量表）
using Windows.UI;

// ⚠️ Windows.Storage.Streams 里也有个 Buffer（WinRT 的字节缓冲），
// 和 System.Buffer 撞名，同时 using 之后 `Buffer.BlockCopy` 报 CS0104 不明确引用。
// 这里显式指向托管那个 —— 我们要的从来都是 BlockCopy
using Buffer = System.Buffer;

// ⚠️ 同理：Microsoft.UI.Xaml.Shapes 里有个画路径的 Path 控件，
// 和 System.IO.Path 撞名，全文件几十处 `Path.GetFileName` 会一起报 CS0104。
// 这里把 Path 钉死在 System.IO 那份，画图要用的 Polygon / Polyline / Line 直接写简单名
using Path = System.IO.Path;

namespace CelesteGallery.Views;

/// <summary>
/// 单窗口看图界面（路线图第 2 步）。
///
/// 三条设计原则：
///   1. **先出字、后出图**：切图时立刻更新文件名和第几张，图片解码完再填上去。
///      哪怕大图要 100 毫秒，用户也感觉不到卡 —— 因为界面已经动了。
///   2. **解码尺寸跟着窗口走**：解码时就按窗口大小的两倍去解（不是先解原图再缩小），
///      这样 8000px 的巨图和 800px 的小图打开速度几乎一样快。
///   3. **预读前后各两张**：按方向键翻页时图已经在内存里了。
/// </summary>
public sealed partial class ViewerPage : Page
{
    private readonly ImageDecodePipeline _decoder = new();
    private readonly ThumbnailService _thumbs;
    private readonly FolderIndex _index = new();

    // 后台线程上调 GetForCurrentThread() 会返回 null，必须在构造时（此时必在 UI 线程）抓下来
    private readonly DispatcherQueue _uiQueue;

    private int _displayWidth;
    private int _displayHeight;
    private string? _pendingPath;
    private CancellationTokenSource? _loadCts;
    private int _fitRetries;

    public ViewerPage()
    {
        InitializeComponent();

        _uiQueue = DispatcherQueue.GetForCurrentThread();
        _thumbs = new ThumbnailService(_decoder, diskCache: DiskThumbnailCache.Shared);

        // 标题栏最左边那张小图（和 exe 图标同一套图案）。
        // 读文件是异步的，读完自己填上去；读不到就算了，标题栏少个图标不影响用。
        _ = LoadTitleIconAsync();

        // Page 默认不吃键盘事件，开了这个再请求焦点才能响应方向键
        IsTabStop = true;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        SizeChanged += OnSizeChanged;
        Scroller.ViewChanged += OnScrollerViewChanged;

        // 页面都卸掉了就别再跑缩放动画 —— CompositionTarget.Rendering 是静态事件，
        // 挂着不摘会一直回调到一个已经不在界面上的页面
        Unloaded += (_, _) => StopZoomAnimation();

        // 第三个参数 true = 即使 ScrollViewer 内部已经处理过滚轮，我们也看得到。
        // 不加的话滚轮事件会被 ScrollViewer 吞掉，做不了"滚轮缩放"。
        Scroller.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPointerWheel),
            true);

        // 同理：按钮装不下时，滚轮在工具条上改做横向滚动。
        // 必须带 handledEventsToo，否则被内层的 ScrollViewer 吞掉。
        BottomBar.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnBottomBarWheel),
            true);

        // 标记层是**盖在图片上面**的一层，不在 Scroller 里面 ——
        // 指针落在它上面时，滚轮事件根本不会经过 Scroller，
        // 于是"标记模式下滚轮缩放"就失效了（和提示文字对不上）。
        // 单独挂一份，OnPointerWheel 内部是按 Scroller 取坐标的，共用没问题。
        // 这一层平时是折叠的，收不到事件，所以不会和上面那条打架。
        MarkLayer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPointerWheel),
            true);

        // 背景层同理：它也是盖在图片上的一层，指针落在它上面时滚轮不经过 Scroller。
        // 不补这一份，"背景模式下滚轮缩放"就是哑的（提示文字却写着能缩放）
        BackgroundLayer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPointerWheel),
            true);

        // 滚轮改去管缩放之后，画面就没法用滚轮滚了，得靠按住鼠标拖动来补。
        // 放大后要细看边角时只能拖，光靠滚动条太别扭。
        Scroller.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPointerPressed),
            true);
        Scroller.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPointerMoved),
            true);
        Scroller.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPointerReleased),
            true);
        Scroller.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnPointerCaptureLost),
            true);
    }

    /// <summary>
    /// 承载这个页面的窗口（主窗口，或者独立看图窗口）。
    ///
    /// 全屏、文件选择框、按返回之后去哪 —— 这些都只有窗口知道答案，
    /// 而 Page 自己拿不到它属于哪个窗口，所以由外面塞进来。
    /// 没塞就退回主窗口（App.Instance），老路径照样能跑。
    /// </summary>
    public IViewerHost? Host { get; set; }

    private IViewerHost? SafeHost => Host ?? App.Instance;

    /// <summary>顶部那条交给窗口当标题栏。</summary>
    public UIElement TitleBarElement => TitleBar;

    /// <summary>把应用图标填到标题栏最左边（读不到就留空，不报错也不重试）。</summary>
    private async Task LoadTitleIconAsync()
    {
        var icon = await AppIcon.LoadPngAsync();
        if (icon is not null) TitleIcon.Source = icon;
    }

    /// <summary>
    /// 退回缩略图墙时把大图放掉。
    /// 一张 8000px 的图光是像素就 190MB，藏着不显示纯属浪费内存。
    /// </summary>
    public void ReleaseImage()
    {
        StopZoomAnimation();
        ImageView.Source = null;
        ImageView.Width = 0;
        ImageView.Height = 0;
        ImageView.Margin = new Microsoft.UI.Xaml.Thickness(0);
        _padX = 0;
        _padY = 0;
        _zoom = 1.0;
        _displayWidth = 0;
        _displayHeight = 0;
        _editBytes = null;
        ReleaseLookPixels();
    }

    /// <summary>
    /// 打开一张图。还没加载完就先记下来，等 Loaded 再处理。
    /// </summary>
    public void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowEmpty("把图片拖到这里");
            return;
        }

        if (!IsLoaded)
        {
            _pendingPath = path;
            return;
        }

        _ = LoadAsync(path);
    }

    // ===== 加载与显示 =====

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupLog.Write("ViewerPage: OnLoaded");

        // 把顶部那条交给窗口当标题栏：窗口能拖着走，右上角还留着最小化/关闭
        SafeHost?.SetTitleBar(TitleBar);
        Focus(FocusState.Programmatic);

        // 首次布局完成后再量一次工具条宽度：SizeChanged 之前它是 0
        UpdateBottomBarWidth();

        if (_pendingPath is not null)
        {
            string path = _pendingPath;
            _pendingPath = null;
            _ = LoadAsync(path);
        }
        else
        {
            ShowEmpty("把图片拖到这里");
        }

        StartupLog.Write("ViewerPage: 就绪");
    }

    private async Task LoadAsync(string path)
    {
        StartupLog.Write($"LoadAsync 开始 → {path}");

        // 压缩包就索引包里的所有页，普通图片就索引它所在的整个目录
        int count = ArchiveIndex.IsArchiveFile(path)
            ? _index.LoadArchive(path)
            : _index.LoadFromFile(path);
        StartupLog.Write($"索引建立完成，共 {count} 张图，当前第 {_index.Position} 张");

        if (count == 0)
        {
            ShowEmpty("这个文件夹里没有能打开的图");
            return;
        }

        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        // 换图就退出裁剪：选框是按"这一张"算的，换张图还留着毫无意义
        ExitSelectMode();

        string? path = _index.CurrentPath;
        if (path is null)
        {
            ShowEmpty("把图片拖到这里");
            return;
        }

        StartupLog.Write($"ShowCurrentAsync → 第 {_index.Position}/{_index.Count} 张: {Path.GetFileName(path)}");

        // 快速连按方向键时，把上一次还没解完的取消掉，
        // 否则会出现"先解完的旧图后到，把新图盖掉"的错位
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        CancellationToken ct = _loadCts.Token;

        // 先把文字刷上去，让界面立刻有反应
        TitleText.Text = Path.GetFileName(path);
        TitleDot.Visibility = Visibility.Visible;
        CounterText.Text = _index.Count > 0 ? $"{_index.Position} / {_index.Count}" : "";
        SlideCounterText.Text = CounterText.Text;
        EmptyState.Visibility = Visibility.Collapsed;

        // 文字面板里的结果是**上一张**的，留着会误导 —— 换图就收起来，
        // 想看新图上的字再按一次 T
        if (_ocrVisible) SetOcrVisible(false);

        int decodeSize = ComputeDecodeSize();

        var bitmap = await _thumbs.GetAsync(path, decodeSize, ct);
        if (bitmap is null || ct.IsCancellationRequested)
        {
            if (!ct.IsCancellationRequested)
            {
                StartupLog.Write($"解码失败（两个解码器都试过了）→ {Path.GetFileName(path)}");
                ShowEmpty("这张图打不开，可能是文件损坏或格式不支持");
            }
            return;
        }

        StartupLog.Write($"解码成功 {bitmap.PixelWidth}x{bitmap.PixelHeight}（请求 {decodeSize}px，用 {bitmap.DecoderName}）");

        var source = await BitmapHelper.ToSourceAsync(bitmap);
        if (source is null || ct.IsCancellationRequested)
        {
            if (!ct.IsCancellationRequested)
            {
                StartupLog.Write("位图转显示源失败");
                ShowEmpty("这张图打不开，可能是文件损坏或格式不支持");
            }
            return;
        }

        ImageView.Source = source;
        _displayWidth = bitmap.PixelWidth;
        _displayHeight = bitmap.PixelHeight;

        // 图库"幻灯片放映"按钮在图还没解出来时就点了：等这张解完立刻进放映
        if (_pendingSlide)
        {
            _pendingSlide = false;
            EnterSlideMode();
        }

        // 翻页 = 上一张的编辑和风格统统作废（没另存就是没另存）
        _editBytes = null;
        _look = default;
        _lookRendered = default;
        _basePixels = null;
        SyncSliders();

        // 直方图用的就是刚解出来的这段像素。
        // 记下来是为了"面板本来就开着"时不用为统计再解一次；
        // 面板关着就完全不做统计 —— 翻页时白算一张图没意义，等按 H 那一刻再算。
        _lastDecoded = bitmap;
        if (_histVisible) _ = RefreshHistogramAsync();

        SizeText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";

        // 换图一律回到 100%，不沿用上一张的缩放（ResetZoomToActual 里有说明）。
        // 100% 是固定值，不依赖视口尺寸，所以不用像"适应窗口"那样等布局算完再重试。
        //
        // **放映中是唯一的例外**：放映看的是"整张照片"，
        // 一张 4000px 的图在屏幕上只显示左上角，那还叫什么观看 ——
        // 所以放映时换成适应窗口，窗口大小变了也跟着重新适应（见 IsAtFitZoom）。
        _uiQueue.TryEnqueue(DispatcherQueuePriority.Low,
            _sliding ? ApplyFitZoom : ResetZoomToActual);

        // 参数面板和预读都不阻塞显示
        _ = LoadInfoAsync(path, ct);
        _thumbs.Prefetch(_index.Neighbors(2), decodeSize);

        // 风格面板开着时翻页：给新图重新准备底图和滤镜缩略图
        if (_lookVisible) _ = PrepareLookAsync();
    }

    private async Task LoadInfoAsync(string path, CancellationToken ct)
    {
        var info = await _decoder.ProbeAsync(path, includeMetadata: true, ct);
        if (info is null || ct.IsCancellationRequested) return;

        InfoName.Text = info.FileName;
        InfoPath.Text = path;
        InfoPixels.Text = $"{info.PixelWidth} × {info.PixelHeight}　{info.Megapixels:F1} MP";
        InfoFileSize.Text = FormatSize(info.FileSize);
        InfoDecoder.Text = $"解码器：{info.DecoderName}";

        bool hasExif = info.CameraModel is not null
                    || info.LensModel is not null
                    || info.FNumber is not null
                    || info.DateTaken is not null;

        InfoCamera.Text = hasExif
            ? $"{info.CameraMake} {info.CameraModel}".Trim()
            : "";
        InfoLens.Text = info.LensModel ?? "";

        string exposure = string.Join("　",
            new[]
            {
                info.FocalLength,
                info.FNumber is null ? null : $"f/{info.FNumber}",
                info.ExposureTime,
                info.IsoSpeed is null ? null : $"ISO {info.IsoSpeed}",
            }.Where(s => !string.IsNullOrEmpty(s)));
        InfoExposure.Text = exposure;

        InfoDate.Text = info.DateTaken?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        InfoNoExif.Visibility = hasExif ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 解码尺寸：按窗口大小的两倍去解。
    /// 这样放大到 200% 仍然清晰，又不用每次都把整张 8000px 巨图解开。
    /// </summary>
    private int ComputeDecodeSize()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double longest = Math.Max(ActualWidth, ActualHeight) * scale;
        int size = (int)Math.Ceiling(longest * 2);
        return Math.Clamp(size, 512, 8000);
    }

    /// <summary>
    /// 适应窗口 —— **瞬时**版本。给"换图"和"窗口大小变了"用。
    ///
    /// 这两处刻意不做过渡：换图时上一张的缩放状态本就该立刻清掉，
    /// 而窗口拖动时每帧都在重新适应，加动画只会互相打架。
    /// </summary>
    private void ApplyFitZoom()
    {
        if (!TryComputeFitZoom(out double factor)) return;

        StopZoomAnimation();
        SetZoom(factor, default, keepAnchor: false);
    }

    /// <summary>
    /// 换图：**重置成 100%**（原尺寸，1 个图像像素 = 1 个屏幕像素），
    /// 不沿用上一张的缩放比例。
    ///
    /// 之前这里是"适应窗口"，但用户要求切换图片一律回到 100% ——
    /// 看图时"上一张放大到哪"不该带到下一张，否则连按方向键时
    /// 每张图的起始大小都不一样，很跳。
    ///
    /// 和 ApplyFitZoom 不同，这里不需要等布局算完：100% 是固定值，
    /// 跟视口多大没关系，所以也没有那套重试逻辑。
    /// </summary>
    private void ResetZoomToActual()
    {
        StopZoomAnimation();

        _zoom = 1.0;
        ApplyZoomToLayout();

        Scroller.UpdateLayout();
        Scroller.ChangeView(0, 0, null, true);
        UpdateZoomText();
    }

    /// <summary>适应窗口 —— **带过渡**版本。给按钮和数字键 0 用。</summary>
    private void ApplyFitZoomAnimated()
    {
        if (!TryComputeFitZoom(out double factor)) return;

        // 锚点取视口正中心：缩小之后图会自然回到正中，
        // 正是点"适应窗口"时想要的效果（和滚轮缩放走同一套代码）
        StartZoomAnimation(factor, new Windows.Foundation.Point(
            Scroller.ViewportWidth / 2,
            Scroller.ViewportHeight / 2));
    }

    /// <summary>
    /// 算出"适应窗口"该用多大缩放。视口尺寸还是 0（布局没算完）时会自己再等一拍。
    /// </summary>
    private bool TryComputeFitZoom(out double factor)
    {
        factor = 1.0;

        if (_displayWidth <= 0 || _displayHeight <= 0) return false;

        double vw = Scroller.ViewportWidth;
        double vh = Scroller.ViewportHeight;

        if (vw <= 0 || vh <= 0)
        {
            // 布局还没算完，再等一拍；试几次还不行就放弃，免得死循环
            if (_fitRetries++ < 10)
                _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyFitZoom);
            return false;
        }

        factor = Math.Clamp(
            Math.Min(vw / _displayWidth, vh / _displayHeight),
            MinZoom,
            MaxZoom);
        return true;
    }

    private void ShowEmpty(string message)
    {
        ExitSelectMode();

        // 图都没了，放映和文字面板就都没有意义了。
        // 这两个是"挂着状态"的界面，不跟着关掉的话，下次打开新图会带着旧状态进来
        if (_sliding) ExitSlideMode();
        if (_ocrVisible) SetOcrVisible(false);

        StopZoomAnimation();
        ImageView.Source = null;
        ImageView.Width = 0;
        ImageView.Height = 0;
        ImageView.Margin = new Microsoft.UI.Xaml.Thickness(0);
        _padX = 0;
        _padY = 0;
        _zoom = 1.0;
        _displayWidth = 0;
        _displayHeight = 0;
        _editBytes = null;
        TitleText.Text = "";
        TitleDot.Visibility = Visibility.Collapsed;
        SizeText.Text = "";
        CounterText.Text = "";
        EmptyText.Text = message;
        EmptyState.Visibility = Visibility.Visible;

        // 直方图跟着没数据了。面板本身不关（用户开着它就让它留着），
        // 只把上一次的图形和数据清掉 —— 留着上一张图的直方图比空着更容易误导
        _lastDecoded = null;
        _histForPath = null;
        if (_histVisible) _ = RefreshHistogramAsync();
    }

    // ===== 翻页 =====

    private void GoNext()
    {
        if (_index.MoveNext()) _ = ShowCurrentAsync();
    }

    private void GoPrevious()
    {
        if (_index.MovePrevious()) _ = ShowCurrentAsync();
    }

    // ===== 缩放 =====

    /*
      ===== 缩放动画是怎么做的，以及为什么这么做 =====

      先说**不**用什么：

      1. 不用 ScrollViewer 自带的补间（ChangeView 最后一个参数传 false）。
         它时长和曲线都改不了，而且不管锚点 —— 动画过程中"指哪儿放大到哪儿"
         会飘；连续滚轮时还会一层叠一层。

      2. 不用 DispatcherQueueTimer 当驱动。
         这是上一版抖动的原因：定时器的 16ms 和显示器刷新根本不同步。
         在 240Hz 屏上，16ms 相当于"每 3.8 帧才动一次"，而且每次都错开一点点相位，
         于是每步停留的帧数在 3/4 之间来回变 —— 肉眼看就是一顿一顿的。

      现在这样：
        · 用 **CompositionTarget.Rendering** 驱动，它在每一帧真正渲染之前触发，
          天然跟着显示器刷新走（240Hz 就是每秒动 240 次）；
        · 每帧按 **指数逼近** 往目标靠：`缩放 += 剩余距离 × (1 - exp(-dt/τ))`。
          这个写法和帧率无关 —— 60Hz 和 240Hz 滑完全程的**用时一样**，
          只是 240Hz 更顺。用「固定时长 + 缓动曲线」的写法就做不到这点，
          因为中途插进来的新目标会让曲线重新起跑。

      每帧算完还是交给 ChangeView(..., disableAnimation: true) 落地，
      不让它再叠自己的动画。
    */

    /// <summary>
    /// 时间常数（秒）。越小越跟手、越大越"飘"。
    ///
    /// 0.05 ≈ 视觉上 150 毫秒滑到位（指数衰减的"感觉结束"大约在 3τ = 150ms）。
    /// 这个值配合下面的 <see cref="ZoomSettle"/> 一起调，改一个就得看另一个。
    /// </summary>
    private const double ZoomTau = 0.05;

    /// <summary>
    /// 收尾门槛，按**相对值**算：剩余距离 / 当前缩放 小于它就直接落到目标。
    ///
    /// 这个值必须足够小，不然收尾会"跳一下"。
    /// 之前取 0.003（0.3%），换成屏幕像素是 4 个像素的突跳 ——
    /// 因为指数衰减的每帧步长只有"剩余距离的 5%"，而门槛比它大十几倍，
    /// 于是最后一帧一步顶前面十几步，肉眼就是"卡一下才停"。
    ///
    /// 取 0.00012（0.012%）：1300px 宽的图上约 0.18 像素，比一个像素还小，
    /// 配上抗锯齿彻底看不出来。
    /// 代价是动画多跑一百多毫秒 —— 但那段时间画面本来就几乎不动，
    /// 感觉上早就停住了，只是收得干净、不留硬着陆。
    /// </summary>
    private const double ZoomSettle = 0.00012;

    private bool _zoomAnimating;
    private double _zoomTarget;          // 目标缩放（连续滚轮会一直往上乘）
    private double _zoomShown;           // 动画过程中"已经滑到哪了"
    private double _zoomLastFrameMs;     // 上一帧的时间，用来算 dt

    /// <summary>
    /// 动画期间 Zoom 保持不动的那个缩放（Z0）。
    /// 画面上的实际缩放 = 它 × 图片 RenderTransform 的 ScaleX。
    /// </summary>
    private double _zoomBaseFactor;

    /// <summary>
    /// 锚点对应的**图像坐标**（原图像素，跟缩放无关）。
    /// 每帧靠它算出终点偏移量，保证鼠标底下那个点尽量停在原处。
    /// </summary>
    private double _anchorContentX;
    private double _anchorContentY;
    private double _anchorViewportX;     // 锚点在视口里的位置，动画期间固定不变
    private double _anchorViewportY;

    /// <summary>上一次写进界面文字的缩放百分比，用来避免每帧都去改 TextBlock。</summary>
    private int _zoomTextShown = -1;

    /// <summary>
    /// 动画期间"正在往哪儿去"的缩放值。
    /// 连续滚轮时新目标要乘在这个值上，而不是乘屏幕上还没滑完的当前值 ——
    /// 否则滚得越快，缩放越跟不上。
    /// </summary>
    private double PendingZoom => _zoomAnimating ? _zoomTarget : _zoom;

    /// <summary>
    /// 当前的缩放倍数。1.0 = 100%（一个图像像素占一个屏幕像素）。
    ///
    /// 这是**我们自己维护**的值，不是问 ScrollViewer 要来的 ——
    /// ScrollViewer 的 ZoomMode 已经关了，它只负责滚，缩放归我们算。
    /// 这样"缩放到多少""画面停在哪"全是可预测的，不会被它内部钳一次。
    /// </summary>
    private double _zoom = 1.0;

    /// <summary>图片比视口小时补在四周的留白（用来居中）。</summary>
    private double _padX;
    private double _padY;

    /// <summary>
    /// 缩放范围。ScrollViewer 自带的缩放被限制在 0.1 ~ 10，
    /// 自己算之后就没有这个限制了 —— 微缩看全图、放很大看细节都放得开。
    /// </summary>
    private const double MinZoom = 0.02;
    private const double MaxZoom = 64.0;

    /// <summary>
    /// 把 <see cref="_zoom"/> 落到图片宽高和留白上。**不动偏移量**。
    ///
    /// 居中靠补 Margin 而不是靠对齐：内容比视口大的时候补白为 0，
    /// 左上角就在视口原点，不会被切掉（用 Center 对齐时大图左上角是滚不到的）。
    /// </summary>
    private void ApplyZoomToLayout()
    {
        if (_displayWidth <= 0 || _displayHeight <= 0) return;

        double w = _displayWidth * _zoom;
        double h = _displayHeight * _zoom;

        ImageView.Width = w;
        ImageView.Height = h;

        double vw = Scroller.ViewportWidth;
        double vh = Scroller.ViewportHeight;

        _padX = vw > w ? (vw - w) / 2 : 0;
        _padY = vh > h ? (vh - h) / 2 : 0;
        ImageView.Margin = new Microsoft.UI.Xaml.Thickness(_padX, _padY, _padX, _padY);
    }

    /// <summary>
    /// 无过渡地把缩放设成 <paramref name="newZoom"/>。
    ///
    /// <paramref name="keepAnchor"/> 为真时，<paramref name="anchor"/>（视口坐标）
    /// 指着的那个图像点在缩放前后停在屏幕同一处 —— 也就是"指哪儿放大到哪儿"。
    /// 这正是"缩放完画面自己回正中"的反面：位置由锚点决定，不会被强行拉回中心。
    /// </summary>
    private void SetZoom(double newZoom, Windows.Foundation.Point anchor, bool keepAnchor)
    {
        if (_displayWidth <= 0 || _displayHeight <= 0) return;

        newZoom = ClampZoom(newZoom);
        double old = _zoom;
        if (Math.Abs(newZoom - old) < 1e-9) return;

        // 锚点对应的图像坐标（未缩放的图像像素）
        double contentX = 0;
        double contentY = 0;
        if (keepAnchor && old > 0)
        {
            contentX = (Scroller.HorizontalOffset + anchor.X - _padX) / old;
            contentY = (Scroller.VerticalOffset + anchor.Y - _padY) / old;
        }

        _zoom = newZoom;
        ApplyZoomToLayout();

        // 改了宽高之后 ScrollViewer 的可滚动范围得重新算一遍。
        // 不走这一步的话，紧接着设偏移量会按**旧范围**被钳掉，
        // 表现正是"缩放完位置不对 / 回到中间"。
        Scroller.UpdateLayout();

        if (!keepAnchor)
        {
            Scroller.ChangeView(0, 0, null, true);
            UpdateZoomText();
            return;
        }

        Scroller.ChangeView(
            contentX * _zoom + _padX - anchor.X,
            contentY * _zoom + _padY - anchor.Y,
            null,
            true);

        UpdateZoomText();
    }

    private void ZoomBy(double multiplier)
    {
        double target = ClampZoom(PendingZoom * multiplier);
        if (Math.Abs(target - PendingZoom) < 1e-6) return;   // 已经顶到缩放边界

        // 按钮和键盘没有鼠标位置，就以视口正中为基准
        StartZoomAnimation(
            target,
            new Windows.Foundation.Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2));
    }

    private double ClampZoom(double value) => Math.Clamp(value, MinZoom, MaxZoom);

    /// <summary>
    /// 刷新底部的百分比。
    /// 动画期间这个函数每帧都会被调用，所以只在**整数百分比变了**的时候才动控件 ——
    /// 每帧改一次 TextBlock 会连带触发一次文本量算，是白白丢掉的帧时间。
    /// </summary>
    private void UpdateZoomText()
    {
        int percent = (int)Math.Round(_zoom * 100);
        if (percent == _zoomTextShown) return;

        _zoomTextShown = percent;
        ZoomText.Text = $"{percent}%";
    }

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateZoomText();

        // 缩放变了，笔尖圆圈的大小跟着变（屏幕上多大，落笔才多大）
        UpdateMarkRingSize();
    }

    /// <summary>
    /// 滚轮缩放一步。参数是"鼠标指的那个点"和这一格的倍率。
    /// </summary>
    private void ZoomAt(Windows.Foundation.Point anchor, double multiplier)
    {
        double target = ClampZoom(PendingZoom * multiplier);
        if (Math.Abs(target - PendingZoom) < 1e-6) return;   // 已经顶到缩放边界

        StartZoomAnimation(target, anchor);
    }

    /// <summary>
    /// 开始（或调整）缩放过渡。锚点就是"鼠标指的那个点"，
    /// 它下面的内容在整个过程中会停在原处 —— 也就是"指哪儿放大到哪儿"。
    ///
    /// 动画已经在跑的时候再调一次是允许的：只是换了目标和锚点，不会重新起跑，
    /// 所以连续滚滚轮是"顺着一路加速上去"，不会一顿一顿。
    ///
    /// ⚠️ 关键：动画期间**完全不碰 ScrollViewer**。
    /// 画面上的变化全由图片的 RenderTransform 做（合成层，GPU），
    /// 滑完了再由 <see cref="CommitZoom"/> 一次性落地。
    /// 每帧去调 ChangeView 会让它每帧重算一次内容范围，
    /// 和画面上的实际位置对不上 —— 那个错位就是"快速闪烁"的来源。
    /// </summary>
    private void StartZoomAnimation(
        double target,
        Windows.Foundation.Point anchor)
    {
        target = ClampZoom(target);
        if (Math.Abs(target - (_zoomAnimating ? _zoomTarget : _zoom)) < 1e-6) return;

        _zoomTarget = target;

        // 已经在滑了就别重启 —— 只换目标，接着往下滑。
        // 锚点刻意**不**跟着更新：跑到一半换锚点会让画面瞬间挪一下，
        // 连续滚轮时那一下一下的位移比"锚点稍旧"难受得多。
        if (_zoomAnimating) return;

        _anchorViewportX = anchor.X;
        _anchorViewportY = anchor.Y;

        // 这一轮动画期间图片宽高就停在 Z0 上不动，画面变化全由 RenderTransform 做
        double base0 = _zoom;
        _zoomBaseFactor = base0;

        /*
          记下"鼠标底下到底是图像的哪个点"（换算到原图像素坐标）。

          图片左上角在视口里的位置是 (补白 - 偏移量)，
          所以视口里 anchor 那个点对应图像内部的位置就是：
            (anchor + 偏移量 - 补白) ÷ 当前缩放
        */
        _anchorContentX = (Scroller.HorizontalOffset + anchor.X - _padX) / base0;
        _anchorContentY = (Scroller.VerticalOffset + anchor.Y - _padY) / base0;

        _zoomShown = base0;
        _zoomLastFrameMs = 0;          // 第一帧只用来对齐时间，不动画面
        _zoomAnimating = true;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    /// <summary>
    /// 每一帧渲染前调一次。参数里的 RenderingTime 是合成器的时间轴，比
    /// Environment.TickCount 更贴帧（后者只到毫秒，而且和刷新不同步）。
    /// </summary>
    private void OnRenderingFrame(object? sender, object e)
    {
        double nowMs = e is RenderingEventArgs args
            ? args.RenderingTime.TotalMilliseconds
            : Environment.TickCount64;

        if (_zoomLastFrameMs <= 0)
        {
            _zoomLastFrameMs = nowMs;
            return;
        }

        double dt = (nowMs - _zoomLastFrameMs) / 1000.0;
        _zoomLastFrameMs = nowMs;

        // 窗口被最小化再恢复、或者系统卡了一下时，dt 会大到离谱，
        // 直接拿来算会一步跳过头。夹一下，最多按 100ms 算
        dt = Math.Clamp(dt, 0.001, 0.1);

        // 从"上一次请求到的值"继续往目标推（不读 Scroller.ZoomFactor，理由见 StartZoomAnimation）
        double zoom = _zoomShown + (_zoomTarget - _zoomShown) * (1 - Math.Exp(-dt / ZoomTau));

        // 快到位了就直接落到目标，别留一根永远收不掉的尾巴
        if (Math.Abs(_zoomTarget - zoom) <= Math.Abs(_zoomTarget) * ZoomSettle)
            zoom = _zoomTarget;

        _zoomShown = zoom;
        ApplyZoomFrame(zoom);

        if (Math.Abs(_zoomTarget - zoom) < 1e-9) StopZoomAnimation();
    }

    private void ApplyZoomFrame(double zoom)
    {
        // 动画期间图片宽高停在 Z0 不动，画面上的实际缩放 = Z0 × k
        double k = _zoomBaseFactor > 0 ? zoom / _zoomBaseFactor : 1.0;
        ZoomScale.ScaleX = k;
        ZoomScale.ScaleY = k;

        // 起点：图片左上角在视口里的位置。
        // 动画期间布局和偏移量都没被动过，所以这个值整段动画里是常数
        double startX = _padX - Scroller.HorizontalOffset;
        double startY = _padY - Scroller.VerticalOffset;

        /*
          位移**必须只跟当前帧的 zoom 有关**，绝不能掺进 _zoomTarget。

          老写法是"先把终点算出来，再按进度 u 从起点挪过去"，而
            u = (zoom - Z0) / (target - Z0)
          分母里带着 target。连续滚滚轮时 target 一直往上乘，分母跟着变大，
          于是**同一个 zoom 算出来的 u 会往回缩** —— 每滚一格，画面就往后跳一下。
          用户看到的就是"没有闪烁了，但有点鬼畜，好像图片想回中"。
          闪烁是没了，可这个跳是另一回事，得单独治。

          现在换成直接解几何关系。"指哪儿放大到哪儿"的意思是：

            锚点底下那个图像点，缩放前后停在屏幕同一处。
            图片左上角在视口里的位置 = X
            那个图像点距图片左上角的距离 = anchorContent × zoom（屏幕像素）
            ⇒ X + anchorContent × zoom = anchorViewport
            ⇒ X = anchorViewport - anchorContent × zoom

          这是 zoom 的**纯函数**：target 怎么变都不影响已经滑到的这一帧该在哪儿。
          滚轮连滚也只是把终点推得更远，画面顺着原来的轨迹继续走，不会回缩。

          另外，图缩到比视口还小时 ScrollViewer 就滚不动了、图会被居中 ——
          这个事实也得算进来，否则落地那一瞬间会"啪"地弹到中间
          （实测能有 270 像素的突跳）。做法是把上面算出来的位置，
          按**当前 zoom 所允许的偏移范围**钳一下，见 ClampFrameOffsets。
        */
        double left = _anchorViewportX - _anchorContentX * zoom;
        double top = _anchorViewportY - _anchorContentY * zoom;

        ClampFrameOffsets(zoom, ref left, ref top);

        ZoomTranslate.X = left - startX;
        ZoomTranslate.Y = top - startY;

        // ⚠️ 这里刻意不调 UpdateZoomText()。
        // 每帧改一次 TextBlock 会连带一次文本量算和重绘，动画期间纯属浪费；
        // 落地时（CommitZoom）和 ViewChanged 回调都会刷新，值不会是旧的。
    }

    /// <summary>
    /// 把"图片左上角想在的位置"按 <paramref name="z"/> 这个缩放下的实际可滚动范围钳一下。
    ///
    /// 为什么非钳不可：偏移量只能在 <c>0 ~ (内容总宽 - 视口宽)</c> 之间，
    /// 想停在范围外是停不住的。图一旦缩到装得下，偏移量只能归零、图被居中 ——
    /// 提前把这件事算进来，动画才会"滑到中间"而不是"最后一帧弹到中间"。
    ///
    /// ⚠️ 关键：钳制范围要按**这一帧的 z** 算，不能按最终目标算（老写法就是按目标算的，
    /// 目标一变范围跟着变，又是一次突跳）。
    /// 落地时 <see cref="ApplyZoomToLayout"/> 也是按最终缩放算补白的，
    /// 两边同一条式子，最后一帧才能严丝合缝地接上、不闪。
    /// </summary>
    private void ClampFrameOffsets(double z, ref double left, ref double top)
    {
        double vw = Scroller.ViewportWidth;
        double vh = Scroller.ViewportHeight;

        double w = _displayWidth * z;
        double h = _displayHeight * z;

        // 图比视口小的时候补白居中（和 ApplyZoomToLayout 完全一样的算法）
        double padX = vw > w ? (vw - w) / 2 : 0;
        double padY = vh > h ? (vh - h) / 2 : 0;

        // 图片左上角 = 补白 - 偏移量，所以偏移量的范围 [0, 内容总宽-视口宽]
        // 反过来就是左上角的范围（左端 - 右端）
        double maxOffX = Math.Max(0, w + 2 * padX - vw);
        double maxOffY = Math.Max(0, h + 2 * padY - vh);

        left = Math.Clamp(left, padX - maxOffX, padX);
        top = Math.Clamp(top, padY - maxOffY, padY);
    }

    /// <summary>
    /// 把动画结果一次性交给 ScrollViewer，同时把图片的 RenderTransform 复位。
    ///
    /// ⚠️ 这两件事必须在同一段代码里连着做：中间只要隔一帧，
    /// 用户就会看到"变换撤掉了但缩放还没跟上"（或者反过来）—— 那也是一次闪。
    /// </summary>
    private void CommitZoom()
    {
        double z = _zoomShown;

        // 先撤掉变换，让图片回到"没被 RenderTransform 动过"的样子
        ZoomScale.ScaleX = 1;
        ZoomScale.ScaleY = 1;
        ZoomTranslate.X = 0;
        ZoomTranslate.Y = 0;

        // 空状态 / 正在换图时不用去动 ScrollViewer，撤掉变换就够了
        if (z <= 0 || _displayWidth <= 0 || _zoomBaseFactor <= 0) return;

        /*
          直接复用"带锚点设缩放"那套做落地：
          动画期间 ScrollViewer 的偏移量和补白都没被动过，
          所以这里按同一条公式算出来的落点是连续的，不会跳一下。
        */
        SetZoom(z, new Windows.Foundation.Point(_anchorViewportX, _anchorViewportY), keepAnchor: true);
    }

    /// <summary>
    /// 停下缩放动画。换图、清理画面、以及"动画没跑完用户就开始拖动"时都要调，
    /// 免得动画和拖动抢着改偏移量，画面来回打架。
    /// </summary>
    private void StopZoomAnimation()
    {
        if (!_zoomAnimating) return;

        _zoomAnimating = false;
        CompositionTarget.Rendering -= OnRenderingFrame;
        CommitZoom();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口一变宽变窄，工具条的可用宽度就得跟着变 ——
        // 不然窗口拉窄之后按钮会被切掉（这正是之前打印按钮只露一半的原因）
        UpdateBottomBarWidth();

        if (_displayWidth <= 0) return;

        // 视口变了，居中补白要跟着重算，否则图会歪在一边
        bool wasFit = IsAtFitZoom();
        ApplyZoomToLayout();
        Scroller.UpdateLayout();

        // 窗口变大变小时，只有处于"适应窗口"状态才跟着重新适应；
        // 用户手动放大了就不动，免得辛苦调的缩放被冲掉
        if (wasFit)
        {
            _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyFitZoom);

            // 裁剪中窗口被拉大拉小：图重新适应之后选框得跟着重画，
            // 否则压暗块和把手还停在旧位置上
            if (_cropping)
                _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateCropVisuals);
        }
    }

    private bool IsAtFitZoom()
    {
        // 放映中**永远**算作"适应窗口"状态。
        // 这一条是必须的：从窗口模式切全屏时尺寸会大变一次，
        // 这里返回 true，OnSizeChanged 才会把图重新适应到新的视口。
        // 不返回 true 的话，刚进全屏那一下图会保持窗口里的缩放，四周留一大圈黑边。
        if (_sliding) return true;

        double vw = Scroller.ViewportWidth;
        double vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0 || _displayWidth <= 0) return true;

        double fit = Math.Min(vw / _displayWidth, vh / _displayHeight);
        return Math.Abs(_zoom - fit) < 0.02;
    }

    // ===== 输入 =====

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 放映模式排在最前：这会儿键盘是"播放控制"，别的什么都别管。
        // ← → 在这里仍然是翻页，但走 ManualSlide —— 它会顺带把自动播放的
        // 计时重新起跑，否则手动翻完两秒又被自动翻走，像是按了个寂寞
        if (_sliding)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                case Windows.System.VirtualKey.F5:
                    ExitSlideMode();
                    break;

                case Windows.System.VirtualKey.Space:
                    SetSlidePlaying(!_slidePlaying);
                    ShowSlideBar();
                    break;

                case Windows.System.VirtualKey.Left:
                case Windows.System.VirtualKey.PageUp:
                    ManualSlide(-1);
                    break;

                case Windows.System.VirtualKey.Right:
                case Windows.System.VirtualKey.PageDown:
                    ManualSlide(1);
                    break;
            }

            e.Handled = true;
            return;
        }

        // 选框模式下键盘只认"应用"和"取消"。其它键一律吃掉 ——
        // 尤其 ← →，不然会在选框上把正在动手的图翻掉
        if (_cropping)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    ExitSelectMode();
                    break;

                case Windows.System.VirtualKey.Enter:
                    _ = ApplySelectionAsync();
                    break;

                // 擦除时数字键直接换填法，一边框一边切最顺手（裁剪下没有这一排，按了也没事）
                case Windows.System.VirtualKey.Number1:
                    PickEraseMode(0);
                    break;

                case Windows.System.VirtualKey.Number2:
                    PickEraseMode(1);
                    break;

                case Windows.System.VirtualKey.Number3:
                    PickEraseMode(2);
                    break;
            }

            e.Handled = true;
            return;
        }

        // 标记模式同理：这会儿键盘是"画笔的快捷键"，不是翻页的。
        // ← → 必须吃掉，不然写着写着图被翻走了
        if (_marking)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    ExitMarkMode(keepCanvasOnScreen: false);
                    break;

                case Windows.System.VirtualKey.Enter:
                    _ = ApplyMarkAsync();
                    break;

                case Windows.System.VirtualKey.Z:
                    if (IsCtrlDown()) MarkUndo_Click(this, new RoutedEventArgs());
                    break;

                case Windows.System.VirtualKey.Y:
                    if (IsCtrlDown()) MarkRedo_Click(this, new RoutedEventArgs());
                    break;

                // 数字键直接换笔，一边画一边切最顺手
                case Windows.System.VirtualKey.Number1:
                    SelectMarkTool(0);
                    break;

                case Windows.System.VirtualKey.Number2:
                    SelectMarkTool(1);
                    break;

                case Windows.System.VirtualKey.Number3:
                    SelectMarkTool(2);
                    break;

                case Windows.System.VirtualKey.Number4:
                    SelectMarkTool(3);
                    break;

                case (Windows.System.VirtualKey)0xBB:      // 主键盘的 =
                case Windows.System.VirtualKey.Add:
                    MarkWidthSlider.Value = Math.Min(PhotoMark.MaxWidth, MarkWidthSlider.Value + 2);
                    break;

                case (Windows.System.VirtualKey)0xBD:      // 主键盘的 -
                case Windows.System.VirtualKey.Subtract:
                    MarkWidthSlider.Value = Math.Max(PhotoMark.MinWidth, MarkWidthSlider.Value - 2);
                    break;
            }

            e.Handled = true;
            return;
        }

        // 背景模式：这会儿键盘是"背景工具的快捷键"。
        // ← → 同样必须吃掉，不然选着背景呢图被翻走了，选区就白做了
        if (_backgrounding)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    ExitBackgroundMode(keepCanvasOnScreen: false);
                    break;

                case Windows.System.VirtualKey.Enter:
                    _ = ApplyBackgroundAsync();
                    break;

                // 1 / 2 / 3 切动作，和擦除那一档同一个路子
                case Windows.System.VirtualKey.Number1:
                    PickBgAction((int)BackgroundAction.Blur);
                    break;

                case Windows.System.VirtualKey.Number2:
                    PickBgAction((int)BackgroundAction.Remove);
                    break;

                case Windows.System.VirtualKey.Number3:
                    PickBgAction((int)BackgroundAction.Replace);
                    break;

                case Windows.System.VirtualKey.B:
                    PickBgTool((int)BgTool.Brush);
                    break;

                case Windows.System.VirtualKey.E:
                    PickBgTool((int)BgTool.Eraser);
                    break;

                case Windows.System.VirtualKey.W:
                    PickBgTool((int)BgTool.Wand);
                    break;

                case Windows.System.VirtualKey.Z:
                    if (IsCtrlDown()) UndoBg();
                    break;

                // [ ] 调笔宽（跟着键盘上那两个键的位置走）
                case (Windows.System.VirtualKey)0xDB:      // [
                    BgWidthSlider.Value = Math.Max(4, BgWidthSlider.Value - 8);
                    break;

                case (Windows.System.VirtualKey)0xDD:      // ]
                    BgWidthSlider.Value = Math.Min(400, BgWidthSlider.Value + 8);
                    break;

                // - = 调虚化强度
                case (Windows.System.VirtualKey)0xBD:      // 主键盘的 -
                case Windows.System.VirtualKey.Subtract:
                    BgStrengthSlider.Value = Math.Max(0, BgStrengthSlider.Value - 4);
                    break;

                case (Windows.System.VirtualKey)0xBB:      // 主键盘的 =
                case Windows.System.VirtualKey.Add:
                    BgStrengthSlider.Value = Math.Min(100, BgStrengthSlider.Value + 4);
                    break;

                // , . 调羽化
                case (Windows.System.VirtualKey)0xBC:      // 主键盘的 ,
                    BgFeatherSlider.Value = Math.Max(0, BgFeatherSlider.Value - 4);
                    break;

                case (Windows.System.VirtualKey)0xBE:      // 主键盘的 .
                    BgFeatherSlider.Value = Math.Min(100, BgFeatherSlider.Value + 4);
                    break;
            }

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.PageUp:
                GoPrevious();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.PageDown:
            case Windows.System.VirtualKey.Space:
                GoNext();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)0xBB:      // 主键盘的 =
                ZoomBy(1.25);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)0xBD:      // 主键盘的 -
                ZoomBy(0.8);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number0:
            case Windows.System.VirtualKey.NumberPad0:
                // 按 0 适应窗口，走带过渡的那版（拖动窗口时的自动适应才是瞬时的）
                _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyFitZoomAnimated);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.F11:
                SafeHost?.ToggleFullScreen();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Escape:
                // 由外到内退：先退全屏，再收右侧面板，再收直方图，最后才退回缩略图墙
                if (SafeHost?.IsFullScreen == true)
                {
                    SafeHost.ToggleFullScreen();
                }
                else if (AnyPanelOpen)
                {
                    CloseSidePanels();
                }
                else if (_histVisible)
                {
                    SetHistVisible(false);
                }
                else
                {
                    SafeHost?.RequestBack();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.I:
                ToggleInfo();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.H:
                // H = Histogram。H 一直空着，首字母也正好对得上
                ToggleHist();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.F:
                ToggleLook();
                e.Handled = true;
                break;

            // F5 = 放映。跟 PowerPoint / 视频播放器一致，不用记新键位
            case Windows.System.VirtualKey.F5:
                EnterSlideMode();
                e.Handled = true;
                break;

            // T = 提取文字。T 空着，而"Text / 文字"首字母正好是它
            case Windows.System.VirtualKey.T:
                ToggleOcr();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.R:
                await ApplyEditAsync(b => ImageEditService.Rotate(b, 90));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.S:
                if (IsCtrlDown())
                {
                    await SaveAsAsync();
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.C:
                // 同一个键两个用途：Ctrl+C 复制，单按 C 进裁剪
                if (IsCtrlDown())
                {
                    await CopyImageAsync();
                }
                else
                {
                    _ = EnterSelectModeAsync(SelectPurpose.Crop);
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.E:
                // E 进擦除。这里不用判 Ctrl —— Ctrl+E 没有别的用途，不用分岔
                _ = EnterSelectModeAsync(SelectPurpose.Erase);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.B:
                // B 进背景处理。和 C / E / M / F 一样是单键 ——
                // 这几个编辑入口的键位保持一致，用户不用记哪几个要加 Shift
                _ = EnterBackgroundModeAsync();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.P:
                if (IsCtrlDown())
                {
                    await PrintAsync();
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.O:
                if (IsCtrlDown())
                {
                    _ = PickAndOpenAsync();
                    e.Handled = true;
                }
                break;
        }
    }

    private bool _infoVisible;

    private void BackButton_Click(object sender, RoutedEventArgs e) => SafeHost?.RequestBack();

    /// <summary>滚轮每格的缩放步长。太小要滚半天，太大画面会跳。</summary>
    private const double WheelZoomStep = 1.15;

    /// <summary>
    /// 滚轮 = 缩放，Shift + 滚轮 = 上一张 / 下一张。
    ///
    /// 翻页之所以让到 Shift 上：看大图时"放大看细节"是最高频的动作，
    /// 而翻页还有方向键、空格、PageUp/PageDown 和底部按钮，路多得很。
    /// </summary>
    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_displayWidth <= 0) return;   // 没图的时候滚轮不做事

        var point = e.GetCurrentPoint(Scroller);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        if (IsShiftDown())
        {
            // 编辑模式里禁止换图：选区、笔画、"背景处理到一半"全都是按当前这张算的，
            // 换一张等于把用户刚做的一半直接扔掉。键盘的 ← → 在这些模式下已经被吃掉了，
            // 这里补上滚轮那条路
            if (!_marking && !_backgrounding && !_cropping)
            {
                if (delta > 0) GoPrevious();
                else GoNext();
            }
        }
        else
        {
            ZoomAt(point.Position, delta > 0 ? WheelZoomStep : 1.0 / WheelZoomStep);
        }

        e.Handled = true;
    }

    // ===== 按住鼠标拖动画面 =====

    private bool _panning;
    private Windows.Foundation.Point _panStart;
    private double _panOriginX;
    private double _panOriginY;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 右侧面板开着时，这一下点按只用来收面板，不进入拖动 ——
        // 否则"点别处收面板"会顺带把图拖走一下，很别扭
        if (AnyPanelOpen)
        {
            CloseSidePanels();
            e.Handled = true;
            return;
        }

        if (_displayWidth <= 0) return;

        var point = e.GetCurrentPoint(Scroller);

        // 只接管鼠标左键。触摸交给 ScrollViewer 自带的那套（手感更好），
        // 所以这里不设 Handled，让它自己处理。
        if (!point.Properties.IsLeftButtonPressed) return;

        _panning = true;
        _panStart = point.Position;
        _panOriginX = Scroller.HorizontalOffset;
        _panOriginY = Scroller.VerticalOffset;

        // 缩放还没滑完就开始拖，得先停掉动画 ——
        // 否则每帧动画都会把偏移量拽回它算的位置，跟拖动对着干，画面会抖
        StopZoomAnimation();

        Scroller.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;

        var point = e.GetCurrentPoint(Scroller);

        // 中途松了键或者指针跑出窗口，收尾
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndPan();
            return;
        }

        // 往右拖，画面跟着往右走 —— 所以偏移量是拿起点减当前点
        Scroller.ChangeView(
            _panOriginX - (point.Position.X - _panStart.X),
            _panOriginY - (point.Position.Y - _panStart.Y),
            null,
            true);

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => EndPan();

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndPan();

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;

        try
        {
            // 一次性放掉所有捕获，省得纠结 e.Pointer 的类型
            Scroller.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了（窗口关掉之类），无所谓
        }
    }

    private static bool IsCtrlDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    // ===== 按钮 =====

    private void PrevButton_Click(object sender, RoutedEventArgs e) => GoPrevious();

    private void NextButton_Click(object sender, RoutedEventArgs e) => GoNext();

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomBy(0.8);

    private void FitButton_Click(object sender, RoutedEventArgs e)
        => _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyFitZoomAnimated);

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        => SafeHost?.ToggleFullScreen();

    private void InfoButton_Click(object sender, RoutedEventArgs e) => ToggleInfo();

    private void ToggleInfo() => SetInfoVisible(!_infoVisible);

    /// <summary>
    /// 参数面板的显隐。**一律走动画**，不做瞬间切换。
    ///
    /// 动画只碰 RenderTransform 和 Opacity —— 这两个走合成层（GPU），
    /// 不会触发布局，所以图片不会因为面板滑出来而被重新排版、重新光栅化。
    /// </summary>
    private void SetInfoVisible(bool visible)
    {
        // 三个面板互斥：开参数面板就把另外两个收掉，免得右边叠两层把图挤没
        if (visible && _lookVisible) SetLookVisible(false);
        if (visible && _ocrVisible) SetOcrVisible(false);

        _infoVisible = visible;
        AnimateSidePanel(InfoPanel, InfoPanelTranslate, visible, ref _infoStoryboard);
    }

    /// <summary>参数面板当前那个动画。切换前要先把它停掉，不然两个动画会抢同一个属性。</summary>
    private Storyboard? _infoStoryboard;

    // ==================== 左下角「直方图」浮层（路线图第 5 步） ====================
    //
    // 和右侧那三个面板**刻意不互斥**：直方图是"参考信息"，调曝光的时候
    // 正需要它和风格面板同时在。塞进互斥组里就没意义了。
    //
    // 所以它也不参与"点图片区收起面板"那套 —— 那个动作收的是挡住画面的右侧面板，
    // 而左下角这块卡片本来就压在画面上、不挡视线，跟着一起消失反而添乱。

    private bool _histVisible;
    private Storyboard? _histStoryboard;

    /// <summary>最近一次解码出来的位图。直方图直接拿它算，不为了统计再解一遍。</summary>
    private DecodedBitmap? _lastDecoded;

    /// <summary>算直方图的取消令牌：连按方向键时，上一张还没算完的就别算了。</summary>
    private CancellationTokenSource? _histCts;

    /// <summary>这次统计是给哪张图算的，用来丢弃"算完但已经翻页"的结果。</summary>
    private string? _histForPath;

    private void HistButton_Click(object sender, RoutedEventArgs e) => ToggleHist();

    private void ToggleHist() => SetHistVisible(!_histVisible);

    private void SetHistVisible(bool visible)
    {
        _histVisible = visible;

        AnimateHistPanel(visible);

        if (visible) _ = RefreshHistogramAsync();
    }

    /// <summary>
    /// 直方图卡片的滑入滑出。和右侧面板同一套手法（只动 RenderTransform 和 Opacity，
    /// 走合成层、不触发布局），只是方向改成从下方。
    ///
    /// 为什么不直接复用 <see cref="AnimateSidePanel"/>：那个方法的滑出距离要加上右边距、
    /// 动画轴固定是 X，改成"可传轴"就得给已经跑顺的右侧三个面板动刀。
    /// 这里重复一小段换右侧面板零风险，划算。
    /// </summary>
    private void AnimateHistPanel(bool visible)
    {
        // 卡片收起时停在"往下 60"，比卡片高度略小 —— 配合淡出，看起来是"沉下去"而不是"滑走"
        const double HiddenOffset = 60;

        // 先把当前值抄下来再停旧动画：Stop() 会把属性弹回动画开始前的值，停完再读就晚了
        double fromY = HistPanelTranslate.Y;
        double fromO = HistPanel.Opacity;

        _histStoryboard?.Stop();
        _histStoryboard = null;

        if (visible)
        {
            HistPanel.Visibility = Visibility.Visible;
            // 第一次打开时先把起点摆好，否则会先在终点闪一下、再动回去
            if (fromO <= 0.01)
            {
                HistPanelTranslate.Y = HiddenOffset;
                fromY = HiddenOffset;
            }
        }

        var sb = new Storyboard();

        var slide = new DoubleAnimation
        {
            From = fromY,
            To = visible ? 0 : HiddenOffset,
            Duration = TimeSpan.FromMilliseconds(visible ? 240 : 180),
            EasingFunction = visible
                ? new CubicEase { EasingMode = EasingMode.EaseOut }    // 出场：快进慢停
                : new CubicEase { EasingMode = EasingMode.EaseIn },    // 退场：慢起快出
        };
        Storyboard.SetTarget(slide, HistPanelTranslate);
        Storyboard.SetTargetProperty(slide, "Y");

        // 淡入淡出比位移短一点，进出更"轻"
        var fade = new DoubleAnimation
        {
            From = fromO,
            To = visible ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(visible ? 170 : 140),
        };
        Storyboard.SetTarget(fade, HistPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        sb.Children.Add(slide);
        sb.Children.Add(fade);

        if (!visible)
        {
            // 判断用"卡片自己当前该不该可见"，而不是闭包里捕获的布尔 ——
            // 中途又被打开的话这条就不生效，不会把刚打开的卡片又藏起来
            sb.Completed += (_, _) =>
            {
                if (!_histVisible) HistPanel.Visibility = Visibility.Collapsed;
            };
        }

        _histStoryboard = sb;
        sb.Begin();
    }

    /// <summary>
    /// 按当前这张图算直方图并画出来。
    ///
    /// 三个要点：
    ///   1. **算在后台线程**。抽稀之后也就二十六万像素，但仍然不该占着 UI 线程 ——
    ///      大图切页时那几十毫秒正好是翻页动画在跑的时候；
    ///   2. **用已经解码好的位图**（<see cref="_lastDecoded"/>），不为了统计再解一遍；
    ///   3. **丢弃迟到结果**。连按方向键时，上一张的统计可能在新图出来之后才算完，
    ///      不拦的话就会把旧图的直方图画在新图上。
    /// </summary>
    private async Task RefreshHistogramAsync()
    {
        DecodedBitmap? decoded = _lastDecoded;
        string? current = _index.CurrentPath;

        if (decoded is null || current is null)
        {
            HistCanvas.Children.Clear();
            HistHintText.Text = "";
            HistStatsText.Text = "没有可统计的图片";
            return;
        }

        _histCts?.Cancel();
        _histCts = new CancellationTokenSource();
        CancellationToken ct = _histCts.Token;

        _histForPath = current;
        HistHintText.Text = "统计中…";
        HistStatsText.Text = "";

        HistogramData data;
        try
        {
            data = await Task.Run(() => HistogramCalculator.Compute(
                decoded.Pixels, decoded.PixelWidth, decoded.PixelHeight), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 算的过程里又翻页了 / 图被换了：这次结果作废
        if (ct.IsCancellationRequested
            || !ReferenceEquals(_lastDecoded, decoded)
            || !string.Equals(_histForPath, current, StringComparison.Ordinal))
        {
            return;
        }

        RenderHistogram(data);
    }

    /// <summary>把统计结果画到 <see cref="HistCanvas"/> 上。</summary>
    private void RenderHistogram(HistogramData data)
    {
        HistCanvas.Children.Clear();

        double w = HistCanvas.Width;
        double h = HistCanvas.Height;
        if (w <= 0 || h <= 0) return;

        if (data.Samples == 0)
        {
            HistHintText.Text = "";
            HistStatsText.Text = "这张图没有可统计的像素（可能是全透明）";
            return;
        }

        // 竖直分格线放在 1/4、2/4、3/4 —— 正好把"阴影 / 中间调 / 高光"分成三段
        for (int i = 1; i <= 3; i++)
        {
            // +0.5 让 1px 的线落在像素中心，不然描边会摊到两个像素上、看着发虚
            double x = Math.Round(w * i / 4) + 0.5;
            HistCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            });
        }

        // 画的顺序不能乱：后面画的压在上面。
        // B → G → R 这样叠，重叠处偏红，和"屏幕三原色相加"的直觉一致。
        AddHistSeries(HistCanvas, data.B, w, h,
            Color.FromArgb(0x5C, 0x4A, 0x9E, 0xFF), Color.FromArgb(0xB0, 0x7A, 0xBD, 0xFF), asLine: false);
        AddHistSeries(HistCanvas, data.G, w, h,
            Color.FromArgb(0x5C, 0x4A, 0xE0, 0x7A), Color.FromArgb(0xB0, 0x82, 0xF0, 0xA8), asLine: false);
        AddHistSeries(HistCanvas, data.R, w, h,
            Color.FromArgb(0x5C, 0xFF, 0x5A, 0x5A), Color.FromArgb(0xB0, 0xFF, 0x8E, 0x8E), asLine: false);

        // 亮度叠在最上面，画成**不填充**的轮廓线：
        // 它填了就会把底下三条 RGB 全盖住，那 RGB 就白画了
        AddHistSeries(HistCanvas, data.Luma, w, h,
            Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), asLine: true);

        HistHintText.Text = $"{data.Samples:N0} 样本";
        HistStatsText.Text =
            $"平均亮度 {data.MeanLuma:0.0}（0~255）\n" +
            $"暗部溢出 {data.ShadowClippedRatio * 100:0.00}%　" +
            $"高光溢出 {data.HighlightClippedRatio * 100:0.00}%";
    }

    /// <summary>
    /// 画一条通道：256 个桶 → 一条折线（或一块填充面积）。
    ///
    /// 桶数（256）和画布宽度（272）不一样，所以横坐标按比例分布；
    /// 竖坐标用 <see cref="HistogramCalculator.ToDisplayHeights"/> 的结果，
    /// 里面已经做过开方压缩，否则一个尖峰就能把其余 255 个桶压平。
    /// </summary>
    private static void AddHistSeries(
        Canvas canvas, int[] bins, double w, double h,
        Color fill, Color stroke, bool asLine)
    {
        double[] heights = HistogramCalculator.ToDisplayHeights(bins);
        if (heights.Length == 0) return;

        int last = heights.Length - 1;
        var curve = new PointCollection();
        for (int i = 0; i <= last; i++)
        {
            curve.Add(new Windows.Foundation.Point(
                w * i / last,
                h - heights[i] * h));
        }

        if (asLine)
        {
            canvas.Children.Add(new Polyline
            {
                Points = curve,
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 1.2,
            });
            return;
        }

        // 填充面积：折线两端补到基线上，围成一个闭合多边形
        var area = new PointCollection { new Windows.Foundation.Point(0, h) };
        foreach (Windows.Foundation.Point p in curve) area.Add(p);
        area.Add(new Windows.Foundation.Point(w, h));

        canvas.Children.Add(new Polygon
        {
            Points = area,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1,
        });
    }

    /// <summary>
    /// 右侧面板的滑入滑出。参数面板和风格面板共用这一套，
    /// 保证两边的手感（时长、曲线、互斥逻辑）完全一致。
    ///
    /// **一律走动画**，不做瞬间切换。
    /// 动画只碰 RenderTransform 和 Opacity —— 这两个走合成层（GPU），
    /// 不会触发布局，所以图片不会因为面板滑出来而被重新排版、重新光栅化。
    /// </summary>
    private static void AnimateSidePanel(
        Border panel,
        TranslateTransform translate,
        bool visible,
        ref Storyboard? running)
    {
        double width = panel.Width > 0 ? panel.Width : 300;

        // 面板是"上下留空隙、离右边也有间距"的浮动卡片，滑出距离得把右边距算进去，
        // 不然停在 X=宽度 时还会露出右侧那几像素的一线边。
        width += panel.Margin.Right;

        // 先把"当前值"抄下来再停掉旧动画。
        // Stop() 会把属性弹回动画开始前的值，等停完再读就已经晚了。
        double fromX = translate.X;
        double fromO = panel.Opacity;

        running?.Stop();
        running = null;

        var sb = new Storyboard();

        var slide = new DoubleAnimation
        {
            From = fromX,
            To = visible ? 0 : width,
            Duration = TimeSpan.FromMilliseconds(visible ? 260 : 190),
            EasingFunction = visible
                ? new CubicEase { EasingMode = EasingMode.EaseOut }    // 出场：快进慢停
                : new CubicEase { EasingMode = EasingMode.EaseIn },    // 退场：慢起快出
        };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "X");

        // 淡入淡出比位移短一点：进出都显得更"轻"，不会拖泥带水
        var fade = new DoubleAnimation
        {
            From = fromO,
            To = visible ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(visible ? 180 : 150),
        };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        sb.Children.Add(slide);
        sb.Children.Add(fade);

        if (visible)
        {
            panel.Visibility = Visibility.Visible;
        }
        else
        {
            // 滑出去之后才真正收起来。
            // 判断用的是 panel 自己当前该不该可见，而不是闭包里捕获的布尔 ——
            // 中途又被打开的话这里就不生效，不会把刚打开的面板又藏起来
            sb.Completed += (_, _) =>
            {
                if (translate.X >= width - 0.5) panel.Visibility = Visibility.Collapsed;
            };
        }

        running = sb;
        sb.Begin();
    }

    /// <summary>右侧那排面板有没有开着一个。</summary>
    private bool AnyPanelOpen => _infoVisible || _lookVisible || _ocrVisible;

    /// <summary>点图片区时收起右侧面板（用户要求的"点别处就收回"）。</summary>
    private void CloseSidePanels()
    {
        if (_infoVisible) SetInfoVisible(false);
        if (_lookVisible) SetLookVisible(false);
        if (_ocrVisible) SetOcrVisible(false);
    }

    /// <summary>按下时的位置，用来区分"点了一下"和"在拖图"。</summary>
    private Windows.Foundation.Point _pressPoint;

    private void Scroller_PointerPressed(object sender, PointerRoutedEventArgs e)
        => _pressPoint = e.GetCurrentPoint(Scroller).Position;

    /// <summary>
    /// 点图片区（也就是参数面板以外的任何地方）把面板收回去。
    ///
    /// 判一次位移：拖动图片时按下和抬起会差出几十像素，那不算"点"，
    /// 否则一拖图面板就没了，很烦。
    /// </summary>
    private void Scroller_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_infoVisible) return;

        var p = e.GetCurrentPoint(Scroller).Position;
        if (Math.Abs(p.X - _pressPoint.X) > 4 || Math.Abs(p.Y - _pressPoint.Y) > 4) return;

        SetInfoVisible(false);
    }

    // ===== 幻灯片放映 =====

    /*
      ===== 放映其实只有三件事 =====

      1. 把界面上的"工具"全收起来：标题栏、底部工具条、右侧面板，
         全屏 + 只留画面和一条会自动隐藏的控制条；
      2. 换图之后**保持适应窗口**，不要像平时那样跳回 100%
         （这就是 ShowCurrentAsync 里那个 _sliding 分支的用途）；
      3. 到点自动翻下一张，翻到最后绕回第一张。

      显示部分**一点没重做** —— 图片还是原来那个 ScrollViewer 在显示，
      上面盖一层透明的 SlideLayer 专门吃鼠标事件。
      好处是解码缓存、预读、EXIF、缩放那一整套原封不动地复用，
      代价只是"放映期间不能拖图"（被那层挡住了，而这正合放映的本意）。

      为什么不干脆做一个独立的放映窗口：又是一套解码、一套翻页、
      一套预读，等于把看图页重写一遍。不划算。
    */

    private bool _sliding;

    /// <summary>
    /// 从外部（图库的"幻灯片放映"按钮）请求进放映，但图还没解出来时的等待标志。
    /// <see cref="StartSlideshow"/> 设它为 true；<see cref="ShowCurrentAsync"/> 解完图、
    /// <c>_displayWidth</c> 一正就进放映。避免"刚开窗口就提示还没有图片"。
    /// </summary>
    private bool _pendingSlide;

    /// <summary>进放映之前本来就是全屏吗。是的话退出时别把用户自己的全屏关掉。</summary>
    private bool _slideWasFullScreen;

    private bool _slidePlaying;
    private double _slideInterval = 5;

    /// <summary>自动翻页的计时器。退出放映要停掉，不然它会在后台一直翻页。</summary>
    private DispatcherQueueTimer? _slideTimer;

    /// <summary>控制条"多久没动鼠标就淡出"的计时器。</summary>
    private DispatcherQueueTimer? _slideBarTimer;

    /// <summary>控制条那次淡入淡出。鼠标动的时候要能把它掐掉，否则动画和赋值会抢 Opacity。</summary>
    private Storyboard? _slideBarFade;

    /// <summary>上一次用滚轮翻页的时刻。滚轮一次滚动会连发好几个事件，得压一压。</summary>
    private long _lastWheelFlipTick;

    private void SlideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sliding) ExitSlideMode();
        else EnterSlideMode();
    }

    private void SlideExit_Click(object sender, RoutedEventArgs e) => ExitSlideMode();

    /// <summary>
    /// 给外部入口用的：从图库的"幻灯片放映"按钮进来。
    /// 图已经显示好了就直接进放映；还没解出来就先记下，等 <see cref="ShowCurrentAsync"/> 解完。
    /// </summary>
    public void StartSlideshow()
    {
        if (_displayWidth > 0) EnterSlideMode();
        else _pendingSlide = true;
    }

    private void EnterSlideMode()
    {
        if (_displayWidth <= 0)
        {
            // 没图可放。给句提示，比"按了没反应"强
            _ = ShowMessageAsync("还没有图片", "先打开一张图片再放映。");
            return;
        }

        // 编辑中的那些模式先退掉：放映是"只看不改"，留着选框只会碍事
        if (_cropping) ExitSelectMode();
        if (_marking) ExitMarkMode(keepCanvasOnScreen: false);
        if (_backgrounding) ExitBackgroundMode(keepCanvasOnScreen: false);
        CloseSidePanels();

        _sliding = true;

        // 本来就在全屏（比如用户先按过 F11）就别动它 ——
        // 退出时要还原成进来的样子，不能"进来全屏、退出变窗口"，
        // 那等于把用户自己的全屏吃掉了
        _slideWasFullScreen = SafeHost?.IsFullScreen == true;
        if (!_slideWasFullScreen) SafeHost?.ToggleFullScreen();

        // 收起"工具"，只留画面
        TitleBar.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
        SlideLayer.Visibility = Visibility.Visible;

        UpdateSlideIntervalButtons();

        // 计数条上的数字靠 ShowCurrentAsync 更新，那玩意儿只有"换图"时才跑。
        // 进放映时当前这张早就显示好了、不会再走一遍 ShowCurrentAsync，
        // 所以这里补一次，不然刚进放映时计数是空的
        SlideCounterText.Text = _index.Count > 0 ? $"{_index.Position} / {_index.Count}" : "";

        ApplyFitZoom();          // 先按当前视口适应一次，全屏生效后 OnSizeChanged 会再适应

        SetSlidePlaying(true);
        ShowSlideBar();

        StartupLog.Write($"进入幻灯片放映（{_index.Count} 张，间隔 {_slideInterval} 秒）");
    }

    private void ExitSlideMode()
    {
        if (!_sliding) return;

        _sliding = false;
        _slidePlaying = false;

        _slideTimer?.Stop();
        _slideBarTimer?.Stop();
        _slideBarFade?.Stop();
        _slideBarFade = null;

        SlideLayer.Visibility = Visibility.Collapsed;
        SlideBar.Opacity = 1;        // 复位，下次放映第一帧就是看得见的
        TitleBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;

        if (!_slideWasFullScreen) SafeHost?.ToggleFullScreen();

        StartupLog.Write("退出幻灯片放映");
    }

    /// <summary>
    /// 播放 / 暂停。图标显示的是"点下去会发生什么"—— 正在播就显示暂停符号。
    /// </summary>
    private void SetSlidePlaying(bool playing)
    {
        _slidePlaying = playing;
        SlidePlayIcon.Glyph = playing ? "\uE769" : "\uE768";

        if (!playing)
        {
            _slideTimer?.Stop();
            return;
        }

        _slideTimer ??= CreateSlideTimer();
        StartSlideTimer();
    }

    private DispatcherQueueTimer CreateSlideTimer()
    {
        var timer = _uiQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Tick += (_, _) => SlideTick();
        return timer;
    }

    /// <summary>重新起跑自动翻页的计时（Start 会重置已经走过的时间）。</summary>
    private void StartSlideTimer()
    {
        if (_slideTimer is null) return;

        _slideTimer.Stop();
        _slideTimer.Interval = TimeSpan.FromSeconds(_slideInterval);
        _slideTimer.Start();
    }

    private void SlideTick()
    {
        if (!_sliding || !_slidePlaying) return;

        // 播到最后一张就绕回第一张。循环看是放映的惯例 ——
        // 播完停在那儿看起来像卡死了
        if (!_index.MoveNext()) _index.MoveTo(0);

        _ = ShowCurrentAsync();
    }

    private void SlidePlay_Click(object sender, RoutedEventArgs e)
    {
        SetSlidePlaying(!_slidePlaying);
        ShowSlideBar();
    }

    private void SlidePrev_Click(object sender, RoutedEventArgs e) => ManualSlide(-1);

    private void SlideNext_Click(object sender, RoutedEventArgs e) => ManualSlide(1);

    /// <summary>
    /// 手动翻一张（点按钮、按方向键、滚轮都走这里）。
    /// 关键是**翻完把自动播放的计时重新起跑** —— 不重来一遍的话，
    /// 手动翻完可能过 0.3 秒就又被自动翻走，看着像按了个寂寞。
    ///
    /// 手动翻页不循环（到头就是到头），跟自动播放不一样：
    /// 人手去点"上一张"时，从第一张跳到末尾属于意外。
    /// </summary>
    private void ManualSlide(int step)
    {
        if (step < 0) GoPrevious();
        else GoNext();

        if (_slidePlaying) StartSlideTimer();
        ShowSlideBar();
    }

    private void SlideInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag) return;
        if (!double.TryParse(tag, out double seconds)) return;

        _slideInterval = seconds;
        UpdateSlideIntervalButtons();

        if (_slidePlaying) StartSlideTimer();   // 换间隔立刻生效，不等这一轮走完
        ShowSlideBar();
    }

    /// <summary>三档间隔里把选中的那档点亮（描边 + 一点点底）。</summary>
    private void UpdateSlideIntervalButtons()
    {
        bool IsOn(double seconds) => Math.Abs(_slideInterval - seconds) < 0.01;

        HighlightInterval(SlideInterval3, IsOn(3));
        HighlightInterval(SlideInterval5, IsOn(5));
        HighlightInterval(SlideInterval10, IsOn(10));
    }

    private static void HighlightInterval(Button button, bool on)
    {
        button.BorderBrush = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));

        button.Background = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>
    /// 把控制条露出来，并重新开始"多久没动鼠标就淡出"的倒计时。
    /// 鼠标每动一下都会调这里，所以它必须够便宜：只碰 Opacity，不触发布局。
    /// </summary>
    private void ShowSlideBar()
    {
        _slideBarFade?.Stop();
        _slideBarFade = null;
        SlideBar.Opacity = 1;

        if (!_sliding) return;

        _slideBarTimer ??= CreateSlideBarTimer();
        _slideBarTimer.Stop();
        _slideBarTimer.Start();
    }

    private DispatcherQueueTimer CreateSlideBarTimer()
    {
        var timer = _uiQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromSeconds(2.6);   // 鼠标停 2.6 秒就把控制条淡掉
        timer.Tick += (_, _) => { if (_sliding) FadeSlideBar(0); };
        return timer;
    }

    private void FadeSlideBar(double to)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, SlideBar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);

        _slideBarFade = storyboard;
        storyboard.Begin();
    }

    private void Slide_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowSlideBar();

    private void Slide_PointerPressed(object sender, PointerRoutedEventArgs e) => ShowSlideBar();

    /// <summary>双击退出放映 —— 跟大多数播放器一致，不用去够那条控制条。</summary>
    private void Slide_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ExitSlideMode();
        e.Handled = true;
    }

    /// <summary>
    /// 放映时滚轮 = 翻页（平时滚轮是缩放，放映时缩放没什么用）。
    ///
    /// ⚠️ 必须做时间节流。滚轮**一次滚动不是一个事件** ——
    /// 一格是 120，而多数鼠标滚一下就是三格，Windows 会连发三个事件。
    /// 每个事件都翻一张的话，滚一下直接飞过三张，根本没法看。
    /// 220 毫秒是试出来的：比一般的滚轮连发间隔长，又不至于让"慢慢滚"变得不跟手。
    /// </summary>
    private void Slide_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(SlideLayer).Properties.MouseWheelDelta;
        if (delta == 0) return;

        long now = Environment.TickCount64;
        if (now - _lastWheelFlipTick < 220)
        {
            e.Handled = true;      // 这一格丢掉，但别让底下那个 ScrollViewer 趁机滚一下
            return;
        }

        _lastWheelFlipTick = now;
        ManualSlide(delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    // ===== 提取文字（OCR） =====

    /*
      识别本身在 Services/PhotoOcr.cs 里，这一块只管界面：
      什么时候开跑、跑的时候面板显示什么、结果怎么摆、复制和存盘。

      为什么要把识别抽出去：那套调用是"纯 WinRT、不碰 XAML"的，
      抽成一个服务之后就能在**命令行里单独跑**（工作区的 _cvocr 探针），
      拿真图测识别效果、测大图缩放路径、测坏数据的报错 ——
      这些在界面上没法手工点出来。

      PhotoOcr 里有两条硬约束写在注释里，改动时一定要连着读：
        · 必须留在 UI 线程调用，**不要套 Task.Run**（WinRT 非敏捷对象跨线程会炸）；
        · 送进去之前先把长边压到 2000 以内（耗时和上限都受这个影响）。
    */

    private bool _ocrVisible;
    private Storyboard? _ocrStoryboard;

    /// <summary>识别出来的全文。面板里显示的和复制的都是它。</summary>
    private string _ocrResult = "";

    /// <summary>正在跑的那次识别。换图或再按一次 T 时要取消掉。</summary>
    private CancellationTokenSource? _ocrCts;

    private void OcrButton_Click(object sender, RoutedEventArgs e) => ToggleOcr();

    private void OcrCloseButton_Click(object sender, RoutedEventArgs e) => SetOcrVisible(false);

    private void ToggleOcr() => SetOcrVisible(!_ocrVisible);

    private void SetOcrVisible(bool visible)
    {
        if (visible && _displayWidth <= 0)
        {
            _ = ShowMessageAsync("还没有图片", "先打开一张图片再提取文字。");
            return;
        }

        // 三个面板互斥，永远只留一个
        if (visible && _infoVisible) SetInfoVisible(false);
        if (visible && _lookVisible) SetLookVisible(false);

        _ocrVisible = visible;
        AnimateSidePanel(OcrPanel, OcrPanelTranslate, visible, ref _ocrStoryboard);

        if (visible)
        {
            // 这条日志是留给自动冒烟的：命令行脚本没法验"面板里显示了什么"，
            // 但能靠这一行确认"按键确实打通了、识别确实跑完了"
            StartupLog.Write("文字面板打开，开始识别");
            _ = RunOcrAsync();
        }
        else
        {
            _ocrCts?.Cancel();
        }
    }

    private async Task RunOcrAsync()
    {
        _ocrCts?.Cancel();
        _ocrCts = new CancellationTokenSource();
        CancellationToken ct = _ocrCts.Token;

        OcrStatus.Text = "正在识别…";
        OcrText.Text = "";
        _ocrResult = "";
        OcrCopyButton.IsEnabled = false;
        OcrSaveButton.IsEnabled = false;

        byte[] bytes = await GetWorkingBytesAsync();
        if (ct.IsCancellationRequested || !_ocrVisible) return;

        if (bytes.Length == 0)
        {
            OcrStatus.Text = "读不到这张图的数据。";
            return;
        }

        // 识别本身全在 PhotoOcr 里，这里只管"把状态和结果摆到面板上"。
        // 抽出去还有个好处：那套 WinRT 调用能在命令行里单独跑压测
        // （见工作区的 _cvocr 探针），不用开界面手工点
        OcrOutcome outcome;
        try
        {
            outcome = await PhotoOcr.RecognizeAsync(bytes, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || !_ocrVisible) return;

        if (!outcome.Ok)
        {
            OcrStatus.Text = "识别失败：" + outcome.Message;
            return;
        }

        if (outcome.LineCount == 0)
        {
            OcrStatus.Text = outcome.Message ?? "这张图里没找到文字。";
            StartupLog.Write("文字识别完成：没找到文字");
            return;
        }

        _ocrResult = outcome.Text;
        OcrText.Text = _ocrResult;
        OcrStatus.Text = $"识别到 {outcome.LineCount} 行 · {_ocrResult.Length} 个字符"
                       + $"（{outcome.LanguageName}）";
        OcrCopyButton.IsEnabled = true;
        OcrSaveButton.IsEnabled = true;

        StartupLog.Write($"文字识别完成：{outcome.LineCount} 行，{_ocrResult.Length} 字（{outcome.LanguageName}）");
    }

    private void OcrCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrResult.Length == 0) return;

        try
        {
            var package = new DataPackage();
            package.SetText(_ocrResult);
            Clipboard.SetContent(package);

            OcrStatus.Text = "已复制到剪贴板。";
        }
        catch
        {
            // 剪贴板被别的程序占着时会失败 —— 跟复制图片那里同一个处理方式
            OcrStatus.Text = "剪贴板被别的程序占着，过一会儿再试。";
        }
    }

    private async void OcrSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrResult.Length == 0) return;

        IntPtr hwnd = SafeHost?.WindowHandle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });

        // 默认文件名跟着图片走：从压缩包里打开的图，路径里带分隔符，
        // 取不出正常文件名，所以先按分隔符切一刀（和另存为那边同一个处理）
        string? current = _index.CurrentPath;
        picker.SuggestedFileName = current is null
            ? "文字"
            : Path.GetFileNameWithoutExtension(current.Split(ArchiveIndex.Separator)[0]);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;      // 用户取消

        try
        {
            await FileIO.WriteTextAsync(file, _ocrResult);
            OcrStatus.Text = $"已存到 {file.Path}";
        }
        catch (Exception ex)
        {
            OcrStatus.Text = "保存失败：" + ex.Message;
        }
    }

    // ===== 裁剪 =====

    /*
      ===== 为什么内部用"比例"记选框，而不是像素 =====

      屏幕上那张图很可能**不是原始尺寸** —— 解码时按窗口大小做过降采样
      （8000px 的巨图只会解出 4000px 左右，见 ComputeDecodeSize），
      所以屏幕坐标和原图像素之间差着一个倍数，而且这个倍数每张图都不一样。

      内部统一用 0~1 的比例记选框，好处是：
        · 窗口大小变了、重新适应窗口了，选框还"贴"在画面的同一个位置上；
        · 换算成原图像素只在最后一步做一次，而且是在**真正要裁的那份字节**
          上现算原始尺寸 —— 不存在"照着屏幕尺寸去裁原图"这种错位。

      比例是按**屏幕像素**算的：选框显示成正方形，裁出来的才是正方形。
      归一化坐标里 x 的 1.0 等于图宽、y 的 1.0 等于图高，两者尺度不同，
      所以每次换比例都要带上"图高 ÷ 图宽"这个系数（见 FitCropAspect）。
    */

    /// <summary>最小裁剪边长（原图像素）。再小就没意义了，也容易让把手叠在一起。</summary>
    private const double MinCropPx = 16;

    /// <summary>把手的视觉尺寸和点击判定半径。判定比视觉大一圈，不然很难点准。</summary>
    private const double CropHandleSize = 12;
    private const double CropHandleHit = 14;

    /// <summary>
    /// 选框拿来干什么：
    ///   Crop  —— 裁剪，留框里那块；
    ///   Erase —— 擦除，把框里那块抹掉（填法见 <see cref="EraseMode"/>）。
    /// 选框本身、把手、压暗这些两边通用，只有"应用"那一步不一样。
    /// </summary>
    private enum SelectPurpose { Crop, Erase }

    private SelectPurpose _selectPurpose = SelectPurpose.Crop;

    private bool _cropping;

    /// <summary>擦除时框里那块填成什么。进擦除模式不改它，用户上次选哪个就还哪个。</summary>
    private EraseMode _eraseMode = EraseMode.Inpaint;

    /// <summary>三个擦除方式的按钮，建好缓存起来反复用。</summary>
    private Button[]? _eraseModeButtons;

    private Button[] EnsureEraseModeButtons()
        => _eraseModeButtons ??= new[] { EraseModeInpaint, EraseModeWhite, EraseModeTransparent };

    private void EraseMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag as string, out int id)) return;

        PickEraseMode(id);
    }

    /// <summary>换擦除方式。数字键和按钮都走这儿，省得两处各写一遍边界检查。</summary>
    private void PickEraseMode(int id)
    {
        if (id < (int)EraseMode.Inpaint || id > (int)EraseMode.Transparent) return;

        _eraseMode = (EraseMode)id;
        UpdateEraseModeButtons();
    }

    /// <summary>当前选中的擦除方式给个底色，一眼看出擦完会变成什么。</summary>
    private void UpdateEraseModeButtons()
    {
        foreach (var button in EnsureEraseModeButtons())
        {
            bool on = int.TryParse(button.Tag as string, out int id) && id == (int)_eraseMode;

            button.Background = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            button.BorderBrush = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        }
    }

    /// <summary>选框（相对图片的 0~1 比例），永远保证 x0 &lt; x1、y0 &lt; y1。</summary>
    private double _cropX0, _cropY0, _cropX1 = 1, _cropY1 = 1;

    /// <summary>锁定的宽高比；0 = 自由。</summary>
    private double _cropAspect;

    /// <summary>
    /// 正在拖什么：-1 没在拖；0~7 是把手（0=左上 1=上 2=右上 3=右 4=右下 5=下 6=左下 7=左）；
    /// 8 = 框内整体挪动；9 = 框外按下、重新画一个。
    /// </summary>
    private int _cropGrip = -1;

    /// <summary>按下时的指针位置，以及那一刻的选框 —— 拖动全程都基于这一份算，避免逐帧累加误差。</summary>
    private Windows.Foundation.Point _cropDragStart;
    private double _cropDragX0, _cropDragY0, _cropDragX1, _cropDragY1;

    /// <summary>这张图的**原始**像素尺寸：屏幕上那张可能是缩过的，输出尺寸和像素换算都得用它。</summary>
    private int _cropSrcW = 1;
    private int _cropSrcH = 1;

    /// <summary>八个把手（下标含义见上面）和比例按钮，建好之后缓起来反复用。</summary>
    private Microsoft.UI.Xaml.Shapes.Rectangle[]? _cropHandles;
    private Button[]? _cropRatioButtons;

    private Microsoft.UI.Xaml.Shapes.Rectangle[] EnsureCropHandles()
        => _cropHandles ??= new[]
        {
            CropHandle0, CropHandle1, CropHandle2, CropHandle3,
            CropHandle4, CropHandle5, CropHandle6, CropHandle7,
        };

    private Button[] EnsureCropRatioButtons()
        => _cropRatioButtons ??= new[]
        {
            CropRatioFree, CropRatio1x1, CropRatio4x3,
            CropRatio3x4, CropRatio16x9, CropRatio9x16,
        };

    private void CropButton_Click(object sender, RoutedEventArgs e)
        => _ = EnterSelectModeAsync(SelectPurpose.Crop);

    private void EraseButton_Click(object sender, RoutedEventArgs e)
        => _ = EnterSelectModeAsync(SelectPurpose.Erase);

    /// <summary>
    /// 进"选区模式"：拉一个框，然后对框里那块动手。
    ///
    /// 裁剪和擦除共用这一套 —— 选框、八个把手、框外压暗、指针形状全都一样，
    /// 差别只有三处：默认框多大、工具条上多出哪一排按钮、点"应用"之后干什么。
    /// 与其再复制一份两百行的拖拽逻辑，不如在这儿分个岔。
    /// </summary>
    private async Task EnterSelectModeAsync(SelectPurpose purpose)
    {
        if (_displayWidth <= 0 || _displayHeight <= 0) return;

        // 屏上可能还挂着没落定的风格参数，先合进像素 ——
        // 否则用户看着是"加了滤镜的图"，裁出来 / 擦下去的却是原图
        await BakeLookAsync();

        CloseSidePanels();
        StopZoomAnimation();

        // 量一下原始像素尺寸。这里只解文件头，很便宜
        byte[] working = await GetWorkingBytesAsync();
        var size = ImageEditService.SizeOf(working);
        if (size.Width <= 0 || size.Height <= 0) return;

        _selectPurpose = purpose;
        _cropSrcW = size.Width;
        _cropSrcH = size.Height;

        // 框选期间图必须**完整可见**：先适应窗口，再把缩放冻住。
        // 要是图还能缩放平移，框和图的对应关系就散了，拖起来会错位
        ApplyFitZoom();
        Scroller.UpdateLayout();

        _cropping = true;
        _cropAspect = 0;
        _cropGrip = -1;

        if (purpose == SelectPurpose.Crop)
        {
            // 裁剪默认整张图：多数时候用户就是想裁掉一点点边
            _cropX0 = 0; _cropY0 = 0; _cropX1 = 1; _cropY1 = 1;
        }
        else
        {
            // 擦除默认给个居中的小框。默认整张图在这儿毫无意义 ——
            // 全选的话框外面一个参考像素都没有，智能填充什么也补不了；
            // 但也不该逼用户"先拖一下才能动手"，所以给个现成的起点。
            // 想重新画就在框外按下拖（和裁剪一样）
            _cropX0 = 1.0 / 3; _cropY0 = 1.0 / 3;
            _cropX1 = 2.0 / 3; _cropY1 = 2.0 / 3;
        }

        UpdateSelectChrome();

        CropLayer.Visibility = Visibility.Visible;
        CropLayer.UpdateLayout();
        CropLayer.SetShape(null);

        UpdateCropHandleVisibility();
        UpdateCropRatioButtons();
        UpdateEraseModeButtons();
        UpdateCropVisuals();
    }

    /// <summary>
    /// 把工具条上"这一趟是裁剪还是擦除"的地方一次换掉：
    /// 两排行谁露谁藏、应用按钮叫什么、两个按钮的提示语。
    /// 进模式时调一次就够，中途不会变。
    /// </summary>
    private void UpdateSelectChrome()
    {
        bool erase = _selectPurpose == SelectPurpose.Erase;

        CropRatioRow.Visibility = erase ? Visibility.Collapsed : Visibility.Visible;
        EraseModeRow.Visibility = erase ? Visibility.Visible : Visibility.Collapsed;

        CropApplyButton.Content = erase ? "擦除" : "应用";

        ToolTipService.SetToolTip(CropSizeText, erase ? "选框的像素大小" : "裁剪后的像素尺寸");

        ToolTipService.SetToolTip(CropApplyButton, erase
            ? "把选框里那块抹掉（回车）"
            : "裁成选框里的部分（回车）");

        ToolTipService.SetToolTip(CropCancelButton, erase
            ? "放弃这次擦除（Esc）"
            : "放弃这次裁剪（Esc）");
    }

    private void ExitSelectMode()
    {
        if (!_cropping) return;

        _cropping = false;
        _cropGrip = -1;
        CropLayer.Visibility = Visibility.Collapsed;
        CropLayer.SetShape(null);

        try
        {
            CropLayer.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了（窗口关掉之类），无所谓
        }
    }

    private void CropCancel_Click(object sender, RoutedEventArgs e) => ExitSelectMode();

    private void CropApply_Click(object sender, RoutedEventArgs e) => _ = ApplySelectionAsync();

    /// <summary>点"应用"到底是要裁还是要擦，在这儿分。</summary>
    private Task ApplySelectionAsync()
        => _selectPurpose == SelectPurpose.Erase ? ApplyEraseAsync() : ApplyCropAsync();

    /// <summary>
    /// 把选框里的部分裁出来。
    ///
    /// 比例 → 像素的换算放在 lambda **里面**做：那里拿到的是真正要被裁的那份字节，
    /// 拿它现量一次尺寸，所以不管前面经过多少次旋转 / 缩放 / 烘焙风格，
    /// 算出来的矩形都落在这份字节的坐标系上，不会错位。
    /// </summary>
    private async Task ApplyCropAsync()
    {
        if (!_cropping) return;

        double fx = _cropX0;
        double fy = _cropY0;
        double fw = _cropX1 - _cropX0;
        double fh = _cropY1 - _cropY0;

        ExitSelectMode();

        // 整张图就是没裁，白跑一趟编解码
        if (fw >= 0.999 && fh >= 0.999) return;
        if (fw <= 0.0005 || fh <= 0.0005) return;

        await ApplyEditAsync(bytes =>
        {
            var (sw, sh) = ImageEditService.SizeOf(bytes);
            if (sw <= 0 || sh <= 0) return Array.Empty<byte>();

            return ImageEditService.Crop(
                bytes,
                (int)Math.Round(fx * sw),
                (int)Math.Round(fy * sh),
                (int)Math.Round(fw * sw),
                (int)Math.Round(fh * sh));
        });
    }

    /// <summary>
    /// 把选框里那块擦掉。
    ///
    /// 和裁剪的区别：裁剪只解文件头量个尺寸就交给解码器了，这里必须**真的拿到
    /// 全分辨率像素** —— 智能填充是逐像素算的。所以多一步解码 + 一步编码，
    /// 大图上会卡个几百毫秒（丢在后台线程，界面不冻结）。
    /// </summary>
    private async Task ApplyEraseAsync()
    {
        if (!_cropping) return;

        double fx = _cropX0, fy = _cropY0;
        double x2 = _cropX1, y2 = _cropY1;

        var mode = _eraseMode;
        ExitSelectMode();

        if (x2 - fx <= 0.0005 || y2 - fy <= 0.0005) return;

        // 失败原因得带回主线程才能弹框，所以用个变量接着（后台线程里弹不了）
        string? problem = null;

        await ApplyEditAsync(bytes =>
        {
            byte[]? pixels = ImageEditService.LoadPixels(bytes, out int w, out int h);
            if (pixels is null || w <= 0 || h <= 0)
            {
                problem = "读不出像素数据，可能是格式太特殊。";
                return Array.Empty<byte>();
            }

            // 比例 → 像素在**这里**换算：拿到的才是真正要被擦的那份字节，
            // 现量一次尺寸，前面经过多少次旋转 / 缩放 / 烘焙风格都不会错位。
            //
            // 下界往下取整、上界往上取整 —— 边上一小条只要被框沾到就一起擦掉。
            // 反过来（往里收）会把水印的边缘留一条毛边，很难看。
            //
            // 那个 1e-6 是吃浮点噪声的：比例是"像素 ÷ 宽"存下来的，乘回来可能变成
            // 99.99999999999999，floor 之后凭空少一格。1e-6 像素远小于肉眼可见，
            // 不会影响真实拖拽，只是不让这种往返误差咬到边界
            const double eps = 1e-6;

            int x0 = (int)Math.Floor(fx * w + eps);
            int y0 = (int)Math.Floor(fy * h + eps);
            int x1 = (int)Math.Ceiling(x2 * w - eps) - 1;
            int y1 = (int)Math.Ceiling(y2 * h - eps) - 1;

            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > w - 1) x1 = w - 1;
            if (y1 > h - 1) y1 = h - 1;

            if (x1 < x0 || y1 < y0)
            {
                problem = "选框太小了，一个像素都没圈住。";
                return Array.Empty<byte>();
            }

            var box = PhotoErase.FillRect(pixels, w, h, x0, y0, x1, y1, mode);
            if (box.IsEmpty)
            {
                // 整张图全选时就是这个结果：框外面没有参考像素可用
                problem = mode == EraseMode.Inpaint
                    ? "整个画面都被框住了，框外面没有像素可以拿来参考。把框拉小一点再擦。"
                    : "这个选框没落在画面上。";
                return Array.Empty<byte>();
            }

            return ImageEditService.FromPixels(pixels, w, h);
        });

        if (problem is not null)
            await ShowMessageAsync("这次没擦掉", problem);
    }

    private void CropRatio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        _cropAspect = ParseAspect(fe.Tag as string);
        if (_cropAspect > 0) ReshapeCropToAspect();

        UpdateCropHandleVisibility();
        UpdateCropRatioButtons();
        UpdateCropVisuals();
    }

    /// <summary>把 "16:9" 这样的标签算成宽高比。认不出来就当自由（0）。</summary>
    private static double ParseAspect(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;
        if (tag == "0") return 0;

        int colon = tag.IndexOf(':');
        if (colon <= 0) return 0;

        if (!int.TryParse(tag[..colon], out int w) || w <= 0) return 0;
        if (!int.TryParse(tag[(colon + 1)..], out int h) || h <= 0) return 0;

        return w / (double)h;
    }

    /// <summary>
    /// 选了比例之后，把当前选框**原地**改成那个比例：中心不动，
    /// 尺寸取"当前选框里能装下的最大的同比例框"。
    ///
    /// 不直接撑满整张图：用户可能已经框好一小块了，点个 1:1 就把框弹到全图很突兀。
    /// </summary>
    private void ReshapeCropToAspect()
    {
        double k = _cropAspect * _cropSrcH / (double)_cropSrcW;
        if (k <= 0) return;

        double curFx = _cropX1 - _cropX0;
        double curFy = _cropY1 - _cropY0;

        // fy = fx / k，要同时不超出当前选框的宽和高
        double fx = Math.Min(curFx, k * curFy);
        double fy = fx / k;

        // 太小了没意义，给个下限（下限本身也不能超出图片）
        double minFx = Math.Min(MinCropPx / (double)_cropSrcW, 1.0);
        if (fx < minFx)
        {
            fx = minFx;
            fy = fx / k;
        }
        if (fy > 1)
        {
            fy = 1;
            fx = k;
        }

        double cx = (_cropX0 + _cropX1) / 2;
        double cy = (_cropY0 + _cropY1) / 2;

        _cropX0 = Math.Clamp(cx - fx / 2, 0, Math.Max(0, 1 - fx));
        _cropX1 = _cropX0 + fx;
        _cropY0 = Math.Clamp(cy - fy / 2, 0, Math.Max(0, 1 - fy));
        _cropY1 = _cropY0 + fy;
    }

    /// <summary>图片在裁剪层坐标系里的位置和大小。</summary>
    private Windows.Foundation.Rect CropImageRect()
    {
        // 直接问视觉树要，不自己拿 _padX/偏移量拼 —— 偏移量是 ChangeView 异步设的，
        // 刚进裁剪模式那一瞬间可能还没落地，拼出来的位置会偏
        var p = ImageView.TransformToVisual(CropCanvas)
                         .TransformPoint(new Windows.Foundation.Point(0, 0));

        return new Windows.Foundation.Rect(
            p.X, p.Y, _displayWidth * _zoom, _displayHeight * _zoom);
    }

    /// <summary>八个把手各自贴在选框的哪一处（占选框宽高的比例）。</summary>
    private static readonly (double FX, double FY)[] CropGripSpots =
    {
        (0, 0),     // 0 左上
        (0.5, 0),   // 1 上
        (1, 0),     // 2 右上
        (1, 0.5),   // 3 右
        (1, 1),     // 4 右下
        (0.5, 1),   // 5 下
        (0, 1),     // 6 左下
        (0, 0.5),   // 7 左
    };

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    /// <summary>按当前选框把压暗块、边框、三分线、把手全部摆一遍。</summary>
    private void UpdateCropVisuals()
    {
        if (!_cropping) return;

        var handles = EnsureCropHandles();

        var img = CropImageRect();
        if (img.Width <= 0 || img.Height <= 0) return;

        double x0 = img.X + _cropX0 * img.Width;
        double y0 = img.Y + _cropY0 * img.Height;
        double x1 = img.X + _cropX1 * img.Width;
        double y1 = img.Y + _cropY1 * img.Height;

        double w = Math.Max(0, x1 - x0);
        double h = Math.Max(0, y1 - y0);

        double layerW = CropLayer.ActualWidth;
        double layerH = CropLayer.ActualHeight;

        // 四块压暗：上、下是通栏的，左右只占中间那一段，避免角落重复叠色变黑
        Place(CropDimTop, 0, 0, layerW, y0);
        Place(CropDimBottom, 0, y1, layerW, Math.Max(0, layerH - y1));
        Place(CropDimLeft, 0, y0, x0, h);
        Place(CropDimRight, x1, y0, Math.Max(0, layerW - x1), h);

        Place(CropBox, x0, y0, w, h);

        double third = w / 3;
        double thirdY = h / 3;
        CropGridV1.X1 = x0 + third; CropGridV1.X2 = x0 + third;
        CropGridV2.X1 = x0 + third * 2; CropGridV2.X2 = x0 + third * 2;
        CropGridV1.Y1 = CropGridV2.Y1 = y0;
        CropGridV1.Y2 = CropGridV2.Y2 = y1;

        CropGridH1.Y1 = y0 + thirdY; CropGridH1.Y2 = y0 + thirdY;
        CropGridH2.Y1 = y0 + thirdY * 2; CropGridH2.Y2 = y0 + thirdY * 2;
        CropGridH1.X1 = CropGridH2.X1 = x0;
        CropGridH1.X2 = CropGridH2.X2 = x1;

        // 把手压在选框的角 / 边中点上，所以中心要往回挪半个身位
        double half = CropHandleSize / 2;
        for (int i = 0; i < handles.Length; i++)
        {
            var (fx, fy) = CropGripSpots[i];
            Place(handles[i],
                  x0 + fx * w - half,
                  y0 + fy * h - half,
                  CropHandleSize, CropHandleSize);
        }

        int outW = (int)Math.Round((_cropX1 - _cropX0) * _cropSrcW);
        int outH = (int)Math.Round((_cropY1 - _cropY0) * _cropSrcH);
        CropSizeText.Text = $"{outW} × {outH}";
    }

    /// <summary>锁比例时只留四个角（边把手没法只动一边），自由时八个都在。</summary>
    private void UpdateCropHandleVisibility()
    {
        var handles = EnsureCropHandles();
        bool locked = _cropAspect > 0;

        for (int i = 0; i < handles.Length; i++)
        {
            bool corner = i == 0 || i == 2 || i == 4 || i == 6;
            handles[i].Visibility = !locked || corner
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>当前选中的比例按钮给个底色，一眼能看出锁没锁、锁的是哪个。</summary>
    private void UpdateCropRatioButtons()
    {
        foreach (var button in EnsureCropRatioButtons())
        {
            bool on = Math.Abs(ParseAspect(button.Tag as string) - _cropAspect) < 1e-6;
            // 透明色直接 FromArgb 拼，不引 Colors 那个类（这个版本的 WinUI 里
            // Windows.UI 只有 Color 结构体，没有 Colors 调色板，少一个依赖少一个坑）
            button.Background = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            button.BorderBrush = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        }
    }

    /// <summary>在选框边上找把手。找不到返回 -1。</summary>
    private int HitCropGrip(Windows.Foundation.Point pos)
    {
        var handles = EnsureCropHandles();
        var img = CropImageRect();
        if (img.Width <= 0 || img.Height <= 0) return -1;

        double x0 = img.X + _cropX0 * img.Width;
        double y0 = img.Y + _cropY0 * img.Height;
        double w = (_cropX1 - _cropX0) * img.Width;
        double h = (_cropY1 - _cropY0) * img.Height;

        for (int i = 0; i < handles.Length; i++)
        {
            // 收起来的把手不参与判定，否则锁比例时边上还是会"抓得到"
            if (handles[i].Visibility != Visibility.Visible) continue;

            var (fx, fy) = CropGripSpots[i];
            if (Math.Abs(pos.X - (x0 + fx * w)) <= CropHandleHit &&
                Math.Abs(pos.Y - (y0 + fy * h)) <= CropHandleHit)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>点在选框里面没有（用来区分"挪框"和"重新画"）。</summary>
    private bool IsInsideCropBox(Windows.Foundation.Point pos)
    {
        var img = CropImageRect();
        if (img.Width <= 0 || img.Height <= 0) return false;

        double x0 = img.X + _cropX0 * img.Width;
        double y0 = img.Y + _cropY0 * img.Height;
        double x1 = img.X + _cropX1 * img.Width;
        double y1 = img.Y + _cropY1 * img.Height;

        return pos.X >= x0 && pos.X <= x1 && pos.Y >= y0 && pos.Y <= y1;
    }

    /// <summary>工具条那一片不参与画框判定 —— 不然点"应用"会先在地上拖出一个框。</summary>
    private bool IsInsideToolbar(Windows.Foundation.Point pos)
    {
        if (CropToolbar.ActualWidth <= 0) return false;

        var p = CropToolbar.TransformToVisual(CropLayer)
                           .TransformPoint(new Windows.Foundation.Point(0, 0));

        return pos.X >= p.X && pos.X <= p.X + CropToolbar.ActualWidth
            && pos.Y >= p.Y && pos.Y <= p.Y + CropToolbar.ActualHeight;
    }

    private void Crop_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropping) return;

        var pos = e.GetCurrentPoint(CropLayer).Position;
        if (IsInsideToolbar(pos)) return;

        _cropDragStart = pos;
        _cropDragX0 = _cropX0; _cropDragY0 = _cropY0;
        _cropDragX1 = _cropX1; _cropDragY1 = _cropY1;

        int hit = HitCropGrip(pos);
        if (hit >= 0) _cropGrip = hit;
        else if (IsInsideCropBox(pos)) _cropGrip = 8;
        else _cropGrip = 9;

        if (_cropGrip == 9)
        {
            // 框外按下：选框立刻塌到按下的那个点，跟着指针长出来
            var img = CropImageRect();
            double fx = FractionOf(pos.X - img.X, img.Width);
            double fy = FractionOf(pos.Y - img.Y, img.Height);
            _cropX0 = _cropX1 = fx;
            _cropY0 = _cropY1 = fy;
        }

        CropLayer.CapturePointer(e.Pointer);
        e.Handled = true;
        UpdateCropVisuals();
    }

    private void Crop_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropping) return;

        var pos = e.GetCurrentPoint(CropLayer).Position;

        if (_cropGrip < 0)
        {
            // 没在拖的时候只干一件事：把指针换成合适的形状
            UpdateCropCursor(pos);
            return;
        }

        var img = CropImageRect();
        if (img.Width <= 0 || img.Height <= 0) return;

        double dx = (pos.X - _cropDragStart.X) / img.Width;
        double dy = (pos.Y - _cropDragStart.Y) / img.Height;

        switch (_cropGrip)
        {
            case 8:
                MoveCrop(dx, dy);
                break;

            case 9:
                DrawCrop(pos, img);
                break;

            default:
                ResizeCrop(_cropGrip, dx, dy);
                break;
        }

        e.Handled = true;
        UpdateCropVisuals();
    }

    private void Crop_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropping || _cropGrip < 0) return;

        _cropGrip = -1;

        try
        {
            CropLayer.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了，无所谓
        }

        e.Handled = true;
    }

    /// <summary>整框挪动：尺寸不变，位置夹在图片里。</summary>
    private void MoveCrop(double dx, double dy)
    {
        double w = _cropDragX1 - _cropDragX0;
        double h = _cropDragY1 - _cropDragY0;

        double x0 = Math.Clamp(_cropDragX0 + dx, 0, Math.Max(0, 1 - w));
        double y0 = Math.Clamp(_cropDragY0 + dy, 0, Math.Max(0, 1 - h));

        _cropX0 = x0; _cropX1 = x0 + w;
        _cropY0 = y0; _cropY1 = y0 + h;
    }

    /// <summary>框外重新画一个。</summary>
    private void DrawCrop(Windows.Foundation.Point pos, Windows.Foundation.Rect img)
    {
        double ax = FractionOf(_cropDragStart.X - img.X, img.Width);
        double ay = FractionOf(_cropDragStart.Y - img.Y, img.Height);
        double bx = FractionOf(pos.X - img.X, img.Width);
        double by = FractionOf(pos.Y - img.Y, img.Height);

        _cropX0 = Math.Min(ax, bx); _cropX1 = Math.Max(ax, bx);
        _cropY0 = Math.Min(ay, by); _cropY1 = Math.Max(ay, by);

        if (_cropAspect > 0) FitCropAspect(ax, ay, bx >= ax, by >= ay);
    }

    /// <summary>
    /// 拖把手调大小。
    ///
    /// 全程基于"按下那一刻的选框"算（_cropDrag*），不是逐帧累加 ——
    /// 逐帧累加会把每次的夹取误差攒起来，拖久了框会自己慢慢缩小。
    /// </summary>
    private void ResizeCrop(int grip, double dx, double dy)
    {
        if (_cropAspect > 0)
        {
            // 锁比例时只有四个角可见。锚点是对角，被拖的那个角跟着指针走
            bool right = grip == 2 || grip == 4;
            bool down = grip == 4 || grip == 6;

            double ax = right ? _cropDragX0 : _cropDragX1;
            double ay = down ? _cropDragY0 : _cropDragY1;

            if (right) _cropX1 = _cropDragX1 + dx; else _cropX0 = _cropDragX0 + dx;
            if (down) _cropY1 = _cropDragY1 + dy; else _cropY0 = _cropDragY0 + dy;

            FitCropAspect(ax, ay, right, down);
            return;
        }

        double l = _cropDragX0, t = _cropDragY0, r = _cropDragX1, b = _cropDragY1;

        if (grip == 0 || grip == 6 || grip == 7) l += dx;   // 左
        if (grip == 2 || grip == 3 || grip == 4) r += dx;   // 右
        if (grip == 0 || grip == 1 || grip == 2) t += dy;   // 上
        if (grip == 4 || grip == 5 || grip == 6) b += dy;   // 下

        double minFx = Math.Min(MinCropPx / (double)_cropSrcW, 1.0);
        double minFy = Math.Min(MinCropPx / (double)_cropSrcH, 1.0);

        // 先把会越界的边拉回图内
        l = Math.Clamp(l, 0, 1); r = Math.Clamp(r, 0, 1);
        t = Math.Clamp(t, 0, 1); b = Math.Clamp(b, 0, 1);

        // 再保证不小于最小尺寸：往"没被拖的那条边"借，
        // 这样被拖的边能一直跟着指针走，手感才对
        bool draggingLeft = grip == 0 || grip == 6 || grip == 7;
        bool draggingTop = grip == 0 || grip == 1 || grip == 2;

        if (r - l < minFx)
        {
            if (draggingLeft) l = Math.Max(0, r - minFx);
            else r = Math.Min(1, l + minFx);
        }

        if (b - t < minFy)
        {
            if (draggingTop) t = Math.Max(0, b - minFy);
            else b = Math.Min(1, t + minFy);
        }

        // 拖过头（越过对边）时左右/上下互换一下，别让宽高变成负数
        _cropX0 = Math.Min(l, r); _cropX1 = Math.Max(l, r);
        _cropY0 = Math.Min(t, b); _cropY1 = Math.Max(t, b);
    }

    /// <summary>
    /// 把选框摆成锁定比例的样子。
    ///
    /// 三个细节：
    ///   1. 锚点 (ax, ay) 是"不动的那一角"，right/down 是选框往哪边伸；
    ///   2. 归一化坐标里 x 的 1.0 是图宽、y 的 1.0 是图高，尺度不同 ——
    ///      像素上"宽 ÷ 高 = aspect"等价于归一化上 fx ÷ fy = aspect × 图高 ÷ 图宽，
    ///      漏掉这个系数，非方形的图锁 1:1 就会出来个长方形；
    ///   3. 取"变化更大的那一维"当主导，另一维按比例推。否则小幅拖动时
    ///      主导维由谁当会来回横跳，框会抖。
    /// </summary>
    private void FitCropAspect(double ax, double ay, bool right, bool down)
    {
        double k = _cropAspect * _cropSrcH / (double)_cropSrcW;
        if (k <= 0) return;

        double fx = Math.Abs(_cropX1 - ax);
        double fy = Math.Abs(_cropY1 - ay);

        if (fx >= k * fy) fy = fx / k;
        else fx = k * fy;

        double maxFx = right ? 1 - ax : ax;
        double maxFy = down ? 1 - ay : ay;

        // 先卡上限再卡下限，顺序反了会把尺寸顶出图片
        fx = Math.Clamp(fx, 0, Math.Max(0, maxFx));
        fy = fx / k;
        if (fy > maxFy)
        {
            fy = Math.Max(0, maxFy);
            fx = k * fy;
        }

        double minFx = Math.Min(MinCropPx / (double)_cropSrcW, Math.Max(0, maxFx));
        if (fx < minFx)
        {
            fx = minFx;
            fy = fx / k;
            if (fy > maxFy)
            {
                fy = Math.Max(0, maxFy);
                fx = k * fy;
            }
        }

        _cropX0 = right ? ax : ax - fx;
        _cropX1 = right ? ax + fx : ax;
        _cropY0 = down ? ay : ay - fy;
        _cropY1 = down ? ay + fy : ay;
    }

    /// <summary>屏幕坐标换成 0~1 比例，并夹进 [0,1]。</summary>
    private static double FractionOf(double offset, double span)
    {
        if (span <= 0) return 0;
        return Math.Clamp(offset / span, 0, 1);
    }

    /// <summary>按鼠标压在哪儿换指针：把手上是斜向箭头，框内是"可平移"，框外是十字。</summary>
    private void UpdateCropCursor(Windows.Foundation.Point pos)
    {
        int hit = HitCropGrip(pos);

        if (hit < 0)
        {
            CropLayer.SetShape(IsInsideCropBox(pos)
                ? Microsoft.UI.Input.InputSystemCursorShape.SizeAll
                : Microsoft.UI.Input.InputSystemCursorShape.Cross);
            return;
        }

        CropLayer.SetShape(hit switch
        {
            0 or 4 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            2 or 6 => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            1 or 5 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            _ => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        });
    }

    // ===== 标记 =====
    //
    // 标记和裁剪不一样：裁剪只是记住一个框，标记是**直接改像素**。
    // 所以进标记模式时要先把当前像素整个拿下来当画布，之后所有笔迹都写在
    // 画布上、屏幕显示的就是画布本身 —— 所见即所得，退出时也不用做任何合成。
    //
    // 三份数据的分工：
    //   _markCanvas —— 正在改的像素，屏幕显示它
    //   _markOrigin —— 没动过的照片像素，橡皮照着它还原（所以橡皮擦不掉照片）
    //   _markStrokes —— 笔画点列，撤销就是删一条重放（不存快照，省内存）

    private bool _marking;
    private MarkTool _markTool = MarkTool.Pen;
    private int _markColorIndex = 0;
    private double _markWidth = 5;

    /// <summary>每支笔各自的粗细。切换工具时记着自己的，不用每次重调。</summary>
    private readonly double[] _markToolWidths = { 5, 26, 6, 22 };

    private byte[]? _markCanvas;
    private byte[]? _markOrigin;
    private int _markW, _markH;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _markBitmap;

    private readonly List<MarkStroke> _markStrokes = new();
    private readonly List<MarkStroke> _markRedo = new();

    /// <summary>手上正拖着的那一笔。抬了手才进 _markStrokes。</summary>
    private MarkStroke? _markCurrent;
    private bool _markDrawing;

    /// <summary>中键拖着平移画面用的。</summary>
    private bool _markPanning;
    private Windows.Foundation.Point _markPanStart;
    private double _markPanH, _markPanV;

    /// <summary>攒起来的脏区（图像像素坐标）。一帧只往屏幕刷一次，别每个鼠标事件都刷。</summary>
    private bool _markDirty;
    private int _markDirtyX0, _markDirtyY0, _markDirtyX1, _markDirtyY1;
    private bool _markFrameHooked;

    private Button[]? _markToolButtons;
    private List<Button>? _markColorButtons;

    /// <summary>
    /// 进标记模式之前底条是不是显示着的，退出时照着还原。
    /// 进标记模式要**把底栏藏掉**：标记自己的工具条也贴在底部，
    /// 两条叠在一起（还都是半透明）看着很脏，而且下面那条的按钮会被压住点不着。
    /// </summary>
    private Visibility _barBeforeMark = Visibility.Visible;

    /// <summary>笔尖圆圈当前停在哪儿（标记层坐标）。NaN 表示还没定位过。</summary>
    private double _ringX = double.NaN;
    private double _ringY = double.NaN;
    private bool _ringShown;

    /// <summary>
    /// 标记模式下指针现在该是什么样。三种状态：
    ///   Ring  —— 在图区画：藏掉系统指针，用自绘的空心圆当笔尖
    ///   Arrow —— 压在工具条上：普通箭头，点按钮才利索
    ///   Pan   —— 中键平移中：四向箭头
    /// 记着当前是哪种，是因为 Hide/Show 和换形状都不便宜，只在切换时做。
    /// </summary>
    private enum MarkPointerMode { Ring, Arrow, Pan }

    private MarkPointerMode _markPointerMode = MarkPointerMode.Ring;

    /// <summary>进标记模式之前的显示状态，取消时原样还回去。</summary>
    private Microsoft.UI.Xaml.Media.ImageSource? _markSavedSource;
    private int _markSavedDisplayW, _markSavedDisplayH;
    private double _markSavedZoom;

    private void MarkButton_Click(object sender, RoutedEventArgs e) => _ = EnterMarkModeAsync();

    private async Task EnterMarkModeAsync()
    {
        if (_marking) return;
        if (_displayWidth <= 0) return;

        // 面板上调过风格但没点"应用"的话，先把风格合进像素。
        // 不然标记是压在那份"没调过色的像素"上，写完一看颜色全没了
        if (!_look.IsNeutral) await BakeLookAsync();

        CloseSidePanels();
        StopZoomAnimation();

        byte[] working = await GetWorkingBytesAsync();
        if (working.Length == 0) return;

        byte[]? pixels = null;
        int w = 0, h = 0;
        await Task.Run(() => pixels = ImageEditService.LoadPixels(working, out w, out h));

        if (pixels is null || w <= 0 || h <= 0)
        {
            await ShowMessageAsync("这张图没法标记", "读不出像素数据，可能是格式太特殊。");
            return;
        }

        var bmp = ToWriteableBitmap(pixels, w, h);
        if (bmp is null)
        {
            await ShowMessageAsync("这张图没法标记", "建画布失败，多半是内存不够。");
            return;
        }

        _markCanvas = pixels;
        _markOrigin = (byte[])pixels.Clone();
        _markW = w;
        _markH = h;
        _markBitmap = bmp;

        _markStrokes.Clear();
        _markRedo.Clear();
        _markCurrent = null;
        _markDrawing = false;
        _markPanning = false;
        _markDirty = false;

        // 记下当前的显示状态，取消时原样还回去
        _markSavedSource = ImageView.Source;
        _markSavedDisplayW = _displayWidth;
        _markSavedDisplayH = _displayHeight;
        _markSavedZoom = _zoom;

        BuildMarkColors();
        UpdateMarkToolButtons();
        UpdateMarkUndoButtons();

        // 每支笔记着自己的粗细，切回来还是上次那个手感
        _markWidth = _markToolWidths[(int)_markTool];
        _syncingMarkWidth = true;
        MarkWidthSlider.Value = _markWidth;
        _syncingMarkWidth = false;
        MarkWidthText.Text = ((int)_markWidth).ToString();

        _marking = true;
        MarkLayer.Visibility = Visibility.Visible;
        MarkLayer.UpdateLayout();
        MarkLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Cross);

        // 底栏先让位：标记工具条也贴底，两条半透明叠一起又脏又点不着
        _barBeforeMark = BottomBar.Visibility;
        BottomBar.Visibility = Visibility.Collapsed;

        // 笔尖用一个自绘的空心圆表示，系统指针就让开（不然十字和圆圈叠在一起很乱）
        _markPointerMode = MarkPointerMode.Ring;
        HideMarkRing();
        MouseCursor.Hide();

        // 换成**全尺寸**的画布来显示（用户放大了看好细节再画，不能是降采样的那份）。
        // 缩放要按比例换算，保证图在屏幕上的大小纹丝不动 ——
        // 否则一进标记模式画面就"啪"地跳一下
        ImageView.Source = bmp;
        _displayWidth = w;
        _displayHeight = h;
        _zoom = ClampZoom(_markSavedZoom * _markSavedDisplayW / w);
        ApplyZoomToLayout();
        Scroller.UpdateLayout();
        UpdateZoomText();
        SizeText.Text = $"{w} × {h}";

        // 圈的大小跟缩放有关，得等上面这轮的 _zoom 定下来再算
        UpdateMarkRingSize();
    }

    /// <summary>退出标记模式。<paramref name="keepCanvasOnScreen"/> 为真表示刚点过"应用"，屏幕上那张留着。</summary>
    private void ExitMarkMode(bool keepCanvasOnScreen)
    {
        if (!_marking) return;

        _marking = false;
        _markDrawing = false;
        _markCurrent = null;
        _markPanning = false;

        MarkLayer.SetShape(null);
        MarkLayer.Visibility = Visibility.Collapsed;

        // 这三样都必须还原，漏一个就是用户直接能感觉到的坏体验：
        // 圆圈留在屏幕上、指针一直看不见、底栏再也不回来
        HideMarkRing();
        MouseCursor.Show();
        BottomBar.Visibility = _barBeforeMark;

        _ringX = double.NaN;
        _ringY = double.NaN;
        _markPointerMode = MarkPointerMode.Ring;

        try
        {
            MarkLayer.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了，无所谓
        }

        StopMarkFlush();

        if (!keepCanvasOnScreen)
        {
            // 取消：显示状态原样还回去，缩放位置都不动
            ImageView.Source = _markSavedSource;
            _displayWidth = _markSavedDisplayW;
            _displayHeight = _markSavedDisplayH;
            _zoom = _markSavedZoom;
            ApplyZoomToLayout();
            UpdateZoomText();
            SizeText.Text = $"{_displayWidth} × {_displayHeight}";
        }

        _markSavedSource = null;
        _markBitmap = null;
        _markCanvas = null;
        _markOrigin = null;
        _markW = 0;
        _markH = 0;
        _markStrokes.Clear();
        _markRedo.Clear();
    }

    private void MarkCancel_Click(object sender, RoutedEventArgs e) => ExitMarkMode(keepCanvasOnScreen: false);

    private void MarkApply_Click(object sender, RoutedEventArgs e) => _ = ApplyMarkAsync();

    private async Task ApplyMarkAsync()
    {
        if (!_marking || _markCanvas is null) return;

        // 手还没抬就点了"应用"：先把这一笔收掉，别丢半截
        if (_markDrawing) FinishMarkStroke();

        byte[] canvas = _markCanvas;
        int w = _markW, h = _markH;

        byte[] png = await Task.Run(() => ImageEditService.FromPixels(canvas, w, h));
        if (png.Length == 0)
        {
            await ShowMessageAsync("标记没能落到图上", "写回像素时出错了。标记还在，可以再点一次「应用」。");
            return;
        }

        _editBytes = png;
        _basePixels = null;      // 像素变了，风格面板的底图下次重新取

        ExitMarkMode(keepCanvasOnScreen: true);
    }

    // ===== 标记：指针 =====

    private void Mark_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_marking || _markCanvas is null || _markOrigin is null) return;

        var pt = e.GetCurrentPoint(MarkLayer);

        // 中键：拖着平移画面。放大之后想画边角，光靠滚动条太别扭
        if (pt.Properties.IsMiddleButtonPressed)
        {
            _markPanning = true;
            _markPanStart = pt.Position;
            _markPanH = Scroller.HorizontalOffset;
            _markPanV = Scroller.VerticalOffset;
            Mark_SyncCursor(pt.Position);      // 平移要看得见指针，所以它会换成四向箭头
            MarkLayer.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;

        // 点在工具条上不算往图上画 —— 否则点"应用"会先在图上戳一个点
        if (IsInsideMarkToolbar(pt.Position)) return;

        var at = MarkImagePoint(pt.Position);
        if (at is null) return;

        // 圆圈先跟上，免得"点下去那一瞬间圈还停在上一次的位置"
        Mark_SyncCursor(pt.Position);

        var color = PhotoMark.Palette[_markColorIndex];

        _markCurrent = new MarkStroke
        {
            Tool = _markTool,
            Width = _markWidth,
            R = color.R,
            G = color.G,
            B = color.B,
        };
        _markCurrent.Add(at.Value.X, at.Value.Y);

        _markDrawing = true;
        _markRedo.Clear();       // 新画一笔，之前撤销掉的就没有"重做"的意义了

        MarkLayer.CapturePointer(e.Pointer);

        PhotoMark.BeginStroke(_markCanvas, _markW, _markH, 16);
        // 第 0 个点这里不画：它多粗要等第二个点才知道。
        // 只点一下不拖的情形，在收笔时补一个圆点
        PhotoMark.AddPoint(_markCanvas, _markOrigin, _markW, _markH, _markCurrent, 0);

        e.Handled = true;
    }

    private void Mark_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marking) return;

        var pt = e.GetCurrentPoint(MarkLayer);

        // 先管指针：圆圈要一直跟着鼠标（不管这会儿在画、在平移、还是什么都没按）
        Mark_SyncCursor(pt.Position);

        if (_markPanning)
        {
            Scroller.ChangeView(
                _markPanH - (pt.Position.X - _markPanStart.X),
                _markPanV - (pt.Position.Y - _markPanStart.Y),
                null,
                true);
            e.Handled = true;
            return;
        }

        if (!_markDrawing || _markCurrent is null) return;
        if (_markCanvas is null || _markOrigin is null) return;

        var at = MarkImagePoint(pt.Position);
        if (at is null) return;

        // 离上一个点太近就不收：点列短很多，撤销时重放更快，
        // 而这么点距离画出来根本看不出区别
        int last = _markCurrent.Count - 1;
        double dx = at.Value.X - _markCurrent.Xs[last];
        double dy = at.Value.Y - _markCurrent.Ys[last];
        if (dx * dx + dy * dy < 0.20) return;

        _markCurrent.Add(at.Value.X, at.Value.Y);

        var dirty = PhotoMark.AddPoint(
            _markCanvas, _markOrigin, _markW, _markH, _markCurrent, _markCurrent.Count - 1);

        AddMarkDirty(dirty);
        e.Handled = true;
    }

    /// <summary>
    /// 鼠标离开标记层（跑出窗口了）。
    ///
    /// 必须在这儿把系统指针还回来 —— 否则指针一出去就还是看不见的，
    /// 用户切到别的窗口会发现鼠标没了，那才叫吓人。
    /// </summary>
    private void Mark_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_marking) return;
        if (_markDrawing || _markPanning) return;   // 拖着指针出去了，别在这儿捣乱

        SetMarkRingVisible(false);
        _markPointerMode = MarkPointerMode.Arrow;   // 下次进图区会自动切回来
        MouseCursor.Show();
    }

    private void Mark_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marking) return;

        if (_markPanning)
        {
            _markPanning = false;
            Mark_SyncCursor(e.GetCurrentPoint(MarkLayer).Position);
            try
            {
                MarkLayer.ReleasePointerCaptures();
            }
            catch
            {
                // 指针已经没了
            }
            e.Handled = true;
            return;
        }

        if (!_markDrawing) return;

        FinishMarkStroke();
        e.Handled = true;
    }

    /// <summary>收笔：把这一笔真正落到撤销栈上。</summary>
    private void FinishMarkStroke()
    {
        _markDrawing = false;

        var stroke = _markCurrent;
        _markCurrent = null;

        try
        {
            MarkLayer.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了
        }

        if (stroke is null || _markCanvas is null || _markOrigin is null) return;

        // 只点一下（只有一个点）的圆点是在这一步补上的
        PhotoMark.FinishStroke(_markCanvas, _markOrigin, _markW, _markH, stroke);

        // 整条笔画的包围盒都刷一遍。中途那些脏区可能还压着没刷出去，
        // 加上这一下最稳妥（一次多刷几十行，比漏刷安全）
        AddMarkDirty(MarkStrokeBounds(stroke));

        _markStrokes.Add(stroke);
        UpdateMarkUndoButtons();
    }

    /// <summary>标记层上的坐标 → 图像像素坐标。离图太远返回 null（那就是误点）。</summary>
    private Windows.Foundation.Point? MarkImagePoint(Windows.Foundation.Point pos)
    {
        var rect = MarkImageRect();
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        double fx = (pos.X - rect.X) / rect.Width;
        double fy = (pos.Y - rect.Y) / rect.Height;

        // 允许出图一点点：笔尖有一半在图外是常事，全挡住反而不好画边角
        if (fx < -0.15 || fx > 1.15 || fy < -0.15 || fy > 1.15) return null;

        return new Windows.Foundation.Point(fx * _markW, fy * _markH);
    }

    /// <summary>图片在标记层坐标系里的位置和大小。</summary>
    private Windows.Foundation.Rect MarkImageRect()
    {
        // 问视觉树要位置，不自己拿 _padX 拼 —— 那是 ChangeView 异步设的，
        // 刚进来的那一瞬间可能还没落地
        var p = ImageView.TransformToVisual(MarkLayer)
                         .TransformPoint(new Windows.Foundation.Point(0, 0));

        return new Windows.Foundation.Rect(
            p.X, p.Y, _displayWidth * _zoom, _displayHeight * _zoom);
    }

    /// <summary>工具条那一片不参与落笔判定。</summary>
    private bool IsInsideMarkToolbar(Windows.Foundation.Point pos)
    {
        if (MarkToolbar.ActualWidth <= 0) return false;

        var p = MarkToolbar.TransformToVisual(MarkLayer)
                           .TransformPoint(new Windows.Foundation.Point(0, 0));

        return pos.X >= p.X && pos.X <= p.X + MarkToolbar.ActualWidth
            && pos.Y >= p.Y && pos.Y <= p.Y + MarkToolbar.ActualHeight;
    }

    // ===== 标记：笔尖圆圈 =====
    //
    // 系统指针（十字）在标记模式下是藏起来的，屏幕上"鼠标在哪 + 笔多粗"全靠这个圈。
    // 圈画两个：底下一圈半透明黑做描边，上面才是白圈 ——
    // 照片上既有雪白的天也有漆黑的夜，单色圈总有一种情况下看不清。

    /// <summary>
    /// 圈的大小 = 笔宽 × 当前缩放。屏幕上看着多大，落笔就是多大。
    ///
    /// 橡皮在引擎里额外向外扩了半格（那是为了擦干净抗锯齿的边），
    /// 这里跟着一起扩，看到的圈就是实际擦掉的范围。
    /// </summary>
    private void UpdateMarkRingSize()
    {
        if (!_marking) return;

        double diameter = _markWidth * _zoom;
        if (_markTool == MarkTool.Eraser) diameter += _zoom;

        // 缩得很小的时候圈会小到看不见，给个下限；上限是怕笔调很粗时圈大到没法定位
        diameter = Math.Clamp(diameter, 6, 400);

        MarkRing.Width = diameter;
        MarkRing.Height = diameter;
        MarkRingShadow.Width = diameter;
        MarkRingShadow.Height = diameter;

        // 橡皮画虚线：一眼能分出"在画"还是"在擦"
        bool dashed = _markTool == MarkTool.Eraser;
        if (_ringDashed != dashed)
        {
            _ringDashed = dashed;
            MarkRing.StrokeDashArray = dashed
                ? new DoubleCollection { 3, 2.5 }
                : new DoubleCollection();
        }

        PositionMarkRing();
    }

    private bool _ringDashed;

    /// <summary>把圈挪到鼠标处（圈心对齐鼠标点）。</summary>
    private void PositionMarkRing(double x, double y)
    {
        _ringX = x;
        _ringY = y;
        PositionMarkRing();
    }

    private void PositionMarkRing()
    {
        if (!_marking || double.IsNaN(_ringX) || double.IsNaN(_ringY)) return;
        if (MarkRing.Width <= 0) return;

        double r = MarkRing.Width / 2;

        // 两个圈都是 Left/Top 对齐 + 固定尺寸，用 Margin 定位最直接
        var margin = new Thickness(_ringX - r, _ringY - r, 0, 0);
        MarkRing.Margin = margin;
        MarkRingShadow.Margin = margin;
    }

    private void SetMarkRingVisible(bool on)
    {
        if (_ringShown == on) return;
        _ringShown = on;

        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MarkRing.Visibility = visibility;
        MarkRingShadow.Visibility = visibility;
    }

    private void HideMarkRing()
    {
        SetMarkRingVisible(false);
        _ringX = double.NaN;
        _ringY = double.NaN;
    }

    /// <summary>
    /// 鼠标一动就调它：决定这一刻"显示什么指针"，顺便让圈跟上去。
    /// 三种状态见 <see cref="MarkPointerMode"/>。
    /// </summary>
    private void Mark_SyncCursor(Windows.Foundation.Point pos)
    {
        var mode = _markPanning
            ? MarkPointerMode.Pan
            : IsInsideMarkToolbar(pos)
                ? MarkPointerMode.Arrow
                : MarkPointerMode.Ring;

        if (mode != _markPointerMode)
        {
            _markPointerMode = mode;

            switch (mode)
            {
                case MarkPointerMode.Ring:
                    // 十字只是留着"占个位"，反正马上会被藏掉；真正给用户看的是那个圈
                    MarkLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Cross);
                    MouseCursor.Hide();
                    break;

                case MarkPointerMode.Arrow:
                    MarkLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
                    MouseCursor.Show();
                    break;

                case MarkPointerMode.Pan:
                    MarkLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                    MouseCursor.Show();
                    break;
            }
        }

        if (mode == MarkPointerMode.Ring)
        {
            PositionMarkRing(pos.X, pos.Y);
            SetMarkRingVisible(true);
        }
        else
        {
            SetMarkRingVisible(false);
        }
    }

    /// <summary>一条笔画在图像像素上的包围盒（含笔宽和一点余量）。</summary>
    private static MarkRect MarkStrokeBounds(MarkStroke stroke)
    {
        if (stroke.Count == 0) return MarkRect.Empty;

        double pad = stroke.Width / 2 + 2;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0; i < stroke.Count; i++)
        {
            double x = stroke.Xs[i], y = stroke.Ys[i];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return new MarkRect(
            (int)Math.Floor(minX - pad), (int)Math.Floor(minY - pad),
            (int)Math.Ceiling(maxX + pad), (int)Math.Ceiling(maxY + pad));
    }

    // ===== 标记：往屏幕刷 =====

    /// <summary>把一块变化攒进脏区。攒到下一帧统一刷一次。</summary>
    private void AddMarkDirty(MarkRect r)
    {
        if (r.IsEmpty) return;

        if (_markDirty)
        {
            if (r.X0 < _markDirtyX0) _markDirtyX0 = r.X0;
            if (r.Y0 < _markDirtyY0) _markDirtyY0 = r.Y0;
            if (r.X1 > _markDirtyX1) _markDirtyX1 = r.X1;
            if (r.Y1 > _markDirtyY1) _markDirtyY1 = r.Y1;
        }
        else
        {
            _markDirty = true;
            _markDirtyX0 = r.X0;
            _markDirtyY0 = r.Y0;
            _markDirtyX1 = r.X1;
            _markDirtyY1 = r.Y1;
        }

        if (_markFrameHooked) return;

        _markFrameHooked = true;
        CompositionTarget.Rendering += OnMarkFrame;
    }

    private void StopMarkFlush()
    {
        if (!_markFrameHooked) return;

        _markFrameHooked = false;
        CompositionTarget.Rendering -= OnMarkFrame;
        _markDirty = false;
    }

    /// <summary>
    /// 一帧刷一次屏幕。
    ///
    /// 拖动时鼠标事件来得比屏幕刷新快得多（高刷屏尤其明显），
    /// 要是每个事件都往位图里写一遍、再让合成器重传一次，
    /// 一张 2K 图每个事件就是 14.7MB，拖起来必然发涩。
    /// 攒到帧上刷，既省事又刚好跟显示同步。
    /// </summary>
    private void OnMarkFrame(object? sender, object e)
    {
        if (!_markDirty || _markBitmap is null || _markCanvas is null)
        {
            StopMarkFlush();
            return;
        }

        int x0 = _markDirtyX0, y0 = _markDirtyY0, x1 = _markDirtyX1, y1 = _markDirtyY1;
        _markDirty = false;

        UploadMarkRegion(x0, y0, x1, y1);
    }

    /// <summary>
    /// 把画布上的一块写进屏幕那张位图。
    ///
    /// 逐行写而不是整张重传：改动的往往只有几十行，整张 14.7MB 重来一遍纯属浪费。
    /// </summary>
    private void UploadMarkRegion(int x0, int y0, int x1, int y1)
    {
        if (_markBitmap is null) return;

        var canvas = _markCanvas;
        if (canvas is null) return;

        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > _markW - 1) x1 = _markW - 1;
        if (y1 > _markH - 1) y1 = _markH - 1;
        if (x0 > x1 || y0 > y1) return;

        try
        {
            using var stream = _markBitmap.PixelBuffer.AsStream();

            int count = (x1 - x0 + 1) * 4;

            for (int y = y0; y <= y1; y++)
            {
                int offset = (y * _markW + x0) * 4;
                stream.Seek(offset, SeekOrigin.Begin);
                stream.Write(canvas, offset, count);
            }

            _markBitmap.Invalidate();
        }
        catch
        {
            // 刷不进去就算了：画面顶多停在上一帧，像素本身没丢
        }
    }

    // ===== 标记：撤销 / 重做 =====

    private void MarkUndo_Click(object sender, RoutedEventArgs e)
    {
        if (!_marking || _markStrokes.Count == 0) return;

        _markRedo.Add(_markStrokes[^1]);
        _markStrokes.RemoveAt(_markStrokes.Count - 1);
        RedrawMark();
    }

    private void MarkRedo_Click(object sender, RoutedEventArgs e)
    {
        if (!_marking || _markRedo.Count == 0) return;

        _markStrokes.Add(_markRedo[^1]);
        _markRedo.RemoveAt(_markRedo.Count - 1);
        RedrawMark();
    }

    /// <summary>
    /// 撤销 / 重做都是"擦干净重画一遍"。
    ///
    /// 与其给每一步算一个反向操作（算错了还看不出来），不如老老实实重放：
    /// 几十条笔画重画一遍也就几十毫秒，比用户眨眼快。
    /// </summary>
    private void RedrawMark()
    {
        if (_markCanvas is null || _markOrigin is null) return;

        PhotoMark.Replay(_markCanvas, _markOrigin, _markW, _markH, _markStrokes);

        AddMarkDirty(new MarkRect(0, 0, _markW - 1, _markH - 1));
        UpdateMarkUndoButtons();
    }

    private void UpdateMarkUndoButtons()
    {
        MarkUndoButton.IsEnabled = _markStrokes.Count > 0;
        MarkRedoButton.IsEnabled = _markRedo.Count > 0;
    }

    // ===== 标记：工具条 =====

    private void MarkTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag as string, out int id)) return;

        SelectMarkTool(id);
    }

    /// <summary>换笔。数字键也走这里。</summary>
    private void SelectMarkTool(int id)
    {
        if (id < 0 || id >= 4) return;
        if ((int)_markTool == id) return;

        // 记下当前这支笔的粗细，切到新笔时把它的粗细找回来 ——
        // 荧光笔要粗、写字要细，共用一个值每次都得重调
        _markToolWidths[(int)_markTool] = _markWidth;
        _markTool = (MarkTool)id;
        _markWidth = _markToolWidths[id];

        _syncingMarkWidth = true;
        MarkWidthSlider.Value = _markWidth;
        _syncingMarkWidth = false;
        MarkWidthText.Text = ((int)_markWidth).ToString();

        UpdateMarkToolButtons();

        // 换了笔就得换圈：橡皮是虚线，而且它的圈要大一格
        UpdateMarkRingSize();
    }

    private bool _syncingMarkWidth;

    private void MarkWidth_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // XAML 解析阶段（Value="6" 和滑块默认值不一样）就会触发一次，
        // 那会儿右边的数字还没建出来，先躲开
        if (MarkWidthText is null) return;
        if (_syncingMarkWidth) return;

        _markWidth = Math.Round(e.NewValue);
        MarkWidthText.Text = ((int)_markWidth).ToString();
        _markToolWidths[(int)_markTool] = _markWidth;

        // 圈的大小实时跟着滑块变 —— 调粗细的时候能直接看到落笔有多粗
        UpdateMarkRingSize();
    }

    private Button[] EnsureMarkToolButtons() => _markToolButtons ??= new[]
    {
        MarkToolPen, MarkToolHighlighter, MarkToolNib, MarkToolEraser,
    };

    private void UpdateMarkToolButtons()
    {
        var buttons = EnsureMarkToolButtons();

        for (int i = 0; i < buttons.Length; i++)
        {
            bool on = (int)_markTool == i;

            buttons[i].Background = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            buttons[i].BorderBrush = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    /// <summary>颜色圆点按颜色表生成，只建一次。</summary>
    private void BuildMarkColors()
    {
        if (_markColorButtons is not null)
        {
            UpdateMarkColorButtons();
            return;
        }

        var list = new List<Button>();

        for (int i = 0; i < PhotoMark.Palette.Length; i++)
        {
            var (name, r, g, b) = PhotoMark.Palette[i];

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
            };

            var button = new Button
            {
                Tag = i.ToString(),
                Content = dot,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };

            ToolTipService.SetToolTip(button, name);
            button.Click += MarkColor_Click;

            list.Add(button);
            MarkColorHost.Children.Add(button);
        }

        _markColorButtons = list;
        UpdateMarkColorButtons();
    }

    private void MarkColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag as string, out int index)) return;
        if (index < 0 || index >= PhotoMark.Palette.Length) return;

        _markColorIndex = index;
        UpdateMarkColorButtons();
    }

    private void UpdateMarkColorButtons()
    {
        if (_markColorButtons is null) return;

        for (int i = 0; i < _markColorButtons.Count; i++)
        {
            bool on = i == _markColorIndex;

            _markColorButtons[i].BorderBrush = on
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            _markColorButtons[i].Background = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    /// <summary>
    /// 工具条不能宽过窗口 —— 否则窄窗口下"应用"会被顶出屏幕，用户就卡在标记里出不去了。
    /// 超了就让里面那条横向滚。
    /// </summary>
    private void MarkLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MarkToolbar.MaxWidth = Math.Max(320, e.NewSize.Width - 48);
    }

    // ===== 背景处理 =====
    //
    // 和标记那一档同一个路子：进模式时按**全分辨率**把图解成像素，屏幕上换成这张位图，
    // 之后一切改动都直接写像素、只把变了的一小块刷上屏幕。
    //
    // 差别在于标记画的是笔迹、背景处理的是"效果"：用户先圈出一块区域
    // （魔棒一点 / 画笔涂），再决定这块区域要变成什么 —— 虚化、抠除（透明）、
    // 替换（纯色或另一张图）。区域存成一张遮罩，效果每帧按遮罩现算。
    //
    // 能实时预览靠三件事，缺一个都会卡：
    //   1. 整幅模糊版**只算一次**缓存着（它跟遮罩无关，却是最贵的一步）；
    //   2. 覆盖率（羽化后的遮罩）**只重算脏区**；
    //   3. 像素混合也只做脏区，一帧往屏幕刷一次。
    // 这三层是配套的，改的时候别把脏区那一层拆掉。

    /// <summary>当前这张图的工作像素（效果已经混进去了，就是屏幕上看到的）。</summary>
    private byte[]? _bgCanvas;

    /// <summary>原样像素。混合的基准，进模式之后一个字节都不动。</summary>
    private byte[]? _bgOrigin;

    /// <summary>整幅模糊版。只有"虚化"用得到，模糊强度变了才重算。</summary>
    private byte[]? _bgBlurred;

    private int _bgW, _bgH;
    private BackgroundMask? _bgMask;
    private BackgroundCoverage? _bgCoverage;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _bgBitmap;

    private BackgroundAction _bgAction = BackgroundAction.Blur;
    private BgTool _bgTool = BgTool.Wand;
    private double _bgWidth = 60;
    private double _bgStrength = 0.55;
    private double _bgFeather = 0.25;
    private int _bgColorIndex;
    private byte[]? _bgImage;
    private int _bgImageW, _bgImageH;
    private byte _bgCustomR = 255, _bgCustomG = 255, _bgCustomB = 255;
    private bool _bgUseCustomColor;

    private bool _backgrounding;
    private bool _bgDrawing;
    private bool _bgPanning;
    private Windows.Foundation.Point _bgPanStart;
    private double _bgPanH, _bgPanV;

    /// <summary>上个落笔点。鼠标事件之间隔得挺远，得连成线才不是一串珠子。</summary>
    private double _bgLastX, _bgLastY;

    /// <summary>攒起来的脏区（图像像素坐标）。一帧只往屏幕刷一次，别每个鼠标事件都刷。</summary>
    private bool _bgDirty;
    private int _bgDirtyX0, _bgDirtyY0, _bgDirtyX1, _bgDirtyY1;
    private bool _bgFrameHooked;

    /// <summary>
    /// 撤销栈。每一项是"某一块遮罩改动前的样子"。
    ///
    /// 不存整张遮罩：四千万像素的图光遮罩就 40MB，存十步直接爆。
    /// 存局部，并且按总字节封顶 —— 大图自动少记几步，小图能记几十步。
    /// </summary>
    private readonly List<(BgRect Box, byte[] Old)> _bgUndo = new();
    private const int BgUndoByteLimit = 48 * 1024 * 1024;

    /// <summary>本笔开始前的整张遮罩快照。抬笔时按实际改动范围裁成一小块进栈。</summary>
    private byte[]? _bgStrokeSnapshot;

    /// <summary>本笔累计改动的范围。</summary>
    private BgRect _bgStrokeBox = BgRect.Empty;

    private Visibility _barBeforeBg = Visibility.Visible;

    /// <summary>进背景模式之前的显示状态，取消时原样还回去。</summary>
    private Microsoft.UI.Xaml.Media.ImageSource? _bgSavedSource;
    private int _bgSavedDisplayW, _bgSavedDisplayH;
    private double _bgSavedZoom;

    private double _bgRingX = double.NaN;
    private double _bgRingY = double.NaN;
    private bool _bgRingShown;
    private bool _bgRingDashed;

    /// <summary>
    /// 背景模式下指针该是什么样：
    ///   Ring  —— 画笔 / 橡皮在图区：藏掉系统指针，用自绘空心圆当笔尖
    ///   Cross —— 魔棒在图区：十字，点哪儿选哪儿
    ///   Arrow —— 压在工具条上：普通箭头
    ///   Pan   —— 中键平移中：四向箭头
    /// </summary>
    private enum BgPointerMode { Ring, Cross, Arrow, Pan }

    private BgPointerMode _bgPointerMode = BgPointerMode.Arrow;

    /// <summary>背景模式里在用的笔。魔棒是默认 —— 一点就选中一大片背景，比拿画笔涂快得多。</summary>
    private enum BgTool { Brush = 0, Eraser = 1, Wand = 2 }

    private List<Button>? _bgColorButtons;
    private Button[]? _bgActionButtons;
    private Button[]? _bgToolButtons;

    /// <summary>往滑块里写值时会触发 ValueChanged，用它挡住那些"不是用户改的"。</summary>
    private bool _syncingBgSliders;

    /// <summary>模糊版正在后台重建。同时只算一份，多的合并成"算完再来一次"。</summary>
    private bool _bgBlurBusy;
    private bool _bgBlurPending;

    /// <summary>魔棒的颜色容差。太小渐变背景选不动，太大又会把主体一起吃进去。</summary>
    private const int BgWandTolerance = 32;

    /// <summary>画笔边缘的柔和度。留一点软边，抠出来的边不会是一圈硬锯齿。</summary>
    private const double BgBrushSoftness = 0.5;

    /// <summary>替换背景用的预设色。</summary>
    private static readonly (string Name, byte R, byte G, byte B)[] BgPalette =
    {
        ("白色", 255, 255, 255),
        ("浅灰", 232, 232, 232),
        ("深灰", 64, 64, 64),
        ("黑色", 0, 0, 0),
        ("天蓝", 120, 190, 240),
        ("草绿", 120, 190, 130),
        ("暖橙", 240, 170, 110),
        ("洋红", 220, 90, 150),
    };

    private void BgButton_Click(object sender, RoutedEventArgs e) => _ = EnterBackgroundModeAsync();

    private async Task EnterBackgroundModeAsync()
    {
        if (_backgrounding || _cropping || _marking) return;
        if (_displayWidth <= 0) return;

        // 面板上调过风格但没点"应用"的，先合进像素 ——
        // 否则虚化/抠图是作用在"没调过色的像素"上，做完一看颜色全没了
        if (!_look.IsNeutral) await BakeLookAsync();

        CloseSidePanels();
        StopZoomAnimation();

        byte[] working = await GetWorkingBytesAsync();
        if (working.Length == 0) return;

        byte[]? pixels = null;
        int w = 0, h = 0;
        await Task.Run(() => pixels = ImageEditService.LoadPixels(working, out w, out h));

        if (pixels is null || w <= 0 || h <= 0)
        {
            await ShowMessageAsync("这张图没法处理背景", "读不出像素数据，可能是格式太特殊。");
            return;
        }

        var bmp = ToWriteableBitmap(pixels, w, h);
        if (bmp is null)
        {
            await ShowMessageAsync("这张图没法处理背景", "建画布失败，多半是内存不够。");
            return;
        }

        _bgOrigin = pixels;
        _bgCanvas = (byte[])pixels.Clone();
        _bgW = w;
        _bgH = h;
        _bgBitmap = bmp;
        _bgMask = new BackgroundMask(w, h);
        _bgCoverage = new BackgroundCoverage(w, h);
        _bgBlurred = null;

        _bgDrawing = false;
        _bgPanning = false;
        _bgDirty = false;
        _bgUndo.Clear();
        _bgStrokeSnapshot = null;
        _bgStrokeBox = BgRect.Empty;
        _bgBlurPending = false;
        _bgImage = null;
        _bgImageW = 0;
        _bgImageH = 0;

        // 记下当前的显示状态，取消时原样还回去
        _bgSavedSource = ImageView.Source;
        _bgSavedDisplayW = _displayWidth;
        _bgSavedDisplayH = _displayHeight;
        _bgSavedZoom = _zoom;

        BuildBgColors();
        SyncBgSliders();
        UpdateBgActionButtons();
        UpdateBgToolButtons();
        UpdateBgUndoButton();
        BgClearImageButton.Visibility = Visibility.Collapsed;
        BgReplaceRow.Visibility = _bgAction == BackgroundAction.Replace
            ? Visibility.Visible
            : Visibility.Collapsed;

        _backgrounding = true;
        BackgroundLayer.Visibility = Visibility.Visible;
        BackgroundLayer.UpdateLayout();

        // 底栏先让位：背景工具条也贴底，两条半透明叠一起又脏又点不着
        _barBeforeBg = BottomBar.Visibility;
        BottomBar.Visibility = Visibility.Collapsed;

        // 指针的最终形态交给 Bg_SyncCursor（鼠标一动就纠正），这里先摆成最普通的箭头，
        // 免得"刚进模式那一瞬间"是个说不清的状态
        _bgPointerMode = BgPointerMode.Arrow;
        BackgroundLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        HideBgRing();
        MouseCursor.Show();

        // 换成全尺寸画布显示。缩放按比例换算，保证图在屏幕上的大小纹丝不动 ——
        // 否则一进背景模式画面就"啪"地跳一下
        ImageView.Source = bmp;
        _displayWidth = w;
        _displayHeight = h;
        _zoom = ClampZoom(_bgSavedZoom * _bgSavedDisplayW / w);
        ApplyZoomToLayout();
        Scroller.UpdateLayout();
        UpdateZoomText();
        SizeText.Text = $"{w} × {h}";

        UpdateBgRingSize();

        // 默认动作是虚化，先把模糊版备好：用户第一笔涂下去就该看到效果，
        // 而不是涂完了才发现"怎么没反应"（模糊版按当前强度算，大图要几百毫秒）
        _ = EnsureBgBlurAsync();
    }

    /// <summary>退出背景模式。<paramref name="keepCanvasOnScreen"/> 为真表示刚点过"应用"，屏幕上那张留着。</summary>
    private void ExitBackgroundMode(bool keepCanvasOnScreen)
    {
        if (!_backgrounding) return;

        _backgrounding = false;
        _bgDrawing = false;
        _bgPanning = false;

        BackgroundLayer.SetShape(null);
        BackgroundLayer.Visibility = Visibility.Collapsed;

        // 这三样必须还原，漏一个都是用户直接能感觉到的坏体验：
        // 圆圈留在屏幕上、指针一直看不见、底栏再也不回来
        HideBgRing();
        MouseCursor.Show();
        BottomBar.Visibility = _barBeforeBg;

        _bgRingX = double.NaN;
        _bgRingY = double.NaN;
        _bgPointerMode = BgPointerMode.Arrow;

        try
        {
            BackgroundLayer.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了，无所谓
        }

        StopBgFlush();

        if (!keepCanvasOnScreen)
        {
            // 取消：显示状态原样还回去，缩放位置都不动
            ImageView.Source = _bgSavedSource;
            _displayWidth = _bgSavedDisplayW;
            _displayHeight = _bgSavedDisplayH;
            _zoom = _bgSavedZoom;
            ApplyZoomToLayout();
            UpdateZoomText();
            SizeText.Text = $"{_displayWidth} × {_displayHeight}";
        }

        _bgSavedSource = null;
        _bgBitmap = null;
        _bgCanvas = null;
        _bgOrigin = null;
        _bgBlurred = null;
        _bgMask = null;
        _bgCoverage = null;
        _bgImage = null;
        _bgImageW = 0;
        _bgImageH = 0;
        _bgW = 0;
        _bgH = 0;
        _bgUndo.Clear();
        _bgStrokeSnapshot = null;
        _bgBlurPending = false;
    }

    private void BgCancel_Click(object sender, RoutedEventArgs e) => ExitBackgroundMode(keepCanvasOnScreen: false);

    private void BgApply_Click(object sender, RoutedEventArgs e) => _ = ApplyBackgroundAsync();

    private async Task ApplyBackgroundAsync()
    {
        if (!_backgrounding || _bgOrigin is null || _bgMask is null) return;

        if (_bgMask.IsEmpty())
        {
            await ShowMessageAsync("还没选区域", "先用魔棒点一下背景，或者用画笔涂出要处理的地方。");
            return;
        }

        byte[] origin = _bgOrigin;
        var mask = _bgMask;
        int w = _bgW, h = _bgH;
        var options = BgOptions();
        double feather = _bgFeather;
        double strength = _bgStrength;
        byte[]? cachedBlur = _bgBlurred;

        byte[]? png = null;

        await Task.Run(() =>
        {
            // 从**原样像素**重新算一遍，而不是把屏幕上那份画布拿去存。
            // 画布是脏区一块块拼出来的，只要有一处漏刷就会存进一张不一致的图；
            // 重新算一遍多花几十毫秒，换一个"结果一定对"
            byte[] work = (byte[])origin.Clone();

            byte[]? blur = cachedBlur;
            if (options.Action == BackgroundAction.Blur && (blur is null || blur.Length < w * h * 4))
                blur = PhotoBackground.BlurCopy(origin, w, h, strength);

            var cover = new BackgroundCoverage(w, h);
            var whole = BgRect.Whole(w, h);

            cover.Rebuild(mask, whole, PhotoBackground.FeatherRadius(feather, w, h));
            PhotoBackground.Render(work, origin, w, h, cover.Data, whole, options, blur);

            png = ImageEditService.FromPixels(work, w, h);
        });

        if (png is null || png.Length == 0)
        {
            await ShowMessageAsync("背景处理没能落到图上", "写回像素时出错了。选区还在，可以再点一次「应用」。");
            return;
        }

        _editBytes = png;
        _basePixels = null;      // 像素变了，风格面板的底图下次重新取

        ExitBackgroundMode(keepCanvasOnScreen: true);
    }

    // ===== 背景：遮罩 → 像素 =====

    private BackgroundOptions BgOptions()
    {
        var (r, g, b) = BgPanelColor();

        return new BackgroundOptions
        {
            Action = _bgAction,
            BlurStrength = _bgStrength,
            R = r,
            G = g,
            B = b,
            ImageBgra = _bgImage,
            ImageWidth = _bgImageW,
            ImageHeight = _bgImageH,
        };
    }

    private int BgFeatherPixels() => PhotoBackground.FeatherRadius(_bgFeather, _bgW, _bgH);

    /// <summary>
    /// 只重画遮罩**变了的那一块**。
    ///
    /// 这是拖笔画时每帧调的那个函数，所以里面每一步都必须是"跟这块成正比"的。
    /// 一不小心写成整幅（比如把 area 传成 Whole）就会掉帧。
    /// </summary>
    private void BgPaintRegion(BgRect area)
    {
        if (area.IsEmpty) return;
        if (_bgCanvas is null || _bgOrigin is null || _bgMask is null || _bgCoverage is null) return;

        int feather = BgFeatherPixels();

        _bgCoverage.Rebuild(_bgMask, area, feather);

        // 羽化会像水波一样往外传两圈（两遍盒式模糊），所以真正要重画的范围比 area 大
        var box = area.Expand(feather * 2, _bgW, _bgH);
        if (box.IsEmpty) return;

        PhotoBackground.Render(
            _bgCanvas, _bgOrigin, _bgW, _bgH, _bgCoverage.Data, box, BgOptions(), _bgBlurred);

        AddBgDirty(box);
    }

    /// <summary>整幅重画。切动作、调羽化、换背景色这类"全局都变"的操作走这儿。</summary>
    private void BgRepaintWhole()
    {
        if (_bgCanvas is null || _bgOrigin is null || _bgMask is null || _bgCoverage is null) return;

        var whole = BgRect.Whole(_bgW, _bgH);

        // 一个区域都没选的时候没什么可算的，直接铺回原样。
        // 这条捷径很有用：刚进模式、以及点"清空"之后，整幅重画会白算一遍羽化
        if (_bgMask.IsEmpty())
        {
            Buffer.BlockCopy(_bgOrigin, 0, _bgCanvas, 0, _bgOrigin.Length);
            AddBgDirty(whole);
            return;
        }

        _bgCoverage.Rebuild(_bgMask, whole, BgFeatherPixels());

        PhotoBackground.Render(
            _bgCanvas, _bgOrigin, _bgW, _bgH, _bgCoverage.Data, whole, BgOptions(), _bgBlurred);

        AddBgDirty(whole);
    }

    /// <summary>
    /// 把整幅模糊版准备好。只有"虚化"用得到。
    ///
    /// 这是全场最贵的一步（两千万像素要一秒上下），所以：
    ///   * 算一次缓存着，拖鼠标、涂遮罩都不重算；
    ///   * 放在后台线程，界面不卡；
    ///   * 拖"虚化"滑块会连着触发很多次，用 busy + pending 合并 ——
    ///     正在算就记一笔"算完再算一次"，不会排出一长串任务。
    /// </summary>
    private async Task EnsureBgBlurAsync()
    {
        if (!_backgrounding || _bgOrigin is null) return;
        if (_bgAction != BackgroundAction.Blur) return;

        if (_bgBlurBusy)
        {
            _bgBlurPending = true;
            return;
        }

        _bgBlurBusy = true;

        try
        {
            byte[] origin = _bgOrigin;
            int w = _bgW, h = _bgH;
            double strength = _bgStrength;

            byte[]? blur = null;
            await Task.Run(() => blur = PhotoBackground.BlurCopy(origin, w, h, strength));

            // 算的这几百毫秒里用户可能已经退出去了，或者图上已经换了别的图
            if (blur is null || !_backgrounding || !ReferenceEquals(origin, _bgOrigin)) return;

            _bgBlurred = blur;
            BgRepaintWhole();     // 有了模糊版，虚化的效果才出得来
        }
        finally
        {
            _bgBlurBusy = false;

            if (_bgBlurPending)
            {
                _bgBlurPending = false;
                _ = EnsureBgBlurAsync();
            }
        }
    }

    // ===== 背景：指针 =====

    private void Bg_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_backgrounding || _bgCanvas is null || _bgOrigin is null || _bgMask is null) return;

        var pt = e.GetCurrentPoint(BackgroundLayer);

        // 中键：拖着平移画面。放大之后想选边角，光靠滚动条太别扭
        if (pt.Properties.IsMiddleButtonPressed)
        {
            _bgPanning = true;
            _bgPanStart = pt.Position;
            _bgPanH = Scroller.HorizontalOffset;
            _bgPanV = Scroller.VerticalOffset;
            Bg_SyncCursor(pt.Position);
            BackgroundLayer.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;

        // 点在工具条上不算往图上涂 —— 否则点"应用"会先在图上盖一笔
        if (IsInsideBgToolbar(pt.Position)) return;

        var at = BgImagePoint(pt.Position);
        if (at is null) return;

        // 圆圈先跟上，免得"点下去那一瞬间圈还停在上一次的位置"
        Bg_SyncCursor(pt.Position);

        // 这一下会改遮罩，先把改之前的样子记下来（抬笔时按实际范围裁进入撤销栈）
        BeginBgEdit();

        if (_bgTool == BgTool.Wand)
        {
            var box = _bgMask.Wand(_bgOrigin, _bgW, _bgH, (int)at.Value.X, (int)at.Value.Y, BgWandTolerance);

            if (!box.IsEmpty)
            {
                TouchBgEdit(box);
                BgPaintRegion(box);
                BgHintText.Text = _bgMask.IsEmpty()
                    ? "先用魔棒点一下背景（或用画笔涂出要处理的地方）"
                    : "选好了就选动作：虚化 / 抠除 / 替换；没选全的地方再用魔棒点几下或拿画笔补";
            }

            EndBgEdit();
            e.Handled = true;
            return;
        }

        double radius = _bgWidth / 2;
        bool erase = _bgTool == BgTool.Eraser;

        var dirty = _bgMask.PaintCircle(at.Value.X, at.Value.Y, radius, BgBrushSoftness, erase);

        _bgLastX = at.Value.X;
        _bgLastY = at.Value.Y;
        _bgDrawing = true;

        if (!dirty.IsEmpty)
        {
            TouchBgEdit(dirty);
            BgPaintRegion(dirty);
        }

        BackgroundLayer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Bg_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_backgrounding) return;

        var pt = e.GetCurrentPoint(BackgroundLayer);

        // 先管指针：圈要一直跟着鼠标（不管这会儿在涂、在平移、还是什么都没按）
        Bg_SyncCursor(pt.Position);

        if (_bgPanning)
        {
            Scroller.ChangeView(
                _bgPanH - (pt.Position.X - _bgPanStart.X),
                _bgPanV - (pt.Position.Y - _bgPanStart.Y),
                null,
                true);
            e.Handled = true;
            return;
        }

        if (!_bgDrawing || _bgMask is null) return;

        var at = BgImagePoint(pt.Position);
        if (at is null) return;

        double radius = _bgWidth / 2;

        var dirty = _bgMask.PaintSegment(
            _bgLastX, _bgLastY, at.Value.X, at.Value.Y,
            radius, BgBrushSoftness, _bgTool == BgTool.Eraser);

        _bgLastX = at.Value.X;
        _bgLastY = at.Value.Y;

        if (!dirty.IsEmpty)
        {
            TouchBgEdit(dirty);
            BgPaintRegion(dirty);
        }

        e.Handled = true;
    }

    /// <summary>
    /// 鼠标离开背景层（跑出窗口了）。
    ///
    /// 必须在这儿把系统指针还回来 —— 否则指针一出去就还是看不见的，
    /// 用户切到别的窗口会发现鼠标没了，那才叫吓人。
    /// </summary>
    private void Bg_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_backgrounding) return;
        if (_bgDrawing || _bgPanning) return;   // 拖着指针出去了，别在这儿捣乱

        SetBgRingVisible(false);
        _bgPointerMode = BgPointerMode.Arrow;   // 下次进图区会自动切回来
        MouseCursor.Show();
    }

    private void Bg_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_backgrounding) return;

        if (_bgPanning)
        {
            _bgPanning = false;
            Bg_SyncCursor(e.GetCurrentPoint(BackgroundLayer).Position);
            try
            {
                BackgroundLayer.ReleasePointerCaptures();
            }
            catch
            {
                // 指针已经没了
            }
            e.Handled = true;
            return;
        }

        if (!_bgDrawing) return;

        _bgDrawing = false;

        try
        {
            BackgroundLayer.ReleasePointerCaptures();
        }
        catch
        {
            // 指针已经没了
        }

        EndBgEdit();
        e.Handled = true;
    }

    /// <summary>背景层上的坐标 → 图像像素坐标。离图太远返回 null（那就是误点）。</summary>
    private Windows.Foundation.Point? BgImagePoint(Windows.Foundation.Point pos)
    {
        var rect = BgImageRect();
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        double fx = (pos.X - rect.X) / rect.Width;
        double fy = (pos.Y - rect.Y) / rect.Height;

        // 允许出图一点点：笔尖有一半在图外是常事，全挡住反而不好涂边角
        if (fx < -0.15 || fx > 1.15 || fy < -0.15 || fy > 1.15) return null;

        return new Windows.Foundation.Point(fx * _bgW, fy * _bgH);
    }

    /// <summary>图片在背景层坐标系里的位置和大小。</summary>
    private Windows.Foundation.Rect BgImageRect()
    {
        // 问视觉树要位置，不自己拿 _padX 拼 —— 那是 ChangeView 异步设的，
        // 刚进来的那一瞬间可能还没落地
        var p = ImageView.TransformToVisual(BackgroundLayer)
                         .TransformPoint(new Windows.Foundation.Point(0, 0));

        return new Windows.Foundation.Rect(
            p.X, p.Y, _displayWidth * _zoom, _displayHeight * _zoom);
    }

    /// <summary>工具条那一片不参与落笔判定。</summary>
    private bool IsInsideBgToolbar(Windows.Foundation.Point pos)
    {
        if (BackgroundToolbar.ActualWidth <= 0) return false;

        var p = BackgroundToolbar.TransformToVisual(BackgroundLayer)
                                 .TransformPoint(new Windows.Foundation.Point(0, 0));

        return pos.X >= p.X && pos.X <= p.X + BackgroundToolbar.ActualWidth
            && pos.Y >= p.Y && pos.Y <= p.Y + BackgroundToolbar.ActualHeight;
    }

    /// <summary>
    /// 鼠标一动就调它：决定这一刻"显示什么指针"，顺便让圈跟上去。
    /// 四种状态见 <see cref="BgPointerMode"/>。
    /// </summary>
    private void Bg_SyncCursor(Windows.Foundation.Point pos)
    {
        var mode = _bgPanning
            ? BgPointerMode.Pan
            : IsInsideBgToolbar(pos)
                ? BgPointerMode.Arrow
                : _bgTool == BgTool.Wand
                    ? BgPointerMode.Cross
                    : BgPointerMode.Ring;

        if (mode != _bgPointerMode)
        {
            _bgPointerMode = mode;

            switch (mode)
            {
                case BgPointerMode.Ring:
                    // 十字只是留着"占个位"，反正马上会被藏掉；真正给用户看的是那个圈
                    BackgroundLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Cross);
                    MouseCursor.Hide();
                    break;

                case BgPointerMode.Cross:
                    // 魔棒没有"笔宽"，画圈反而误导，就用系统十字
                    BackgroundLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Cross);
                    MouseCursor.Show();
                    break;

                case BgPointerMode.Arrow:
                    BackgroundLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
                    MouseCursor.Show();
                    break;

                case BgPointerMode.Pan:
                    BackgroundLayer.SetShape(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                    MouseCursor.Show();
                    break;
            }
        }

        if (mode == BgPointerMode.Ring)
        {
            PositionBgRing(pos.X, pos.Y);
            SetBgRingVisible(true);
        }
        else
        {
            SetBgRingVisible(false);
        }
    }

    // ===== 背景：笔尖圆圈 =====
    //
    // 和标记模式一个道理：系统指针在涂的时候是藏起来的，
    // 屏幕上"鼠标在哪 + 笔多粗"全靠这个圈。画两个，底下一圈半透明黑做描边 ——
    // 照片上既有雪白的天也有漆黑的夜，单色圈总有一种情况下看不清。

    /// <summary>圈的大小 = 笔宽 × 当前缩放。屏幕上看着多大，涂下去就是多大。</summary>
    private void UpdateBgRingSize()
    {
        if (!_backgrounding) return;

        // 魔棒是按颜色选的，没有"笔宽"这回事，不画圈
        if (_bgTool == BgTool.Wand)
        {
            SetBgRingVisible(false);
            return;
        }

        double diameter = Math.Clamp(_bgWidth * _zoom, 6, 400);

        BgRing.Width = diameter;
        BgRing.Height = diameter;
        BgRingShadow.Width = diameter;
        BgRingShadow.Height = diameter;

        // 橡皮画虚线：一眼能分出"在涂"还是"在擦"
        bool dashed = _bgTool == BgTool.Eraser;
        if (_bgRingDashed != dashed)
        {
            _bgRingDashed = dashed;
            BgRing.StrokeDashArray = dashed
                ? new DoubleCollection { 3, 2.5 }
                : new DoubleCollection();
        }

        PositionBgRing();
    }

    private void PositionBgRing(double x, double y)
    {
        _bgRingX = x;
        _bgRingY = y;
        PositionBgRing();
    }

    private void PositionBgRing()
    {
        if (!_backgrounding || double.IsNaN(_bgRingX) || double.IsNaN(_bgRingY)) return;
        if (BgRing.Width <= 0) return;

        double r = BgRing.Width / 2;

        // 两个圈都是 Left/Top 对齐 + 固定尺寸，用 Margin 定位最直接
        var margin = new Thickness(_bgRingX - r, _bgRingY - r, 0, 0);
        BgRing.Margin = margin;
        BgRingShadow.Margin = margin;
    }

    private void SetBgRingVisible(bool on)
    {
        if (_bgRingShown == on) return;
        _bgRingShown = on;

        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BgRing.Visibility = visibility;
        BgRingShadow.Visibility = visibility;
    }

    private void HideBgRing()
    {
        SetBgRingVisible(false);
        _bgRingX = double.NaN;
        _bgRingY = double.NaN;
    }

    // ===== 背景：往屏幕刷 =====

    /// <summary>把一块变化攒进脏区。攒到下一帧统一刷一次。</summary>
    private void AddBgDirty(BgRect r)
    {
        if (r.IsEmpty) return;

        if (_bgDirty)
        {
            if (r.X0 < _bgDirtyX0) _bgDirtyX0 = r.X0;
            if (r.Y0 < _bgDirtyY0) _bgDirtyY0 = r.Y0;
            if (r.X1 > _bgDirtyX1) _bgDirtyX1 = r.X1;
            if (r.Y1 > _bgDirtyY1) _bgDirtyY1 = r.Y1;
        }
        else
        {
            _bgDirty = true;
            _bgDirtyX0 = r.X0;
            _bgDirtyY0 = r.Y0;
            _bgDirtyX1 = r.X1;
            _bgDirtyY1 = r.Y1;
        }

        if (_bgFrameHooked) return;

        _bgFrameHooked = true;
        CompositionTarget.Rendering += OnBgFrame;
    }

    private void StopBgFlush()
    {
        if (!_bgFrameHooked) return;

        _bgFrameHooked = false;
        CompositionTarget.Rendering -= OnBgFrame;
        _bgDirty = false;
    }

    /// <summary>
    /// 一帧刷一次屏幕。
    ///
    /// 拖动时鼠标事件来得比屏幕刷新快得多（高刷屏尤其明显），
    /// 要是每个事件都往位图里写一遍、再让合成器重传一次，
    /// 一张 2K 图每个事件就是 14.7MB，拖起来必然发涩。
    /// </summary>
    private void OnBgFrame(object? sender, object e)
    {
        if (!_bgDirty || _bgBitmap is null || _bgCanvas is null)
        {
            StopBgFlush();
            return;
        }

        int x0 = _bgDirtyX0, y0 = _bgDirtyY0, x1 = _bgDirtyX1, y1 = _bgDirtyY1;
        _bgDirty = false;

        UploadBgRegion(x0, y0, x1, y1);
    }

    /// <summary>
    /// 把画布上的一块写进屏幕那张位图。
    ///
    /// 逐行写而不是整张重传：改动的往往只有几十行，整张 14.7MB 重来一遍纯属浪费。
    /// </summary>
    private void UploadBgRegion(int x0, int y0, int x1, int y1)
    {
        if (_bgBitmap is null || _bgCanvas is null) return;

        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > _bgW - 1) x1 = _bgW - 1;
        if (y1 > _bgH - 1) y1 = _bgH - 1;
        if (x0 > x1 || y0 > y1) return;

        try
        {
            using var stream = _bgBitmap.PixelBuffer.AsStream();

            int count = (x1 - x0 + 1) * 4;

            for (int y = y0; y <= y1; y++)
            {
                int offset = (y * _bgW + x0) * 4;
                stream.Seek(offset, SeekOrigin.Begin);
                stream.Write(_bgCanvas, offset, count);
            }

            _bgBitmap.Invalidate();
        }
        catch
        {
            // 刷不进去就算了：画面顶多停在上一帧，像素本身没丢
        }
    }

    // ===== 背景：撤销 =====

    /// <summary>动遮罩之前调一次，把"改之前的样子"记在手上。</summary>
    private void BeginBgEdit()
    {
        if (_bgMask is null) return;

        _bgStrokeSnapshot = (byte[])_bgMask.Data.Clone();
        _bgStrokeBox = BgRect.Empty;
    }

    /// <summary>本笔动了哪一块，累加起来（抬笔时才知道要存多大）。</summary>
    private void TouchBgEdit(BgRect r)
    {
        if (_bgStrokeSnapshot is null || r.IsEmpty) return;

        _bgStrokeBox = BgRect.Union(_bgStrokeBox, r);
    }

    /// <summary>
    /// 收尾一次编辑：把"改动范围"那份旧遮罩裁出来进撤销栈。
    ///
    /// 存局部而不是整张：整张在四千万像素的图上是 40MB，存几步就爆内存。
    /// </summary>
    private void EndBgEdit()
    {
        var snapshot = _bgStrokeSnapshot;
        _bgStrokeSnapshot = null;

        if (snapshot is null || _bgMask is null) return;

        var box = _bgStrokeBox.Expand(0, _bgW, _bgH);
        _bgStrokeBox = BgRect.Empty;

        if (box.IsEmpty) return;

        int bw = box.Width;

        var old = new byte[bw * box.Height];
        for (int y = 0; y < box.Height; y++)
            Buffer.BlockCopy(snapshot, (box.Y0 + y) * _bgW + box.X0, old, y * bw, bw);

        _bgUndo.Add((box, old));

        // 按总字节封顶。大图自动少记几步，小图能记几十步 ——
        // 总比"涂到一半内存爆了"强
        int total = 0;
        foreach (var item in _bgUndo) total += item.Old.Length;

        while (_bgUndo.Count > 1 && total > BgUndoByteLimit)
        {
            total -= _bgUndo[0].Old.Length;
            _bgUndo.RemoveAt(0);
        }

        UpdateBgUndoButton();
    }

    private void BgUndo_Click(object sender, RoutedEventArgs e) => UndoBg();

    private void UndoBg()
    {
        if (!_backgrounding || _bgMask is null || _bgUndo.Count == 0) return;

        var (box, old) = _bgUndo[^1];
        _bgUndo.RemoveAt(_bgUndo.Count - 1);

        int bw = box.Width;
        for (int y = 0; y < box.Height; y++)
            Buffer.BlockCopy(old, y * bw, _bgMask.Data, (box.Y0 + y) * _bgW + box.X0, bw);

        BgPaintRegion(box);
        UpdateBgUndoButton();
    }

    private void UpdateBgUndoButton() => BgUndoButton.IsEnabled = _bgUndo.Count > 0;

    private void BgClear_Click(object sender, RoutedEventArgs e)
    {
        if (!_backgrounding || _bgMask is null) return;

        BeginBgEdit();
        _bgMask.Clear();
        TouchBgEdit(BgRect.Whole(_bgW, _bgH));
        EndBgEdit();

        BgRepaintWhole();
        BgHintText.Text = "先用魔棒点一下背景（或用画笔涂出要处理的地方），再选虚化 / 抠除 / 替换";
    }

    // ===== 背景：工具条 =====

    private Button[] EnsureBgActionButtons()
        => _bgActionButtons ??= new[] { BgActionBlur, BgActionRemove, BgActionReplace };

    private Button[] EnsureBgToolButtons()
        => _bgToolButtons ??= new[] { BgToolBrush, BgToolEraser, BgToolWand };

    private void BgAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag as string, out int id)) return;

        PickBgAction(id);
    }

    /// <summary>换动作。数字键和按钮都走这儿，省得两处各写一遍边界检查。</summary>
    private void PickBgAction(int id)
    {
        if (id < (int)BackgroundAction.Blur || id > (int)BackgroundAction.Replace) return;

        var action = (BackgroundAction)id;
        if (_bgAction == action) return;

        _bgAction = action;
        UpdateBgActionButtons();

        // 只有"替换"才有第三行（颜色 / 图片）
        BgReplaceRow.Visibility = action == BackgroundAction.Replace
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (action == BackgroundAction.Blur && _bgBlurred is null)
        {
            // 还没算过模糊版（或者强度刚改过还没算完）。让它算完自己重画，
            // 这样用户点了"虚化"之后立刻能看到效果，不用再动一下
            _ = EnsureBgBlurAsync();
            return;
        }

        BgRepaintWhole();
    }

    /// <summary>当前选中的动作给个底色，一眼看出这块区域会变成什么。</summary>
    private void UpdateBgActionButtons()
    {
        foreach (var button in EnsureBgActionButtons())
        {
            bool on = int.TryParse(button.Tag as string, out int id) && id == (int)_bgAction;

            button.Background = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            button.BorderBrush = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        }
    }

    private void BgTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag as string, out int id)) return;

        PickBgTool(id);
    }

    private void PickBgTool(int id)
    {
        if (id < (int)BgTool.Brush || id > (int)BgTool.Wand) return;
        if ((int)_bgTool == id) return;

        _bgTool = (BgTool)id;
        UpdateBgToolButtons();
        UpdateBgRingSize();

        // 圈的样式（实线 / 虚线 / 有没有）变了，位置没变；
        // 指针形态下次鼠标一动就会自己纠正，这里不用管
    }

    private void UpdateBgToolButtons()
    {
        foreach (var button in EnsureBgToolButtons())
        {
            bool on = int.TryParse(button.Tag as string, out int id) && id == (int)_bgTool;

            button.Background = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            button.BorderBrush = new SolidColorBrush(on
                ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        }
    }

    /// <summary>把字段里的值刷进滑块和数字标签（进模式时调一次）。</summary>
    private void SyncBgSliders()
    {
        _syncingBgSliders = true;

        BgWidthSlider.Value = _bgWidth;
        BgStrengthSlider.Value = _bgStrength * 100;
        BgFeatherSlider.Value = _bgFeather * 100;

        _syncingBgSliders = false;

        BgWidthText.Text = ((int)Math.Round(_bgWidth)).ToString();
        BgStrengthText.Text = ((int)Math.Round(_bgStrength * 100)).ToString();
        BgFeatherText.Text = ((int)Math.Round(_bgFeather * 100)).ToString();
    }

    private void BgWidth_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // ⚠️ 这里必须挡住两种情况：
        //   1. `_syncingBgSliders` —— 是我们在回填值，不是用户在改；
        //   2. `BgWidthText is null` —— **XAML 还在解析**。
        // 解析到 `Minimum="4"` 那一行时，Slider 的 Value 会从默认的 0 被抬到 4，
        // 当场触发这个事件；而那一刻标签控件还没建出来，直接写就是空引用。
        // 坑在报错上：WinUI 把它包成
        // `Failed to assign to property 'RangeBase.Minimum'`，指向 Minimum 那行、
        // 没有任何堆栈线索，看起来像"XAML 写错了"，其实是事件处理器里崩的。
        if (_syncingBgSliders || BgWidthText is null) return;

        _bgWidth = e.NewValue;
        BgWidthText.Text = ((int)Math.Round(_bgWidth)).ToString();

        UpdateBgRingSize();
    }

    private void BgStrength_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 同上：XAML 解析期间的自动触发要挡住（见 BgWidth_ValueChanged 的说明）
        if (_syncingBgSliders || BgStrengthText is null) return;

        _bgStrength = e.NewValue / 100.0;
        BgStrengthText.Text = ((int)Math.Round(e.NewValue)).ToString();

        // 强度一变整幅模糊版就作废了，得重算一遍。
        // 拖着滑块会连着触发，靠 EnsureBgBlurAsync 里的 busy/pending 合并
        if (_bgAction == BackgroundAction.Blur) _ = EnsureBgBlurAsync();
    }

    private void BgFeather_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 同上：XAML 解析期间的自动触发要挡住（见 BgWidth_ValueChanged 的说明）
        if (_syncingBgSliders || BgFeatherText is null) return;

        _bgFeather = e.NewValue / 100.0;
        BgFeatherText.Text = ((int)Math.Round(e.NewValue)).ToString();

        // 羽化半径变了，**整幅**的覆盖率都得重算（它是全局卷积，没法只算一块） ——
        // 这是三个滑块里唯一一个拖起来会有顿挫的，但它调的是"边缘软硬"，
        // 本来就是停下来微调的动作，不是边涂边拖的
        BgRepaintWhole();
    }

    /// <summary>颜色圆点按调色板生成，只建一次。</summary>
    private void BuildBgColors()
    {
        if (_bgColorButtons is not null)
        {
            UpdateBgColorButtons();
            return;
        }

        var list = new List<Button>();

        for (int i = 0; i < BgPalette.Length; i++)
        {
            var (name, r, g, b) = BgPalette[i];

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
            };

            var button = new Button
            {
                Tag = i.ToString(),
                Content = dot,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };

            ToolTipService.SetToolTip(button, name);
            button.Click += BgColor_Click;

            list.Add(button);
            BgColorHost.Children.Add(button);
        }

        _bgColorButtons = list;
        UpdateBgColorButtons();
    }

    private void BgColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag as string, out int id)) return;

        PickBgColor(id);
    }

    /// <summary>
    /// 挑了个预设色。
    ///
    /// 顺手把"用图片当背景"取消掉 —— 否则用户选着图片的时候点色块毫无反应，
    /// 只会以为是卡了。点了色块就是要用颜色，这是最直白的解释。
    /// </summary>
    private void PickBgColor(int id)
    {
        if (id < 0 || id >= BgPalette.Length) return;

        _bgColorIndex = id;
        _bgUseCustomColor = false;
        _bgImage = null;
        _bgImageW = 0;
        _bgImageH = 0;
        BgClearImageButton.Visibility = Visibility.Collapsed;

        UpdateBgColorButtons();

        if (_bgAction == BackgroundAction.Replace) BgRepaintWhole();
    }

    /// <summary>当前用来填充的颜色：自定义色优先，否则是调色板里那个。</summary>
    private (byte R, byte G, byte B) BgPanelColor()
    {
        if (_bgUseCustomColor) return (_bgCustomR, _bgCustomG, _bgCustomB);

        var (_, r, g, b) = BgPalette[Math.Clamp(_bgColorIndex, 0, BgPalette.Length - 1)];
        return (r, g, b);
    }

    private void UpdateBgColorButtons()
    {
        if (_bgColorButtons is null) return;

        // 用了图片或自定义色的时候，预设色块一个都不该亮
        bool preset = !_bgUseCustomColor && _bgImage is null;

        for (int i = 0; i < _bgColorButtons.Count; i++)
        {
            bool on = preset && i == _bgColorIndex;

            _bgColorButtons[i].BorderBrush = on
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            _bgColorButtons[i].Background = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    /// <summary>拖色盘时重画很贵（整幅一遍），限制到大概每秒七次。</summary>
    private DateTime _bgLastRepaint = DateTime.MinValue;

    private void RequestBgRepaint()
    {
        var now = DateTime.UtcNow;
        if ((now - _bgLastRepaint).TotalMilliseconds < 140) return;

        _bgLastRepaint = now;

        if (_backgrounding) BgRepaintWhole();
    }

    private void BgPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (!_backgrounding) return;

        var (cr, cg, cb) = BgPanelColor();

        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsHexInputVisible = true,
            Color = Windows.UI.Color.FromArgb(255, cr, cg, cb),
        };

        var flyout = new Flyout { Content = picker };

        // 拖色盘时实时预览（已节流）。注意这里的字段更新要放最前面：
        // 节流只挡住"重画"，不能挡住"记住用户挑的颜色"
        picker.ColorChanged += (_, args) =>
        {
            _bgCustomR = args.NewColor.R;
            _bgCustomG = args.NewColor.G;
            _bgCustomB = args.NewColor.B;
            _bgUseCustomColor = true;

            if (!_backgrounding) return;

            UpdateBgColorButtons();
            RequestBgRepaint();
        };

        // 关上色盘时补一次精确重画，顺便保证动作停在"替换"上
        flyout.Closed += (_, _) =>
        {
            if (!_backgrounding) return;

            if (_bgAction != BackgroundAction.Replace)
                PickBgAction((int)BackgroundAction.Replace);
            else
                BgRepaintWhole();
        };

        // ⚠️ ShowAt 是 void（不是 Task），不能 await —— 写 `await flyout.ShowAt(...)`
        // 报 CS4008 无法等待"void"。色盘是弹出的浮层，本来也不需要等它
        flyout.ShowAt(BgPickColorButton);
    }

    private async void BgPickImage_Click(object sender, RoutedEventArgs e)
    {
        if (!_backgrounding) return;

        try
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

            foreach (string ext in ImageFormats.Common.Concat(ImageFormats.Extended))
                picker.FileTypeFilter.Add(ext);

            // WinUI3 里 picker 必须绑到窗口句柄，否则一调用就抛异常
            IntPtr hwnd = SafeHost?.WindowHandle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null || !_backgrounding) return;

            byte[] bytes = await File.ReadAllBytesAsync(file.Path);
            if (bytes.Length == 0) return;

            byte[]? pixels = null;
            int w = 0, h = 0;
            await Task.Run(() => pixels = ImageEditService.LoadPixels(bytes, out w, out h));

            if (pixels is null || w <= 0 || h <= 0)
            {
                await ShowMessageAsync("这张图用不了", "读不出像素数据，换一张试试。");
                return;
            }

            if (!_backgrounding) return;

            _bgImage = pixels;
            _bgImageW = w;
            _bgImageH = h;
            _bgUseCustomColor = false;

            BgClearImageButton.Visibility = Visibility.Visible;
            UpdateBgColorButtons();

            // 选了图片，用户要的显然是"换背景" —— 顺手把动作切过去，
            // 省得他选了图却发现画面上什么都没变
            if (_bgAction != BackgroundAction.Replace)
                PickBgAction((int)BackgroundAction.Replace);
            else
                BgRepaintWhole();
        }
        catch
        {
            // 用户取消、或者窗口还没准备好，都不需要弹任何东西
        }
    }

    private void BgClearImage_Click(object sender, RoutedEventArgs e)
    {
        if (!_backgrounding) return;

        _bgImage = null;
        _bgImageW = 0;
        _bgImageH = 0;
        BgClearImageButton.Visibility = Visibility.Collapsed;

        UpdateBgColorButtons();

        if (_bgAction == BackgroundAction.Replace) BgRepaintWhole();
    }

    /// <summary>
    /// 工具条不能宽过窗口 —— 否则窄窗口下"应用"会被顶出屏幕，用户就卡在背景模式里出不去了。
    /// 超了就让里面那条横向滚。
    /// </summary>
    private void BgLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        BackgroundToolbar.MaxWidth = Math.Max(320, e.NewSize.Width - 48);
    }

    // ===== 底部工具条：装不下时横向滚动 =====

    /// <summary>工具条最多占满窗口宽度（两边各留 16），绝不超出 —— 超了就会被切掉。</summary>
    private void UpdateBottomBarWidth()
    {
        double avail = ActualWidth > 0 ? ActualWidth : 800;
        BottomBar.MaxWidth = Math.Max(180, avail - 32);
        UpdateBarOverflow();
    }

    private void BarScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBarOverflow();

    private void BarButtons_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBarOverflow();

    private void BarScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateBarOverflow();

    /// <summary>
    /// 决定左右两个小箭头要不要出来。
    ///
    /// 用 Opacity + IsEnabled 藏，而不是 Visibility ——
    /// 箭头的占位宽度不变，就不会出现"藏箭头 → 条变宽 → 装得下了 → 又显示箭头"的抖动。
    /// </summary>
    private void UpdateBarOverflow()
    {
        double scrollable = BarScroll.ScrollableWidth;
        if (scrollable <= 1)
        {
            SetChevron(BarLeftButton, false);
            SetChevron(BarRightButton, false);
            return;
        }

        SetChevron(BarLeftButton, BarScroll.HorizontalOffset > 1);
        SetChevron(BarRightButton, BarScroll.HorizontalOffset < scrollable - 1);
    }

    private static void SetChevron(Button button, bool on)
    {
        button.Opacity = on ? 0.75 : 0;
        button.IsEnabled = on;
    }

    /// <summary>一次翻 160 像素，大约是四个按钮，不会一下跳过去看不清。</summary>
    private void BarLeftButton_Click(object sender, RoutedEventArgs e)
        => BarScroll.ChangeView(Math.Max(0, BarScroll.HorizontalOffset - 160), null, null);

    private void BarRightButton_Click(object sender, RoutedEventArgs e)
        => BarScroll.ChangeView(
            Math.Min(BarScroll.ScrollableWidth, BarScroll.HorizontalOffset + 160), null, null);

    /// <summary>
    /// 滚轮在工具条上改做横向滚动。
    /// 装得下的时候直接放行（return），滚轮照常去做它该做的事。
    /// </summary>
    private void OnBottomBarWheel(object sender, PointerRoutedEventArgs e)
    {
        if (BarScroll.ScrollableWidth <= 1) return;

        int delta = e.GetCurrentPoint(BottomBar).Properties.MouseWheelDelta;
        if (delta == 0) return;

        double next = Math.Clamp(BarScroll.HorizontalOffset - delta, 0, BarScroll.ScrollableWidth);
        BarScroll.ChangeView(next, null, null);
        e.Handled = true;
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

            // zip / cbz 也列出来：它们虽然不是图片，但打开后能在里面翻页
            foreach (string ext in ImageFormats.Common
                                       .Concat(ImageFormats.Extended)
                                       .Concat(ImageFormats.Archives))
                picker.FileTypeFilter.Add(ext);

            // WinUI3 里 picker 必须绑到窗口句柄，否则一调用就抛异常
            IntPtr hwnd = SafeHost?.WindowHandle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is not null) OpenPath(file.Path);
        }
        catch
        {
            // 用户取消、或者窗口还没准备好，都不需要弹任何东西
        }
    }

    // ===== 编辑：旋转 / 另存为 / 复制 / 改尺寸 / 打印 =====

    /*
      编辑的原则：**只改内存，不碰原文件**。

      旋转、改尺寸的结果存在 _editBytes 里，画面立刻更新，
      但硬盘上那个文件一个字节都不动 —— 只有点了"另存为"才落盘。
      这样反复试效果不会把原图搞坏，也不会在硬盘上堆一串中间文件。
      翻到下一张时 _editBytes 清空，编辑跟着作废（没保存就是没保存，符合直觉）。
    */

    /// <summary>编辑之后的图像（PNG 字节）。null = 还没动过，屏幕上就是原图。</summary>
    private byte[]? _editBytes;

    /// <summary>把当前这张图的原始字节读出来（压缩包里的图也支持）。</summary>
    private static async Task<byte[]?> LoadOriginalBytesAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            if (ArchiveIndex.IsVirtual(path))
            {
                if (!ArchiveIndex.TrySplit(path, out string zip, out string entry)) return null;
                var ms = await Task.Run(() => ArchiveIndex.ReadEntry(zip, entry));
                return ms?.ToArray();
            }

            return await File.ReadAllBytesAsync(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>该拿去编辑 / 保存的那份字节：编辑过就用编辑结果，否则读原文件。</summary>
    private async Task<byte[]> GetWorkingBytesAsync()
    {
        if (_editBytes is not null) return _editBytes;
        return await LoadOriginalBytesAsync(_index.CurrentPath ?? "") ?? Array.Empty<byte>();
    }

    /// <summary>跑一次编辑操作，然后把结果显示出来。</summary>
    private async Task ApplyEditAsync(Func<byte[], byte[]> operation)
    {
        // 先把手上没落定的风格合进像素：否则旋转会作用在"没风格的原图"上，
        // 屏幕上明明有风格，转完却没了，很莫名
        await BakeLookAsync();

        byte[] source = await GetWorkingBytesAsync();
        if (source.Length == 0) return;

        byte[] result = await Task.Run(() => operation(source));
        if (result.Length == 0) return;

        _editBytes = result;
        _basePixels = null;      // 像素已经变了，底图下次重新取
        await DisplayBytesAsync(result);
    }

    private async Task DisplayBytesAsync(byte[] bytes)
    {
        var decoded = await BitmapHelper.DecodeBytesAsync(bytes);
        if (decoded is null)
        {
            ShowEmpty("这张图改完之后打不开了");
            return;
        }

        StopZoomAnimation();
        ImageView.Source = decoded.Source;
        _displayWidth = decoded.Width;
        _displayHeight = decoded.Height;
        SizeText.Text = $"{decoded.Width} × {decoded.Height}";

        _zoom = 1.0;
        ApplyZoomToLayout();
        Scroller.UpdateLayout();
        Scroller.ChangeView(0, 0, null, true);
        UpdateZoomText();
    }

    private async void RotateButton_Click(object sender, RoutedEventArgs e)
        => await ApplyEditAsync(b => ImageEditService.Rotate(b, 90));

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
        => await SaveAsAsync();

    private async Task SaveAsAsync()
    {
        // 调过风格但没点"应用"的话，这里先合进去 ——
        // 用户看到屏幕上有效果，存出去当然也该有，不该要他去记着先点应用
        await BakeLookAsync();

        byte[] bytes = await GetWorkingBytesAsync();
        if (bytes.Length == 0) return;

        IntPtr hwnd = SafeHost?.WindowHandle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var picker = new FileSavePicker();
        foreach (var (name, ext) in ImageEditService.SaveFormats)
            picker.FileTypeChoices.Add(name, new List<string> { ext });

        string? current = _index.CurrentPath;
        picker.SuggestedFileName = current is null
            ? "未命名"
            : Path.GetFileNameWithoutExtension(current.Split(ArchiveIndex.Separator)[0]);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;      // 用户取消

        bool ok = await Task.Run(() => ImageEditService.Save(bytes, file.Path));
        await ShowMessageAsync(
            ok ? "已保存" : "保存失败",
            ok ? file.Path : "可能是那个位置不允许写入，换个地方试试。");
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
        => await CopyImageAsync();

    /// <summary>
    /// 把图片复制到剪贴板。之后可以直接粘到微信、Word、画图里。
    ///
    /// 走内存流而不是临时文件：StorageFile.GetFileFromPathAsync 在启动阶段
    /// 有过 5/8 概率的崩溃（黑匣子里记着），能绕就绕。
    /// </summary>
    private async Task CopyImageAsync()
    {
        await BakeLookAsync();

        byte[] bytes = await GetWorkingBytesAsync();
        if (bytes.Length == 0) return;

        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
        }
        catch
        {
            // 剪贴板被别的程序占着时会失败，静默处理 —— 弹窗比失败更烦人
        }
    }

    private async void ResizeButton_Click(object sender, RoutedEventArgs e)
        => await ResizeAsync();

    private async Task ResizeAsync()
    {
        byte[] bytes = await GetWorkingBytesAsync();
        if (bytes.Length == 0) return;

        var (w0, h0) = await Task.Run(() => ImageEditService.SizeOf(bytes));
        if (w0 <= 0 || h0 <= 0) return;

        var widthBox = new TextBox { Text = w0.ToString(), Width = 110, Header = "宽度（像素）" };
        var heightBox = new TextBox { Text = h0.ToString(), Width = 110, Header = "高度（像素）" };
        var lockRatio = new CheckBox { Content = "保持宽高比", IsChecked = true };

        // 改一边、另一边按比例跟着变。updating 是防两边互相触发死循环的闸门
        bool updating = false;

        widthBox.TextChanged += (_, _) =>
        {
            if (updating || lockRatio.IsChecked != true) return;
            if (!int.TryParse(widthBox.Text, out int w) || w <= 0) return;

            updating = true;
            heightBox.Text = Math.Max(1, (int)Math.Round(w * h0 / (double)w0)).ToString();
            updating = false;
        };

        heightBox.TextChanged += (_, _) =>
        {
            if (updating || lockRatio.IsChecked != true) return;
            if (!int.TryParse(heightBox.Text, out int h) || h <= 0) return;

            updating = true;
            widthBox.Text = Math.Max(1, (int)Math.Round(h * w0 / (double)h0)).ToString();
            updating = false;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(widthBox);
        row.Children.Add(heightBox);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(row);
        panel.Children.Add(lockRatio);
        panel.Children.Add(new TextBlock
        {
            Text = $"原尺寸 {w0} × {h0}",
            FontSize = 12,
            Opacity = 0.7,
        });

        var dialog = new ContentDialog
        {
            Title = "调整图像大小",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (!int.TryParse(widthBox.Text, out int width)
            || !int.TryParse(heightBox.Text, out int height)
            || width <= 0 || height <= 0) return;

        await ApplyEditAsync(b => ImageEditService.Resize(b, width, height));
    }

    private async void PrintButton_Click(object sender, RoutedEventArgs e)
        => await PrintAsync();

    private async Task PrintAsync()
    {
        if (ImageView.Source is null || _displayWidth <= 0) return;

        IntPtr hwnd = SafeHost?.WindowHandle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var helper = new PrintHelper(hwnd, ImageView.Source, _displayWidth, _displayHeight);
        await helper.PrintAsync();
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "好",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // 窗口正在关闭时 XamlRoot 会失效，弹不出来就算了
        }
    }

    // ===== 风格：滤镜 + 调整 =====

    /*
      两条原则：

      1. **不碰原文件**。滤镜和调整全在内存里算，屏幕上看到的是预览。
         点"应用"才把结果固化进 _editBytes；不点就翻页，效果自动作废。
         另存为 / 复制会自动带上当前风格，不用先点应用 —— 所见即所得。

      2. **预览走降采样**。8000px 的图逐像素算一遍要一秒多，拖滑块会卡成幻灯片。
         所以预览固定缩到长边 1600px 再算（约 40ms，拖着也跟手），
         只有"应用 / 另存"时才在原始分辨率上全量跑一次。
    */

    /// <summary>预览的最大边长。画质和速度的折中，这个尺寸下肉眼看不出和原图的差别。</summary>
    private const int LookPreviewMaxSide = 1600;

    private bool _lookVisible;
    private Storyboard? _lookStoryboard;

    /// <summary>当前风格的底图（BGRA 原始像素）。null = 还没加载。</summary>
    private byte[]? _basePixels;
    private int _baseWidth;
    private int _baseHeight;

    /// <summary>当前生效的风格参数。翻页时归零 —— 换一张图就是重新开始。</summary>
    private LookSettings _look;

    /// <summary>上一次已经渲到屏幕上的参数。跟 _look 一样就不用重算。</summary>
    private LookSettings _lookRendered;

    private CancellationTokenSource? _lookCts;

    /// <summary>所有滑块。重置和自动增强之后要统一刷一遍。</summary>
    private readonly List<(Slider Slider, TextBlock Value, Func<LookSettings, int, LookSettings> With)> _adjustSliders = new();

    /// <summary>
    /// 程序去设 Slider.Value 时也会触发 ValueChanged，用这个闸门挡掉 ——
    /// 否则"重置"会变成"重置 → 触发重算 → 再重置"的死循环。
    /// </summary>
    private bool _adjusting;

    /// <summary>
    /// 十项调整。用表驱动而不是写十段重复代码：
    /// 加一项只动这一处，滑块、数值、重置、自动增强全自动跟着。
    /// </summary>
    private static readonly (string Name, Func<LookSettings, int> Get, Func<LookSettings, int, LookSettings> With)[] AdjustItems =
    {
        ("亮度",     s => s.Brightness, (s, v) => { s.Brightness = v; return s; }),
        ("曝光",     s => s.Exposure,   (s, v) => { s.Exposure = v; return s; }),
        ("对比度",   s => s.Contrast,   (s, v) => { s.Contrast = v; return s; }),
        ("突出显示", s => s.Highlights, (s, v) => { s.Highlights = v; return s; }),
        ("阴影",     s => s.Shadows,    (s, v) => { s.Shadows = v; return s; }),
        ("晕影",     s => s.Vignette,   (s, v) => { s.Vignette = v; return s; }),
        ("饱和度",   s => s.Saturation, (s, v) => { s.Saturation = v; return s; }),
        ("暖度",     s => s.Warmth,     (s, v) => { s.Warmth = v; return s; }),
        ("色调",     s => s.Tint,       (s, v) => { s.Tint = v; return s; }),
        ("清晰度",   s => s.Clarity,    (s, v) => { s.Clarity = v; return s; }),
    };

    private void LookButton_Click(object sender, RoutedEventArgs e) => ToggleLook();

    private void ToggleLook() => SetLookVisible(!_lookVisible);

    private void LookCloseButton_Click(object sender, RoutedEventArgs e) => SetLookVisible(false);

    private void SetLookVisible(bool visible)
    {
        // 三个面板互斥：开风格面板就把另外两个收掉
        if (visible && _infoVisible) SetInfoVisible(false);
        if (visible && _ocrVisible) SetOcrVisible(false);

        _lookVisible = visible;
        AnimateSidePanel(LookPanel, LookPanelTranslate, visible, ref _lookStoryboard);

        // 第一次打开才去加载像素和缩略图；像素按图缓存，反复开关不重复解码
        if (visible) _ = PrepareLookAsync();
    }

    /// <summary>
    /// 面板打开时做准备：拿到底图像素、生成滤镜缩略图、建好滑块。
    /// 底图按"当前这张图"缓存 —— 同一次打开反复开关不会重复解码。
    /// </summary>
    private async Task PrepareLookAsync()
    {
        if (_displayWidth <= 0) return;

        if (_basePixels is null)
        {
            byte[] working = await GetWorkingBytesAsync();
            if (working.Length == 0) return;

            byte[]? pixels = null;
            int w = 0, h = 0;
            await Task.Run(() => pixels = ImageEditService.LoadPixels(working, out w, out h));

            if (pixels is null || w <= 0 || h <= 0)
            {
                await ShowMessageAsync("这张图没法调整", "读不出像素数据，可能是格式太特殊。");
                return;
            }

            _basePixels = pixels;
            _baseWidth = w;
            _baseHeight = h;
        }

        BuildAdjustSliders();
        _ = BuildFilterThumbsAsync();
        await RefreshLookPreviewAsync();
    }

    /// <summary>换图 / 关窗口时把底图扔掉。几十 MB 的像素数据不该一直挂着。</summary>
    private void ReleaseLookPixels()
    {
        _lookCts?.Cancel();
        _lookCts = null;
        _basePixels = null;
        _baseWidth = 0;
        _baseHeight = 0;
        _look = default;
        _lookRendered = default;
    }

    // ===== 滤镜缩略图 =====

    /// <summary>
    /// 给十几个滤镜各算一张缩略图。
    ///
    /// 缩略图是从**当前这张图**现算的（不是画死的示意图），
    /// 所以每个滤镜在这张图上到底什么效果，点之前就看得见。
    /// 这一步在后台跑，一张 96px 的小图算得极快，十几个也不到 100ms。
    /// </summary>
    private async Task BuildFilterThumbsAsync()
    {
        if (_basePixels is null) return;

        byte[]? thumbs = await Task.Run(() =>
            PhotoLook.Downscale(_basePixels, _baseWidth, _baseHeight, 220));

        if (thumbs is null) return;

        // Downscale 和引擎内部用同一套取整算法，宽高这么算是准的
        double k = Math.Min(1.0, 220.0 / Math.Max(_baseWidth, _baseHeight));
        int tw = Math.Max(1, (int)Math.Round(_baseWidth * k));
        int th = Math.Max(1, (int)Math.Round(_baseHeight * k));

        var cards = new List<FrameworkElement>();
        int count = PhotoLook.Filters.Length;

        for (int i = 0; i < count; i++)
        {
            var (name, look) = PhotoLook.Filters[i];

            byte[] preview = PhotoLook.Apply(thumbs, tw, th, look);
            var bmp = ToWriteableBitmap(preview, tw, th);
            if (bmp is null) continue;

            var card = MakeFilterCard(name, bmp, look);
            cards.Add(card);
        }

        // 三列一组，最后一组不满也照样铺
        FilterHost.Children.Clear();
        for (int i = 0; i < cards.Count; i += 3)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int c = 0; c < 3 && i + c < cards.Count; c++)
            {
                Grid.SetColumn(cards[i + c], c);
                cards[i + c].Margin = new Thickness(c == 0 ? 0 : 3, 0, c == 2 ? 0 : 3, 0);
                row.Children.Add(cards[i + c]);
            }

            FilterHost.Children.Add(row);
        }
    }

    private FrameworkElement MakeFilterCard(string name, Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap thumb, LookSettings look)
    {
        var image = new Microsoft.UI.Xaml.Controls.Image
        {
            Source = thumb,
            Height = 52,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = name,
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 190, 190)),
        };

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(image);
        stack.Children.Add(label);

        var button = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 6, 4, 5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };

        button.Click += (_, _) =>
        {
            _look = look;
            SyncSliders();
            _ = RefreshLookPreviewAsync();
        };

        return button;
    }

    // ===== 调整滑块 =====

    private void BuildAdjustSliders()
    {
        if (_adjustSliders.Count > 0) return;   // 只建一次

        foreach (var (name, get, with) in AdjustItems)
        {
            var value = new TextBlock
            {
                Text = "0",
                FontSize = 11,
                MinWidth = 28,
                TextAlignment = TextAlignment.Right,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 140, 140, 140)),
            };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 210, 210, 210)),
            });
            Grid.SetColumn(value, 1);
            header.Children.Add(value);

            var slider = new Slider
            {
                Minimum = PhotoLook.SliderMin,
                Maximum = PhotoLook.SliderMax,
                StepFrequency = 1,
                Value = 0,
                Margin = new Thickness(0, -6, 0, 0),
            };

            // 拖动时同步数值文字，并请求一次重算（RefreshLookPreviewAsync 内部有合并，
            // 拖得再快也不会堆出一串没完成的渲染任务）
            slider.ValueChanged += (_, _) =>
            {
                if (_adjusting) return;

                int v = (int)Math.Round(slider.Value);
                value.Text = v.ToString();
                _look = with(_look, v);
                _ = RefreshLookPreviewAsync();
            };

            var block = new StackPanel { Spacing = 0 };
            block.Children.Add(header);
            block.Children.Add(slider);

            SliderHost.Children.Add(block);
            _adjustSliders.Add((slider, value, with));
        }
    }

    /// <summary>把滑块和数值文字刷成 _look 当前的值（选了滤镜、重置、自动增强之后用）。</summary>
    private void SyncSliders()
    {
        _adjusting = true;
        try
        {
            for (int i = 0; i < _adjustSliders.Count; i++)
            {
                var (slider, value, _) = _adjustSliders[i];
                int v = AdjustItems[i].Get(_look);

                slider.Value = v;
                value.Text = v.ToString();
            }
        }
        finally
        {
            _adjusting = false;
        }
    }

    // ===== 预览渲染 =====

    /// <summary>
    /// 按当前 _look 重新渲一遍预览。
    ///
    /// **合并请求**：连着调的话，后一次会把前一次取消掉（_cancel），
    /// 所以拖滑块再快，也只会有一个渲染任务在跑，不会越拖越卡。
    /// </summary>
    private async Task RefreshLookPreviewAsync()
    {
        if (_basePixels is null) return;
        if (_look.Equals(_lookRendered)) return;

        _lookCts?.Cancel();
        _lookCts = new CancellationTokenSource();
        CancellationToken ct = _lookCts.Token;

        LookSettings look = _look;
        _lookRendered = look;

        byte[]? result = await Task.Run(() =>
        {
            byte[] src = _basePixels!;
            int w = _baseWidth, h = _baseHeight;

            // 预览才降采样；原图本来就比预览小的话就不动（降采样只会让它糊）
            if (Math.Max(w, h) > LookPreviewMaxSide)
            {
                src = PhotoLook.Downscale(src, w, h, LookPreviewMaxSide);
                double k = LookPreviewMaxSide / (double)Math.Max(w, h);
                w = Math.Max(1, (int)Math.Round(w * k));
                h = Math.Max(1, (int)Math.Round(h * k));
            }

            return PhotoLook.Apply(src, w, h, look);
        }, ct);

        if (result is null || ct.IsCancellationRequested) return;

        // 风格是"原图"时 Apply 会直接把原数组还回来 —— 那可能就是 _basePixels 本身，
        // 尺寸得用全尺寸的，不能按预览尺寸算
        int outW = _baseWidth, outH = _baseHeight;
        if (Math.Max(_baseWidth, _baseHeight) > LookPreviewMaxSide)
        {
            double k = LookPreviewMaxSide / (double)Math.Max(_baseWidth, _baseHeight);
            outW = Math.Max(1, (int)Math.Round(_baseWidth * k));
            outH = Math.Max(1, (int)Math.Round(_baseHeight * k));
        }

        var bmp = ToWriteableBitmap(result, outW, outH);
        if (bmp is null) return;

        StopZoomAnimation();
        ImageView.Source = bmp;
        _displayWidth = outW;
        _displayHeight = outH;
        SizeText.Text = $"{outW} × {outH}";

        _zoom = 1.0;
        ApplyZoomToLayout();
        UpdateZoomText();
    }

    /// <summary>BGRA 字节 → 可显示的位图。尺寸对不上返回 null，不抛异常。</summary>
    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? ToWriteableBitmap(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

        try
        {
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
            using var stream = bmp.PixelBuffer.AsStream();
            stream.Write(bgra, 0, width * height * 4);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ===== 应用 / 重置 / 自动增强 =====

    private async void ResetLookButton_Click(object sender, RoutedEventArgs e)
    {
        _look = default;
        SyncSliders();
        await RefreshLookPreviewAsync();
    }

    private async void AutoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_basePixels is null) return;

        // 自动增强是**按这张图自己的直方图**算的，所以先用降采样的样品算参数，
        // 再把参数套到全图上 —— 缩放不影响直方图的形状，结果一样，但快得多
        LookSettings auto = await Task.Run(() =>
        {
            byte[] src = _basePixels;
            int w = _baseWidth, h = _baseHeight;

            if (Math.Max(w, h) > 320)
            {
                src = PhotoLook.Downscale(src, w, h, 320);
                double k = 320.0 / Math.Max(w, h);
                w = Math.Max(1, (int)Math.Round(w * k));
                h = Math.Max(1, (int)Math.Round(h * k));
            }
            return PhotoLook.AutoEnhance(src, w, h);
        });

        _look = auto;
        SyncSliders();
        await RefreshLookPreviewAsync();
    }

    private async void ApplyLookButton_Click(object sender, RoutedEventArgs e)
        => await BakeLookAsync(showMessage: true);

    /// <summary>
    /// 把当前风格**全量**烘进 _editBytes（在原始分辨率上算，不是预览尺寸）。
    ///
    /// 之后风格参数归零：效果已经进了像素，再留着参数就会叠加两次。
    /// 另存为和复制都会先调它，所以屏幕上看到的和存出去的一定一致。
    /// </summary>
    private async Task BakeLookAsync(bool showMessage = false)
    {
        if (_basePixels is null || _look.IsNeutral) return;

        LookSettings look = _look;
        byte[]? result = await Task.Run(() =>
            PhotoLook.Apply(_basePixels, _baseWidth, _baseHeight, look));

        if (result is null || result.Length == 0) return;

        byte[] png = await Task.Run(() =>
            ImageEditService.FromPixels(result, _baseWidth, _baseHeight));

        if (png.Length == 0)
        {
            if (showMessage) await ShowMessageAsync("应用失败", "这张图处理完存不下来。");
            return;
        }

        _editBytes = png;
        _look = default;
        SyncSliders();

        await DisplayBytesAsync(png);

        // 图已经被改过了，底图得重新取（否则下次开面板还是旧的像素）。
        // 面板还开着的话立刻补一份新的，用户接着拖滑块才不会失效。
        _basePixels = null;
        if (_lookVisible) _ = PrepareLookAsync();

        if (showMessage) await ShowMessageAsync("已应用", "效果已经合进图片，继续调就是在新图上叠加。");
    }

    // ===== 拖放 =====

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "用 CelesteGallery 打开";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault();
        if (file is not null) OpenPath(file.Path);
    }

    // ===== 杂项 =====

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
