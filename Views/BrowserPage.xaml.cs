using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CelesteViewer.Helpers;
using CelesteViewer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

// Windows.Storage 和 System.IO 都有一个叫 FileAttributes 的枚举，
// 不加别名会报"不明确的引用"
using FileAttributes = System.IO.FileAttributes;

namespace CelesteViewer.Views;

/// <summary>
/// 浏览界面（路线图第 3 步）：左边目录树，右边缩略图墙。
///
/// 这个页面唯一真正难的地方是**让大目录不卡**。三件事一起做才有效果：
///
///   1. **格子要真虚拟化** —— 用 ItemsRepeater + UniformGridLayout，
///      而不是 GridView（后者的虚拟化在 WinUI3 上有已知问题）。
///   2. **解码也要虚拟化** —— 格子真正出现在屏幕上时才去解它。
///      光有 1 是不够的：如果一进目录就把五千张全排进解码队列，
///      界面照样卡死。
///   3. **磁盘缓存** —— 第二次打开同一目录直接读文件，不用解码。
///
/// 另外目录树是**懒加载**的：展开哪个才去读哪个，
/// 不然一上来把整个 C 盘递归扫一遍要等半天。
/// </summary>
public sealed partial class BrowserPage : Page
{
    /// <summary>
    /// 缩略图解码边长。刻意做成固定值，不跟"缩略图大小"滑块联动 ——
    /// 联动会导致每次拖动滑块都把缓存全部作废、重新解一遍，
    /// 而显示尺寸和 320px 的图在视觉上几乎没差别。
    /// </summary>
    private const int ThumbDecodeSize = 320;

    private readonly ImageDecodePipeline _decoder = new();
    private readonly ThumbnailService _thumbs;
    private readonly DispatcherQueue _uiQueue;

    /// <summary>
    /// 这是第几次加载。
    ///
    /// 扫目录挪到后台线程之后就有了新问题：用户连着点两个目录，两次扫描会同时跑，
    /// 谁先回来不一定 —— 先发起的那个如果后回来，就会把新目录的图盖掉。
    /// 每次加载记一个号，回来时对不上就直接丢掉。
    /// </summary>
    private int _loadSeq;

    /// <summary>
    /// 目录树的根节点"图库"。它下面挂的是用户收进来的文件夹。
    /// </summary>
    private TreeViewNode? _libraryRoot;

    /// <summary>哪些节点是图库的直接条目（只有这些能右键"从图库中移除"）。</summary>
    private readonly HashSet<TreeViewNode> _libraryNodes = new();

    private ObservableCollection<ThumbnailItem> _items = new();
    private DispatcherQueueTimer? _sizeDebounce;
    private ThumbnailItem? _selected;
    private string? _currentFolder;

    /// <summary>是否处于多选模式（工具栏"选择"按钮或右键"选择多项"进入）。</summary>
    private bool _multiSelect;

    /// <summary>多选模式下被选中的格子集合。</summary>
    private readonly HashSet<ThumbnailItem> _selectedSet = new();

    /// <summary>缩略图布局：方形（等高宽方块）/ 等高（定高变宽）。</summary>
    private enum ThumbLayout { Square, UniformHeight }

    private ThumbLayout _layout = ThumbLayout.Square;

    /// <summary>方形布局（XAML 里那套 UniformGridLayout），缓存引用方便来回切。</summary>
    private UniformGridLayout? _squareLayout;

    /// <summary>等高布局（自定义 UniformHeightLayout）。</summary>
    private readonly UniformHeightLayout _uniformLayout = new();

    /// <summary>用户双击某张图（或回车）→ 请求窗口切到单图页。</summary>
    public event Action<string>? OpenRequested;

    public BrowserPage()
    {
        // 必须在 InitializeComponent 之前抓好：
        // XAML 解析时 Slider 的 Value 就会被赋值，ValueChanged 立刻触发，
        // 那时 _uiQueue 还是空的就会炸。
        _uiQueue = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();

        _thumbs = new ThumbnailService(_decoder, diskCache: DiskThumbnailCache.Shared);

        // 标题栏最左边那张小图（和 exe 图标同一套图案）。
        // 读文件是异步的，读完自己填上去；读不到就算了，标题栏少个图标不影响用。
        _ = LoadTitleIconAsync();

        Thumbs.ItemsSource = _items;

        // 缓存方形布局引用，给等高布局喂宽高比（等高模式算位置只读它，不建 UI 元素）
        _squareLayout = Thumbs.Layout as UniformGridLayout;
        _uniformLayout.AspectOf = i =>
            (i >= 0 && i < _items.Count) ? _items[i].Aspect : 1.0;
        _uniformLayout.RowHeight = SizeSlider.Value;

        IsTabStop = true;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;

        Root.AllowDrop = true;
        Root.DragOver += Root_DragOver;
        Root.Drop += Root_Drop;
    }

