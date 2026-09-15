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

    /// <summary>
    /// 读一张图的非破坏性编辑参数。没编辑过（或读不到）返回空参数 ——
    /// 空参数的 <c>IsIdentity</c> 为真，渲染时会直接跳过整条流水线。
    /// </summary>
    public PhotoEdits EditsOf(string path)
    {
        try { return Index?.GetEdits(path) ?? new PhotoEdits(); }
        catch (Exception ex)
        {
            StartupLog.Write("LibraryIndexService: 读编辑参数失败", ex);
            return new PhotoEdits();
        }
    }

    /// <summary>
    /// 写编辑参数。传"等于没改"的参数（或 null）等于**清除**，库里那一列变 NULL。
    /// 存之前会过一遍 <see cref="PhotoEdits.Normalized"/> —— 保证库里永远是合法形态。
    /// </summary>
    public void SetEdits(string path, PhotoEdits? edits)
    {
        try { Index?.SetEdits(path, edits); }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 写编辑参数失败", ex); }
    }

    /// <summary>这张图有没有编辑过（列表上打"已编辑"角标用）。</summary>
    public bool HasEdits(string path)
    {
        try { return !(Index?.GetEdits(path) ?? new PhotoEdits()).IsIdentity; }
        catch { return false; }
    }

    /// <summary>整个库里有几张图编辑过。</summary>
    public int CountEdited()
    {
        try { return Index?.CountEdited() ?? 0; }
        catch (Exception ex) { StartupLog.Write("LibraryIndexService: 统计编辑失败", ex); return 0; }
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
