using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace CelesteViewer.Services;

/// <summary>
/// 媒体种类。
///
/// 现在只有图片真正写进库里，但**字段从第一天就留好**：
/// 视频的缩略图、文件信息、按日期归组，跟图片是同一套逻辑，
/// 将来要收视频只是把这些值换一换，表和查询都不用动。
/// </summary>
public enum MediaKind
{
    Image = 0,
    Video = 1,
}

/// <summary>左侧树"按什么分"。每加一个值，界面上就多一种分类，别的都不用改。</summary>
public enum GroupBy
{
    Folder,
    Date,
    Camera,
    Lens,
    Rating,
}

/// <summary>右侧墙"按什么排"。</summary>
public enum SortKey
{
    DateTakenDesc,
    DateTakenAsc,
    FileNameAsc,
    FileNameDesc,
    FileSizeDesc,
    AddedDesc,
}

/// <summary>
/// 一次查询的全部条件。
///
/// 刻意做成一个纯数据对象：左侧树给一个"分组值"，搜索框给一段文字，
/// 排序菜单给一个 SortKey，三者叠在一起就是一次查询。
/// 界面上每加一个筛选入口，都只是给这里加一个字段。
/// </summary>
public sealed class MediaQuery
{
    /// <summary>搜索文字。会去比文件名、相机型号、镜头型号。</summary>
    public string? Text { get; init; }

    /// <summary>限定在某个目录（含它的子目录）里。传 null 表示整个图库。</summary>
    public string? FolderPrefix { get; init; }

    /// <summary>
    /// 当前选中的分组值。比如按日期分，这里就是 "2026-09"；
    /// 按相机分，这里就是 "Canon EOS R6"。
    /// 它的含义由 <see cref="Group"/> 决定，查询时按同一个维度去筛。
    /// </summary>
    public string? GroupValue { get; init; }

    /// <summary>和 GroupValue 配套的维度。只填 GroupValue 不填这个，等于没筛。</summary>
    public GroupBy Group { get; init; } = GroupBy.Folder;

    public int? MinRating { get; init; }
    public MediaKind? Kind { get; init; }
    public SortKey Sort { get; init; } = SortKey.DateTakenDesc;

    /// <summary>
    /// 最多返回多少条。
    ///
    /// 这是个安全阀：图库将来可能几万张，但一屏只显示得下几百张，
    /// 全捞出来再筛是浪费。真要做"全部"的场景（比如幻灯片）再放大它。
    /// </summary>
    public int Limit { get; init; } = 20000;
}

/// <summary>左侧树上的一个分组条目。</summary>
public sealed class GroupEntry
{
    /// <summary>机器用的值（"2026-09" / "Canon EOS R6" / 文件夹全路径）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>给人看的（"2026 年 9 月" / "Canon EOS R6" / "旅行"）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>这个组里有多少张。</summary>
    public int Count { get; init; }

    /// <summary>组里的第一张图，用来当这个组的封面。</summary>
    public string? CoverPath { get; init; }
}

/// <summary>一次扫描的结果。</summary>
public sealed class IndexReport
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Removed { get; set; }

    public int Total => Added + Updated + Skipped;
}

