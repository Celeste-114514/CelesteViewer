using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteViewer.Services;

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
