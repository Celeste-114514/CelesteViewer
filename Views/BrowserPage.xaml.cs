using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CelesteGallery.Helpers;
using CelesteGallery.Services;
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
using WinRT;
using WinRT.Interop;

// Windows.Storage 和 System.IO 都有一个叫 FileAttributes 的枚举，
// 不加别名会报"不明确的引用"
using FileAttributes = System.IO.FileAttributes;

namespace CelesteGallery.Views;

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

    // ===== 分类与搜索 =====

    /// <summary>
    /// 左侧树当前按什么分。默认"按文件夹" —— 也就是**以前唯一的那一种**，
    /// 而且按文件夹时走的还是原来那条直接读磁盘的路，
    /// 所以用户不主动换分类，行为和加这个功能之前一模一样。
    /// </summary>
    private GroupBy _groupBy = GroupBy.Folder;

    /// <summary>
    /// 当前选中的分组值（"2026-09" / "Canon EOS R6"）。null = 没选 = 全部。
    ///
    /// 注意"没选"和"空串"是两回事：空串代表"无日期""未知相机"那一组，
    /// 点它应该筛出那批图，而不是什么都不筛。
    /// </summary>
    private string? _groupValue;

    private string _searchText = string.Empty;
    private DispatcherQueueTimer? _searchDebounce;

    /// <summary>
    /// 正在生效的标签筛选。null = 没在按标签筛。
    ///
    /// 它和 _searchText 是两条独立的筛选线：搜索词去比文件名/相机，
    /// 标签去比 Tags 表，两者可以叠加（搜"Canon"再看打了"旅行"标签的）。
    /// 入口在搜索框的建议列表里 —— 敲字时图库里已有的标签会冒出来，点一个就筛。
    /// </summary>
    private string? _tagFilter;

    /// <summary>
    /// 用户手动选的排序方式。null = "默认（跟随分类）"。
    ///
    /// 为什么默认值不是一个具体的 SortKey、而是 null：
    /// 按文件夹时"文件名升序"最自然（和资源管理器一致），
    /// 按日期/相机时"拍摄时间倒序"最自然（刚拍的在最前）。
    /// 把它记成"跟随分类"，换分类时顺序也跟着换，不用用户自己再调一次。
    /// </summary>
    private SortKey? _sortOverride;

    /// <summary>当前真正生效的排序。没手动选过就跟着分类走。</summary>
    private SortKey EffectiveSort()
        => _sortOverride ?? (_groupBy == GroupBy.Folder
            ? SortKey.FileNameAsc
            : SortKey.DateTakenDesc);

    /// <summary>
    /// 是不是走在"查索引"这条路上。
    ///
    /// 只有按日期/相机/镜头分，或者搜索框里有字时才为 true；
    /// 按文件夹空搜索时照旧直接读磁盘 —— 那条路不用等索引、不会被索引影响。
    /// </summary>
    private bool IndexMode => _groupBy != GroupBy.Folder
                              || !string.IsNullOrWhiteSpace(_searchText)
                              || _favOnly   // 收藏只存在于索引库里，开了它就必须走查库这条路
                              || _tagFilter is not null;   // 标签同理

    /// <summary>"只看收藏"开关。开着时永远走索引那条路（磁盘直读没有收藏的概念）。</summary>
    private bool _favOnly;

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

        // 订阅"编辑参数变了"。看图窗口在那边改了图，靠这条线通知回来刷新格子。
        // 事件是静态的，所以必须成对地挂钩 / 摘钩（见 Unloaded）——
        // 只挂不摘的话，页面被回收之后事件还攥着它，那一格就永远刷不掉了。
        HookEditsChanged();
        Unloaded += (_, _) => UnhookEditsChanged();

        Root.AllowDrop = true;
        Root.DragOver += Root_DragOver;
        Root.Drop += Root_Drop;
    }

    /// <summary>顶部那条交给窗口当标题栏（窗口能拖，右上角仍由系统画按钮）。</summary>
    public UIElement TitleBarElement => TitleBar;

    /// <summary>
    /// 把应用图标填到标题栏最左边（读不到就留空，不报错也不重试）。
    ///
    /// 取的是 TitleMark.png（单张相框、纯白、按 16px 优化过），不是主图标 AppIcon.png ——
    /// 主图标是"三张叠影 + 青蓝渐变"，缩到标题栏这个尺寸会糊成一坨浅蓝。
    /// </summary>
    private async Task LoadTitleIconAsync()
    {
        var icon = await AppIcon.LoadTitleMarkAsync();
        if (icon is not null) TitleIcon.Source = icon;
    }

    // ===== 左栏宽度（可拖拽） =====

    /// <summary>
    /// 左栏默认宽度。XAML 里 <c>TreeCol</c> 的初值也是 236，改的时候两边一起改。
    /// </summary>
    private const double DefaultTreeWidth = 236;

    /// <summary>
    /// 恢复上次拖出来的左栏宽度，并给分隔条接线。
    ///
    /// 必须在 Loaded 之后做（构造函数里 XamlRoot 还是 null）：
    /// 分隔条算最大宽度时要用窗口宽度，那会儿取不到，会算出一个错的上限。
    /// </summary>
    private void RestoreTreeWidth()
    {
        TreeSplitter.Attach(TreeCol, DefaultTreeWidth);

        // 拖动结束时才写盘。拖动过程中每一帧都写文件没必要，而且
        // settings.txt 是整份重写的，拖一次写几十遍不值当。
        TreeSplitter.WidthCommitted += (_, width) =>
            AppSettings.Set("TreePaneWidth", (int)Math.Round(width));

        // Attach 会把宽度重置成默认值，所以保存过的宽度要在它之后设
        int saved = AppSettings.GetInt("TreePaneWidth", (int)DefaultTreeWidth);
        if (saved > 0) TreeCol.Width = new GridLength(saved);

        // 存下来的是"上回那个窗口宽度下"的值，这次窗口可能小得多，先重量一次
        TreeSplitter.ReclampForWindow();

        // 窗口被拖小之后，左栏不能还占着固定像素（那会把右边内容区挤没）
        SizeChanged += (_, _) => TreeSplitter.ReclampForWindow();
    }

    // ===== 启动 =====

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupLog.Write("BrowserPage: OnLoaded");

        // 页面被卸载过一次又装回来时（换主题、被移出可视树再放回）要把订阅补上。
        // 带自锁，重复调用不会挂两遍 —— 挂两遍会让一次编辑刷两次格子。
        HookEditsChanged();

        // 后台先把"哪些图编辑过"读进内存。缩略图墙每一格都要问它一次，
        // 不能等到第一屏几十个格子一起问的时候才现查库。
        LibraryIndexService.Shared.WarmEditsCache();

        App.Instance?.SetCustomTitleBar(TitleBar);
        BuildTree();
        Focus(FocusState.Programmatic);

        RestoreTreeWidth();

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

            // 排序也要在载入目录之前恢复好，否则第一屏会先按默认顺序铺一遍再重排
            string? sort = AppSettings.Get("SortKey");
            if (!string.IsNullOrEmpty(sort) && Enum.TryParse<SortKey>(sort, out SortKey k))
                _sortOverride = k;

            SyncSortUi();
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

        // 探测本机有没有微信 / QQ / 企业微信 的缓存图（只读目录结构，很快），
        // 有还没进图库的就冒一条提示条。放最后、且不 await —— 别拖慢启动。
        _ = RefreshSocialHintAsync();

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
    /// 重建左侧树。按当前分类维度分派：
    /// 文件夹维度走 <see cref="BuildFolderTree"/>（读磁盘），
    /// 其它维度走 <see cref="BuildGroupTree"/>（查索引）。
    ///
    /// 之所以要做成两个而不是一个里 if 一下：两者的节点类型、数据来源、
    /// 右键菜单完全不是一回事，混在一段里会变成谁都看不懂的分支。
    /// </summary>
    private void BuildTree()
    {
        if (IndexMode) BuildGroupTree();
        else BuildFolderTree();
    }

    /// <summary>
    /// 按文件夹重建（这条就是以前的全部内容，没改过行为）。
    ///
    /// 结构：根节点是"图库"，下面挂用户收进来的文件夹；每个文件夹再展开就是它的子目录。
    ///
    /// 收进来的目录记在 %LOCALAPPDATA%\CelesteGallery\library.txt，下次启动还在。
    /// 以前这里写死"桌面/图片/下载"三个根 —— 那样放别处的照片只能靠工具条上的按钮，
    /// 而且访问过的目录不会被记住，用户反过来问"为什么打开过的不在图库里"。
    ///
    /// 微信 / QQ / 企业微信的缓存目录**单独成一段**放在下面（加一行"社交缓存"标题 + 分隔线），
    /// 节点名也换成应用名。理由：它们是十几万张的量级、名字又是哈希，
    /// 跟桌面/图片/下载排在一起既不好认也不好找；分开放之后一眼就知道
    /// "上面是我自己的文件夹，下面是软件缓存"。
    /// </summary>
    private void BuildFolderTree()
    {
        FolderTree.RootNodes.Clear();
        _libraryNodes.Clear();

        _libraryRoot = new TreeViewNode
        {
            Content = new FolderNode { Path = "", Label = "图库" },
        };
        FolderTree.RootNodes.Add(_libraryRoot);

        var folders = LibraryStore.Load();

        // 先把记录分成"普通文件夹"和"社交缓存"两拨，顺序按 library.txt 原样。
        // 拿不存在的先剔掉（移动硬盘没插、目录被删），但**不删记录** ——
        // 盘插回来它还在，比"悄悄消失"友好得多。
        var normal = new List<string>();
        var social = new List<string>();
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                StartupLog.Write($"BrowserPage: 图库条目暂时不可用 → {folder}");
                continue;
            }

            if (SocialCacheDetector.SourceOf(folder) is null) normal.Add(folder);
            else social.Add(folder);
        }

        int shown = 0;

        foreach (string folder in normal)
        {
            AddLibraryNode(folder, LibraryLabel(folder, folders));
            shown++;
        }

        if (social.Count > 0)
        {
            // 同一个应用登过多个账号时（本机有两个 QQ、两个企业微信），
            // 光写"QQ"会出来两行一模一样的，把账号缀上才分得清谁是谁。
            var sameAppCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string folder in social)
            {
                string app = SocialCacheDetector.SourceOf(folder)!;
                sameAppCount[app] = sameAppCount.GetValueOrDefault(app) + 1;
            }

            // 分区标题。Path 留空 → IsSection + IsRoot 都为真，
            // 于是"点了不加载""不会去展开子目录"这两件事自动成立（走既有的 IsRoot 判断）。
            _libraryRoot.Children.Add(new TreeViewNode
            {
                Content = new FolderNode { Path = "", Label = "社交缓存", IsSection = true },
                HasUnrealizedChildren = false,
            });

            foreach (string folder in social)
            {
                string app = SocialCacheDetector.SourceOf(folder)!;
                AddLibraryNode(folder, SocialCacheLabel(folder, app, sameAppCount[app]));
                shown++;
            }
        }

        _libraryRoot.IsExpanded = true;

        StartupLog.Write($"BrowserPage: 图库 {shown} 个文件夹（记录 {folders.Count} 条，其中社交缓存 {social.Count} 个）");

        // 重建之后把选中态挪回"当前正在看的目录"（没在看的就选第一个），
        // 否则重建会把选中高亮弄丢
        SelectTreeNodeForCurrentFolder();
    }

    /// <summary>挂一个图库直接条目到根节点上，并记进 <see cref="_libraryNodes"/>。</summary>
    private void AddLibraryNode(string folder, string label)
    {
        var node = MakeNode(folder, label: label, inLibrary: true);
        _libraryNodes.Add(node);
        _libraryRoot!.Children.Add(node);
    }

    /// <summary>
    /// 社交缓存条目的名字：直接用应用名（QQ / 微信 / 企业微信），
    /// 而不是目录名（"Pic""Cache""wxid_ea0e…"），否则左栏里根本认不出是什么。
    /// 同一个应用有多个账号时把账号缀在后面。
    /// </summary>
    private static string SocialCacheLabel(string folder, string app, int sameAppCount)
    {
        if (sameAppCount <= 1) return app;

        string acct = SocialCacheDetector.AccountOf(folder) ?? DisplayName(folder);
        return $"{app} · {Shorten(acct)}";
    }

    /// <summary>太长的账号（微信的 wxid_xxx_xxxx 有二十多个字符）截一下，左栏放不下。</summary>
    private static string Shorten(string s)
        => s.Length <= 16 ? s : string.Concat(s.AsSpan(0, 15), "…");

    /// <summary>
    /// 按当前维度重建左侧树（查索引，不读磁盘）。
    ///
    /// 结构：根是"全部"，下面一行一个组。点"全部"等于不筛，
    /// 点某个组就把右侧墙限定到那一组里。
    ///
    /// 索引还是空的时候这里会建出一棵只有一个"全部（0）"的树 ——
    /// 那不是 bug，是还没整理过图库。调用方负责先跑一遍整理，见
    /// <see cref="SwitchToIndexModeAsync"/>。
    /// </summary>
    private void BuildGroupTree()
    {
        FolderTree.RootNodes.Clear();
        _libraryNodes.Clear();

        string glyph = _groupBy switch
        {
            // E787 = 日历，E722 = 相机，E716 = 联系人（代表"来自哪个软件"）
            GroupBy.Date => "\uE787",
            GroupBy.Camera => "\uE722",
            GroupBy.Lens => "\uE722",
            GroupBy.Source => "\uE716",
            _ => "\uE8B7",
        };

        var groups = LibraryIndexService.Shared.Group(_groupBy, FilterWithoutGroup());
        int total = groups.Sum(g => g.Count);

        var all = new TreeViewNode
        {
            Content = new GroupNode
            {
                Key = string.Empty,
                Label = $"全部　{total}",
                Count = total,
                IsAll = true,
                Glyph = glyph,
            },
        };
        all.IsExpanded = true;
        FolderTree.RootNodes.Add(all);

        foreach (GroupEntry g in groups)
        {
            all.Children.Add(new TreeViewNode
            {
                Content = new GroupNode
                {
                    Key = g.Key,
                    Label = $"{g.Label}　{g.Count}",
                    Count = g.Count,
                    Glyph = glyph,
                },
            });
        }

        // 选中态挪回当前分组（没选就落在"全部"上），否则重建会把高亮弄丢
        TreeViewNode? pick = all;
        if (_groupValue is not null)
        {
            foreach (TreeViewNode child in all.Children)
            {
                if (child.Content is GroupNode gn && gn.Key == _groupValue) { pick = child; break; }
            }
        }

        FolderTree.SelectedNode = pick;

        StartupLog.Write($"BrowserPage: 分组树 {_groupBy} → {groups.Count} 组 / 共 {total} 张");
    }

    /// <summary>给"分组"用的查询条件：带搜索词，但不带分组值（分组时不能用自己筛自己）。</summary>
    private MediaQuery FilterWithoutGroup() => new()
    {
        Text = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
        FavoritesOnly = _favOnly,
        Tag = _tagFilter,
    };

    /// <summary>给"右侧墙"用的查询条件。</summary>
    private MediaQuery CurrentQuery() => new()
    {
        Text = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
        Group = _groupBy,
        GroupValue = _groupValue,
        FavoritesOnly = _favOnly,
        Tag = _tagFilter,
        // 按文件夹时保持"跟资源管理器一样的名字顺序"，其余维度按拍摄时间倒序更实用。
        // 用户在排序菜单里选过的话以他选的为准。
        Sort = EffectiveSort(),
    };

    /// <summary>
    /// 从索引查一批路径铺到墙上（分类模式 / 搜索模式走这条路）。
    /// </summary>
    private async Task LoadFromIndexAsync()
    {
        int seq = ++_loadSeq;

        var q = CurrentQuery();

        // 查库是同步的 SQLite 调用，几千条也就几毫秒，但为了不挡 UI 还是挪到后台
        List<string> paths = await Task.Run(() => LibraryIndexService.Shared.Query(q));

        if (seq != _loadSeq) return;

        // 文件名序要和"直接读磁盘"那条路对齐（自然序）：
        // SQLite 排名字是字典序，IMG_10 会排在 IMG_2 前面，切个分类就觉得顺序乱了
        if (q.Sort == SortKey.FileNameAsc || q.Sort == SortKey.FileNameDesc)
            paths = LibraryIndexService.SortNatural(paths, q.Sort == SortKey.FileNameDesc);

        string heading = DescribeCurrentView();

        PathText.Text = heading;
        TitleText.Text = _tagFilter is not null ? $"标签「{_tagFilter}」"
            : !string.IsNullOrWhiteSpace(_searchText) ? _searchText
            : DescribeGroupLabel();
        TitleDot.Visibility = Visibility.Visible;

        if (paths.Count == 0)
        {
            // 必须先把墙清空再显示空状态：
            // 否则"搜不到"的时候上一屏的图还挂在那儿，看着像搜出来了结果
            _items = new ObservableCollection<ThumbnailItem>();
            Thumbs.ItemsSource = _items;

            ShowEmpty(string.IsNullOrWhiteSpace(_searchText)
                ? "这一类里还没有图"
                : _tagFilter is not null
                    ? $"没有匹配的图（标签「{_tagFilter}」）"
                    : $"没有匹配「{_searchText}」的图");
            return;
        }

        var files = new List<(string Path, string? Sub)>(paths.Count);

        foreach (string p in paths)
        {
            string? sub = null;
            try
            {
                // 按日期/相机分组时图散在各处，角标标出它所在文件夹的名字 ——
                // 一眼能分清是手机相册还是相机卡里的
                string? dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) sub = Path.GetFileName(dir);
            }
            catch { }

            files.Add((p, sub));
        }

        ApplyFiles(files, $"共 {paths.Count} 张　·　单击选中，双击打开");
    }

    private string DescribeCurrentView()
    {
        if (_tagFilter is not null)
            return $"标签「{_tagFilter}」"
                   + (string.IsNullOrWhiteSpace(_searchText)
                       ? ""
                       : $" · 搜索「{_searchText}」");
        return !string.IsNullOrWhiteSpace(_searchText)
            ? $"搜索「{_searchText}」"
            : _groupBy switch
            {
                GroupBy.Date => "全部照片 · 按拍摄日期",
                GroupBy.Camera => "全部照片 · 按相机",
                GroupBy.Lens => "全部照片 · 按镜头",
                GroupBy.Rating => "全部照片 · 按评分",
                GroupBy.Source => "全部照片 · 按来源",
                _ => "全部照片",
            };
    }

    /// <summary>标题栏上跟在程序名后面的那个词。</summary>
    private string DescribeGroupLabel()
    {
        if (_groupValue is null) return "全部照片";

        if (FolderTree.SelectedNode?.Content is GroupNode gn)
        {
            // Label 里带了数量（"2026 年 9 月　128"），标题栏只要前面那段
            int cut = gn.Label.IndexOf('　');
            return cut > 0 ? gn.Label.Substring(0, cut) : gn.Label;
        }

        return _groupValue;
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
        var node = args.InvokedItem as TreeViewNode ?? FolderTree.SelectedNode;

        // 分组条目：点它就把它限定为当前分组，右侧墙重新查一次
        if (node?.Content is GroupNode group)
        {
            _groupValue = group.IsAll ? null : group.Key;
            _ = LoadFromIndexAsync();
            return;
        }

        // "图库"那一行是分组标题，点了不加载任何目录
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
        // 分组模式下的条目不是文件夹，没有"路径"可言 ——
        // 给"在资源管理器中打开""加入图库"这些动作只会让人困惑，
        // 所以这里换成一套跟索引有关的动作。
        if (IndexMode)
        {
            var groupMenu = new MenuFlyout();
            bool busy = LibraryIndexService.Shared.IsIndexing;

            groupMenu.Items.Add(MakeMenuItem(busy ? "停止整理" : "整理图库", "\uE72C",
                () => _ = IndexActionAsync()));
            groupMenu.Items.Add(MakeMenuItem("刷新", "\uE72C", () =>
            {
                BuildTree();
                _ = LoadFromIndexAsync();
            }));

            return groupMenu;
        }

        var flyout = new MenuFlyout();
        var info = node?.Content as FolderNode;

        // 空白处 / "图库"分组标题：给的是图库级别的操作
        if (info is null || info.IsRoot)
        {
            flyout.Items.Add(MakeMenuItem("添加文件夹到图库…", "\uE8E5", () => _ = PickFolderAndAddAsync()));
            flyout.Items.Add(MakeMenuItem("添加社交缓存…", "\uE716", () => _ = AddSocialCachesAsync()));
            flyout.Items.Add(MakeMenuItem("刷新图库", "\uE72C", BuildTree));
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
        BuildTree();
    }

    private void RemoveFromLibrary(string path)
    {
        LibraryStore.Remove(path);
        StartupLog.Write($"BrowserPage: 从图库移除 → {path}");
        BuildTree();
    }

    // ===== 社交缓存（微信 / QQ / 企业微信）=====
    //
    // 这些软件的缓存图在资源管理器里几乎没法看（哈希文件名、散在多层目录、没 EXIF），
    // 收进图库后就能按"来源"和"日期"当正常相册浏览。
    // 全程**只读**：只把目录路径记进图库清单，一个字节都不改人家的缓存。

    /// <summary>用户点过提示条上的叉之后，就不再主动提示（可在右键菜单里手动加）。</summary>
    private const string SocialHintDismissedKey = "SocialHintDismissed";

    /// <summary>提示条上"待加入"的那批目录。点"加入图库"时直接用，不重新探测。</summary>
    private List<SocialCacheRoot> _socialRoots = new();

    /// <summary>
    /// 探测社交缓存目录，有还没进图库的就冒一条提示条。
    ///
    /// 探测只是"看几个固定目录在不在"，很快，但仍丢到后台线程：
    /// 将来若是要递归统计张数，放 UI 线程会直接卡住窗口。
    /// </summary>
    private async Task RefreshSocialHintAsync()
    {
        SocialHint.Visibility = Visibility.Collapsed;

        if (AppSettings.GetBool(SocialHintDismissedKey, false)) return;

        List<SocialCacheRoot> found;
        try
        {
            found = await Task.Run(SocialCacheDetector.Detect);
        }
        catch (Exception ex)
        {
            StartupLog.Write("BrowserPage: 探测社交缓存失败", ex);
            return;
        }

        _socialRoots = NotInLibrary(found);
        if (_socialRoots.Count == 0) return;

        // 按来源归并成一句话，别把四个目录列成四行
        var apps = _socialRoots.Select(r => r.App).Distinct().ToList();

        SocialHintText.Text =
            $"发现 {string.Join("、", apps)} 的缓存图片（{_socialRoots.Count} 个目录）。" +
            "加入图库后可用「按来源」统一浏览。";

        SocialHint.Visibility = Visibility.Visible;

        StartupLog.Write(
            $"BrowserPage: 探测到未入图库的社交缓存 {_socialRoots.Count} 个 → {string.Join(" | ", apps)}");
    }

    /// <summary>去掉已经在图库清单里的那些目录（按忽略大小写、忽略结尾斜杠比较）。</summary>
    private static List<SocialCacheRoot> NotInLibrary(List<SocialCacheRoot> roots)
    {
        var inLibrary = LibraryStore.Load();

        return roots.Where(r => !inLibrary.Exists(p =>
                    string.Equals(
                        Path.TrimEndingDirectorySeparator(p),
                        Path.TrimEndingDirectorySeparator(r.Path),
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();
    }

    /// <summary>提示条上的"加入图库"和右键菜单"添加社交缓存…"都走这里。</summary>
    private async void SocialAddButton_Click(object sender, RoutedEventArgs e)
        => await AddSocialCachesAsync();

    private void SocialHintClose_Click(object sender, RoutedEventArgs e)
    {
        SocialHint.Visibility = Visibility.Collapsed;
        AppSettings.Set(SocialHintDismissedKey, true);
        StartupLog.Write("BrowserPage: 社交缓存提示已关闭（不再提示）");
    }

    /// <summary>
    /// 把社交缓存目录收进图库，然后整理一遍索引。
    ///
    /// 为什么要紧接着整理：这些目录可能几十万张，用户切到"按来源"时
    /// 如果索引是空的会看到一棵空树，以为功能坏了。
    /// </summary>
    private async Task AddSocialCachesAsync()
    {
        List<SocialCacheRoot> found;
        try
        {
            found = await Task.Run(SocialCacheDetector.Detect);
        }
        catch (Exception ex)
        {
            StartupLog.Write("BrowserPage: 探测社交缓存失败", ex);
            return;
        }

        var todo = NotInLibrary(found);

        if (todo.Count == 0)
        {
            ShowEmpty(found.Count == 0
                ? "本机没找到微信 / QQ / 企业微信 的缓存图片目录"
                : "社交缓存目录已经都在图库里了");
            return;
        }

        foreach (SocialCacheRoot root in todo)
        {
            LibraryStore.Add(root.Path);
            StartupLog.Write($"BrowserPage: 社交缓存加入图库 → {root.App} {root.Path}");
        }

        SocialHint.Visibility = Visibility.Collapsed;
        _socialRoots = new List<SocialCacheRoot>();

        var apps = todo.Select(r => r.App).Distinct().ToList();
        StartupLog.Write($"BrowserPage: 社交缓存加入完成，共 {todo.Count} 个目录（{string.Join("、", apps)}）");

        // 这些目录动辄几十万张，第一次整理要等一会儿 —— 进度显示在右侧空白处
        BuildTree();
        await EnsureIndexedAsync();
        BuildTree();
        if (IndexMode) await LoadFromIndexAsync();

        ShowEmpty($"已加入 {string.Join("、", apps)} 的缓存目录。把左上角分类切到「按来源」即可查看。");
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

        if (LibraryIndexService.Shared.Available)
        {
            // 评分子菜单：0 星 = 取消评分
            var svc = LibraryIndexService.Shared;
            int current = svc.RatingOf(item.Path);

            var rate = new MenuFlyoutSubItem
            {
                Text = current > 0 ? $"评分　{new string('★', current)}" : "评分",
                Icon = new FontIcon { Glyph = "\uE734" },   // ☆
            };

            for (int s = 5; s >= 0; s--)
            {
                int stars = s;   // 闭包要抓副本，不能直接用循环变量
                string label = s == 0
                    ? "取消评分"
                    : new string('★', s);

                if (s == current) label += "　✓";

                rate.Items.Add(MakeMenuItem(label, s > 0 ? "\uE735" : "\uE8D9",
                    () => ApplyRating(item, stars)));
            }

            flyout.Items.Add(rate);

            bool fav = svc.IsFavorite(item.Path);
            flyout.Items.Add(MakeMenuItem(fav ? "取消收藏" : "收藏",
                fav ? "\uE8D9" : "\uE735", () => ToggleFavorite(item)));

            // E8EC = Tag
            flyout.Items.Add(MakeMenuItem("标签…", "\uE8EC", () => _ = EditTagsAsync(item)));
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
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

    // ===== 用户数据：评分 / 收藏 / 标签 =====
    //
    // 这三样只存在索引库里（第 3 版表结构），图库重扫不会丢。
    // 索引不可用时右键菜单里根本不会出现这些项，所以这里不做兜底判断。

    private void ApplyRating(ThumbnailItem item, int stars)
        => LibraryIndexService.Shared.SetRating(item.Path, stars);

    private void ToggleFavorite(ThumbnailItem item)
    {
        LibraryIndexService.Shared.ToggleFavorite(item.Path);

        // "只看收藏"开着的时候，取消收藏意味着这张图不该再留在墙上
        if (_favOnly && IndexMode) _ = LoadFromIndexAsync();
    }

    /// <summary>弹个小对话框编辑标签。存的是整批替换：清空文本框 = 删光标签。</summary>
    private async Task EditTagsAsync(ThumbnailItem item)
    {
        var svc = LibraryIndexService.Shared;

        var input = new TextBox
        {
            PlaceholderText = "用空格或逗号分开，比如：旅行 家人",
            Text = string.Join("  ", svc.GetTags(item.Path)),
        };

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = $"标签 — {item.FileName}",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // "旅行, 家人 朋友" → ["旅行", "家人", "朋友"]。空段丢掉，重复段去重。
        var tags = (input.Text ?? string.Empty)
            .Split(new[] { ' ', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        svc.SetTags(item.Path, tags);

        // 开着"只看收藏"或以后有"按标签筛"时保持视图同步
        if (_favOnly && IndexMode) await LoadFromIndexAsync();
    }

    // ===== 工具栏"只看收藏"开关 =====

    private async void FavToggle_Checked(object sender, RoutedEventArgs e)
    {
        var svc = LibraryIndexService.Shared;

        // 索引用不了就别硬开：开关弹回去，提示一句话
        if (!svc.Available)
        {
            _favOnly = false;
            try { FavToggle.IsChecked = false; } finally { UpdateFavIcon(); }
            ShowEmpty("索引库打不开，收藏功能暂时用不了");
            return;
        }

        _favOnly = true;
        UpdateFavIcon();

        // 从"按文件夹、无搜索"的纯磁盘模式切过来的话，把旧的模式残留清掉，
        // 跟 ApplySearchAsync 切索引模式时的处理保持一致
        if (_groupBy == GroupBy.Folder && string.IsNullOrWhiteSpace(_searchText))
            _groupValue = null;

        await SwitchToIndexModeAsync();
    }

    private async void FavToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _favOnly = false;
        UpdateFavIcon();

        // 只在查库模式下需要刷新；正常顺序是先 Checked（切到索引）再 Unchecked，
        // 所以这里几乎总是 IndexMode == true
        if (IndexMode) await LoadFromIndexAsync();
    }

        /// <summary>E734 空心星 = 没开；E735 实心星 = 开着。跟右键菜单里用的图标一致。</summary>
        private void UpdateFavIcon() => FavIcon.Glyph = _favOnly ? "\uE735" : "\uE734";

        // ===== 工具栏"查找重复 / 相似"智能相册 =====
        //
        // 和"只看收藏"平级的第二个智能相册。它和收藏最大的不同是：
        // 结果不是"一条平铺的过滤列表"，而是"一组一组的重复/相似图"，
        // 所以不能用现成的 Query 那条路，得单独把结果铺到 DupPanel 里，
        // 每组默认保留第一张、其余可以被"移到回收站"（可还原，不是硬删）。

        /// <summary>查重视图的子模式。</summary>
        private enum DupMode { None, Exact, Similar }

        /// <summary>当前查重子模式。None = 没在查重视图里。</summary>
        private DupMode _dupMode = DupMode.None;

        /// <summary>DupToggle 是否开着（= 是否进入了查重视图）。</summary>
        private bool _dupActive;

        /// <summary>同步那两个子开关时挡一下，避免互相取消又触发渲染。</summary>
        private bool _dupUiSyncing;

        /// <summary>查重结果：每一组是一份 ThumbnailItem 集合（第一张默认"保留"）。</summary>
        private readonly List<ObservableCollection<ThumbnailItem>> _dupGroups = new();

        /// <summary>进入查重前记住该回哪个视图，退出时还原（避免把用户之前的浏览状态弄丢）。</summary>
        private DupReturnState? _dupReturn;

        /// <summary>进入查重前要记住的视图状态（用于退出时还原）。</summary>
        private sealed class DupReturnState
        {
            public string? CurrentFolder;
            public GroupBy GroupBy;
            public string? GroupValue;
            public string SearchText = "";
            public string? TagFilter;
            public bool FavOnly;
            public SortKey? SortOverride;
        }

        private async void DupToggle_Checked(object sender, RoutedEventArgs e)
        {
            var svc = LibraryIndexService.Shared;
            if (!svc.Available)
            {
                _dupActive = false;
                try { DupToggle.IsChecked = false; } catch { }
                ShowEmpty("索引库打不开，查找重复功能暂时用不了");
                return;
            }

            // 记住现在的视图，退出时还原
            _dupReturn = new DupReturnState
            {
                CurrentFolder = _currentFolder,
                GroupBy = _groupBy,
                GroupValue = _groupValue,
                SearchText = _searchText,
                TagFilter = _tagFilter,
                FavOnly = _favOnly,
                SortOverride = _sortOverride,
            };

            _dupActive = true;
            _dupMode = DupMode.Exact;

            // 切到查重视图：藏起主墙，亮出查重面板和命令条
            GridScroller.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            MultiSelectBar.Visibility = Visibility.Collapsed;
            DupPanel.Visibility = Visibility.Visible;
            DupBar.Visibility = Visibility.Visible;

            // 同步子开关（精确重复默认勾上）
            _dupUiSyncing = true;
            try { DupExactToggle.IsChecked = true; DupSimilarToggle.IsChecked = false; }
            finally { _dupUiSyncing = false; }

            // 索引还是空的，先整理一遍（顺带算好指纹）
            if (svc.Count == 0) await EnsureIndexedAsync();

            await RenderDupViewAsync();
        }

        private async void DupToggle_Unchecked(object sender, RoutedEventArgs e)
            => await ExitDupModeAsync();

        /// <summary>退出查重视图，还原进入前的浏览状态。幂等。</summary>
        private async Task ExitDupModeAsync()
        {
            if (!_dupActive && _dupMode == DupMode.None) return;

            _dupActive = false;
            _dupMode = DupMode.None;
            _dupGroups.Clear();
            DupGroups.Children.Clear();
            DupStatusText.Text = "";

            DupPanel.Visibility = Visibility.Collapsed;
            DupBar.Visibility = Visibility.Collapsed;
            GridScroller.Visibility = Visibility.Visible;

            var state = _dupReturn;
            _dupReturn = null;

            if (state is null)
            {
                ShowEmpty("从左边选一个文件夹");
                return;
            }

            _currentFolder = state.CurrentFolder;
            _groupBy = state.GroupBy;
            _groupValue = state.GroupValue;
            _searchText = state.SearchText;
            _tagFilter = state.TagFilter;
            _favOnly = state.FavOnly;
            _sortOverride = state.SortOverride;

            BuildTree();
            if (IndexMode) await LoadFromIndexAsync();
            else if (_currentFolder is not null) await LoadFolderAsync(_currentFolder);
            else ShowEmpty("从左边选一个文件夹");
        }

        private void DupExitButton_Click(object sender, RoutedEventArgs e)
        {
            // 走 DupToggle 的 Unchecked 统一退出，避免两处各写一遍还原逻辑
            DupToggle.IsChecked = false;
        }

        /// <summary>切精确重复 / 相似：同步子开关 + 重算结果。</summary>
        private void SetDupMode(DupMode mode)
        {
            _dupMode = mode;
            _dupUiSyncing = true;
            try
            {
                DupExactToggle.IsChecked = mode == DupMode.Exact;
                DupSimilarToggle.IsChecked = mode == DupMode.Similar;
            }
            finally { _dupUiSyncing = false; }

            _ = RenderDupViewAsync();
        }

        private void DupExactToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_dupUiSyncing) return;
            SetDupMode(DupMode.Exact);
        }

        private void DupExactToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_dupUiSyncing) return;
            // 两个不能都空：另一个没勾时才把这一颗按回去
            if (DupSimilarToggle.IsChecked != true) DupExactToggle.IsChecked = true;
        }

        private void DupSimilarToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_dupUiSyncing) return;
            SetDupMode(DupMode.Similar);
        }

        private void DupSimilarToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_dupUiSyncing) return;
            if (DupExactToggle.IsChecked != true) DupSimilarToggle.IsChecked = true;
        }

        /// <summary>查重复 / 查相似：补齐指纹 → 查分组 → 铺到 DupPanel。</summary>
        private async Task RenderDupViewAsync()
        {
            var svc = LibraryIndexService.Shared;
            if (!svc.Available) { DupStatusText.Text = "索引库不可用"; return; }

            DupStatusText.Text = "正在查找…";
            DupGroups.Children.Clear();
            _dupGroups.Clear();

            // 首次进入或指纹不全时先补齐指纹。
            // 精确重复只要 MD5（纯 C# 哈希，快）；相似还要 PHash（要动用 Magick 解码，慢，
            // 但只在切到"相似图片"时才算，不会拖慢精确重复）。
            bool needPhash = _dupMode == DupMode.Similar;
            int backfilled = await Task.Run(
                () => svc.BackfillHashesAsync(needPhash, null, CancellationToken.None));
            StartupLog.Write($"BrowserPage: 查重前补算指纹 {backfilled} 条（phash={needPhash}）");

            // 在后台线程跑 SQLite 查询，不挡 UI
            List<DuplicateGroup>? dups = null;
            List<SimilarGroup>? sims = null;
            await Task.Run(() =>
            {
                if (_dupMode == DupMode.Similar) sims = svc.FindSimilar(10);
                else dups = svc.FindDuplicates();
            });

            int totalImages = 0;

            if (dups is not null)
            {
                for (int i = 0; i < dups.Count; i++)
                {
                    if (dups[i].Count < 2) continue;
                    totalImages += AddDupGroup(i + 1, dups[i].Paths);
                }
            }
            else if (sims is not null)
            {
                for (int i = 0; i < sims.Count; i++)
                {
                    if (sims[i].Count < 2) continue;
                    totalImages += AddDupGroup(i + 1, sims[i].Paths);
                }
            }

            if (_dupGroups.Count == 0)
            {
                DupStatusText.Text = _dupMode == DupMode.Similar
                    ? "没有发现视觉相似的图片"
                    : "没有发现完全重复的图片";
                var tip = new TextBlock
                {
                    Text = DupStatusText.Text,
                    FontSize = 14,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0),
                };
                DupGroups.Children.Add(tip);
                return;
            }

            DupStatusText.Text = $"找到 {_dupGroups.Count} 组 · 共 {totalImages} 张　" +
                                 "（点图可改「保留」哪张，再点「未保留的移到回收站」）";
        }

        /// <summary>把一组路径建成一个"组块"（标题 + 横向缩略图排）塞进 DupPanel，返回这组图数量。</summary>
        private int AddDupGroup(int index, List<string> paths)
        {
            var items = new ObservableCollection<ThumbnailItem>(
                paths.Select(p => new ThumbnailItem { Path = p, FileName = Path.GetFileName(p) }));
            if (items.Count > 0) items[0].IsKeep = true;   // 默认保留每组第一张
            _dupGroups.Add(items);

            var block = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 14),
            };

            block.Children.Add(new TextBlock
            {
                Text = $"第 {index} 组 · 共 {items.Count} 张",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray),
            });

            var repeater = new ItemsRepeater
            {
                Layout = new UniformGridLayout
                {
                    MinItemWidth = 150,
                    MinItemHeight = 150,
                    MinColumnSpacing = 6,
                    MinRowSpacing = 6,
                    ItemsStretch = UniformGridLayoutItemsStretch.Fill,
                    Orientation = Orientation.Horizontal,
                    MaximumRowsOrColumns = 12,
                },
                ItemTemplate = (DataTemplate)Resources["TileTemplate"],
                ItemsSource = items,
            };
            repeater.ElementPrepared += DupThumbs_ElementPrepared;
            block.Children.Add(repeater);

            DupGroups.Children.Add(block);
            return items.Count;
        }

        /// <summary>查重视图里每个组块的格子出现时才解码（和主墙同一个节流逻辑）。</summary>
        private void DupThumbs_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (sender.ItemsSource is not System.Collections.IList list) return;
            if (args.Index < 0 || args.Index >= list.Count) return;
            if (list[args.Index] is not ThumbnailItem item) return;
            if (item.LoadRequested) return;

            item.LoadRequested = true;
            _ = LoadThumbAsync(item);
        }

        /// <summary>在一组里切换"保留"那张：清掉别的、把这一张标成保留。</summary>
        private void ToggleDupKeep(ThumbnailItem item)
        {
            var group = _dupGroups.FirstOrDefault(g => g.Contains(item));
            if (group is null || item.IsKeep) return;   // 已经是保留的，点了没意义

            foreach (var it in group) it.IsKeep = false;
            item.IsKeep = true;
        }

        /// <summary>把没标"保留"的图移到回收站（可还原），并刷新查重结果。</summary>
        private async void DupDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var toRemove = new List<string>();
            foreach (var g in _dupGroups)
                foreach (var it in g)
                    if (!it.IsKeep) toRemove.Add(it.Path);

            if (toRemove.Count == 0)
            {
                DupStatusText.Text = "没有需要删除的（每组都已保留一张）";
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "移到回收站",
                Content = $"将把 {toRemove.Count} 张未保留的图片移到回收站。\n" +
                          "回收站里的文件可以在资源管理器里还原，不会立即永久删除。",
                PrimaryButtonText = "移到回收站",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            DupStatusText.Text = "正在移动…";

            var (moved, aborted) = await Task.Run(() => RecycleBin.Send(toRemove));

            if (moved == 0)
            {
                DupStatusText.Text = aborted ? "移动失败或被取消" : "没有文件被移动";
                return;
            }

            // 从索引里删掉这些记录，免得下次查重它们又冒出来
            LibraryIndexService.Shared.RemovePaths(toRemove);

            // 重新查一遍：被移走的图不在了，剩下的组只剩"保留"那张 → 查重结果会清空
            await RenderDupViewAsync();
        }

        /// <summary>右键点在墙的空白处（没落在某张图上）。</summary>
    private void Wall_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(MakeMenuItem("刷新", "\uE72C", Refresh));

        // 索引模式下"当前文件夹"这个概念不成立（图可能来自十几个目录），
        // 那些针对单个目录的操作就不该出现在菜单里
        if (_currentFolder is not null && !IndexMode)
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
            // 排序也放进后台：按大小 / 时间排要给每个文件取一次属性，
            // 上千张时在 UI 线程上做会明显卡一下
            (files, truncated) = await Task.Run(() =>
            {
                var r = Scan(folder, includeSub);
                SortDiskFiles(r.Files);
                return r;
            });
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

        string scope = includeSub ? "（含子文件夹）" : "";
        string status = truncated
            ? $"共 {files.Count} 张{scope}　·　太多了，只铺出前 {files.Count} 张"
            : $"共 {files.Count} 张{scope}　·　单击选中，双击打开";

        ApplyFiles(files, status);

        // 顺手把这个目录收进索引库。
        // 按文件夹浏览这条路本身不需要索引，但提前攒好数据，
        // 等用户切到"按日期 / 按相机"时就能立刻出结果，不用先干等一轮整理。
        // 扫描是增量的（文件没变就跳过），所以反复点同一个目录几乎零成本。
        if (LibraryIndexService.Shared.Available)
            _ = LibraryIndexService.Shared.IndexAsync(new[] { folder }, includeSub);
    }

    /// <summary>
    /// 把一批文件铺到缩略图墙上 —— "直接扫盘"和"查索引"两条路共用这一段。
    /// </summary>
    private void ApplyFiles(List<(string Path, string? Sub)> files, string status)
    {
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

        StatusText.Text = status;
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
    /// "直接读磁盘"那条路（按文件夹浏览）的排序。
    ///
    /// 索引那条路交给 SQLite，又快又准；这条没有元数据可用，只能就地算：
    ///   · 文件名       → 自然序（IMG_2 排在 IMG_10 前面，和索引那条路口径一致）
    ///   · 文件大小     → 逐个取文件长度
    ///   · 拍摄时间     → 逐个取文件修改时间
    ///
    /// 最后一条要说清楚：按文件夹浏览时压根没读过 EXIF，所以用的是修改时间。
    /// 而索引库里"没有 EXIF 的图"用的也正是修改时间（见 MediaIndex），
    /// 也就是说两边的"拍摄时间"在没有 EXIF 的时候是同一个东西，不会自相矛盾。
    /// </summary>
    private void SortDiskFiles(List<(string Path, string? Sub)> files)
    {
        if (files.Count < 2) return;

        switch (EffectiveSort())
        {
            case SortKey.FileNameAsc:
                files.Sort((a, b) => FolderIndex.CompareNatural(
                    Path.GetFileName(a.Path), Path.GetFileName(b.Path)));
                break;

            case SortKey.FileNameDesc:
                files.Sort((a, b) => FolderIndex.CompareNatural(
                    Path.GetFileName(b.Path), Path.GetFileName(a.Path)));
                break;

            case SortKey.FileSizeDesc:
            {
                // 每个文件的大小只取一次：比较器会被调用 O(n·log n) 次，
                // 每次都 new 一个 FileInfo 的话，一千张图就是几万次系统调用
                var size = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files) size[f.Path] = SafeLength(f.Path);

                files.Sort((a, b) => size[b.Path].CompareTo(size[a.Path]));
                break;
            }

            case SortKey.DateTakenDesc:
            case SortKey.DateTakenAsc:
            {
                var time = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files) time[f.Path] = SafeWriteTime(f.Path);

                // 注意不能写成 cond ? 升序lambda : 降序lambda ——
                // 两个 lambda 没有共同类型，条件表达式推不出来，编译不过
                if (EffectiveSort() == SortKey.DateTakenDesc)
                    files.Sort((a, b) => time[b.Path].CompareTo(time[a.Path]));
                else
                    files.Sort((a, b) => time[a.Path].CompareTo(time[b.Path]));
                break;
            }
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return default; }
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

    // ===== 分类维度 / 搜索 / 整理图库 =====

    /// <summary>
    /// 换分类维度。
    ///
    /// 注意 XAML 里第一项写了 IsSelected="True"：解析到那一行就会触发本方法，
    /// 那时后面几行的控件（比如"图库"标题）还没建出来，碰一下就崩。
    /// 好在这时候选中的正是"按文件夹"，和 _groupBy 的初值相同，
    /// 会被下面的早退挡住 —— 和 SetLayout 里那个"解析期别去碰控件"
    /// 是同一个坑的两种形态，都是 XAML 自下而上解析造成的。
    /// </summary>
    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<GroupBy>(tag, out GroupBy by)) return;
        if (by == _groupBy) return;

        _groupBy = by;
        _groupValue = null;

        // 收藏/标签筛开着时不允许退回"直接读磁盘"——它们只存在于索引里
        bool folderMode = by == GroupBy.Folder && string.IsNullOrWhiteSpace(_searchText)
                          && !_favOnly && _tagFilter is null;

        // "包含子文件夹"只对"直接读磁盘"那条路有意义：
        // 索引模式下图库里的目录（含子目录）全都收进来了，这个开关没有作用对象
        IncludeSubRow.Visibility = folderMode ? Visibility.Visible : Visibility.Collapsed;
        TreeTitle.Text = (item.Content as string) ?? "图库";

        StartupLog.Write($"BrowserPage: 分类 = {by}");

        if (folderMode)
        {
            // 退回原来的浏览方式：直接读磁盘，行为和加这个功能之前完全一致
            BuildTree();
            if (_currentFolder is not null) _ = LoadFolderAsync(_currentFolder);
            else ShowEmpty("从左边选一个文件夹");
            return;
        }

        _ = SwitchToIndexModeAsync();
    }

    /// <summary>搜索框：每敲一个字都查一遍太浪费，等手停下来再说。</summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 这里没去判 args.Reason：本搜索框从不在代码里改 Text（UpdateTextOnSelect 关了），
        // 触发这个事件的只可能是用户敲键盘。少依赖一个 API 就少一处版本差异。
        if (_searchDebounce is null)
        {
            _searchDebounce = _uiQueue.CreateTimer();
            _searchDebounce.Interval = TimeSpan.FromMilliseconds(320);
            _searchDebounce.IsRepeating = false;
            _searchDebounce.Tick += (_, _) => _ = ApplySearchAsync();
        }

        // 顺手把标签建议填上：图库里已有的标签里，包含这几个字的都列出来。
        // 标签表就几十行，同步查一把毫秒级，不值得为它上异步。
        // 查不到（索引不可用/没有匹配）就给 null，不弹列表。
        string text = (sender.Text ?? string.Empty).Trim();
        var svc = LibraryIndexService.Shared;

        if (text.Length == 0 || !svc.Available)
        {
            sender.ItemsSource = null;
        }
        else
        {
            var suggestions = svc.AllTags()
                .Where(t => t.Tag.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Count)
                .Take(6)
                .Select(t => (object)new TagSuggestion(t.Tag, t.Count))
                .ToList();

            sender.ItemsSource = suggestions.Count > 0 ? suggestions : null;
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>搜索框里按回车：不等防抖，立刻查。</summary>
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // 点了建议列表里的标签 → 按标签筛，搜索词清掉（两条筛选线不混着记账）。
        // 防抖定时器还挂着的话要掐掉，不然 320ms 后它一响又把标签筛冲掉。
        if (args.ChosenSuggestion is TagSuggestion chosen)
        {
            _searchDebounce?.Stop();
            _tagFilter = chosen.Tag;
            _searchText = string.Empty;

            _groupValue = null;
            _ = SwitchToIndexModeAsync();
            return;
        }

        _ = ApplySearchAsync();
    }

    /// <summary>建议列表里的一项：标签名 + 打了这个标签的图有多少张。</summary>
    private sealed class TagSuggestion
    {
        public TagSuggestion(string tag, int count) { Tag = tag; Count = count; }

        public string Tag { get; }
        public int Count { get; }

        // AutoSuggestBox 没配 ItemTemplate 时直接拿 ToString 当显示文本
        public override string ToString() => $"{Tag}　（{Count} 张）";
    }

    private async Task ApplySearchAsync()
    {
        _searchText = (SearchBox.Text ?? string.Empty).Trim();

        // 搜索框清空 = 全部重来：搜索词和标签筛一起撤
        if (string.IsNullOrWhiteSpace(_searchText)) _tagFilter = null;

        // 搜索框清空 + 按文件夹 = 回到原来那条直接读磁盘的路
        if (!IndexMode)
        {
            IncludeSubRow.Visibility = Visibility.Visible;
            BuildTree();
            if (_currentFolder is not null) await LoadFolderAsync(_currentFolder);
            else ShowEmpty("从左边选一个文件夹");
            return;
        }

        _groupValue = null;
        await SwitchToIndexModeAsync();
    }

    /// <summary>
    /// 切到"查索引"这条模式。索引还是空的话先整理一遍再显示 ——
    /// 否则用户只会看到一棵空的树，不知道是没图还是没整理。
    /// </summary>
    private async Task SwitchToIndexModeAsync()
    {
        var svc = LibraryIndexService.Shared;

        if (!svc.Available)
        {
            ShowEmpty("索引库打不开，按日期/相机的分类和搜索暂时用不了（按文件夹浏览不受影响）");
            return;
        }

        if (svc.Count == 0) await EnsureIndexedAsync();

        BuildTree();
        await LoadFromIndexAsync();
    }

    /// <summary>
    /// 把图库里的目录收进索引库（增量，跑过一次以后再点基本是秒完成）。
    /// </summary>
    private async Task EnsureIndexedAsync()
    {
        var folders = LibraryStore.Load();
        if (folders.Count == 0 && _currentFolder is not null) folders.Add(_currentFolder);

        if (folders.Count == 0)
        {
            ShowEmpty("图库还是空的 —— 先用右上角的「+」把放照片的文件夹加进来");
            return;
        }

        ShowEmpty("第一次用这个分类，正在整理图库…");

        var progress = new Progress<IndexReport>(r =>
            EmptyText.Text = $"正在整理图库…　已处理 {r.Total} 张（新增 {r.Added}）");

        var report = await LibraryIndexService.Shared.IndexAsync(folders, recursive: true, progress);

        StartupLog.Write(
            $"BrowserPage: 整理图库完成 新增{report.Added} 更新{report.Updated} " +
            $"跳过{report.Skipped} 失败{report.Failed} 清理{report.Removed}");
    }

    /// <summary>
    /// 左上角"整理图库"按钮：正在整理时点它是"停止"，否则是"整理一遍 + 刷新"。
    /// </summary>
    private async void IndexButton_Click(object sender, RoutedEventArgs e)
        => await IndexActionAsync();

    private async Task IndexActionAsync()
    {
        var svc = LibraryIndexService.Shared;

        if (svc.IsIndexing)
        {
            svc.Cancel();
            StartupLog.Write("BrowserPage: 用户中止整理图库");
            return;
        }

        await EnsureIndexedAsync();

        BuildTree();
        if (IndexMode) await LoadFromIndexAsync();
    }

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
        BuildTree();
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
            // 先读这张图的非破坏性编辑参数（走内存映射，不查库、不抢锁）。
            // 墙要按它出图 + 打角标：编辑不改原文件，不主动套参数的话，
            // 用户在查看器里转了半天，退回来看到墙上还是老样子。
            PhotoEdits edits = LibraryIndexService.Shared.EditsOf(item.Path);

            if (edits.IsIdentity)
            {
                item.Edited = false;
                item.EditSummary = "";
            }
            else
            {
                item.Edited = true;
                item.EditSummary = edits.Describe();

                // 只在"这张确实编辑过"时打一行。黑匣子里能看出
                // "墙上到底按参数出图了没有"—— 这类"看着像没生效"的问题，
                // 光看界面是分不清"没渲染"还是"渲染了但参数本身就是这个样子"的。
                StartupLog.Write(
                    $"图墙：按编辑参数出图 → {System.IO.Path.GetFileName(item.Path)}（{item.EditSummary}）");
            }

            var bitmap = await _thumbs.GetAsync(item.Path, ThumbDecodeSize, edits, CancellationToken.None);
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

    /// <summary>
    /// 某张图的编辑参数变了 —— 把墙上对应的那一格换成新样子。
    ///
    /// 谁触发的：看图窗口（PhotoWindow）里的旋转 / 翻转 / 裁剪 / 调色 / 还原。
    /// 看图窗口和缩略图墙是两个窗口，主窗口对那边干了什么一无所知，
    /// 所以靠 <see cref="LibraryIndexService.EditsChanged"/> 这根线通知过来。
    ///
    /// 这里刻意**不清空旧缩略图**：新图算好之前那个位置留着旧画面，
    /// 用户看不到"闪一下变占位图标"，只有一百来毫秒后悄悄换成新样子。
    /// 滚出屏幕的格子会被虚拟化回收，下次出现时自然按新参数重新解 ——
    /// 所以这里只处理"当前就在墙上"的那些。
    /// </summary>
    private void OnEditsChanged(string path)
    {
        // 事件是从改编辑的那个地方发出来的（现在是 UI 线程，但不写死这个假设），
        // 排队回本页的 UI 线程再碰控件
        if (!DispatcherQueue.TryEnqueue(() => RefreshEditedTile(path)))
        {
            StartupLog.Write($"BrowserPage: 编辑通知排队失败（已在关闭中？）→ {path}");
        }
    }

    /// <summary>挂钩（自锁，重复调用不会挂两遍）。</summary>
    private void HookEditsChanged()
    {
        if (_editsHooked) return;
        LibraryIndexService.EditsChanged += OnEditsChanged;
        _editsHooked = true;
    }

    /// <summary>摘钩。</summary>
    private void UnhookEditsChanged()
    {
        if (!_editsHooked) return;
        LibraryIndexService.EditsChanged -= OnEditsChanged;
        _editsHooked = false;
    }

    /// <summary>是不是已经挂上 <see cref="LibraryIndexService.EditsChanged"/> 了。</summary>
    private bool _editsHooked;

    private void RefreshEditedTile(string path)
    {
        try
        {
            // 内存缓存里的旧图（原图那份和所有编辑变体）全部作废。
            // 不丢的话下一句照样把旧图取回来，改了等于没改。
            _thumbs.Invalidate(path);

            ThumbnailItem? item = null;
            foreach (var it in _items)
            {
                if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    item = it;
                    break;
                }
            }

            // 不在当前这一屏里（换过目录、或在别的分类下）就到此为止：
            // 参数已经落库了，等它下次上墙时 LoadThumbAsync 会读到新的
            if (item is null) return;

            PhotoEdits edits = LibraryIndexService.Shared.EditsOf(path);
            item.Edited = !edits.IsIdentity;
            item.EditSummary = edits.Describe();

            // 解除"已经排队过"的封印，让它重新走一遍加载
            item.LoadRequested = false;
            _ = LoadThumbAsync(item);

            StartupLog.Write(edits.IsIdentity
                ? $"图墙：刷新已还原的格子 → {System.IO.Path.GetFileName(path)}"
                : $"图墙：刷新编辑后的格子 → {System.IO.Path.GetFileName(path)}（{edits.Describe()}）");
        }
        catch (Exception ex)
        {
            StartupLog.Write("BrowserPage: 刷新编辑后的格子失败", ex);
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
        // 查找重复模式：点一下 = 在这组里切换"保留"那张（不走选中/打开那套逻辑）
        if (_dupMode != DupMode.None)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThumbnailItem di) ToggleDupKeep(di);
            return;
        }

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

    // 非打包程序里调系统选择器必须先把窗口句柄喂进去，否则一闪就关。
    // 这个 COM 接口的 IID 是固定的，直接按 GUID 声明最稳（不同 WinRT 版本里
    // 它的 C# 投影类型名字可能不一样，自己声明就不依赖那个名字）。
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3E68D4BD-7135-4D10-8015-9FBF3936F305")]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    // ===== 批量处理（路线图第 7 步）=====

    private CancellationTokenSource? _batchCts;
    private string? _batchOutputDir;
    private bool _batchRunning;

    private void BatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSet.Count == 0)
        {
            BatchStatus.Text = "请先勾选至少一张图。";
            return;
        }
        OpCombo_SelectionChanged(OpCombo, null); // 同步一次分组可见性
        BatchPanel.Visibility = Visibility.Visible;
        StartupLog.Write("BrowserPage: 打开批量处理面板");
    }

    private void BatchCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRunning) return; // 跑着时不让关，避免状态乱
        BatchPanel.Visibility = Visibility.Collapsed;
    }

    private void OpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 构造过程中 ComboBox 的选中项一落地就会触发本事件，
        // 那时排在后面的分组（GpTone 等）还没实例化出来，全是 null，直接访问会炸。
        // 所以这里先做一次空检查——这不是防御过度，是必须的。
        if (GpRotate is null || GpTone is null || GpResize is null || GpConvert is null || GpRename is null)
            return;

        var tag = (OpCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        GpRotate.Visibility  = tag == "RotateFlip" ? Visibility.Visible : Visibility.Collapsed;
        GpTone.Visibility    = tag == "Tone"       ? Visibility.Visible : Visibility.Collapsed;
        GpResize.Visibility  = tag == "Resize"     ? Visibility.Visible : Visibility.Collapsed;
        GpConvert.Visibility = tag == "Convert"    ? Visibility.Visible : Visibility.Collapsed;
        GpRename.Visibility  = tag == "Rename"     ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PickDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(App.Instance!);
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add("*");
            picker.As<IInitializeWithWindow>().Initialize(hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                _batchOutputDir = folder.Path;
                DirText.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            BatchStatus.Text = "选择目录失败：" + ex.Message;
        }
    }

    private async void RunBatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRunning) return;
        if (_selectedSet.Count == 0) { BatchStatus.Text = "没有选中的图。"; return; }

        var opt = BuildBatchOptions();
        if (opt is null) return; // 参数有误，已在状态栏提示

        var list = _selectedSet.Select(it => it.Path).ToList();
        if (list.Count == 0) { BatchStatus.Text = "没有选中的图。"; return; }

        _batchRunning = true;
        RunBatchButton.IsEnabled = false;
        BatchProgress.Visibility = Visibility.Visible;
        BatchProgress.Value = 0;
        BatchStatus.Text = $"正在处理 0 / {list.Count} …";

        _batchCts = new CancellationTokenSource();
        var progress = new Progress<BatchProgress>(p =>
        {
            BatchProgress.Value = list.Count == 0 ? 0 : (double)p.Done / list.Count;
            BatchStatus.Text = p.LastWasError
                ? $"第 {p.Done}/{p.Total} 张：{p.CurrentFile} 失败（{p.LastError}）"
                : $"正在处理 {p.Done} / {p.Total} …";
        });

        try
        {
            BatchResult result = await BatchEditService.RunAsync(list, opt, progress, _batchCts.Token);
            BatchStatus.Text = $"完成：成功 {result.Succeeded} 张，失败 {result.Failed} 张。"
                + (result.Failed > 0 ? " 失败的见日志。" : "");
            StartupLog.Write($"批量处理完成：成功 {result.Succeeded} / 失败 {result.Failed}（共 {result.Total}）");
        }
        catch (OperationCanceledException)
        {
            BatchStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            BatchStatus.Text = "批处理出错：" + ex.Message;
        }
        finally
        {
            _batchRunning = false;
            RunBatchButton.IsEnabled = true;
            if (list.Count > 0) BatchProgress.Value = 1;
            // 重新扫描当前目录：新生成的批量文件要出现在墙上，被重命名的原图也要消失
            if (_currentFolder is not null)
            {
                try { await LoadFolderAsync(_currentFolder); } catch { }
            }
        }
    }

    private BatchOptions? BuildBatchOptions()
    {
        var op = (OpCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var opt = new BatchOptions
        {
            OutputDirectory = _batchOutputDir,
            Suffix = SuffixBox.Text,
            Overwrite = OverwriteCheck.IsChecked == true,
        };

        switch (op)
        {
            case "RotateFlip":
                opt.Operation = BatchOperation.RotateFlip;
                opt.Rotation = int.Parse((RotCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0");
                opt.FlipH = FlipHToggle.IsChecked == true;
                opt.FlipV = FlipVToggle.IsChecked == true;
                break;
            case "Tone":
                opt.Operation = BatchOperation.Tone;
                var look = new LookSettings();
                look.Brightness = (int)ToneBright.Value;
                look.Contrast = (int)ToneContrast.Value;
                look.Saturation = (int)ToneSat.Value;
                opt.Look = look;
                break;
            case "Resize":
                opt.Operation = BatchOperation.Resize;
                if (!int.TryParse(ResizeBox.Text, out int le) || le <= 0)
                {
                    BatchStatus.Text = "长边需为正整数。"; return null;
                }
                opt.LongEdge = le;
                break;
            case "Convert":
                opt.Operation = BatchOperation.Convert;
                opt.TargetExtension = (FmtCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ".png";
                break;
            case "Rename":
                opt.Operation = BatchOperation.Rename;
                opt.RenamePattern = string.IsNullOrWhiteSpace(RenameBox.Text) ? "{n}_{name}" : RenameBox.Text;
                break;
            default:
                BatchStatus.Text = "请选择操作。"; return null;
        }

        // 既不改后缀也不换目录 = 输出会覆盖原图，除非明确允许
        if (opt.Operation != BatchOperation.Rename
            && string.IsNullOrWhiteSpace(opt.Suffix)
            && _batchOutputDir is null
            && !opt.Overwrite)
        {
            BatchStatus.Text = "请填写后缀或选择输出目录，否则会覆盖原图。";
            return null;
        }

        return opt;
    }



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

    // ===== 排序（只管右侧这一屏的顺序） =====

    /// <summary>排序菜单里的六个选项。XAML 解析期可能有几个还是 null，所以是 nullable。</summary>
    private ToggleButton?[] SortToggles()
        => new[]
        {
            SortDefaultToggle, SortNameAscToggle, SortNameDescToggle,
            SortDateDescToggle, SortDateAscToggle, SortSizeDescToggle,
        };

    /// <summary>
    /// 同步勾选状态时置起这个标记，避免"设 IsChecked"反过来又触发一次重新载入。
    /// 启动时恢复上次设置会走这条路 —— 那会儿可不该再读一遍目录。
    /// </summary>
    private bool _sortUiSyncing;

    private void SortToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_sortUiSyncing) return;
        if (sender is not ToggleButton tb) return;

        // 六个互斥：勾上这个就把其余的取消
        foreach (var t in SortToggles())
            if (t is not null && !ReferenceEquals(t, tb)) t.IsChecked = false;

        SortKey? pick = null;
        if (tb.Tag is string tag && Enum.TryParse<SortKey>(tag, out SortKey k)) pick = k;

        // 同 SetLayout：解析期 XAML 还没建完，别去碰别的控件
        if (Thumbs is null) return;

        if (pick == _sortOverride) return;
        _sortOverride = pick;

        AppSettings.Set("SortKey", pick?.ToString() ?? string.Empty);
        StartupLog.Write($"BrowserPage: 排序 = {pick?.ToString() ?? "默认（跟随分类）"}");

        _ = ReloadCurrentViewAsync();
    }

    /// <summary>
    /// 已经勾着的那个再点一下会被取消勾选，结果六个全空、看着很懵。
    /// 排序任何时候都得有一个生效值，所以这里把它按回去。
    /// </summary>
    private void SortToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_sortUiSyncing) return;
        if (sender is not ToggleButton tb) return;

        foreach (var t in SortToggles())
            if (t is not null && t.IsChecked == true) return;   // 还有别人勾着，不管

        _sortUiSyncing = true;
        try { tb.IsChecked = true; } finally { _sortUiSyncing = false; }
    }

    /// <summary>把菜单里的勾选对齐到当前生效的排序（启动时恢复设置用）。</summary>
    private void SyncSortUi()
    {
        _sortUiSyncing = true;
        try
        {
            SortKey? cur = _sortOverride;

            SetToggle(SortDefaultToggle, cur is null);
            SetToggle(SortNameAscToggle, cur == SortKey.FileNameAsc);
            SetToggle(SortNameDescToggle, cur == SortKey.FileNameDesc);
            SetToggle(SortDateDescToggle, cur == SortKey.DateTakenDesc);
            SetToggle(SortDateAscToggle, cur == SortKey.DateTakenAsc);
            SetToggle(SortSizeDescToggle, cur == SortKey.FileSizeDesc);
        }
        finally { _sortUiSyncing = false; }
    }

    private static void SetToggle(ToggleButton? tb, bool on)
    {
        if (tb is not null) tb.IsChecked = on;
    }

    /// <summary>按当前条件重新铺一屏（换了排序之后用）。</summary>
    private async Task ReloadCurrentViewAsync()
    {
        if (IndexMode)
        {
            // 只重查右侧，不重建左边的树 —— 重建会把用户选中的分组高亮弄丢
            await LoadFromIndexAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentFolder))
            await LoadFolderAsync(_currentFolder);
    }

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
        e.DragUIOverride.Caption = "用 CelesteGallery 打开";
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
