using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// 按"来源"分：这张图是从哪儿来的（微信 / QQ / 企业微信 / 本地）。
    /// 值在写入时由 <see cref="SocialCacheDetector.SourceOf"/> 从路径算出来，见 Upsert。
    /// </summary>
    Source,
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

    /// <summary>只要收藏的（"智能相册 → 收藏"用）。</summary>
    public bool FavoritesOnly { get; init; }

    /// <summary>只要打了这个标签的。大小写不敏感。</summary>
    public string? Tag { get; init; }

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

/// <summary>左栏"标签"一节里的一行：标签名 + 有多少张。</summary>
public sealed class TagEntry
{
    public string Tag { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>一组精确重复（MD5 相同）的图。</summary>
public sealed class DuplicateGroup
{
    /// <summary>这组共同的 MD5（调试/展示用）。</summary>
    public string? Md5 { get; init; }
    /// <summary>重复的完整路径列表。</summary>
    public List<string> Paths { get; init; } = new();
    public int Count => Paths.Count;
}

/// <summary>一组视觉相似的图（PHash 汉明距离 ≤ 阈值）。</summary>
public sealed class SimilarGroup
{
    public List<string> Paths { get; init; } = new();
    public int Count => Paths.Count;
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
    /// <summary>
    /// 当前表结构版本。改了建表语句就加一。
    ///
    /// 注意 v3 之后**不能再靠删表重建来升版**了 —— 库里开始存用户数据
    /// （评分 / 收藏 / 标签），删表等于把人家的评分悄悄清空。见 <see cref="MigrateFrom"/>。
    /// </summary>
    private const int SchemaVersion = 5;

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
            // WAL 这些是连接级设置，每次开库都要重设（它们不写进文件）
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");

            using var cmd = _conn.CreateCommand();

            // user_version 是 SQLite 自带的一个整数位，拿它当结构版本号正好
            cmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

            if (version == SchemaVersion) return;   // 已经是最新的，什么都不用做

            if (version == 0)
            {
                CreateSchema();                     // 全新库
            }
            else if (version < SchemaVersion)
            {
                MigrateFrom(version);               // 老库升级（只加不删）
            }
            else
            {
                // 库是用比现在更新的版本写的（比如装回旧版）。
                // 猜不出新版本长什么样，整库重建最稳 —— 正常情况下不会走到这里。
                DropAll();
                CreateSchema();
            }

            Exec($"PRAGMA user_version = {SchemaVersion};");
        }
    }