/// <summary>
/// 图库索引（SQLite）。
///
/// 为什么必须有它：
///   现在的做法是"点开哪个目录就扫哪个目录"，代价是图库只能按文件夹分。
///   想按日期、相机、评分来分，就得先知道"整个图库里都有什么"——
///   这件事问磁盘太慢（要挨个读 EXIF），只能先把元数据落到一张表里。
///   落表之后，所有分类维度都只是对这张表的不同查法。
///
/// 增量策略（这是它能不能用的关键）：
///   每次扫描只对比"文件大小 + 最后修改时间"，两个都没变就直接跳过，
///   不重新读 EXIF。所以第一次慢（几万张要几分钟），以后基本是秒开。
///
/// 线程：所有方法内部加锁，可以多线程调；但 SQLite 同一时刻只能一个写者，
/// 所以扫描这种大批量操作请走 <see cref="IndexFolderAsync"/>，它在后台线程上跑。
/// </summary>
public sealed class MediaIndex : IDisposable
{
    /// <summary>当前表结构版本。改了建表语句就加一，旧库会自动重建。</summary>
    private const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// 默认库文件位置：%LOCALAPPDATA%\CelesteViewer\library.db
    ///
    /// 放在本地应用数据目录而不是安装目录：安装目录在 Program Files 下
    /// 普通权限写不进去（之前做用户级安装包时踩过）。
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteViewer",
            "library.db");

    public MediaIndex(string? dbPath = null)
    {
        string path = dbPath ?? DefaultPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _conn.Open();
        EnsureSchema();
    }

    // ===== 建表 =====

    private void EnsureSchema()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();

            // user_version 是 SQLite 自带的一个整数位，拿它当结构版本号正好
            cmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

            if (version == SchemaVersion)
            {
                // 表已经是对的，但 WAL 这些连接级设置每次开库都要重设
                Exec("PRAGMA journal_mode=WAL;");
                Exec("PRAGMA synchronous=NORMAL;");
                return;
            }

            // 版本对不上（旧库或坏库）：整库重建。
            // 索引库是纯派生数据 —— 删了大不了重扫一遍，不值得为它写迁移代码。
            Exec("DROP TABLE IF EXISTS Media;");
            Exec("DROP TABLE IF EXISTS Meta;");

            Exec(@"
                CREATE TABLE Media (
                    Path          TEXT    PRIMARY KEY,
                    PathLower     TEXT    NOT NULL,
                    Directory     TEXT    NOT NULL,
                    FileName      TEXT    NOT NULL,
                    Kind          INTEGER NOT NULL DEFAULT 0,
                    FileSize      INTEGER NOT NULL DEFAULT 0,
                    ModifiedTicks INTEGER NOT NULL DEFAULT 0,
                    PixelWidth    INTEGER NOT NULL DEFAULT 0,
                    PixelHeight   INTEGER NOT NULL DEFAULT 0,
                    DateTaken     INTEGER NULL,
                    DateEstimated INTEGER NOT NULL DEFAULT 0,
                    CameraMake    TEXT    NULL,
                    CameraModel   TEXT    NULL,
                    LensModel     TEXT    NULL,
                    FNumber       TEXT    NULL,
                    ExposureTime  TEXT    NULL,
                    IsoSpeed      TEXT    NULL,
                    FocalLength   TEXT    NULL,
                    Rating        INTEGER NOT NULL DEFAULT 0,
                    IndexedAt     INTEGER NOT NULL DEFAULT 0
                );");

            // 按日期、按相机、按目录是界面上最常用的三种分法，给它们单独建索引。
            // PathLower 是给"文件搬过家"时做不区分大小写的匹配用的。
            Exec("CREATE INDEX IX_Media_DateTaken   ON Media(DateTaken);");
            Exec("CREATE INDEX IX_Media_CameraModel ON Media(CameraModel);");
            Exec("CREATE INDEX IX_Media_LensModel   ON Media(LensModel);");
            Exec("CREATE INDEX IX_Media_Directory   ON Media(Directory);");
            Exec("CREATE INDEX IX_Media_Rating      ON Media(Rating);");

            Exec($"PRAGMA user_version = {SchemaVersion};");
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ===== 写入 =====

    /// <summary>
    /// 判断一个文件需不需要重新读元数据。
    ///
    /// 只看"大小 + 修改时间"：这两个没变，内容就不可能变，
    /// 于是第二次扫描几万张图几乎是瞬间完成的。
    /// </summary>
    public bool NeedsUpdate(string path)
    {
        try
        {
            var io = new FileInfo(path);
            if (!io.Exists) return false;

            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    "SELECT ModifiedTicks, FileSize FROM Media WHERE Path = $p;";
                cmd.Parameters.AddWithValue("$p", path);

                using var r = cmd.ExecuteReader();
                if (!r.Read()) return true;           // 库里没有，得加
                if (r.IsDBNull(0)) return true;

                long ticks = r.GetInt64(0);
                long size = r.IsDBNull(1) ? 0 : r.GetInt64(1);

                return ticks != io.LastWriteTimeUtc.Ticks || size != io.Length;
            }
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 把一张图的元数据写进库（已存在就覆盖）。
    ///
    /// 不读磁盘、不读 EXIF —— 调用方先把 <see cref="PhotoInfo"/> 准备好。
    /// 这样索引逻辑和"怎么读一张图"是分开的，将来换探测方式不用动这里。
    /// </summary>
    public void Upsert(PhotoInfo info, MediaKind kind = MediaKind.Image, int rating = 0)
    {
        if (string.IsNullOrEmpty(info.Path)) return;

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Media (
                    Path, PathLower, Directory, FileName, Kind,
                    FileSize, ModifiedTicks, PixelWidth, PixelHeight,
                    DateTaken, DateEstimated, CameraMake, CameraModel, LensModel,
                    FNumber, ExposureTime, IsoSpeed, FocalLength,
                    Rating, IndexedAt)
                VALUES (
                    $path, $lower, $dir, $name, $kind,
                    $size, $ticks, $w, $h,
                    $date, $est, $make, $model, $lens,
                    $fnum, $exp, $iso, $focal,
                    $rating, $now)
                ON CONFLICT(Path) DO UPDATE SET
                    PathLower     = excluded.PathLower,
                    Directory     = excluded.Directory,
                    FileName      = excluded.FileName,
                    Kind          = excluded.Kind,
                    FileSize      = excluded.FileSize,
                    ModifiedTicks = excluded.ModifiedTicks,
                    PixelWidth    = excluded.PixelWidth,
                    PixelHeight   = excluded.PixelHeight,
                    DateTaken     = excluded.DateTaken,
                    DateEstimated = excluded.DateEstimated,
                    CameraMake    = excluded.CameraMake,
                    CameraModel   = excluded.CameraModel,
                    LensModel     = excluded.LensModel,
                    FNumber       = excluded.FNumber,
                    ExposureTime  = excluded.ExposureTime,
                    IsoSpeed      = excluded.IsoSpeed,
                    FocalLength   = excluded.FocalLength,
                    Rating        = excluded.Rating,
                    IndexedAt     = excluded.IndexedAt;";

            string dir = string.Empty;
            try { dir = Path.GetDirectoryName(info.Path) ?? string.Empty; } catch { }

            cmd.Parameters.AddWithValue("$path", info.Path);
            cmd.Parameters.AddWithValue("$lower", info.Path.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$dir", dir);
            cmd.Parameters.AddWithValue("$name", info.FileName);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$size", info.FileSize);
            cmd.Parameters.AddWithValue("$ticks", info.LastModified.UtcTicks);
            cmd.Parameters.AddWithValue("$w", info.PixelWidth);
            cmd.Parameters.AddWithValue("$h", info.PixelHeight);
            // 拍摄时间优先，没有就用文件修改时间兜底 —— 见 ResolveDate 的说明
            var (dateMs, estimated) = ResolveDate(info);

            cmd.Parameters.AddWithValue("$date", (object?)dateMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$est", estimated ? 1 : 0);
            cmd.Parameters.AddWithValue("$make", (object?)info.CameraMake ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", (object?)info.CameraModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lens", (object?)info.LensModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fnum", (object?)info.FNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", (object?)info.ExposureTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$iso", (object?)info.IsoSpeed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$focal", (object?)info.FocalLength ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rating", rating);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 这张图"算作什么时候的"。
    ///
    /// 为什么要兜底：只有相机/手机拍的照片才写 EXIF 拍摄时间。
    /// 截图、AI 生成图、还有将来要收进来的微信/QQ 缓存图，一张都没有 ——
    /// 实测本机 72 张图里 72 张都没有拍摄时间，那时"按日期"这个维度
    /// 整屏只剩一个"无日期"，等于白做。
    ///
    /// 文件修改时间是最合适的替补：它至少能说明"这张图大概什么时候出现在
    /// 电脑上"，对组织照片来说够用了。Lightroom、Windows 照片也是这么干的。
    ///
    /// 用 DateEstimated 记下"这个时间是猜的"，
    /// 将来要区分"真拍摄时间"和"推算时间"时有据可查。
    /// </summary>
    private static (long? Ms, bool Estimated) ResolveDate(PhotoInfo info)
    {
        if (info.DateTaken.HasValue)
            return (info.DateTaken.Value.ToUnixTimeMilliseconds(), false);

        if (info.LastModified != default)
            return (info.LastModified.ToUnixTimeMilliseconds(), true);

        return (null, true);
    }

    /// <summary>删掉一条记录（文件被删了、或从图库里移除了）。</summary>
    public void Remove(string path)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Media WHERE Path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 清掉磁盘上已经不存在的记录。
    ///
    /// 只对"从某个根目录下扫出来的"做清理 —— 不能无脑全表扫描去 File.Exists，
    /// 图库几万条时那是几万次磁盘查询。
    /// </summary>
    public int RemoveStale(IEnumerable<string> roots)
    {
        int removed = 0;

        lock (_gate)
        {
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;

                string prefix = root.TrimEnd(Path.DirectorySeparatorChar,
                                             Path.AltDirectorySeparatorChar);

                var dead = new List<string>();

                using (var cmd = _conn.CreateCommand())
                {
                    // 目录本身 + 它的所有子目录，两条前缀覆盖全
                    cmd.CommandText =
                        "SELECT Path FROM Media WHERE Directory = $d OR Directory LIKE $p;";
                    cmd.Parameters.AddWithValue("$d", prefix);
                    cmd.Parameters.AddWithValue("$p", prefix + Path.DirectorySeparatorChar + "%");

                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string p = r.GetString(0);
                        try { if (!File.Exists(p)) dead.Add(p); }
                        catch { dead.Add(p); }
                    }
                }

                if (dead.Count == 0) continue;

                using var tx = _conn.BeginTransaction();
                foreach (string p in dead)
                {
                    using var del = _conn.CreateCommand();
                    del.CommandText = "DELETE FROM Media WHERE Path = $p;";
                    del.Parameters.AddWithValue("$p", p);
                    removed += del.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        return removed;
    }

    /// <summary>清空整个索引。</summary>
    public void Clear()
    {
        lock (_gate) Exec("DELETE FROM Media;");
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Media;";
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }
    }

    // ===== 扫描 =====

    /// <summary>
    /// 把一个目录（可选含子目录）里的图片收进索引。
    ///
    /// 跑在后台线程上。每处理完一批就回报一次进度，
    /// 调用方可以拿它更新界面上的"正在整理…（1234/5000）"。
    /// </summary>
    public async Task<IndexReport> IndexFolderAsync(
        string folder,
        bool recursive,
        IProgress<IndexReport>? progress = null,
        CancellationToken ct = default)
    {
        var report = new IndexReport();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return report;

        var files = new List<string>();

        await Task.Run(() =>
        {
            // 复用 FolderIndex：它已经把"哪些扩展名算图片""递归几层""自然排序"
            // 这些事都做对了，没必要再写一遍。
            // 这里不设 maxFiles 上限 —— 索引是后台慢慢跑的，截断反而会漏图。
            var index = new FolderIndex();
            index.LoadFolder(folder, recursive, int.MaxValue, int.MaxValue,
                             includeArchives: false);
            files.AddRange(index.All);
        }, ct).ConfigureAwait(false);

        var decoder = new ImageDecodePipeline();
        int batch = 0;

        foreach (string path in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!NeedsUpdate(path))
                {
                    report.Skipped++;
                }
                else
                {
                    bool existed = Exists(path);

                    var info = await decoder.ProbeAsync(path, includeMetadata: true, ct)
                                            .ConfigureAwait(false);
                    if (info is null)
                    {
                        report.Failed++;
                    }
                    else
                    {
                        Upsert(info);
                        if (existed) report.Updated++; else report.Added++;
                    }
                }
            }
            catch
            {
                report.Failed++;
            }

            // 每 50 张回报一次。太频繁会让界面疯狂刷新，太少用户以为卡住了
            if (++batch >= 50)
            {
                batch = 0;
                progress?.Report(Clone(report));
            }
        }

        report.Removed = RemoveStale(new[] { folder });
        progress?.Report(Clone(report));
        return report;
    }

    private static IndexReport Clone(IndexReport r) => new()
    {
        Added = r.Added,
        Updated = r.Updated,
        Skipped = r.Skipped,
        Failed = r.Failed,
        Removed = r.Removed,
    };

    public bool Exists(string path)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Media WHERE Path = $p LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteScalar() is not null;
        }
    }

    // ===== 查询 =====

    /// <summary>
    /// 按条件查出一批文件路径，顺序已经排好，直接喂给缩略图墙。
    /// </summary>
    public List<string> Query(MediaQuery q)
    {
        var result = new List<string>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            var where = new List<string>();

            if (!string.IsNullOrWhiteSpace(q.Text))
            {
                string like = "%" + EscapeLike(q.Text!) + "%";
                where.Add("(FileName LIKE $t ESCAPE '\\' OR CameraModel LIKE $t ESCAPE '\\' OR LensModel LIKE $t ESCAPE '\\')");
                cmd.Parameters.AddWithValue("$t", like);
            }

            if (!string.IsNullOrEmpty(q.FolderPrefix))
            {
                string prefix = q.FolderPrefix!
                                  .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                where.Add("(Directory = $d OR Directory LIKE $dp ESCAPE '\\')");
                cmd.Parameters.AddWithValue("$d", prefix);
                cmd.Parameters.AddWithValue("$dp", prefix + Path.DirectorySeparatorChar + "%");
            }

            // 分组值 → 筛选条件。这里和 Group() 用的是同一套"值怎么来的"规则，
            // 两边必须一致，否则点了"2026 年 9 月"会筛出别的东西。
            Cond? groupCond = GroupCondition(q.Group, q.GroupValue);
            if (groupCond is not null)
            {
                where.Add(groupCond.cond);
                foreach (var kv in groupCond.parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value);
            }

            if (q.MinRating.HasValue)
            {
                where.Add("Rating >= $r");
                cmd.Parameters.AddWithValue("$r", q.MinRating.Value);
            }

            if (q.Kind.HasValue)
            {
                where.Add("Kind = $k");
                cmd.Parameters.AddWithValue("$k", (int)q.Kind.Value);
            }

            string sql = "SELECT Path FROM Media";
            if (where.Count > 0)
                sql += " WHERE " + string.Join(" AND ", where);

            sql += " ORDER BY " + OrderBy(q.Sort);
            sql += " LIMIT " + Math.Max(1, q.Limit) + ";";

            cmd.CommandText = sql;

            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// 按某个维度分组，给左侧树用。
    /// <paramref name="filter"/> 里的文字搜索、目录限定同样生效，
    /// 但 <see cref="MediaQuery.GroupValue"/> 会被忽略（分组时不能用自己筛自己）。
    /// </summary>
    public List<GroupEntry> Group(GroupBy by, MediaQuery? filter = null)
    {
        var result = new List<GroupEntry>();
        filter ??= new MediaQuery();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            var where = new List<string>();

            if (!string.IsNullOrWhiteSpace(filter.Text))
            {
                string like = "%" + EscapeLike(filter.Text!) + "%";
                where.Add("(FileName LIKE $t ESCAPE '\\' OR CameraModel LIKE $t ESCAPE '\\' OR LensModel LIKE $t ESCAPE '\\')");
                cmd.Parameters.AddWithValue("$t", like);
            }

            if (!string.IsNullOrEmpty(filter.FolderPrefix))
            {
                string prefix = filter.FolderPrefix!
                                  .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                where.Add("(Directory = $d OR Directory LIKE $dp ESCAPE '\\')");
                cmd.Parameters.AddWithValue("$d", prefix);
                cmd.Parameters.AddWithValue("$dp", prefix + Path.DirectorySeparatorChar + "%");
            }

            if (filter.Kind.HasValue)
            {
                where.Add("Kind = $k");
                cmd.Parameters.AddWithValue("$k", (int)filter.Kind.Value);
            }

            string expr = GroupExpression(by);
            string sql = $"SELECT {expr} AS G, COUNT(*), MIN(Path) FROM Media";

            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);

            sql += " GROUP BY G ORDER BY " + GroupOrder(by) + ";";

            cmd.CommandText = sql;

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                int count = r.GetInt32(1);
                string? cover = r.IsDBNull(2) ? null : r.GetString(2);

                result.Add(new GroupEntry
                {
                    Key = key,
                    Label = GroupLabel(by, key),
                    Count = count,
                    CoverPath = cover,
                });
            }
        }

        return result;
    }

    /// <summary>分组用的 SQL 表达式。改这里就等于改"值长什么样"。</summary>
    private static string GroupExpression(GroupBy by) => by switch
    {
        GroupBy.Folder => "Directory",
        // 没有拍摄时间的图单独归到 "无日期"，别让它们混进某个月里
        GroupBy.Date => "COALESCE(strftime('%Y-%m', DateTaken / 1000, 'unixepoch'), '')",
        GroupBy.Camera => "COALESCE(CameraModel, '')",
        GroupBy.Lens => "COALESCE(LensModel, '')",
        // 评分是整数列，这里统一转成文本再交给 reader.GetString，
        // 免得下面读的时候碰到整数列直接抛类型转换异常
        GroupBy.Rating => "CAST(Rating AS TEXT)",
        _ => "Directory",
    };

    private static string GroupOrder(GroupBy by) => by switch
    {
        // 最近的日期排最前。空值（无日期）自然排最后，正好
        GroupBy.Date => "G DESC",
        GroupBy.Rating => "G DESC",
        _ => "G ASC",
    };

    private static string GroupLabel(GroupBy by, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return by switch
            {
                GroupBy.Date => "无日期",
                GroupBy.Camera => "未知相机",
                GroupBy.Lens => "未知镜头",
                GroupBy.Rating => "未评分",
                _ => "(根目录)",
            };
        }

        if (by == GroupBy.Date && key.Length >= 7)
        {
            // "2026-09" → "2026 年 9 月"
            string y = key.Substring(0, 4);
            string m = key.Substring(5, 2).TrimStart('0');
            return $"{y} 年 {m} 月";
        }

        if (by == GroupBy.Folder)
        {
            try
            {
                string trimmed = key.TrimEnd(Path.DirectorySeparatorChar,
                                             Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
        }

        if (by == GroupBy.Rating)
            return new string('★', Math.Max(0, Math.Min(5, int.TryParse(key, out int n) ? n : 0)));

        return key;
    }

    private sealed class Cond
    {
        public string cond = string.Empty;
        public Dictionary<string, object> parameters = new();
    }

    /// <summary>把"分组值"翻译成 WHERE 条件。必须和 GroupExpression 对得上。</summary>
    private static Cond? GroupCondition(GroupBy by, string? value)
    {
        // 注意这里是"是 null 才跳过"，不是"是空串才跳过"：
        // 空串有实际含义 —— 它代表"无日期""未知相机""未评分"那一组，
        // 点它应该筛出那批图，而不是什么都不筛（那会变成列出全部）。
        if (value is null) return null;

        var c = new Cond();

        switch (by)
        {
            case GroupBy.Folder:
                c.cond = "Directory = $g";
                c.parameters["$g"] = value;
                return c;

            case GroupBy.Date:
                if (value.Length < 7)
                {
                    c.cond = "DateTaken IS NULL";
                    return c;
                }
                {
                    // 用区间比而不是比字符串前缀：
                    // 这样 DateTaken 上的索引能真正派上用场
                    c.cond = "DateTaken >= $gFrom AND DateTaken < $gTo";
                    c.parameters["$gFrom"] = MonthStartMs(value);
                    c.parameters["$gTo"] = MonthStartMs(NextMonth(value));
                    return c;
                }

            case GroupBy.Camera:
                c.cond = "COALESCE(CameraModel, '') = $g";
                c.parameters["$g"] = value;
                return c;

            case GroupBy.Lens:
                c.cond = "COALESCE(LensModel, '') = $g";
                c.parameters["$g"] = value;
                return c;

            case GroupBy.Rating:
                c.cond = "Rating = $g";
                c.parameters["$g"] = int.TryParse(value, out int r) ? r : 0;
                return c;

            default:
                return null;
        }
    }

    private static long MonthStartMs(string yyyyMm)
    {
        try
        {
            int year = int.Parse(yyyyMm.Substring(0, 4));
            int month = int.Parse(yyyyMm.Substring(5, 2));
            var dt = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
            return dt.ToUnixTimeMilliseconds();
        }
        catch
        {
            return 0;
        }
    }

    private static string NextMonth(string yyyyMm)
    {
        try
        {
            int year = int.Parse(yyyyMm.Substring(0, 4));
            int month = int.Parse(yyyyMm.Substring(5, 2));
            if (month >= 12) { year++; month = 1; } else { month++; }
            return $"{year:D4}-{month:D2}";
        }
        catch
        {
            return yyyyMm;
        }
    }

    private static string OrderBy(SortKey key) => key switch
    {
        // 没有拍摄时间的排最后，不要混在最前面
        SortKey.DateTakenDesc => "DateTaken IS NULL ASC, DateTaken DESC",
        SortKey.DateTakenAsc => "DateTaken IS NULL ASC, DateTaken ASC",
        SortKey.FileNameAsc => "FileName COLLATE NOCASE ASC",
        SortKey.FileNameDesc => "FileName COLLATE NOCASE DESC",
        SortKey.FileSizeDesc => "FileSize DESC",
        SortKey.AddedDesc => "IndexedAt DESC",
        _ => "DateTaken DESC",
    };

    /// <summary>
    /// 转义 LIKE 的通配符。
    /// 用户搜 "IMG_100" 里的下划线如果不转义，就会匹配到 "IMGX100"。
    /// </summary>
    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _disposed = true;
            try { _conn.Dispose(); } catch { }
        }
    }
}
