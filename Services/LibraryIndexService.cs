using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteGallery.Services;

/// <summary>
/// 图库索引的"前台接待"。
///
/// 为什么不直接在界面里 new 一个 <see cref="MediaIndex"/> 用：
///   1. 它握着一条 SQLite 连接，整个进程开一条就够，多了互相抢锁。
///   2. 后台扫描得能取消、能去重 —— 用户连点几下不该跑起好几个扫描。
///   3. 界面只想要"这一屏的数据"，不该关心事务、进度回报这些细节。
///
/// 一句话：<see cref="MediaIndex"/> 管"库里有什么"，这一层管"界面什么时候能拿到"。
/// </summary>
public sealed class LibraryIndexService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    private readonly Lazy<MediaIndex?> _lazy = new(
        () =>
        {
            try
            {
                return new MediaIndex();
            }
            catch (Exception ex)
            {
                // 索引库建不起来（磁盘没权限、文件被占用…）不该让程序起不来。
                // 退化成"没有索引"：界面照常按文件夹浏览，只是少了分类和搜索。
                StartupLog.Write("LibraryIndexService: 索引库打不开，退化为无索引模式", ex);
                return null;
            }
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static LibraryIndexService Shared { get; } = new();

    private LibraryIndexService() { }

    /// <summary>
    /// 底层索引。索引库没建成时是 null —— 调用方必须判空，
    /// 别拿到手就用（那时会 NullReferenceException）。
    /// </summary>
    public MediaIndex? Index => _lazy.Value;

    /// <summary>索引库能不能用。界面拿它决定要不要显示"分类/搜索"这些入口。</summary>
    public bool Available => _lazy.Value is not null;

    /// <summary>库里目前有多少条记录。</summary>
    public int Count
    {
        get
        {
            try { return _lazy.Value?.Count ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>是不是正在后台扫描。</summary>
    public bool IsIndexing => _cts is not null;

    // ===== 扫描 =====

    /// <summary>
    /// 把一批目录收进索引。已经在扫的时候直接返回，不会起第二个任务。
    ///
    /// 增量是这里能成立的关键：文件"大小 + 修改时间"没变就不重读 EXIF，
    /// 所以第二次扫同一个目录几乎是瞬间返回（实测 5000 条约 2 ms）。
    /// </summary>
    public async Task<IndexReport> IndexAsync(
        IEnumerable<string> folders,
        bool recursive,
        IProgress<IndexReport>? progress = null)
    {
        var total = new IndexReport();
        var index = Index;
        if (index is null) return total;

        // 拿不到锁 = 已经在扫了。这时不排队等待：
        // 排着的用户要等到上一次扫完才知道发生了什么，体感比"稍后再点"更差
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return total;

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            foreach (string folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                cts.Token.ThrowIfCancellationRequested();

                var report = await index.IndexFolderAsync(folder, recursive, progress, cts.Token)
                                        .ConfigureAwait(false);

                total.Added += report.Added;
                total.Updated += report.Updated;
                total.Skipped += report.Skipped;
                total.Failed += report.Failed;
                total.Removed += report.Removed;
            }
        }
        catch (OperationCanceledException)
        {
            StartupLog.Write("LibraryIndexService: 索引已取消");
        }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 索引失败", ex);
        }
        finally
        {
            _cts = null;
            _gate.Release();
        }

        return total;
    }

    /// <summary>停掉正在跑的扫描。</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 取消失败", ex); }
    }

    // ===== 查询 =====

    /// <summary>按条件取一批文件路径。索引不可用时返回空表（界面会退回扫盘模式）。</summary>
    public List<string> Query(MediaQuery q)
    {
        try { return Index?.Query(q) ?? new List<string>(); }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 查询失败", ex);
            return new List<string>();
        }
    }

    /// <summary>按维度分组，给左侧树用。</summary>
    public List<GroupEntry> Group(GroupBy by, MediaQuery? filter = null)
    {
        try { return Index?.Group(by, filter) ?? new List<GroupEntry>(); }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 分组失败", ex);
            return new List<GroupEntry>();
        }
    }

    /// <summary>
    /// 把路径列表按"文件名自然序"重排。
    ///
    /// 为什么需要它：SQLite 排文件名是字典序，于是 IMG_10 会排在 IMG_2 前面 ——
    /// 而扫盘那条老路径用的是 FolderIndex 的自然序（数字按大小比），
    /// 两条路径给出的顺序不一样，用户切换分类时会觉得"图的顺序乱了"。
    /// 索引查出来的条数最多两万，内存重排几毫秒的事，不值得为此在 SQL 里造轮子。
    /// </summary>
    // ===== 评分 / 收藏 / 标签 =====
    //
    // 这三个都是**用户数据**：图库重扫一遍不能把它们弄丢。
    // MediaIndex 那边已经保证了（Upsert 默认不动评分、升表结构只加列），
    // 这一层只负责"索引不可用时别崩"—— 索引打不开时全部返回默认值，
    // 宁可暂时用不了，也不能让右键菜单弹个异常出来。

    public int RatingOf(string path)
    {
        try { return Index?.GetRating(path) ?? 0; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 读评分失败", ex); return 0; }
    }

    public void SetRating(string path, int rating)
    {
        try { Index?.SetRating(path, rating); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 写评分失败", ex); }
    }

    public bool IsFavorite(string path)
    {
        try { return Index?.IsFavorite(path) ?? false; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 读收藏失败", ex); return false; }
    }

    /// <summary>切换收藏，返回切换之后的状态（菜单文案要用）。</summary>
    public bool ToggleFavorite(string path)
    {
        try
        {
            var index = Index;
            if (index is null) return false;

            bool on = !index.IsFavorite(path);
            index.SetFavorite(path, on);
            return on;
        }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 写收藏失败", ex);
            return false;
        }
    }

    public int CountFavorites()
    {
        try { return Index?.CountFavorites() ?? 0; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 统计收藏失败", ex); return 0; }
    }

    public List<string> GetTags(string path)
    {
        try { return Index?.GetTags(path) ?? new List<string>(); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 读标签失败", ex); return new List<string>(); }
    }

    /// <summary>整批替换标签。输入可以是"旅行, 家人"这种一行字，也可以是一个列表。</summary>
    public void SetTags(string path, IEnumerable<string> tags)
    {
        try { Index?.SetTags(path, tags); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 写标签失败", ex); }
    }

    public List<TagEntry> AllTags()
    {
        try { return Index?.AllTags() ?? new List<TagEntry>(); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 列标签失败", ex); return new List<TagEntry>(); }
    }

    // ===== 编辑参数（路线图第 6 步）=====
    //
    // 和评分 / 收藏 / 标签同类：都是**用户的操作**，重扫磁盘不该丢。
    // 索引不可用时返回"没编辑过"（空参数），宁可暂时显示原图，也不能崩。
    //
    // 为什么不每次直接查库：缩略图墙每一格都要知道"这张改过没有、改成什么样"。
    // 一屏几十格、来回滚动就是成百上千次单行查询，而库那边所有操作共用一把锁 ——
    // 后台正在索引时，墙的每一次查询都要和它排队，表现就是滚动发涩。
    // 所以这里放一份**只装编辑过的图**的内存映射（正常情况是零条或几条），
    // 一次批量读进来之后全是 O(1) 的字典查找，不碰库也不抢锁。

    /// <summary>路径 → 编辑参数。只装"确实编辑过"的（<c>IsIdentity</c> 的不进来）。</summary>
    private Dictionary<string, PhotoEdits>? _editsMap;

    private readonly object _editsSync = new();

    /// <summary>
    /// 某张图的编辑参数变了（存了新的，或者被清除）。
    ///
    /// 为什么需要这个事件：看图是**另一个窗口**（PhotoWindow），
    /// 用户在那边转了个方向，主窗口的缩略图墙完全不知情，
    /// 于是墙上还是老样子 —— 用户会以为"编辑没生效"。
    /// 有了它，缩略图墙就能当场把那一格换掉。
    ///
    /// 参数是图片路径。触发点在 UI 线程（编辑操作都从界面发起），
    /// 但订阅方仍应假设自己不在 UI 线程上，自己排一次队再碰控件。
    /// </summary>
    public static event Action<string>? EditsChanged;

    /// <summary>
    /// 读一张图的非破坏性编辑参数。没编辑过（或读不到）返回空参数 ——
    /// 空参数的 <c>IsIdentity</c> 为真，渲染时会直接跳过整条流水线。
    ///
    /// 返回的是**副本**：调用方（查看器）拿到手就会就地改，
    /// 交出映射里那个对象的话，"改一下"会把缓存里的存档值一起改掉。
    /// </summary>
    public PhotoEdits EditsOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return new PhotoEdits();

        try
        {
            var map = EditsMap();
            return map.TryGetValue(path, out var e) ? e.Clone() : new PhotoEdits();
        }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 读编辑参数失败", ex);
            return new PhotoEdits();
        }
    }

    /// <summary>
    /// 写编辑参数。传"等于没改"的参数（或 null）等于**清除**，库里那一列变 NULL。
    /// 存之前会过一遍 <see cref="PhotoEdits.Normalized"/> —— 保证库里永远是合法形态。
    ///
    /// 写完顺手把内存映射对齐并发出 <see cref="EditsChanged"/>，
    /// 这样缩略图墙不用自己去轮询"用户刚才改了什么"。
    /// </summary>
    public void SetEdits(string path, PhotoEdits? edits)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            PhotoEdits? normalized = edits?.Normalized();
            if (normalized is null || normalized.IsIdentity) normalized = null;

            Index?.SetEdits(path, normalized);

            lock (_editsSync)
            {
                // 已经加载过才维护 —— 没加载的话下次 EditsMap() 会从库里读到最新值，
                // 没必要为了"写一次"先把整张表拉一遍
                if (_editsMap is not null)
                {
                    if (normalized is null) _editsMap.Remove(path);
                    else _editsMap[path] = normalized;
                }
            }

            EditsChanged?.Invoke(path);
        }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 写编辑参数失败", ex);
        }
    }

    /// <summary>这张图有没有编辑过（列表上打"已编辑"角标用）。</summary>
    public bool HasEdits(string path)
    {
        try { return !string.IsNullOrEmpty(path) && EditsMap().ContainsKey(path); }
        catch { return false; }
    }

    /// <summary>整个库里有几张图编辑过。</summary>
    public int CountEdited()
    {
        try { lock (_editsSync) return EditsMap().Count; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 统计编辑失败", ex); return 0; }
    }

    /// <summary>
    /// 提前把编辑参数读进内存。
    ///
    /// 铺第一屏缩略图时几十个格子会**同时**问"这张改过没有"，
    /// 第一次问会触发一次全表扫描。让它落在后台线程上，
    /// 而不是卡在铺图的那一下（用户感知就是"进目录要顿一瞬"）。
    /// 失败无所谓 —— 真用到时 EditsMap 会自己再试一次。
    /// </summary>
    public void WarmEditsCache()
    {
        _ = Task.Run(() =>
        {
            try { lock (_editsSync) EditsMap(); }
            catch (Exception ex) { StartupLog.Write("LibraryIndexService: 预热编辑参数失败", ex); }
        });
    }

    /// <summary>
    /// 懒加载那个映射。只在第一次查一次库。
    ///
    /// 库里的值进来还要 <see cref="PhotoEdits.Parse"/> 一遍：存的是文本、外面要的是对象。
    /// 顺手把解析出来等于"没改"的条目剔掉 —— 手工改过的库、旧版本写下的空壳，
    /// 都不该让墙上多出一个角标。
    /// </summary>
    private Dictionary<string, PhotoEdits> EditsMap()
    {
        lock (_editsSync)
        {
            if (_editsMap is not null) return _editsMap;

            var map = new Dictionary<string, PhotoEdits>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var raw = Index?.LoadAllEdits();
                if (raw is not null)
                {
                    foreach (var kv in raw)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;

                        PhotoEdits parsed = PhotoEdits.Parse(kv.Value);
                        if (parsed.IsIdentity) continue;

                        map[kv.Key] = parsed;
                    }
                }

                StartupLog.Write($"索引：读入 {map.Count} 张已编辑图片的参数");
            }
            catch (Exception ex)
            {
                // 读不到就当"都没有编辑过"：墙上显示原图，比弹个错误强
                StartupLog.Write("LibraryIndexService: 批量读编辑参数失败", ex);
            }

            _editsMap = map;
            return map;
        }
    }

    // ===== 重复 / 相似 =====

    /// <summary>找精确重复组（MD5 相同）。索引不可用时返回空列表。</summary>
    public List<DuplicateGroup> FindDuplicates()
    {
        try { return Index?.FindDuplicates() ?? new List<DuplicateGroup>(); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 查重复失败", ex); return new List<DuplicateGroup>(); }
    }

    /// <summary>找视觉相似组（PHash 汉明距离 ≤ 阈值）。索引不可用时返回空列表。</summary>
    public List<SimilarGroup> FindSimilar(int threshold = 10)
    {
        try { return Index?.FindSimilar(threshold) ?? new List<SimilarGroup>(); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 查相似失败", ex); return new List<SimilarGroup>(); }
    }

    /// <summary>有多少组精确重复（智能相册入口红点用）。</summary>
    public int CountDuplicates()
    {
        try { return Index?.CountDuplicates() ?? 0; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 统计重复失败", ex); return 0; }
    }

    /// <summary>有多少组相似（默认阈值）。</summary>
    public int CountSimilar(int threshold = 10)
    {
        try { return Index?.CountSimilar(threshold) ?? 0; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 统计相似失败", ex); return 0; }
    }

    /// <summary>给老库 / 没算过指纹的记录补算指纹（"查找重复"首次打开时调）。索引不可用返回 0。</summary>
    public Task<int> BackfillHashesAsync(bool computePhash = true, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        try { return Index?.BackfillHashesAsync(computePhash, progress, ct) ?? Task.FromResult(0); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 补算指纹失败", ex); return Task.FromResult(0); }
    }

    /// <summary>把一批路径从索引里删掉（"查找重复"里把图移到回收站后用，免得它们又冒出来）。</summary>
    public void RemovePaths(IEnumerable<string> paths)
    {
        try { Index?.RemoveMany(paths); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 删除索引记录失败", ex); }
    }

    /// <summary>
    /// 把"旅行, 家人" / "旅行 家人" / "旅行；家人" 这种一行输入拆成标签列表。
    /// 用户不会乖乖只用一个分隔符，逗号空格分号全都会混着来。
    /// </summary>
    public static List<string> ParseTags(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (string part in text!.Split(new[] { ',', '，', ';', '；', '\n', '\r' },
                                            StringSplitOptions.RemoveEmptyEntries))
        {
            string t = part.Trim();
            if (t.Length > 0 && !list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
        }

        return list;
    }

    public static List<string> SortNatural(IEnumerable<string> paths, bool descending = false)
    {
        var list = paths.ToList();

        list.Sort((a, b) =>
        {
            int c = FolderIndex.CompareNatural(
                Path.GetFileName(a),
                Path.GetFileName(b));
            return descending ? -c : c;
        });

        return list;
    }
}
