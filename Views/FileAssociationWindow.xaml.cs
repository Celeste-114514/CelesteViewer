using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CelesteGallery.Views;

/// <summary>
/// 「文件关联」窗口：按类别列出本看图器能打开的每一种格式，
/// 用户勾哪些就把哪些格式的"双击打开"交给本程序。
///
/// 逻辑整份照搬 CelesteMusicPlayer 的 FileAssociationWindow（那份已经实机跑过），
/// 只有三处按看图器的情况作了调整：
///   1. 格式清单换成图片格式（含相机 RAW 和可当一叠图翻的压缩包）；
///   2. 样式换成和「关于」「属性」一致的深色卡片；
///   3. 定位改成相对主窗口居中（<see cref="WindowPlacement"/>）。
///
/// 三条不能改的规矩（改了就会出问题，MusicPlayer 那边踩过）：
///   - 默认只勾推荐的一批，不全选：.psd / RAW / .zip 默认抢过来会动到别人的默认程序；
///   - 「应用」时先注册勾选的、再解除这次没勾的，顺序反了会把刚写好的 ProgId 删掉；
///   - 解除只删"默认值指向本程序"的键，别人的关联一个都不碰。
/// </summary>
public sealed partial class FileAssociationWindow : Window
{
    private sealed record FormatItem(string Ext, string Name, string Desc);

    private sealed record FormatGroup(string Title, string Hint, FormatItem[] Items);

    private static readonly FormatGroup[] Groups =
    {
        new("常见格式", "日常照片、截图、动图，建议全选", new[]
        {
            new FormatItem(".jpg", ".jpg", "JPEG · 最常见的照片格式"),
            new FormatItem(".jpeg", ".jpeg", "JPEG · 长扩展名写法"),
            new FormatItem(".png", ".png", "PNG · 无损，支持透明"),
            new FormatItem(".gif", ".gif", "GIF · 动图"),
            new FormatItem(".bmp", ".bmp", "BMP · 未压缩"),
            new FormatItem(".webp", ".webp", "WebP · 体积小，网页常用"),
        }),
        new("高清与现代格式", "新一代压缩格式、高动态范围、扫描件", new[]
        {
            new FormatItem(".heic", ".heic", "HEIC · iPhone 照片默认格式"),
            new FormatItem(".heif", ".heif", "HEIF · HEIC 的容器形式"),
            new FormatItem(".avif", ".avif", "AVIF · 新一代高压缩比"),
            new FormatItem(".jxl", ".jxl", "JPEG XL · 下一代 JPEG"),
            new FormatItem(".tif", ".tif", "TIFF · 扫描与印刷常用"),
            new FormatItem(".tiff", ".tiff", "TIFF · 长扩展名写法"),
            new FormatItem(".exr", ".exr", "EXR · 高动态范围"),
            new FormatItem(".hdr", ".hdr", "HDR · 高动态范围"),
        }),
        new("相机 RAW", "各家的原始底片，由 Magick.NET 解码（比常见格式慢一些）", new[]
        {
            new FormatItem(".arw", ".arw", "索尼"),
            new FormatItem(".cr2", ".cr2", "佳能（旧）"),
            new FormatItem(".cr3", ".cr3", "佳能（新）"),
            new FormatItem(".nef", ".nef", "尼康"),
            new FormatItem(".nrw", ".nrw", "尼康（紧凑）"),
            new FormatItem(".orf", ".orf", "奥林巴斯"),
            new FormatItem(".rw2", ".rw2", "松下"),
            new FormatItem(".raf", ".raf", "富士"),
            new FormatItem(".dng", ".dng", "Adobe 通用 RAW"),
            new FormatItem(".pef", ".pef", "宾得"),
            new FormatItem(".srw", ".srw", "三星"),
        }),
        new("设计与其它", "修图软件的工程文件与一些老格式", new[]
        {
            new FormatItem(".psd", ".psd", "PSD · Photoshop"),
            new FormatItem(".psb", ".psb", "PSB · 大型 Photoshop 文件"),
            new FormatItem(".xcf", ".xcf", "XCF · GIMP"),
            new FormatItem(".tga", ".tga", "TGA · Targa"),
            new FormatItem(".pcx", ".pcx", "PCX · 老格式"),
            new FormatItem(".ico", ".ico", "ICO · 图标"),
            new FormatItem(".jxr", ".jxr", "JPEG XR"),
        }),
        new("压缩包（当一叠图翻）", "打开后直接按页翻，不用先解压", new[]
        {
            new FormatItem(".zip", ".zip", "ZIP · 压缩包"),
            new FormatItem(".cbz", ".cbz", "CBZ · 漫画压缩包"),
        }),
    };