    private void CreateSchema()
    {
        Exec(@"
            CREATE TABLE IF NOT EXISTS Media (
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
                Favorite      INTEGER NOT NULL DEFAULT 0,
                Md5           TEXT    NULL,
                PHash         TEXT    NULL,
                Source        TEXT    NULL,
                IndexedAt     INTEGER NOT NULL DEFAULT 0
            );");

        // 按日期、按相机、按目录是界面上最常用的三种分法，给它们单独建索引。
        // PathLower 是给"文件搬过家"时做不区分大小写的匹配用的。
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_DateTaken   ON Media(DateTaken);");
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_CameraModel ON Media(CameraModel);");
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_LensModel   ON Media(LensModel);");
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_Directory   ON Media(Directory);");
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_Rating      ON Media(Rating);");
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_Favorite    ON Media(Favorite);");
        // Md5 用于"精确重复"查询（按值分组）；PHash 用于"相似"两两比，不按列查，不建索引。
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_Md5         ON Media(Md5);");
        // "按来源"分组 + 点某个来源筛图都走这一列
        Exec("CREATE INDEX IF NOT EXISTS IX_Media_Source      ON Media(Source);");

        CreateTagsTable();
    }

    private void CreateTagsTable()
    {
        // 标签单独一张表，而不是在 Media 上开一个逗号分隔的文本列：
        // 文本列要统计"每个标签各有多少张"就得全表 LIKE 扫一遍，
        // 标签一多（几百个）左栏每次刷新都要卡一下。
        //
        // TagLower 是给"大小写不同算同一个标签"用的（"旅行" 和 "Travel"
        // 当然是两个标签，但 "Travel" 和 "travel" 不该是两个）。
        Exec(@"
            CREATE TABLE IF NOT EXISTS Tags (
                Path     TEXT NOT NULL,
                Tag      TEXT NOT NULL,
                TagLower TEXT NOT NULL,
                PRIMARY KEY (Path, TagLower)
            );");
        Exec("CREATE INDEX IF NOT EXISTS IX_Tags_TagLower ON Tags(TagLower);");
    }

    private void DropAll()
    {
        Exec("DROP TABLE IF EXISTS Media;");
        Exec("DROP TABLE IF EXISTS Tags;");
        Exec("DROP TABLE IF EXISTS Meta;");   // 早期版本留的空表
    }

    /// <summary>
    /// 老库升级。
    ///
    /// 这里必须**真的迁移**，不能像以前那样直接删表重建。
    /// 原因：从 v3 开始库里存了评分 / 收藏 / 标签 —— 那是用户一张张点出来的，
    /// 不是重扫一遍磁盘就能回来的东西（索引里的尺寸、EXIF 才是）。
    /// 升个级就把人家的评分清空，这种事发生一次就没人敢用第二次了。
    /// </summary>
    private void MigrateFrom(int from)
    {
        // 2 → 3：加"收藏"列 + 标签表。（1 → 2 那版没对外发过，不用管）
        if (from < 3)
        {
            // ALTER TABLE 没有 IF NOT EXISTS，重复加列会报错，所以先问一句
            if (!ColumnExists("Media", "Favorite"))
                Exec("ALTER TABLE Media ADD COLUMN Favorite INTEGER NOT NULL DEFAULT 0;");

            CreateTagsTable();
        }

        // 3 → 4：加指纹列（Md5 精确重复 + PHash 感知哈希相似）。
        // 也都是"只加不删"，老库升级不碰已有的评分/收藏/标签。
        if (from < 4)
        {
            if (!ColumnExists("Media", "Md5"))
                Exec("ALTER TABLE Media ADD COLUMN Md5 TEXT NULL;");
            if (!ColumnExists("Media", "PHash"))
                Exec("ALTER TABLE Media ADD COLUMN PHash TEXT NULL;");
        }

        // 4 → 5：加"来源"列（微信 / QQ / 企业微信 缓存图的归类）。
        // 同样是只加不删。列是空的也没关系 —— 下次整理图库时按路径补上，
        // 在那之前"按来源"只会显示一个"本地"组，不会崩。
        if (from < 5)
        {
            if (!ColumnExists("Media", "Source"))
                Exec("ALTER TABLE Media ADD COLUMN Source TEXT NULL;");
        }
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // table_info 的第 2 列是列名
            if (!r.IsDBNull(1) &&
                string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
    /// <param name="rating">
    /// 评分。**传 null（默认）= 保留库里原来的值**，不是清零。
    /// 这一点很关键：整理图库时每张图都会走一遍这里，
    /// 如果默认清零，用户辛苦打的分每整理一次就没一次。
    /// 回归测试 F 段专门盯着这条。
    /// </param>
    /// <param name="md5">
    /// 文件内容的 MD5（精确重复检测用）。**传 null = 保留原值**——
    /// 这是派生数据，本该每次重扫都重算，但万一哪次调用方没传，
    /// 也不至于把已经算好的指纹清掉（扫描时调用方总是会传）。
    /// </param>
    /// <param name="phash">
    /// 感知哈希（相似检测用，见 <see cref="PerceptualHash"/>）。同样传 null 保留原值。
    /// </param>
    public void Upsert(PhotoInfo info, MediaKind kind = MediaKind.Image,
                       int? rating = null, string? md5 = null, string? phash = null)
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
                    Rating, Md5, PHash, Source, IndexedAt)
                VALUES (
                    $path, $lower, $dir, $name, $kind,
                    $size, $ticks, $w, $h,
                    $date, $est, $make, $model, $lens,
                    $fnum, $exp, $iso, $focal,
                    COALESCE($rating, 0), $md5, $phash, $src, $now)
                -- 注意：下面 DO UPDATE 里**故意不写 Favorite**。
                -- 文件重扫一遍不该把用户标的收藏冲掉，不写就等于保留原值。
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
                    -- 没传评分就保留原来的（COALESCE 的第二个 Rating 指更新前的那一行）。
                    -- 写死成 excluded.Rating 的话，整理一次图库评分就全清零了。
                    Rating        = COALESCE($rating, Rating),
                    -- Md5 / PHash 是派生数据：扫描时调用方总是会传新值（覆盖更新），
                    -- 万一某次没传（理论上不该发生），保留原值，别把算好的指纹清掉。
                    Md5           = COALESCE(excluded.Md5, Md5),
                    PHash         = COALESCE(excluded.PHash, PHash),
                    -- 来源是纯派生的（从 Path 算出来），每次扫描都重算一遍最省心。
                    -- Path 是主键不会变，所以不存在把用户手工改的来源冲掉的问题。
                    Source        = excluded.Source,
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
            cmd.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$md5", (object?)md5 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$phash", (object?)phash ?? DBNull.Value);

            // 来源不用调用方传，从路径自己算 —— 这样所有 Upsert 调用点
            // （正常扫描、以及压测工具）都自动带上，函数签名也不用改。
            cmd.Parameters.AddWithValue("$src",
                (object?)SocialCacheDetector.SourceOf(info.Path) ?? DBNull.Value);

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

            // 标签跟着一起删，否则会攒下一堆"指向不存在的文件"的孤儿标签，
            // 左栏的标签列表里就会出现删不掉的幽灵标签
            using var tag = _conn.CreateCommand();
            tag.CommandText = "DELETE FROM Tags WHERE Path = $p;";
            tag.Parameters.AddWithValue("$p", path);
            tag.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 批量删记录（"查找重复"里把若干张移到回收站后用）。
    /// 每条都顺带清孤儿标签，逻辑和 <see cref="Remove"/> 一致。
    /// </summary>
    public void RemoveMany(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;

                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Media WHERE Path = $p;";
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();

                using var tag = _conn.CreateCommand();
                tag.CommandText = "DELETE FROM Tags WHERE Path = $p;";
                tag.Parameters.AddWithValue("$p", path);
                tag.ExecuteNonQuery();
            }
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

                    using var delTag = _conn.CreateCommand();
                    delTag.CommandText = "DELETE FROM Tags WHERE Path = $p;";
                    delTag.Parameters.AddWithValue("$p", p);
                    delTag.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        return removed;
    }

    /// <summary>
    /// 清空整个索引。
    ///
    /// 注意它连评分 / 收藏 / 标签一起清 —— 这些是用户数据，
    /// 所以界面上"整理图库"走的是增量扫描，不会调到这里。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            Exec("DELETE FROM Media;");
            Exec("DELETE FROM Tags;");
        }
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