    /// <summary>顶部那条交给窗口当标题栏（窗口能拖，右上角仍由系统画按钮）。</summary>
    public UIElement TitleBarElement => TitleBar;

    /// <summary>把应用图标填到标题栏最左边（读不到就留空，不报错也不重试）。</summary>
    private async Task LoadTitleIconAsync()
    {
        var icon = await AppIcon.LoadPngAsync();
        if (icon is not null) TitleIcon.Source = icon;
    }

    // ===== 启动 =====

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupLog.Write("BrowserPage: OnLoaded");

        App.Instance?.SetCustomTitleBar(TitleBar);
        BuildLibraryTree();
        Focus(FocusState.Programmatic);

        // 恢复上次用的缩略图大小。放在这里而不是构造函数里，
        // 是因为构造函数阶段 XAML 还没解析完，控件都还是 null。
        try
        {
            int tile = AppSettings.GetInt("TileSize", (int)DefaultTile);
            SizeSlider.Value = Math.Clamp(tile, (int)SizeSlider.Minimum, (int)SizeSlider.Maximum);

            // 开关也要在载入目录**之前**设好：
            // 否则第一屏会先按默认值扫一遍，再按开关值重扫一遍
            _restoringSettings = true;
            IncludeSubSwitch.IsOn = AppSettings.GetBool("IncludeSubfolders", false);
            _restoringSettings = false;
        }
        catch { }

        // 直接带着图片文件启动的话（双击图片），窗口会切到单图页，
        // 这边就不用再白读一次目录了
        if (App.StartFilePath is null)
        {
            string? start = ResolveStartFolder();
            if (start is not null)
            {
                _ = LoadFolderAsync(start);
            }
            else
            {
                ShowEmpty("从左边选一个文件夹");
            }
        }