    private readonly Dictionary<string, ToggleSwitch> _toggles = new(StringComparer.OrdinalIgnoreCase);
    private string _executablePath = string.Empty;
    private bool _suppressToggleEvents;

    private static FileAssociationWindow? _current;

    /// <summary>打开窗口；已经开着就把它抬到前面，不重复开第二个。</summary>
    public static void Show()
    {
        if (_current is not null)
        {
            _current.BringToFront();
            return;
        }

        _current = new FileAssociationWindow();
    }

    public FileAssociationWindow()
    {
        InitializeComponent();

        Title = "文件关联";
        AppIcon.ApplyToWindow(AppWindow);

        ConfigureWindowChrome();

        _executablePath = Environment.ProcessPath
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "CelesteGallery.exe");

        BuildGroups();
        LoadCurrentSelection();
        RefreshStatus();

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
        };

        BringToFront();
    }

    private void ConfigureWindowChrome()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }

            // 620 刚好容下两列开关行不挤；720 在 1080p 上还留有余量
            WindowPlacement.ResizeAndCenterOnOwner(AppWindow, 620, 720);
        }
        catch (Exception ex)
        {
            StartupLog.Write("FileAssociationWindow: 设置窗口外观失败", ex);
        }
    }

    /// <summary>
    /// 把自己弄到最前。和 AboutWindow 同款问题、同款修法（见那里的注释）：
    /// 程序已经在跑的时候新建窗口，光靠 Activate() 会落在主窗口后面。
    /// </summary>
    private void BringToFront()
    {
        Activate();
        WindowForeground.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));

        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            WindowForeground.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));
        });
    }

    /// <summary>
    /// 按分组铺出开关行：左边是扩展名 + 一句说明，开关靠右。
    /// 点左边文字也能切换 —— 二十多项挨个去够右边的小开关太累。
    /// </summary>
    private void BuildGroups()
    {
        foreach (FormatGroup group in Groups)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
            };

            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF0, 0xFF, 0xFF)),
            });
            section.Children.Add(new TextBlock
            {
                Text = group.Hint,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x96, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 0, 0, 2),
            });

            foreach (FormatItem item in group.Items)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel
                {
                    Spacing = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    // 想点文字也能切换，就得有非空背景，否则命中测试收不到点击
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                };
                text.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE4, 0xFF, 0xFF)),
                });
                text.Children.Add(new TextBlock
                {
                    Text = item.Desc,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8C, 0xFF, 0xFF)),
                    TextWrapping = TextWrapping.Wrap,
                });

                var toggle = new ToggleSwitch
                {
                    Tag = item.Ext,
                    OffContent = "关",
                    OnContent = "开",
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                };
                toggle.Toggled += FormatToggle_Toggled;
                _toggles[item.Ext] = toggle;

                text.Tapped += (_, _) => toggle.IsOn = !toggle.IsOn;

                Grid.SetColumn(toggle, 1);
                row.Children.Add(text);
                row.Children.Add(toggle);
                section.Children.Add(row);
            }

            card.Child = section;
            GroupHost.Children.Add(card);
        }
    }

    /// <summary>回显当前关联状态：关联过的就打开；一次都没关联过给推荐默认值。</summary>
    private void LoadCurrentSelection()
    {
        List<string> associated = FileAssociationHelper.GetAssociatedExtensions();

        _suppressToggleEvents = true;
        try
        {
            if (associated.Count > 0)
            {
                foreach (string ext in associated) SetToggled(ext, true);
            }
            else
            {
                foreach (string ext in FileAssociationHelper.RecommendedExtensions) SetToggled(ext, true);
            }
        }
        finally
        {
            _suppressToggleEvents = false;
        }

        UpdateSummary();
    }

    private void SetToggled(string ext, bool value)
    {
        if (_toggles.TryGetValue(ext, out ToggleSwitch? toggle)) toggle.IsOn = value;
    }

    private void SetAllToggled(bool value, IEnumerable<string>? only = null)
    {
        _suppressToggleEvents = true;
        try
        {
            var target = only?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ToggleSwitch> pair in _toggles)
                pair.Value.IsOn = target == null || target.Contains(pair.Key);
        }
        finally
        {
            _suppressToggleEvents = false;
        }

        UpdateSummary();
    }

    private void FormatToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents) return;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int picked = _toggles.Values.Count(t => t.IsOn);
        SelectionSummaryText.Text = $"已选择 {picked} / {_toggles.Count} 个格式";
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) => SetAllToggled(true);

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e) => SetAllToggled(false);

    private void SelectRecommendedButton_Click(object sender, RoutedEventArgs e)
        => SetAllToggled(false, FileAssociationHelper.RecommendedExtensions);

    /// <summary>顶部状态：本版本能不能做关联 / 关联指到了哪个 exe / 有没有指到用不了的版本。</summary>
    private void RefreshStatus()
    {
        bool selfPackaged = FileAssociationHelper.IsPackagedLayout(_executablePath);
        PickExecutableButton.Visibility = selfPackaged ? Visibility.Visible : Visibility.Collapsed;

        string? registered = FileAssociationHelper.GetRegisteredExecutable();
        bool registeredPackaged = registered != null && FileAssociationHelper.IsPackagedLayout(registered);

        if (registeredPackaged)
        {
            SetStatus("⚠ 当前文件关联指向的程序打不开文件（调试/打包版）：\n" + registered
                      + "\n请点「手动选择程序文件…」换成免安装版或安装后的正式版。", ok: false);
        }
        else if (selfPackaged)
        {
            SetStatus("⚠ 你现在运行的是调试（打包）版本，Windows 不允许它作为文件的默认打开程序，"
                      + "双击图片不会有任何反应。\n请点「手动选择程序文件…」换成 Release（免安装）版或安装后的正式版本。",
                      ok: false);
        }
        else if (!string.IsNullOrEmpty(registered))
        {
            SetStatus("✓ 当前关联指向：\n" + registered, ok: true);
        }
        else
        {
            SetStatus("✓ 当前版本可用于文件关联（尚未关联任何格式）。", ok: true);
        }
    }

    private void SetStatus(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(ok
            ? Windows.UI.Color.FromArgb(255, 76, 175, 80)
            : Windows.UI.Color.FromArgb(255, 220, 120, 80));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> picked = _toggles
            .Where(pair => pair.Value.IsOn)
            .Select(pair => pair.Key)
            .ToList();

        try
        {
            if (picked.Count == 0)
            {
                FileAssociationHelper.Unregister();
                AppliedHintText.Text = "已取消全部文件关联。";
                RefreshStatus();
                return;
            }

            // 1) 先按勾选写注册表（ProgId + 这些扩展名）
            FileAssociationHelper.Register(_executablePath, picked);

            // 2) 再解除"这次没勾"的格式里、默认值指向本程序的那些。
            //    顺序不能反：Unregister() 会把 ProgId 整个删掉，先解除就白注册了。
            List<string> stale = FileAssociationHelper.AllExtensions
                .Where(ext => !picked.Contains(ext, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (stale.Count > 0) FileAssociationHelper.UnregisterExtensions(stale);

            AppliedHintText.Text = $"已应用：关联 {picked.Count} 个格式。";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StartupLog.Write("FileAssociationWindow.Apply", ex);
            AppliedHintText.Text = string.Empty;
            SetStatus("关联失败：" + ex.Message, ok: false);
        }
    }

    private async void UnregisterAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ContentDialog confirm = new()
            {
                Title = "取消全部关联？",
                Content = "将移除 CelesteGallery 对所有图片格式的关联，之后双击图片不会再打开本程序。",
                PrimaryButtonText = "取消关联",
                CloseButtonText = "算了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            FileAssociationHelper.Unregister();
            SetAllToggled(false);
            AppliedHintText.Text = "已取消全部文件关联。";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StartupLog.Write("FileAssociationWindow.UnregisterAll", ex);
            SetStatus("取消失败：" + ex.Message, ok: false);
        }
    }

    /// <summary>让用户挑一个 .exe（把关联指到能用的那个版本上）。</summary>
    private async void PickExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".exe");

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null || string.IsNullOrEmpty(file.Path)) return;

            _executablePath = file.Path;
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StartupLog.Write("FileAssociationWindow.PickExecutable", ex);
            SetStatus("选择失败：" + ex.Message, ok: false);
        }
    }

    private void OpenDefaultAppsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StartupLog.Write("FileAssociationWindow.OpenDefaultApps", ex);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