    // ===== 重复 / 相似 =====
    //
    // 这两类查询不走 WHERE 过滤，是"全库两两比"：
    //   · FindDuplicates 按 Md5 分组（字节级完全相同）
    //   · FindSimilar 按 PHash 汉明距离归组（视觉相近）
    // 结果直接喂给"查找重复"智能相册，界面按组展示。

    /// <summary>
    /// 找精确重复：MD5 完全相同的图，每组 ≥ 2 张。
    /// 返回空列表 = 没有重复（不是出错）。
    /// </summary>
    public List<DuplicateGroup> FindDuplicates()
    {
        var groups = new List<DuplicateGroup>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Md5 FROM Media
                WHERE Md5 IS NOT NULL
                GROUP BY Md5 HAVING COUNT(*) > 1;";

            var md5s = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) md5s.Add(r.GetString(0));

            foreach (string m in md5s)
            {
                using var q = _conn.CreateCommand();
                q.CommandText = "SELECT Path FROM Media WHERE Md5 = $m;";
                q.Parameters.AddWithValue("$m", m);

                var g = new DuplicateGroup { Md5 = m };
                using var r = q.ExecuteReader();
                while (r.Read()) g.Paths.Add(r.GetString(0));
                groups.Add(g);
            }
        }
        return groups;
    }

    /// <summary>
    /// 找视觉相似：PHash 汉明距离 ≤ <paramref name="threshold"/> 的图归为一组。
    /// 阈值越小越严格（只捞几乎一样的）；越大越松（连同构图不同曝光也算相似）。
    /// 默认 10 对 64 位哈希来说已经比较宽松，能捞到"同一场景不同参数"，
    /// 又不至于把完全不同的图乱凑一起。
    /// </summary>
    public List<SimilarGroup> FindSimilar(int threshold = 10)
    {
        List<(string Path, string PHash)> items;
        lock (_gate)
        {
            items = new List<(string, string)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Path, PHash FROM Media WHERE PHash IS NOT NULL;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) items.Add((r.GetString(0), r.GetString(1)));
        }

        if (items.Count == 0) return new List<SimilarGroup>();

        // path → phash 字典，避免每次都线性查找代表图
        var phashOf = new Dictionary<string, string>(items.Count);
        foreach (var it in items) phashOf[it.Path] = it.PHash;

        var groups = new List<SimilarGroup>();
        foreach (var item in items)
        {
            // 贪心：并入第一个"代表图跟它距离 ≤ 阈值"的组。
            // 贪心不保证全局最优（A 像 B、B 像 C 但 A 不像 C 时可能分两组），
            // 但对"找重复"够用，且 O(N²) 对几千张图一两秒完事。
            SimilarGroup? hit = null;
            foreach (var g in groups)
            {
                if (PerceptualHash.HammingDistance(item.PHash, phashOf[g.Paths[0]]) <= threshold)
                {
                    hit = g;
                    break;
                }
            }

            if (hit is null) groups.Add(new SimilarGroup { Paths = new List<string> { item.Path } });
            else hit.Paths.Add(item.Path);
        }

        // 只有 1 张的"组"不算相似，过滤掉
        return groups.Where(g => g.Count > 1).ToList();
    }

    /// <summary>有多少组精确重复（给智能相册入口显示红点数字用）。</summary>
    public int CountDuplicates()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) FROM (
                    SELECT Md5 FROM Media WHERE Md5 IS NOT NULL
                    GROUP BY Md5 HAVING COUNT(*) > 1
                );";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>有多少组相似（默认阈值）。</summary>
    public int CountSimilar(int threshold = 10) => FindSimilar(threshold).Count;

    // ==================== 评分 / 收藏 / 标签 ====================
    //
    // 这三个和上面那些字段有本质区别：**它们是用户数据，不是派生数据**。
    // 尺寸、EXIF、拍摄时间丢了重扫一遍就有；
    // 用户一张张点出来的评分丢一次，他就不会再用第二次。
    //
    // 由此推出两条硬规矩，改这块代码时别破：
    //   1. 升表结构只能 ALTER 加列，绝不能删表重建（见 MigrateFrom）
    //   2. Upsert 的 ON CONFLICT DO UPDATE 里**故意不碰** Favorite，
    //      Tags 也不在那条语句里 —— 图被改动后重扫一遍，
    //      不该顺手把用户标的收藏冲掉

    /// <summary>设评分。0 = 未评分，1~5 = 星级。超出范围会被夹住。</summary>
    public void SetRating(string path, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE Media SET Rating = $r WHERE Path = $p;";
            cmd.Parameters.AddWithValue("$r", rating);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>读评分。库里没这张图就当未评分。</summary>
    public int GetRating(string path) => QueryInt("Rating", path);

    /// <summary>收藏 / 取消收藏。</summary>
    public void SetFavorite(string path, bool on)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE Media SET Favorite = $f WHERE Path = $p;";
            cmd.Parameters.AddWithValue("$f", on ? 1 : 0);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsFavorite(string path) => QueryInt("Favorite", path) == 1;

    /// <summary>取单个整数列的小工具（评分、收藏都是这种）。</summary>
    private int QueryInt(string column, string path)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {column} FROM Media WHERE Path = $p;";
            cmd.Parameters.AddWithValue("$p", path);

            object? v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt32(v);
        }
    }

    /// <summary>整批替换这张图的标签（传空集合 = 清空）。</summary>
    public void SetTags(string path, IEnumerable<string> tags)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();

            using (var del = _conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM Tags WHERE Path = $p;";
                del.Parameters.AddWithValue("$p", path);
                del.ExecuteNonQuery();
            }

            foreach (string t in tags) InsertTag(path, t);

            tx.Commit();
        }
    }

    /// <summary>加一个标签。已经有就不重复加。</summary>
    public void AddTag(string path, string tag)
    {
        lock (_gate) InsertTag(path, tag);
    }

    /// <summary>去掉一个标签。没有这个标签也不报错。</summary>
    public void RemoveTag(string path, string tag)
    {
        string t = NormalizeTag(tag);
        if (t.Length == 0) return;

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Tags WHERE Path = $p AND TagLower = $l;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$l", t.ToLowerInvariant());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>这张图上的标签，按名字排好。</summary>
    public List<string> GetTags(string path)
    {
        var list = new List<string>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Tag FROM Tags WHERE Path = $p ORDER BY Tag;";
            cmd.Parameters.AddWithValue("$p", path);

            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }

        return list;
    }

    /// <summary>
    /// 全库用过的标签，带"各有多少张"，按张数从多到少排。
    /// 左栏的"标签"那一节就靠它。
    /// </summary>
    public List<TagEntry> AllTags()
    {
        var list = new List<TagEntry>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();

            // 只统计"还存在的图"上的标签：图删了以后它的标签会变成孤儿，
            // 不 JOIN 一下的话左栏会冒出删不掉的幽灵标签
            cmd.CommandText = @"
                SELECT t.Tag, COUNT(*)
                FROM Tags t
                INNER JOIN Media m ON m.Path = t.Path
                GROUP BY t.TagLower
                ORDER BY COUNT(*) DESC, t.Tag ASC;";

            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TagEntry { Tag = r.GetString(0), Count = r.GetInt32(1) });
        }

        return list;
    }

    /// <summary>收藏了多少张（左栏"收藏"那一节显示数量用）。</summary>
    public int CountFavorites()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Media WHERE Favorite = 1;";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    private void InsertTag(string path, string tag)
    {
        string t = NormalizeTag(tag);
        if (t.Length == 0) return;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Tags (Path, Tag, TagLower) VALUES ($p, $t, $l)
            ON CONFLICT(Path, TagLower) DO NOTHING;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$t", t);
        cmd.Parameters.AddWithValue("$l", t.ToLowerInvariant());
        cmd.ExecuteNonQuery();
    }

    /// <summary>标签名规范化：去首尾空白。空的直接丢掉，不进库。</summary>
    private static string NormalizeTag(string tag) => (tag ?? string.Empty).Trim();

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

            // 社交缓存里的表情包 / 头像 / 界面图标不进库（数量巨大，跟照片不搭）。
            // 只对社交缓存路径生效，用户自己的同名文件夹不受影响。
            // 不计入 Skipped —— 它不是"这次跳过、以后会补"，而是压根不该进来。
            if (SocialCacheDetector.IsNoise(path)) continue;

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
                        // 顺手把指纹也算了：MD5 字节级（精确重复），PHash 解码缩略（相似）。
                        // 失败（格式不支持/损坏）会是 null，Upsert 用 COALESCE 保留原值，不崩。
                        string? md5 = PerceptualHash.ComputeMd5(path);
                        string? phash = PerceptualHash.ComputePhash(path);
                        Upsert(info, md5: md5, phash: phash);
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

    /// <summary>
    /// 给老库 / 从来没算过指纹的记录补算 Md5（+ 可选 PHash）。
    ///
    /// 为什么需要它：增量扫描只给"新图或变更图"算指纹；
    /// 但 v3→v4 升上来的库、或用户第一次点"查找重复"之前，
    /// 库里大量图是带着指纹 null 的。不补齐，FindDuplicates / FindSimilar 就查不出东西。
    /// 用在"查找重复/相似"入口首次打开时，先确保指纹齐全再查（带进度回报，可取消）。
    ///
    /// <param name="computePhash">是否顺带算 PHash（视觉相似需要）。
    /// 只查"精确重复"（MD5）时传 false，可跳过 PHash 这条要动用 Magick 解码的慢路径，
    /// 只做纯 C# 的字节哈希，几千张图一两秒完事。</param>
    /// </summary>
    public async Task<int> BackfillHashesAsync(bool computePhash = true,
                                               IProgress<int>? progress = null,
                                               CancellationToken ct = default)
    {
        List<string> paths;
        lock (_gate)
        {
            paths = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = computePhash
                ? "SELECT Path FROM Media WHERE Md5 IS NULL OR PHash IS NULL;"
                : "SELECT Path FROM Media WHERE Md5 IS NULL;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) paths.Add(r.GetString(0));
        }

        int done = 0;
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();

            string? md5 = null;
            string? phash = null;
            try
            {
                // 文件可能已经被删了（索引里还有幽灵记录），算不了就跳过
                if (File.Exists(path))
                {
                    md5 = PerceptualHash.ComputeMd5(path);
                    if (computePhash) phash = PerceptualHash.ComputePhash(path);
                }
            }
            catch { }

            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE Media SET Md5 = $m, PHash = $p WHERE Path = $path;";
                cmd.Parameters.AddWithValue("$m", (object?)md5 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$p", (object?)phash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$path", path);
                cmd.ExecuteNonQuery();
            }

            if (++done % 50 == 0) progress?.Report(done);
        }

        progress?.Report(done);
        return done;
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

            if (q.FavoritesOnly)
                where.Add("Favorite = 1");

            if (!string.IsNullOrWhiteSpace(q.Tag))
            {
                // 用 EXISTS 而不是 JOIN：JOIN 会让同一张图因为有多个标签而重复出现
                where.Add("EXISTS (SELECT 1 FROM Tags t WHERE t.Path = Media.Path AND t.TagLower = $tag)");
                cmd.Parameters.AddWithValue("$tag", q.Tag!.Trim().ToLowerInvariant());
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

            // 和 Query() 用同一套筛选条件，
            // 否则"收藏"里显示的张数和点进去看到的对不上
            if (filter.FavoritesOnly)
                where.Add("Favorite = 1");

            if (!string.IsNullOrWhiteSpace(filter.Tag))
            {
                where.Add("EXISTS (SELECT 1 FROM Tags t WHERE t.Path = Media.Path AND t.TagLower = $tag)");
                cmd.Parameters.AddWithValue("$tag", filter.Tag!.Trim().ToLowerInvariant());
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
        // 来源列可能是 NULL（老库升级后还没重扫），统一成空串好归到"本地"那一组
        GroupBy.Source => "COALESCE(Source, '')",
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
                // 没有来源 = 不是从社交软件缓存里来的，就是用户自己的图
                GroupBy.Source => "本地",
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

            case GroupBy.Source:
                // 和 GroupExpression 一样把 NULL 当空串，否则点"本地"筛不出东西
                c.cond = "COALESCE(Source, '') = $g";
                c.parameters["$g"] = value;
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
