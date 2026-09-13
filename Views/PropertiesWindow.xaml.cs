using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CelesteViewer.Helpers;
using CelesteViewer.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CelesteViewer.Views;

/// <summary>
/// 图片「属性」窗口。
///
/// 从缩略图墙右键「属性」弹出，居中显示这一张图的信息：
/// 文件名 / 路径 / 类型 / 尺寸 / 体积 / 修改时间 / 拍摄参数(EXIF) / 解码器。
///
/// 拍不到的参数（比如没有 EXIF 的截图）直接不显示那一行，不留空行。
/// 窗口本身是正常的对话框：标题栏有关闭按钮，也能按 Esc 关，
/// 底部「确定」只是给鼠标用户一个明显出口。
///
/// 居中 + 抬到最前的写法和 AboutWindow 一致（见那里的注释）：
/// 程序已经在跑的时候新建窗口，光靠 Activate() 会落在主窗口后面。
/// </summary>
public sealed partial class PropertiesWindow : Window
{
    /// <summary>持有当前窗口引用，避免被垃圾回收（"窗口闪一下就没了"的坑）。</summary>
    private static PropertiesWindow? _current;

    private readonly AppWindow? _appWindow;
    private bool _closed;
    private string? _path;

    public static void Show(string path)
    {
        if (_current is not null)
        {
            _current.BringToFront();
            return;
        }

        _current = new PropertiesWindow(path);
    }

    public PropertiesWindow(string path)
    {
        InitializeComponent();

        _path = path;
        _appWindow = AppWindow;
        Title = "属性";
        AppIcon.ApplyToWindow(_appWindow);

        NameText.Text = Path.GetFileName(path);

        ConfigureWindowChrome();
        _ = LoadDetailsAsync(path);

        Closed += OnClosed;
        BringToFront();
    }

    private void ConfigureWindowChrome()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // 属性窗口可以缩放（信息多了能拉高看），但不能最小化/最大化到全屏
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }

            // 比关于窗口矮一点：属性是单屏信息，560 在 1080p 上还留有余量。
            // 相对主窗口居中（和「关于」一样），不然主窗口偏着摆时会弹出在老远的地方。
            WindowPlacement.ResizeAndCenterOnOwner(AppWindow, 440, 560);
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("PropertiesWindow: 设置窗口外观失败", ex);
        }
    }

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

    private void ResizeAndCenter(int width, int height)
    {
        if (_appWindow is null) return;

        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;

            _appWindow.Resize(new SizeInt32(width, height));
            _appWindow.Move(new PointInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2));
        }
        catch
        {
            // 拿不到显示器信息就用系统默认位置，不影响看
        }
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    // ===== 读信息 =====

    private async Task LoadDetailsAsync(string path)
    {
        try
        {
            // 用和主程序同一个解码总入口；includeMetadata=true 才会去读 EXIF。
            // 失败（坏图 / svg 这类不支持的格式）就返回 null，下面给一句提示，不崩。
            var info = await new ImageDecodePipeline()
                .ProbeAsync(path, includeMetadata: true, CancellationToken.None);

            if (info is null)
            {
                AddRow("信息", "无法读取该文件的详细信息（可能格式不支持或文件已损坏）。");
                return;
            }

            FillDetails(info);
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write($"PropertiesWindow: 读取属性失败 {path}", ex);
            AddRow("信息", "读取属性时出错。");
        }
    }

    private void FillDetails(PhotoInfo info)
    {
        // 路径（可整段选中复制）
        AddRow("路径", info.Path, wrap: true);

        // 类型：扩展名大写 + 中文
        string ext = Path.GetExtension(info.Path);
        AddRow("类型", string.IsNullOrEmpty(ext)
            ? "图片"
            : ext.TrimStart('.').ToUpperInvariant() + " 图片");

        // 尺寸 + 百万像素
        if (info.PixelWidth > 0 && info.PixelHeight > 0)
        {
            string size = $"{info.PixelWidth} × {info.PixelHeight}";
            if (info.Megapixels >= 0.05)
                size += $"　（{info.Megapixels:F1} 百万像素）";
            AddRow("尺寸", size);
        }

        // 体积
        if (info.FileSize > 0)
            AddRow("文件大小", FormatBytes(info.FileSize));

        // 修改时间
        if (info.LastModified != default)
            AddRow("修改时间", info.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        // ===== EXIF（拍不到就跳过这一行）=====
        if (info.DateTaken is not null)
            AddRow("拍摄时间", info.DateTaken.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        if (!string.IsNullOrEmpty(info.CameraMake) || !string.IsNullOrEmpty(info.CameraModel))
        {
            string camera = string.Join(" ",
                new[] { info.CameraMake, info.CameraModel }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            AddRow("相机", camera);
        }

        if (!string.IsNullOrEmpty(info.LensModel))
            AddRow("镜头", info.LensModel);

        if (!string.IsNullOrEmpty(info.FNumber)) AddRow("光圈", "f/" + info.FNumber.TrimStart('f', 'F', '/'));
        if (!string.IsNullOrEmpty(info.ExposureTime)) AddRow("快门", info.ExposureTime);
        if (!string.IsNullOrEmpty(info.IsoSpeed)) AddRow("ISO", info.IsoSpeed);
        if (!string.IsNullOrEmpty(info.FocalLength)) AddRow("焦距", info.FocalLength);
        if (!string.IsNullOrEmpty(info.ColorSpace)) AddRow("色彩空间", info.ColorSpace);

        // 解码器：WIC / Magick（落到了兜底说明慢一些）
        if (!string.IsNullOrEmpty(info.DecoderName))
            AddRow("解码器", info.DecoderName == "WIC" ? "系统解码（WIC）" : "Magick.NET 兜底");
    }

    /// <summary>往明细区加一行（标签 + 值）。值太长（如路径）就换行可整段选中。</summary>
    private void AddRow(string label, string value, bool wrap = false)
    {
        // 值那个 TextBlock 要留个引用：Grid.SetColumn 要的是 FrameworkElement，
        // 而 grid.Children[i] 取出来是 UIElement，直接传编译不过（CS1503）。
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            Foreground = Brush("#E2E2E2"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = wrap,
        };

        var grid = new Grid
        {
            ColumnSpacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontSize = 12.5,
                    Foreground = Brush("#7AFFFFFF"),
                    VerticalAlignment = VerticalAlignment.Top,
                },
                valueText,
            },
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(valueText, 1);

        Details.Children.Add(grid);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
    }

    /// <summary>把 #AARRGGBB / #RRGGBB 解析成画刷（WinUI3 的 Colors 没有 Parse，手写）。</summary>
    private static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(string hex)
    {
        string h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;

        byte a = Convert.ToByte(h.Substring(0, 2), 16);
        byte r = Convert.ToByte(h.Substring(2, 2), 16);
        byte g = Convert.ToByte(h.Substring(4, 2), 16);
        byte b = Convert.ToByte(h.Substring(6, 2), 16);

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }

    // ===== 底部按钮 =====

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_path)) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_path);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_path)) ExplorerHelper.RevealFile(_path);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
