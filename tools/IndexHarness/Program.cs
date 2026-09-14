using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CelesteViewer.Services;

namespace CelesteViewer.IndexHarness;

/// <summary>
/// 图库索引实测。
///
/// 分两段：
///   A. 真实文件 —— 扫用户机器上的实际图片，验证"功能对不对"
///   B. 合成数据 —— 造几千条记录，验证"规模撑不撑得住"
///
/// 为什么要分开：找几万张真图不现实，而查询逻辑在 60 张和 6000 张上是一样的，
/// 性能问题只在规模上暴露。合成数据专治后者。
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}  {detail}");
        if (!ok) _fail++;
    }

    private static async Task<int> Main()
    {
        string db = Path.Combine(Path.GetTempPath(), "cvidx-test.db");
        try { File.Delete(db); } catch { }
        try { File.Delete(db + "-wal"); } catch { }
        try { File.Delete(db + "-shm"); } catch { }

        await RealFilesAsync(db);
        await SyntheticAsync(db);

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "==== 全部通过 ====" : $"==== 有 {_fail} 项失败 ====");
        return _fail == 0 ? 0 : 1;
    }

    // ==================== A. 真实文件 ====================

    private static async Task RealFilesAsync(string db)
    {
        Console.WriteLine("=== A. 真实文件 ===");

        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures"),
            @"C:\Users\admin\WorkBuddy\2026-09-09-18-56-42",
        };

        using var index = new MediaIndex(db);

        var sw = Stopwatch.StartNew();
        int scanned = 0;

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;

            var report = await index.IndexFolderAsync(
                root, recursive: true,
                progress: null,
                ct: default);

            scanned += report.Total;
            Console.WriteLine($"  {root}");
            Console.WriteLine($"    新增 {report.Added} / 更新 {report.Updated} / 跳过 {report.Skipped} / 失败 {report.Failed}");
        }

        sw.Stop();

        int total = index.Count;
        Console.WriteLine($"  索引总数 = {total}（扫描命中 {scanned}，用时 {sw.ElapsedMilliseconds} ms）");

        Check("真实文件能进索引", total > 0, $"共 {total} 条");

        if (total == 0) return;

        // ---- 增量：第二次扫应该几乎全跳过 ----
        var sw2 = Stopwatch.StartNew();
        var again = await index.IndexFolderAsync(roots[0], recursive: true);
        sw2.Stop();

        Check("第二次扫描走增量（不应有新增）",
              again.Added == 0 && again.Skipped > 0,
              $"新增 {again.Added} / 跳过 {again.Skipped} / 更新 {again.Updated}，用时 {sw2.ElapsedMilliseconds} ms");

        // ---- 分组：按日期 ----
        var byDate = index.Group(GroupBy.Date);
        Console.WriteLine($"  按日期分组：{byDate.Count} 组");
        foreach (var g in byDate.Take(5))
            Console.WriteLine($"      {g.Label}  ({g.Key})  {g.Count} 张");

        int sumDate = byDate.Sum(g => g.Count);
        Check("按日期分组：各组数量之和 = 总数", sumDate == total, $"{sumDate} vs {total}");

        // ---- 分组：按文件夹 ----
        var byFolder = index.Group(GroupBy.Folder);
        int sumFolder = byFolder.Sum(g => g.Count);
        Check("按文件夹分组：各组数量之和 = 总数", sumFolder == total, $"{sumFolder} vs {total}");

        // ---- 点到某一组，能筛出对应的图 ----
        if (byDate.Count > 0)
        {
            var pick = byDate.First(g => g.Count > 0);
            var hit = index.Query(new MediaQuery
            {
                Group = GroupBy.Date,
                GroupValue = pick.Key,
            });
            Check($"点「{pick.Label}」能筛出对等的图",
                  hit.Count == pick.Count,
                  $"期望 {pick.Count} 张，实得 {hit.Count} 张");
        }

        if (byFolder.Count > 0)
        {
            var pick = byFolder.OrderByDescending(g => g.Count).First();
            var hit = index.Query(new MediaQuery
            {
                Group = GroupBy.Folder,
                GroupValue = pick.Key,
            });
            Check($"点文件夹「{pick.Label}」能筛出对等的图",
                  hit.Count == pick.Count,
                  $"期望 {pick.Count} 张，实得 {hit.Count} 张");
        }

        // ---- 搜索 ----
        string? sampleName = index.Query(new MediaQuery { Limit = 1 }).FirstOrDefault();
        if (sampleName is not null)
        {
            string bare = Path.GetFileNameWithoutExtension(sampleName);
            if (bare.Length >= 4)
            {
                string needle = bare.Substring(0, Math.Min(6, bare.Length));

                // 注意：文件名里常见 "_"，如果 LIKE 通配没转义，搜 "IMG_1" 会连 "IMGX1" 一起命中。
                // 这里用"搜到的每一条文件名都真的包含 needle"来验证转义是对的。
                var found = index.Query(new MediaQuery { Text = needle });
                bool allContain = found.All(p =>
                    Path.GetFileName(p).Contains(needle, StringComparison.OrdinalIgnoreCase));

                Check($"搜索「{needle}」命中的都真的包含它", allContain,
                      $"命中 {found.Count} 条");
                Check("搜索有结果", found.Count > 0, $"命中 {found.Count} 条");
            }
        }

        // ---- 排序 ----
        var byNameAsc = index.Query(new MediaQuery { Sort = SortKey.FileNameAsc, Limit = 50 });
        var byNameDesc = index.Query(new MediaQuery { Sort = SortKey.FileNameDesc, Limit = 50 });

        if (byNameAsc.Count >= 2)
        {
            bool asc = string.Compare(
                Path.GetFileName(byNameAsc[0]),
                Path.GetFileName(byNameAsc[byNameAsc.Count - 1]),
                StringComparison.OrdinalIgnoreCase) <= 0;

            bool desc = string.Compare(
                Path.GetFileName(byNameDesc[0]),
                Path.GetFileName(byNameDesc[byNameDesc.Count - 1]),
                StringComparison.OrdinalIgnoreCase) >= 0;

            Check("按文件名升序：首条 ≤ 末条", asc);
            Check("按文件名降序：首条 ≥ 末条", desc);
        }

        // ---- Limit 生效 ----
        var limited = index.Query(new MediaQuery { Limit = 3 });
        Check("Limit 生效", limited.Count <= 3, $"实得 {limited.Count}");

        // ---- 删除清理 ----
        int before = index.Count;
        index.Remove(index.Query(new MediaQuery { Limit = 1 }).First());
        Check("删除一条后总数减一", index.Count == before - 1, $"{before} → {index.Count}");
    }

    // ==================== B. 合成数据 ====================

    private static async Task SyntheticAsync(string db)
    {
        Console.WriteLine();
        Console.WriteLine("=== B. 合成数据（5000 条）===");

        const int N = 5000;
        string db2 = Path.Combine(Path.GetTempPath(), "cvidx-bulk.db");
        // 三个文件都要删：开了 WAL 之后库旁边会有 -wal / -shm，
        // 只删主文件的话 SQLite 会把旧数据从 wal 里恢复回来，
        // 于是每次跑都像是在用上一次的库（这个坑让我白查了十分钟）。
        foreach (string f in new[] { db2, db2 + "-wal", db2 + "-shm" })
        {
            try { File.Delete(f); } catch { }
        }

        using var index = new MediaIndex(db2);

        var rng = new Random(20260915);
        // 数组里刻意放 null：真实图库里就是有一批图读不到相机/镜头信息，
        // 索引必须能正确处理"这个字段没有"，而不是崩掉或者塞个空字符串进去
        string?[] cameras = { "Canon EOS R6", "SONY ILCE-7M4", "NIKON Z 6_2", null, "FUJIFILM X-T5" };
        string?[] lenses = { "RF50mm F1.8", "FE 35mm F1.8", "NIKKOR Z 24-70", null, "XF23mmF1.4" };

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < N; i++)
        {
            // 三年内的随机日期，造出大约 36 个月份分组
            var date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
                       .AddDays(rng.Next(0, 1000))
                       .AddHours(rng.Next(0, 24));

            string cam = cameras[rng.Next(cameras.Length)] ?? string.Empty;
            string lens = lenses[rng.Next(lenses.Length)] ?? string.Empty;

            // 每 100 条故意把下划线换成 X。
            // 这是给搜索转义下的套：如果 LIKE 的通配符没转义，
            // 搜 "IMG_01" 时 "_" 会匹配任意字符，于是 IMGX00100 这类也会被捞出来。
            string stem = (i % 100 == 0) ? $"IMGX{i:D5}" : $"IMG_{i:D5}";

            index.Upsert(new PhotoInfo
            {
                Path = $@"D:\Photos\2026\{i % 12 + 1:D2}\{stem}.jpg",
                FileSize = 1_000_000 + rng.Next(0, 8_000_000),
                PixelWidth = 4000,
                PixelHeight = 3000,
                LastModified = date,
                DateTaken = rng.Next(10) == 0 ? null : date,   // 约 10% 没有拍摄时间
                CameraModel = cam.Length == 0 ? null : cam,
                LensModel = lens.Length == 0 ? null : lens,
            });
        }

        sw.Stop();
        Console.WriteLine($"  写入 {N} 条，用时 {sw.ElapsedMilliseconds} ms");

        Check("合成数据全部入库", index.Count == N, $"{index.Count} / {N}");

        // ---- 分组规模 ----
        var byDate = index.Group(GroupBy.Date);
        var byCamera = index.Group(GroupBy.Camera);
        Console.WriteLine($"  按日期 {byDate.Count} 组 / 按相机 {byCamera.Count} 组");

        Check("日期分组数量合理（应在 30~40 之间）",
              byDate.Count >= 30 && byDate.Count <= 40, $"{byDate.Count} 组");
        Check("日期分组之和 = 总数", byDate.Sum(g => g.Count) == N);
        Check("相机分组之和 = 总数", byCamera.Sum(g => g.Count) == N);
        Check("存在「无日期」组（对应没 EXIF 的图）",
              byDate.Any(g => g.Label == "无日期"),
              byDate.FirstOrDefault(g => g.Label == "无日期")?.Count + " 张");

        // ---- 查询性能：这才是规模测试的重点 ----
        var swQ = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            index.Query(new MediaQuery { Sort = SortKey.DateTakenDesc });
        swQ.Stop();
        double avgQuery = swQ.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"  全库查询平均 {avgQuery:F1} ms");

        Check("全库查询应在 50 ms 内", avgQuery < 50, $"{avgQuery:F1} ms");

        var swG = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            index.Group(GroupBy.Date);
        swG.Stop();
        double avgGroup = swG.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"  分组统计平均 {avgGroup:F1} ms");

        Check("分组统计应在 50 ms 内", avgGroup < 50, $"{avgGroup:F1} ms");

        // ---- 搜索性能 + 通配符转义 ----
        var swS = Stopwatch.StartNew();
        var hits = index.Query(new MediaQuery { Text = "IMG_01" });
        swS.Stop();
        Console.WriteLine($"  搜索命中 {hits.Count} 条，用时 {swS.ElapsedMilliseconds} ms");

        Check("搜索有结果且够快", hits.Count > 0 && swS.ElapsedMilliseconds < 200,
              $"{hits.Count} 条 / {swS.ElapsedMilliseconds} ms");

        // 关键：下划线必须当普通字符比。若被当成 LIKE 通配符，
        // 下面这个数会 > 0（那些 IMGX00100 之类的会被错误地捞进来）
        int wrongUnderscore = hits.Count(p =>
            Path.GetFileName(p).StartsWith("IMGX", StringComparison.OrdinalIgnoreCase));

        Check("下划线没被当成通配符（IMGX 不该被搜出来）",
              wrongUnderscore == 0,
              $"误捞 {wrongUnderscore} 条");

        // 反过来验：搜 IMGX 应该只出 IMGX 那批，数量等于 N/100
        var exHits = index.Query(new MediaQuery { Text = "IMGX" });
        Check("搜 IMGX 只出 IMGX 那批",
              exHits.Count == N / 100,
              $"实得 {exHits.Count}，期望 {N / 100}");

        // ---- 点月份 → 数量必须和分组标注的一致（最容易写错的地方）----
        int mismatch = 0;
        foreach (var g in byDate)
        {
            var hit = index.Query(new MediaQuery { Group = GroupBy.Date, GroupValue = g.Key });
            if (hit.Count != g.Count) mismatch++;
        }
        Check("每个月份点进去，数量都和标注一致", mismatch == 0, $"{mismatch} 个对不上");

        int mismatchCam = 0;
        foreach (var g in byCamera)
        {
            var hit = index.Query(new MediaQuery { Group = GroupBy.Camera, GroupValue = g.Key });
            if (hit.Count != g.Count) mismatchCam++;
        }
        Check("每个相机点进去，数量都和标注一致", mismatchCam == 0, $"{mismatchCam} 个对不上");

        // ---- 排序正确性：按拍摄时间倒序，第一条应该最新 ----
        var recent = index.Query(new MediaQuery { Sort = SortKey.DateTakenDesc, Limit = 5 });
        Check("按拍摄时间倒序有结果", recent.Count == 5);

        await Task.CompletedTask;
    }
}