        StartupLog.Write("BrowserPage: 就绪");
    }

    private const double DefaultTile = 180;

    /// <summary>
    /// 决定启动时打开哪个目录，优先级：
    ///   1. 上次打开过的（用户最可能还想看那个）
    ///   2. "图片"文件夹往下找到的第一个真有图的目录
    ///
    /// 第 2 条是必要的：Windows 的"图片"文件夹顶层通常一张图都没有，
    /// 照片都在子目录里，直接打开顶层会是一片空白。
    /// </summary>
    private static string? ResolveStartFolder()
    {
        string? last = AppSettings.Get("LastFolder");
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
        {
            StartupLog.Write($"BrowserPage: 沿用上次的目录 {last}");
            return last;
        }

        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string? found = FolderIndex.FindFolderWithImages(pictures);

        if (found is not null) StartupLog.Write($"BrowserPage: 自动定位到 {found}");
        else StartupLog.Write("BrowserPage: 没找到含图片的目录");

        return found;
    }

    // ===== 图库（左侧目录树） =====

    /// <summary>
    /// 重建左侧树。
    ///
    /// 结构：根节点是"图库"，下面挂用户收进来的文件夹；每个文件夹再展开就是它的子目录。
    ///
    /// 收进来的目录记在 %LOCALAPPDATA%\CelesteViewer\library.txt，下次启动还在。
    /// 以前这里写死"桌面/图片/下载"三个根 —— 那样放别处的照片只能靠工具条上的按钮，
    /// 而且访问过的目录不会被记住，用户反过来问"为什么打开过的不在图库里"。
    /// </summary>
    private void BuildLibraryTree()
    {
        FolderTree.RootNodes.Clear();
        _libraryNodes.Clear();

        _libraryRoot = new TreeViewNode
        {
            Content = new FolderNode { Path = "", Label = "图库" },
        };
        FolderTree.RootNodes.Add(_libraryRoot);

        var folders = LibraryStore.Load();
        int shown = 0;

        foreach (string folder in folders)
        {
            // 移动硬盘没插、目录被删掉的，这一轮先不显示。
            // 注意**不删记录** —— 盘插回来它还在，比"悄悄消失"友好得多。
            if (!Directory.Exists(folder))
            {
                StartupLog.Write($"BrowserPage: 图库条目暂时不可用 → {folder}");
                continue;
            }

            var node = MakeNode(folder, label: LibraryLabel(folder, folders), inLibrary: true);
            _libraryNodes.Add(node);
            _libraryRoot.Children.Add(node);
            shown++;
        }

        _libraryRoot.IsExpanded = true;

        StartupLog.Write($"BrowserPage: 图库 {shown} 个文件夹（记录 {folders.Count} 条）");

        // 重建之后把选中态挪回"当前正在看的目录"（没在看的就选第一个），
        // 否则重建会把选中高亮弄丢
        SelectTreeNodeForCurrentFolder();
    }

    private void SelectTreeNodeForCurrentFolder()
    {
        TreeViewNode? fallback = null;

        foreach (var node in _libraryNodes)
        {
            fallback ??= node;

            if (_currentFolder is not null
                && node.Content is FolderNode info
                && PathEquals(info.Path, _currentFolder))
            {
                FolderTree.SelectedNode = node;
                return;
            }
        }

        if (fallback is not null) FolderTree.SelectedNode = fallback;
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    private TreeViewNode MakeNode(string path, string label, bool inLibrary = false)
        => new()
        {
            Content = new FolderNode { Path = path, Label = label, InLibrary = inLibrary },
            HasUnrealizedChildren = HasSubdirectories(path),
        };

    /// <summary>
    /// 两个不同目录同名时（比如两个"照片"），只显示名字根本分不清，
    /// 这种情况退化成显示完整路径。
    /// </summary>
    private static string LibraryLabel(string folder, List<string> all)
    {
        string leaf = DisplayName(folder);

        foreach (string other in all)
        {
            if (PathEquals(other, folder)) continue;
            if (string.Equals(DisplayName(other), leaf, StringComparison.OrdinalIgnoreCase))
                return folder;
        }

        return leaf;
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        // 已经填过就不重复填（用户反复折叠/展开时）
        if (args.Node.Children.Count > 0) return;

        FillChildren(args.Node);
    }

    /// <summary>把一个节点下面的子目录填进去（懒加载：只在展开时才读）。</summary>
    private void FillChildren(TreeViewNode parent)
    {
        if (parent.Content is not FolderNode info || info.IsRoot) return;

        try
        {
            var dirs = new List<string>();
            foreach (string d in Directory.EnumerateDirectories(info.Path))
            {
                try
                {
                    var attr = File.GetAttributes(d);
                    // 隐藏和系统目录（比如 $RECYCLE.BIN）不显示，免得噪音
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                        continue;
                    dirs.Add(d);
                }
                catch { }
            }

            dirs.Sort((a, b) => FolderIndex.CompareNatural(Path.GetFileName(a), Path.GetFileName(b)));

            parent.Children.Clear();
            foreach (string d in dirs)
                parent.Children.Add(MakeNode(d, Path.GetFileName(d)));
        }
        catch (Exception ex)
        {
            // 权限不足的目录很常见（系统目录、别的用户的目录），不该打扰用户
            StartupLog.Write($"BrowserPage: 展开目录失败 {info.Path}", ex);
        }
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // "图库"那一行是分组标题，点了不加载任何目录
        var node = args.InvokedItem as TreeViewNode ?? FolderTree.SelectedNode;
        if (node?.Content is not FolderNode info || info.IsRoot) return;

        _ = LoadFolderAsync(info.Path);
    }

    // ===== 目录树的右键菜单 =====

    /// <summary>右键点在某个目录条目上。</summary>
    private void FolderNode_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not TreeViewNode node) return;

        // 右键顺手选中它 —— 否则菜单里的"打开"指的是哪一个得靠猜
        FolderTree.SelectedNode = node;

        ShowMenu(element, args, BuildFolderMenu(node));
        args.Handled = true;
    }

    /// <summary>右键点在这一栏的空白处（没落在任何条目上）。</summary>
    private void FolderTree_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        ShowMenu(FolderTree, args, BuildFolderMenu(null));
        args.Handled = true;
    }

    private MenuFlyout BuildFolderMenu(TreeViewNode? node)
    {
        var flyout = new MenuFlyout();
        var info = node?.Content as FolderNode;

        // 空白处 / "图库"分组标题：给的是图库级别的操作
        if (info is null || info.IsRoot)
        {
            flyout.Items.Add(MakeMenuItem("添加文件夹到图库…", "\uE8E5", () => _ = PickFolderAndAddAsync()));
            flyout.Items.Add(MakeMenuItem("刷新图库", "\uE72C", BuildLibraryTree));
            return flyout;
        }

        string path = info.Path;

        flyout.Items.Add(MakeMenuItem("打开", "\uE8B7", () => _ = LoadFolderAsync(path)));
        flyout.Items.Add(MakeMenuItem("在资源管理器中打开", "\uE838", () => ExplorerHelper.OpenFolder(path)));
        flyout.Items.Add(MakeMenuItem("复制路径", "\uE8C8", () => SetClipboardText(path)));
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (info.InLibrary)
            flyout.Items.Add(MakeMenuItem("从图库中移除", "\uE74D", () => RemoveFromLibrary(path)));
        else
            flyout.Items.Add(MakeMenuItem("加入图库", "\uE710", () => AddToLibrary(path)));

        flyout.Items.Add(MakeMenuItem("重新读取子目录", "\uE72C", () => RefreshNode(node!)));

        return flyout;
    }

    private void AddToLibrary(string path)
    {
        LibraryStore.Add(path);
        StartupLog.Write($"BrowserPage: 加入图库 → {path}");
        BuildLibraryTree();
    }

    private void RemoveFromLibrary(string path)
    {
        LibraryStore.Remove(path);
        StartupLog.Write($"BrowserPage: 从图库移除 → {path}");
        BuildLibraryTree();
    }

    /// <summary>重新读一个节点的子目录（用户手动按的，所以直接展开着填）。</summary>
    private void RefreshNode(TreeViewNode node)
    {
        if (node.Content is not FolderNode info || info.IsRoot) return;

        node.Children.Clear();
        FillChildren(node);
        node.HasUnrealizedChildren = node.Children.Count > 0;
        node.IsExpanded = true;

        // 用户正在看这个目录的话，内容也一起刷新
        if (_currentFolder is not null && PathEquals(info.Path, _currentFolder))
            _ = LoadFolderAsync(info.Path);
    }

    // ===== 缩略图墙的右键菜单 =====

    private void Thumb_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;

        var item = ItemOf(sender);
        if (item is null) return;

        SelectItem(item);   // 右键顺手选中

        var flyout = new MenuFlyout();
        flyout.Items.Add(MakeMenuItem("打开", "\uE8B7", () => OpenRequested?.Invoke(item.Path)));
        flyout.Items.Add(MakeMenuItem("在资源管理器中显示", "\uE838",
            () => ExplorerHelper.RevealFile(item.Path)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MakeMenuItem("选择多项", "\uE73A", () => EnterMultiSelectFrom(item)));
        flyout.Items.Add(MakeMenuItem("属性", "\uE946", () => ShowProperties(item)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MakeMenuItem("复制图片", "\uE8C8", () => _ = CopyImageToClipboardAsync(item.Path)));
        flyout.Items.Add(MakeMenuItem("复制文件路径", "\uE8C8", () => SetClipboardText(item.Path)));
        flyout.Items.Add(MakeMenuItem("复制文件名", "\uE8C8", () => SetClipboardText(item.FileName)));

        ShowMenu(element, args, flyout);
        args.Handled = true;
    }

    /// <summary>右键菜单"选择多项"：进入多选模式，并把右键的这一张先选进集合。</summary>
    private void EnterMultiSelectFrom(ThumbnailItem item)
    {
        EnterMultiSelect();
        _selectedSet.Add(item);
        item.MultiSelected = true;
        UpdateSelCount();
    }

    /// <summary>右键菜单"属性"：弹出一个居中的属性窗口（见 PropertiesWindow）。</summary>
    private void ShowProperties(ThumbnailItem item) => PropertiesWindow.Show(item.Path);

    /// <summary>右键点在墙的空白处（没落在某张图上）。</summary>
    private void Wall_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(MakeMenuItem("刷新", "\uE72C", Refresh));

        if (_currentFolder is not null)
        {
            string folder = _currentFolder;

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(MakeMenuItem("在资源管理器中打开当前文件夹", "\uE838",
                () => ExplorerHelper.OpenFolder(folder)));
            flyout.Items.Add(MakeMenuItem("复制当前文件夹路径", "\uE8C8",
                () => SetClipboardText(folder)));

            bool inLibrary = LibraryStore.Load().Exists(p => PathEquals(p, folder));
            if (!inLibrary)
                flyout.Items.Add(MakeMenuItem("把当前文件夹加入图库", "\uE710", () => AddToLibrary(folder)));
        }

        ShowMenu(GridScroller, args, flyout);
        args.Handled = true;
    }

    private void Refresh()
    {
        if (_currentFolder is not null) _ = LoadFolderAsync(_currentFolder);
    }

    private static MenuFlyoutItem MakeMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static void ShowMenu(
        FrameworkElement target, ContextRequestedEventArgs args, MenuFlyout flyout)
    {
        // 尽量在鼠标点上弹出（键盘调出来的菜单没有坐标，就退回默认位置）
        if (args.TryGetPosition(target, out Windows.Foundation.Point position))
            flyout.ShowAt(target, new FlyoutShowOptions { Position = position });
        else
            flyout.ShowAt(target);
    }

    private static void SetClipboardText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            StartupLog.Write("BrowserPage: 复制到剪贴板失败", ex);
        }
    }

    private static async Task CopyImageToClipboardAsync(string path)
    {
        try
        {
            // 和 WicImageDecoder 里一样，这里也刻意避开 StorageFile ——
            // 它是那套会在启动期把进程直接搞崩的 WinRT 文件代理。
            // 复制图片本来就是用户手动触发的低频操作，读进内存流最省事也最稳。
            byte[] bytes = await File.ReadAllBytesAsync(path);

            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            StartupLog.Write($"BrowserPage: 复制图片失败 {path}", ex);
        }
    }

    /// <summary>
    /// 这个目录下有没有子目录。只探测第一个就返回，不整个枚举完 ——
    /// 放着几千个子目录的目录（比如 node_modules）不能卡住界面。
    /// </summary>
    private static bool HasSubdirectories(string path)
    {
        try
        {
            foreach (string d in Directory.EnumerateDirectories(path))
            {
                try
                {
                    var attr = File.GetAttributes(d);
                    if (!attr.HasFlag(FileAttributes.Hidden) && !attr.HasFlag(FileAttributes.System))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    // ===== 缩略图墙 =====

    private async Task LoadFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        int seq = ++_loadSeq;

        _currentFolder = folder;
        _selected = null;

        bool includeSub = IncludeSubSwitch.IsOn;

        PathText.Text = folder;
        TitleText.Text = DisplayName(folder);
        TitleDot.Visibility = Visibility.Visible;

        // 先把墙清空。扫盘要花时间，留着上一个目录的图会让人以为点错了
        _items = new ObservableCollection<ThumbnailItem>();
        Thumbs.ItemsSource = _items;
        ShowEmpty("正在读取…");

        // 记住这个目录，下次启动直接回到这里
        AppSettings.Set("LastFolder", folder);

        List<(string Path, string? Sub)> files;
        bool truncated;

        try
        {
            // 扫盘必须放后台线程。只扫一层还看不出来，但开了"包含子文件夹"
            // 又点到层级很深的大目录时，在 UI 线程上同步扫会直接把窗口冻住
            (files, truncated) = await Task.Run(() => Scan(folder, includeSub));
        }
        catch (Exception ex)
        {
            StartupLog.Write($"BrowserPage: 读取目录失败 {folder}", ex);
            if (seq == _loadSeq) ShowEmpty("这个文件夹读不了（可能没有权限）");
            return;
        }

        // 期间用户又点了别的目录，这批结果已经过期，直接丢掉
        if (seq != _loadSeq) return;

        StartupLog.Write($"BrowserPage: 载入目录 {folder}（含子文件夹={includeSub}），找到 {files.Count} 张");

        if (files.Count == 0)
        {
            ShowEmpty(await DescribeEmptyAsync(folder, includeSub));
            return;
        }

        var list = new List<ThumbnailItem>(files.Count);
        foreach (var (path, sub) in files)
        {
            list.Add(new ThumbnailItem
            {
                Path = path,
                FileName = Path.GetFileName(path),
                SubFolder = sub ?? "",
            });
        }

        // 换一个全新的集合而不是逐条清空：ItemsRepeater 会一次性重建，
        // 不需要为每一张都发一次变更通知
        _items = new ObservableCollection<ThumbnailItem>(list);
        Thumbs.ItemsSource = _items;

        EmptyState.Visibility = Visibility.Collapsed;
        GridScroller.ChangeView(null, 0, null, true);

        string scope = includeSub ? "（含子文件夹）" : "";
        StatusText.Text = truncated
            ? $"共 {files.Count} 张{scope}　·　太多了，只铺出前 {files.Count} 张"
            : $"共 {files.Count} 张{scope}　·　单击选中，双击打开";
        UpdateCacheText();
    }

    /// <summary>
    /// 真正扫盘的那一段（跑在后台线程上）。
    /// 顺便把"这张图在哪个子目录"算好 —— 开了递归之后，
    /// 墙上得标出来它来自哪一层，不然用户不知道图是从哪儿冒出来的。
    /// </summary>
    private static (List<(string Path, string? Sub)> Files, bool Truncated) Scan(
        string folder, bool includeSub)
    {
        var index = new FolderIndex();

        // includeArchives：把 zip / cbz 也铺到墙上（缩略图用包里第一张当封面），
        // 双击它就直接进单图页在包里翻。单图页自己翻页时不会收压缩包，见那边。
        index.LoadFolder(folder, includeSub,
                         FolderIndex.DefaultMaxDepth, FolderIndex.DefaultMaxFiles,
                         includeArchives: true);

        var result = new List<(string, string?)>(index.Count);

        foreach (string path in index.All)
        {
            string? sub = null;

            if (includeSub)
            {
                try
                {
                    // 相对当前目录的路径，减掉文件名就是它所在的子目录；
                    // 就在当前目录下的图，相对路径里没有分隔符，sub 保持空
                    string relative = Path.GetRelativePath(folder, path);
                    string? dir = Path.GetDirectoryName(relative);
                    if (!string.IsNullOrEmpty(dir)) sub = dir;
                }
                catch { }
            }

            result.Add((path, sub));
        }

        return (result, index.Truncated);
    }

    /// <summary>
    /// 空目录该说什么，分三种情况。
    ///
    /// 最要紧的是第二种：目录自己一张图都没有、图全在子目录里 ——
    /// 这是照片按 2026\09 分层存放时的常态，干巴巴一句"没有图片"会让用户
    /// 以为文件丢了。所以这里先探一下，真探到了就直接告诉他去勾左下角的开关。
    /// </summary>
    private static async Task<string> DescribeEmptyAsync(string folder, bool includeSub)
    {
        if (includeSub) return "这个文件夹和它下面的子文件夹里都没有能打开的图";

        string? deeper = await Task.Run(() => FolderIndex.FindFolderWithImages(folder, 3));

        return deeper is null
            ? "这个文件夹里没有能打开的图"
            : "这个文件夹自己没有图片，图都在子文件夹里 —— 打开左上角的「包含子文件夹」就能一起看到";
    }

    private static string DisplayName(string folder)
    {
        string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    // ===== 目录树的开关与入口 =====

    /// <summary>
    /// 恢复设置时为 true。
    /// ToggleSwitch 的 Toggled 在**代码赋值**时也会触发，
    /// 不加这个闸门的话，一进界面就会因为"把开关拨到位"而白扫一遍目录。
    /// </summary>
    private bool _restoringSettings;

    /// <summary>顶部"包含子文件夹"开关。切换后立刻按新范围重扫当前目录。</summary>
    private void IncludeSubSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        bool on = IncludeSubSwitch.IsOn;
        AppSettings.Set("IncludeSubfolders", on);

        StartupLog.Write($"BrowserPage: 包含子文件夹 = {on}");

        if (_restoringSettings) return;
        if (_currentFolder is not null) _ = LoadFolderAsync(_currentFolder);
    }

    /// <summary>左下角"关于"：弹一个小窗口，点别处或按"确定"就消失。</summary>
    private void AboutButton_Click(object sender, RoutedEventArgs e)
        => AboutWindow.Show();

    /// <summary>
    /// "文件关联"：把图片格式的"双击打开"交给本程序。
    /// 做法和 CelesteMusicPlayer 那个一致 —— 弹一个按类别勾选格式的窗口，
    /// 用户勾完点「应用」才真正写注册表。
    /// </summary>
    private void FileAssocButton_Click(object sender, RoutedEventArgs e)
        => FileAssociationWindow.Show();

    /// <summary>
    /// "打开文件夹"：给放在别处（比如 D 盘）的照片留的入口。
    /// 选完的目录会**记进图库**，下次直接从左侧点，不用再翻一遍。
    /// </summary>
    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        => await PickFolderAndAddAsync();

    /// <summary>左栏标题右边那个"+"，和图条上的"打开文件夹"是同一件事。</summary>
    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        => await PickFolderAndAddAsync();

    private async Task PickFolderAndAddAsync()
    {
        string? folder = await PickFolderAsync();
        if (folder is null) return;   // 用户取消了

        LibraryStore.Add(folder);
        BuildLibraryTree();
        await LoadFolderAsync(folder);
    }

    /// <summary>弹系统文件夹选择框。用户取消时返回 null。</summary>
    private static async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

            // 非打包模式下必须给个类型过滤，不然 PickSingleFolderAsync 直接抛异常
            picker.FileTypeFilter.Add("*");

            // WinUI3 里 picker 必须绑到窗口句柄，否则一调用就抛异常
            if (App.Instance is not null)
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Instance);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            // 用户取消是正常操作，但真出别的错也要能在黑匣子里查到
            StartupLog.Write("BrowserPage: 选择文件夹失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 格子真正出现在屏幕上时才去解它 —— "解码虚拟化"的落地点。
    /// 一万张的目录，这里也只会被调用到看得见的那几十个。
    /// </summary>
    private void Thumbs_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _items.Count) return;

        var item = _items[args.Index];
        if (item.LoadRequested) return;

        item.LoadRequested = true;
        _ = LoadThumbAsync(item);
    }

    private async Task LoadThumbAsync(ThumbnailItem item)
    {
        try
        {
            var bitmap = await _thumbs.GetAsync(item.Path, ThumbDecodeSize, CancellationToken.None);
            if (bitmap is null)
            {
                item.MarkFailed();
                return;
            }

            var source = await BitmapHelper.ToSourceAsync(bitmap);
            if (source is null)
            {
                item.MarkFailed();
                return;
            }

            item.Thumbnail = source;
        }
        catch
        {
            item.MarkFailed();
        }
    }

    private void SizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 拖动时每秒会触发几十次，直接重建布局会卡；等手停下来再做
        if (_sizeDebounce is null)
        {
            _sizeDebounce = _uiQueue.CreateTimer();
            _sizeDebounce.Interval = TimeSpan.FromMilliseconds(120);
            _sizeDebounce.IsRepeating = false;
            _sizeDebounce.Tick += (_, _) => ApplyTileSize();
        }

        _sizeDebounce.Stop();
        _sizeDebounce.Start();
    }

    private void ApplyTileSize()
    {
        try
        {
            double size = SizeSlider.Value;

            if (Thumbs.Layout is UniformGridLayout layout)
            {
                layout.MinItemWidth = size;
                layout.MinItemHeight = size;
            }

            Thumbs.InvalidateMeasure();

            AppSettings.Set("TileSize", (int)Math.Round(size));
        }
        catch (Exception ex)
        {
            StartupLog.Write("BrowserPage: 调整缩略图尺寸失败", ex);
        }
    }

    private void UpdateCacheText()
    {
        var cache = _thumbs.DiskCache;
        if (cache is null) return;

        long bytes = cache.CachedBytes;
        double mb = bytes / 1024.0 / 1024.0;

        CacheText.Text = bytes <= 0
            ? ""
            : $"缩略图缓存 {mb:F0} MB　命中 {cache.Hits}";
    }

    // ===== 交互 =====

    private ThumbnailItem? ItemOf(object sender)
    {
        if (sender is not UIElement element) return null;

        int index = Thumbs.GetElementIndex(element);
        return index >= 0 && index < _items.Count ? _items[index] : null;
    }

    private void Thumb_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item is null) return;

        // 多选模式下，点一下 = 在集合里加 / 撤这张
        if (_multiSelect)
        {
            if (_selectedSet.Contains(item))
            {
                _selectedSet.Remove(item);
                item.MultiSelected = false;
            }
            else
            {
                _selectedSet.Add(item);
                item.MultiSelected = true;
            }

            UpdateSelCount();
            return;
        }

        SelectItem(item);
    }

    /// <summary>选中一张图（单击、右键、键盘都走这里，省得三处各写一遍）。</summary>
    private void SelectItem(ThumbnailItem item)
    {
        if (_selected is not null && !ReferenceEquals(_selected, item)) _selected.Selected = false;
        _selected = item;
        item.Selected = true;

        int index = _items.IndexOf(item);
        StatusText.Text = index >= 0
            ? $"{item.FileName}　·　第 {index + 1} / {_items.Count} 张"
            : item.FileName;
    }

    private void Thumb_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item is null) return;

        StartupLog.Write($"BrowserPage: 双击打开 {item.FileName}");
        OpenRequested?.Invoke(item.Path);
    }

    // ===== 工具栏：选择 / 幻灯片 / 展示方式 =====

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_multiSelect) ExitMultiSelect();
        else EnterMultiSelect();
    }

    /// <summary>进入多选模式：亮出命令条，收掉普通单选高亮。</summary>
    private void EnterMultiSelect()
    {
        _multiSelect = true;
        _selectedSet.Clear();
        if (_selected is not null) { _selected.Selected = false; _selected = null; }

        MultiSelectBar.Visibility = Visibility.Visible;
        UpdateSelCount();
        StartupLog.Write("BrowserPage: 进入多选模式");
    }

    /// <summary>退出多选模式：清掉所有勾选，藏起命令条。</summary>
    private void ExitMultiSelect()
    {
        _multiSelect = false;
        foreach (var it in _selectedSet) it.MultiSelected = false;
        _selectedSet.Clear();

        MultiSelectBar.Visibility = Visibility.Collapsed;
        UpdateSelCount();
        StartupLog.Write("BrowserPage: 退出多选模式");
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _items) { it.MultiSelected = true; _selectedSet.Add(it); }
        UpdateSelCount();
    }

    private void ClearSelButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _selectedSet) it.MultiSelected = false;
        _selectedSet.Clear();
        UpdateSelCount();
    }

    private void ExitMultiButton_Click(object sender, RoutedEventArgs e) => ExitMultiSelect();

    private void UpdateSelCount()
    {
        if (SelCountText is not null) SelCountText.Text = $"已选 {_selectedSet.Count} 张";
    }

    /// <summary>幻灯片放映：用当前目录（优先选中那张 / 多选的第一张）开幻灯。</summary>
    private void SlideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        int start = _selected is not null
            ? _items.IndexOf(_selected)
            : (_selectedSet.Count > 0 ? _items.IndexOf(_selectedSet.First()) : 0);
        if (start < 0) start = 0;

        PhotoWindow.Show(_items[start].Path);
        PhotoWindow.StartSlideshow();
    }

    // ===== 展示方式：布局（方形 / 等高）+ 大小（小 / 中 / 大）=====

    private void LayoutSquareToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (LayoutUniformToggle is not null) LayoutUniformToggle.IsChecked = false;
        SetLayout(ThumbLayout.Square);
    }

    private void LayoutSquareToggle_Unchecked(object sender, RoutedEventArgs e) { }

    private void LayoutUniformToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (LayoutSquareToggle is not null) LayoutSquareToggle.IsChecked = false;
        SetLayout(ThumbLayout.UniformHeight);
    }

    private void LayoutUniformToggle_Unchecked(object sender, RoutedEventArgs e) { }

    private void SetLayout(ThumbLayout mode)
    {
        // XAML 里 ToggleButton 写了 IsChecked="True"，解析到那一行就会当场触发 Checked。
        // 而 Thumbs / _squareLayout 是在 InitializeComponent() 之后才赋值的，
        // 那时还是 null，直接往下走就是把 null 塞给 Thumbs.Layout ——
        // 报出来却是 "Failed to assign to property 'ToggleButton.IsChecked'"（位置在 XAML），
        // 真凶其实在这里。所以解析期一律跳过，等构造完用户真的点了才生效。
        if (Thumbs is null || _squareLayout is null) return;

        if (_layout == mode) return;
        _layout = mode;
        Thumbs.Layout = mode == ThumbLayout.Square ? _squareLayout! : _uniformLayout;
        Thumbs.InvalidateMeasure();
        StartupLog.Write($"BrowserPage: 布局 = {mode}");
    }

    private void SizePresetToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string tag) return;

        // 三个互斥：勾上这个就把另外两个取消
        foreach (var t in new[] { SizeSmallToggle, SizeMediumToggle, SizeLargeToggle })
            if (t is not null && !ReferenceEquals(t, tb)) t.IsChecked = false;

        // 同上：解析期 SizeSlider 还没建出来，这里必须挡一下
        if (SizeSlider is null) return;
        if (double.TryParse(tag, out double v)) SizeSlider.Value = v;
    }

    private void SizePresetToggle_Unchecked(object sender, RoutedEventArgs e) { }

    /// <summary>
    /// 展开子目录时，新冒出来的节点淡入 + 从上往下轻微滑入（比"啪"地一下硬弹出好看得多）。
    ///
    /// 为什么在代码里做，而不是 XAML 里写 &lt;TreeView.ItemContainerTransitions&gt;：
    /// TreeView 根本没有这个属性（那是 ListViewBase 的），XamlCompiler 碰到它
    /// 会内部 null 引用直接崩（WMC9999），还连带把 BrowserPage 等一堆类型
    /// 报成 "Unknown type" —— 报错位置在 XAML，真凶却是这个不存在的属性。
    /// 挂在 ItemTemplate 根元素的 Loaded 上：TreeView 并没有 ContainerContentChanging
    /// （试过，报 CS1061 —— 那同样是 ListViewBase 的），拿不到容器创建时机；
    /// 而节点模板每次被建出来都会 Loaded 一次，展开时新冒出来的子节点正好走到这里。
    /// </summary>
    private void FolderNode_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        const int ms = 240;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();

        // 淡入
        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, fe);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        // 轻微下滑（RenderTransform 换成 TranslateTransform 才能动 Y）
        fe.RenderTransform = new TranslateTransform();
        var slide = new DoubleAnimation
        {
            From = -10, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(slide, fe);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        sb.Children.Add(slide);

        sb.Begin();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        int index = _selected is null ? -1 : _items.IndexOf(_selected);
        int stride = ColumnCount();

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                Select(Math.Max(0, index - 1));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Right:
                Select(Math.Min(_items.Count - 1, index + 1));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                Select(index < 0 ? 0 : Math.Max(0, index - stride));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                Select(index < 0 ? 0 : Math.Min(_items.Count - 1, index + stride));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Home:
                Select(0);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.End:
                Select(_items.Count - 1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
            case Windows.System.VirtualKey.Space:
                if (_selected is not null) OpenRequested?.Invoke(_selected.Path);
                e.Handled = true;
                break;
        }
    }

    private int ColumnCount()
    {
        double tile = SizeSlider.Value + 6;
        double width = GridScroller.ViewportWidth;
        if (width <= 0 || tile <= 0) return 1;
        return Math.Max(1, (int)(width / tile));
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        if (_selected is not null) _selected.Selected = false;
        _selected = _items[index];
        _selected.Selected = true;

        StatusText.Text = $"{_selected.FileName}　·　第 {index + 1} / {_items.Count} 张";

        // 把选中项滚进视野。按"行"估算位置就够了 ——
        // 拿精确坐标得等布局算完，反而会慢半拍
        int columns = ColumnCount();
        double row = index / columns;
        double offset = row * (SizeSlider.Value + 6);
        if (offset < GridScroller.VerticalOffset
            || offset > GridScroller.VerticalOffset + GridScroller.ViewportHeight - SizeSlider.Value)
        {
            GridScroller.ChangeView(null, Math.Max(0, offset - 8), null, false);
        }
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        StatusText.Text = "";
        CacheText.Text = "";
    }

    // ===== 拖放 =====

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "用 CelesteViewer 打开";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count == 0) return;

            // 拖文件夹 → 在这里浏览；拖图片 → 直接进单图页
            if (items[0] is StorageFolder folder)
            {
                _ = LoadFolderAsync(folder.Path);
            }
            else if (items[0] is StorageFile file)
            {
                OpenRequested?.Invoke(file.Path);
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write("BrowserPage: 拖放处理失败", ex);
        }
    }
}
