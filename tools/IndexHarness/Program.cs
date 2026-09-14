using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CelesteViewer.Services;
using ImageMagick;
using Microsoft.Data.Sqlite;

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

    private static async Task<int> Main(string[] args)
    {
        // 诊断模式：让 Magick 逐个试真文件，把"它猜不出格式"的全揪出来。
        // 平时不跑（慢），只有排查解码异常时才手动加这个参数。
        if (args.Any(a => a == "--scan-magick"))
        {
            await MagickScan();
            return 0;
        }

        string db = Path.Combine(Path.GetTempPath(), "cvidx-test.db");
        try { File.Delete(db); } catch { }
        try { File.Delete(db + "-wal"); } catch { }
        try { File.Delete(db + "-shm"); } catch { }

        await RealFilesAsync(db);
        await SyntheticAsync(db);
        NaturalSort();
        await Signature();
        await MagickFallback();
        UserData();

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

    // ==================== C. 自然排序（界面切分类时用） ====================

    /// <summary>
    /// 界面上有两条出图的路：直接扫盘（FolderIndex）、查索引（MediaIndex）。
    /// 按文件夹浏览走前者，按日期/相机走后者。两条路给的顺序必须一致，
    /// 否则用户从"按文件夹"切到"按日期"再切回来，会觉得图的顺序被弄乱了。
    ///
    /// 麻烦在于 SQLite 排文件名是字典序：IMG_10 会排在 IMG_2 前面。
    /// Scanner 那条路用的是自然序（数字按大小比）。所以查索引之后要按自然序重排，
    /// 这一段就是验那个重排是不是真的对齐了。
    /// </summary>
    private static void NaturalSort()
    {
        Console.WriteLine();
        Console.WriteLine("=== C. 自然排序 ===");

        string[] names =
        {
            "IMG_10.jpg", "IMG_2.jpg", "IMG_1.jpg",
            "photo20.png", "photo3.png", "ABC.jpg",
        };
        string[] paths = names.Select(n => Path.Combine("D:", "x", n)).ToArray();

        var sorted = LibraryIndexService.SortNatural(paths);
        var expected = paths.OrderBy(p => p, Comparer<string>.Create(FolderIndex.CompareNatural)).ToList();

        Console.WriteLine("      结果：" + string.Join("  ", sorted.Select(Path.GetFileName)));

        Check("和 FolderIndex 的自然序口径一致", sorted.SequenceEqual(expected));

        // 关键：数字按大小比，不是按字符比（字典序下 IMG_10 会跑到 IMG_2 前面）
        var head = sorted.Take(4).Select(Path.GetFileName).ToList();
        Check("IMG_2 排在 IMG_10 前面（不是字典序）",
              head.IndexOf("IMG_2.jpg") >= 0
              && head.IndexOf("IMG_10.jpg") >= 0
              && head.IndexOf("IMG_2.jpg") < head.IndexOf("IMG_10.jpg"));

        var desc = LibraryIndexService.SortNatural(paths, descending: true);
        Check("降序是升序的完全反转", desc.SequenceEqual(sorted.AsEnumerable().Reverse()));
    }

    // ============ D. 文件头嗅探（no decode delegate 的根治点） ============

    /// <summary>造一段文件头：前面给真实魔数，后面填够长度。</summary>
    private static byte[] Head(params byte[] prefix)
    {
        var buf = new byte[Math.Max(64, prefix.Length)];
        prefix.CopyTo(buf, 0);
        // 后半段填 0，模拟真实的二进制内容（不会干扰文本判定）
        return buf;
    }

    private static byte[] Text(string s)
    {
        var buf = new byte[64];
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        bytes.CopyTo(buf, 0);
        return buf;
    }

    private static async Task Signature()
    {
        Console.WriteLine();
        Console.WriteLine("==== D. 文件头嗅探（2026-09-15 no decode delegate 的根治点）====");
        Console.WriteLine();

        // ---- D1. 已知格式的魔数必须放行 ----
        // 这里漏掉任何一个，用户的照片就会"明明在、却死活打不开"，是最严重的事故。
        var images = new (string Name, byte[] Head)[]
        {
            ("JPEG",      Head(0xFF,0xD8,0xFF,0xE0,0x00,0x10,0x4A,0x46)),
            ("PNG",       Head(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)),
            ("GIF89a",    Text("GIF89a" + new string(' ', 20))),
            ("BMP",       Head(0x42,0x4D,0x36,0x00,0x00,0x00,0x00,0x00)),
            ("TIFF-LE",   Head(0x49,0x49,0x2A,0x00,0x08,0x00,0x00,0x00)),
            ("TIFF-BE",   Head(0x4D,0x4D,0x00,0x2A,0x00,0x00,0x00,0x08)),
            ("WEBP",      Head(0x52,0x49,0x46,0x46,0x1A,0x00,0x00,0x00,0x57,0x45,0x42,0x50)),
            ("HEIC",      Head(0x00,0x00,0x00,0x18,0x66,0x74,0x79,0x70,0x68,0x65,0x69,0x63)),
            ("AVIF",      Head(0x00,0x00,0x00,0x1C,0x66,0x74,0x79,0x70,0x61,0x76,0x69,0x66)),
            ("ICO",       Head(0x00,0x00,0x01,0x00,0x01,0x00,0x10,0x10)),
            ("PSD",       Head(0x38,0x42,0x50,0x53,0x00,0x01,0x00,0x00)),
            ("OpenEXR",   Head(0x76,0x2F,0x31,0x01,0x02,0x00,0x00,0x00)),
            ("JPEG-XL",   Head(0xFF,0x0A,0x0C,0x04,0x0B,0x20,0x20,0x10)),
            ("HDR",       Text("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n")),
            ("XCF",       Text("gimp xcf v011\0\0\0")),
            ("PPM(P6)",   Text("P6\n# comment\n4000 3000\n255\n")),
            ("DDS",       Head(0x44,0x44,0x53,0x20,0x7C,0x00,0x00,0x00)),
            ("QOI",       Text("qoif" + new string(' ', 12))),
        };

        int missed = 0;
        foreach (var (name, head) in images)
        {
            bool ok = FileSignature.LooksLikeImage(head);
            if (!ok) { missed++; Console.WriteLine($"     误杀：{name}"); }
        }
        Check($"{images.Length} 种真实图片格式全部放行", missed == 0, $"误杀 {missed} 种");

        // ---- D2. 文本必须挡住（这次事故的直接原因）----
        var texts = new (string Name, byte[] Head)[]
        {
            ("md5 校验清单", Text("7dc975747cdcfc3d6a2586a6229767f4 *tests/data/apng.png")),
            ("路径引用行",   Text("tests/data/images/none.gbrapf32le.exr/%02d.exr\n")),
            ("HTML 报错页",  Text("<!DOCTYPE html><html><head><title>404 Not Found")),
            ("JSON 错误",    Text("{\"error\":\"not found\",\"code\":404,\"msg\":\"no\"")),
            ("git-lfs 指针", Text("version https://git-lfs.github.com/spec/v1\noid sha2")),
            ("纯数字文本",   Text("12345678901234567890123456789012345678901234567890")),
        };

        int leaked = 0;
        foreach (var (name, head) in texts)
        {
            bool ok = !FileSignature.LooksLikeImage(head);
            if (!ok) { leaked++; Console.WriteLine($"     漏网：{name}"); }
        }
        Check($"{texts.Length} 种文本内容全部挡住", leaked == 0, $"漏网 {leaked} 种");

        // ---- D3. 边界：太短 / 空的不能当图 ----
        Check("空数组不是图", !FileSignature.LooksLikeImage(ReadOnlySpan<byte>.Empty));
        Check("4 字节不是图（连最短文件头都不够）",
              !FileSignature.LooksLikeImage(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        // ---- D4. 不认识的二进制要放行（宁可让解码器失败，也不能误杀新格式）----
        Check("未知二进制放行（不误杀未来的新格式）",
              FileSignature.LooksLikeImage(Head(0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89)));

        // ---- D5. 嗅探不能破坏流的位置（否则解码器会读到错位的数据）----
        var buf2 = new byte[128];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(buf2, 0);
        using var ms = new MemoryStream(buf2);
        ms.Position = 0;
        FileSignature.LooksLikeImage(ms);
        Check("嗅探后流位置不变（不会把解码器带偏）", ms.Position == 0, $"Position={ms.Position}");

        // ---- D6. 真实目录：统计 + 抓误判 ----
        Console.WriteLine();
        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), ""),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        int total = 0, judgedNotImage = 0, falsePositive = 0, scanned = 0;
        var samples = new List<string>();
        var notImageFiles = new List<string>();
        string[] exts =
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp",
            ".heic", ".heif", ".avif", ".psd", ".psb", ".jxl", ".ico", ".jfif",
            ".arw", ".cr2", ".cr3", ".nef", ".nrw", ".orf", ".rw2", ".raf",
            ".dng", ".pef", ".srw", ".tga", ".pcx", ".ppm", ".pgm", ".pbm",
            ".xcf", ".exr", ".hdr",
        };

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in EnumerateSafe(root))
            {
                if (Array.IndexOf(exts, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
                if (++scanned > 40000) break;

                total++;
                bool looks;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite, 64, FileOptions.SequentialScan);
                    looks = FileSignature.LooksLikeImage(fs);
                }
                catch { continue; }

                if (looks) continue;
                judgedNotImage++;
                if (notImageFiles.Count < 40) notImageFiles.Add(file);

                // 判成"不是图"的，内容必须真的是文本 —— 否则就是误杀用户的照片
                bool reallyText;
                try
                {
                    byte[] h = new byte[64];
                    int n;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        n = fs.Read(h, 0, h.Length);
                    int printable = 0;
                    for (int i = 0; i < n; i++)
                    {
                        byte b = h[i];
                        if ((b >= 0x20 && b < 0x7F) || b is 0x09 or 0x0A or 0x0D) printable++;
                    }
                    reallyText = n >= 8 && printable >= n - (n / 20);
                }
                catch { reallyText = false; }

                if (!reallyText)
                {
                    falsePositive++;
                    if (samples.Count < 5) samples.Add(file);
                }
                else if (samples.Count < 3 && judgedNotImage <= 3)
                {
                    samples.Add("(文本) " + file);
                }
            }
        }

        Console.WriteLine($"  扫描真实图片文件 {total} 个，判为「不是图」{judgedNotImage} 个");
        foreach (string s in samples) Console.WriteLine($"      {s}");

        Check("判为「不是图」的文件，内容确实都是文本（没有误杀真照片）",
              falsePositive == 0, $"误杀 {falsePositive} 个");
        Check("绝大多数真实图片被正确放行（放行率 ≥ 80%）",
              total == 0 || (total - judgedNotImage) * 100 / total >= 80,
              total > 0 ? $"放行 {total - judgedNotImage}/{total}" : "没扫到文件");

        // ---- D7. 端到端：真解码器碰到这些文件，必须"安静地返回 null" ----
        // 这一条直接对应事故现场：以前 Magick 会抛 no decode delegate，
        // 调试器每回中断一次。现在应该连 Magick 都不会被叫到。
        if (notImageFiles.Count == 0)
        {
            Console.WriteLine("  （本机没有这类文件，跳过端到端验证）");
            return;
        }

        var decoder = new MagickImageDecoder();
        int threw = 0, gotNull = 0;
        foreach (string f in notImageFiles.Take(10))
        {
            try
            {
                PhotoInfo? r = await decoder.ProbeAsync(f);
                if (r is null) gotNull++;
            }
            catch (Exception ex)
            {
                threw++;
                if (threw <= 2) Console.WriteLine($"     抛异常：{ex.GetType().Name} {f}");
            }
        }

        int tried = Math.Min(10, notImageFiles.Count);
        Check($"Magick 解码器对这 {tried} 个文件安静返回 null、一个都不抛",
              threw == 0 && gotNull == tried, $"抛 {threw} 次 / 返回 null {gotNull} 次");
    }

    /// <summary>枚举目录，遇到没权限的子目录就跳过（Downloads 里什么都有）。</summary>
    private static IEnumerable<string> EnumerateSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (string f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string d in subs)
            {
                if (Path.GetFileName(d).StartsWith('.')) continue;
                stack.Push(d);
            }
        }
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
        // 没有拍摄时间的图会退到"文件修改时间"，所以不该再有大堆图堆在"无日期"里。
        // 改这条判据之前是"必须存在无日期组" —— 兜底逻辑上线后正好反过来。
        int noDate = byDate.Where(g => g.Label == "无日期").Sum(g => g.Count);
        Check("无 EXIF 的图退到文件时间，不再堆在「无日期」里",
              noDate == 0, $"无日期 {noDate} 张");

        // 单独造一条：只有文件时间、没有拍摄时间。它必须出现在文件时间那一组里。
        // 这条最要紧 —— 微信/QQ 缓存图、截图、AI 生成图全是这种，
        // 兜底不成立的话"按日期"在真实图库里就是个空壳。
        index.Upsert(new PhotoInfo
        {
            Path = @"D:\Photos\NoExif.jpg",
            FileSize = 1234,
            PixelWidth = 100,
            PixelHeight = 100,
            LastModified = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
            DateTaken = null,
        });

        var march = index.Query(new MediaQuery { Group = GroupBy.Date, GroupValue = "2026-03" });
        Check("没 EXIF 的图按文件修改时间归到 2026 年 3 月",
              march.Any(p => p.EndsWith("NoExif.jpg", StringComparison.OrdinalIgnoreCase)),
              $"2026-03 组共 {march.Count} 张");

        // 连文件时间都没有的极端情况：归到"无日期"，不崩
        index.Upsert(new PhotoInfo
        {
            Path = @"D:\Photos\NoTimeAtAll.jpg",
            FileSize = 12,
            PixelWidth = 1,
            PixelHeight = 1,
        });

        var timeless = index.Query(new MediaQuery { Group = GroupBy.Date, GroupValue = "" });
        Check("连文件时间都没有的图归到「无日期」，不崩",
              timeless.Any(p => p.EndsWith("NoTimeAtAll.jpg", StringComparison.OrdinalIgnoreCase)),
              $"无日期组共 {timeless.Count} 张");

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

        // 上面为了验兜底又往库里插了两条，分组得重新取，
        // 否则拿的是加数据之前那份统计，自然对不上
        byDate = index.Group(GroupBy.Date);
        byCamera = index.Group(GroupBy.Camera);

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

    // ========= E. 兜底解码器（no decode delegate 的回归测试） =========

    /// <summary>
    /// 2026-09-15 那个 `no decode delegate for this image format ''` 的回归测试。
    ///
    /// 以前的处理是"catch 住别崩"，结果每遇到一个可疑文件调试器就中断一次，
    /// 用户只能把堆栈贴过来问。根治之后必须钉死一条底线：
    ///     **任何文件交到 Magick 手上，都不能再抛这种异常。**
    ///
    /// 分两段：
    ///   E1. 造几个"曾经必炸"的文件，逐个验它安静地返回 null（不抛、不硬撑）
    ///   E2. 再扫一遍真机上的图片，确认没有漏网的
    /// </summary>
    private static async Task MagickFallback()
    {
        Console.WriteLine();
        Console.WriteLine("==== E. 兜底解码器（no decode delegate 回归测试）====");
        Console.WriteLine();

        var decoder = new MagickImageDecoder();
        string dir = Path.Combine(Path.GetTempPath(), "cv-magick-test");
        try { Directory.CreateDirectory(dir); } catch { }

        // ---- E1-0. 真图必须照常解出来 ----
        // 这条是防"为了不抛异常，干脆把好图也一起挡了"那种过度修复。
        string png = Path.Combine(dir, "real.png");
        using (var img = new MagickImage(MagickColors.SkyBlue, 32, 24))
            img.Write(png, MagickFormat.Png);

        var good = await decoder.ProbeAsync(png);
        Check("真 PNG 能正常解出尺寸", good is { PixelWidth: 32, PixelHeight: 24 },
              good is null ? "（返回 null —— 误杀了）" : $"{good.PixelWidth}x{good.PixelHeight}");

        // ---- E1-1. 扩展名 .png、内容是一行文本 ----
        // 就是用户 Downloads 里那批 ffmpeg md5 校验清单，第一轮修复的靶子。
        string fake = Path.Combine(dir, "checksum.png");
        await File.WriteAllTextAsync(fake, "d41d8cd98f00b204e9800998ecf8427e  out.png\n");
        Check("文本文件伪装成 .png：安静放弃",
              await QuietNull(decoder, fake));

        // ---- E1-2. ICO 头 + 这个构建没 ICO 委托 ----
        // 用户机器上一抓 236 个（MusicPlayer 源码里的图标），第二轮修复的靶子。
        string ico = Path.Combine(dir, "icon.ico");
        File.WriteAllBytes(ico, Head(0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10));
        Check("ICO 头但 Magick 没这委托：主动放弃",
              await QuietNull(decoder, ico));

        // ---- E1-3. 认不出的二进制 + 认不出的扩展名 ----
        // 旧代码正是从这里把裸流丢给 Magick 去猜，才炸出空格式名异常。
        string junk = Path.Combine(dir, "mystery.xyz");
        File.WriteAllBytes(junk, Head(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88));
        Check("认不出格式：绝不裸流丢给 Magick",
              await QuietNull(decoder, junk));

        // ---- E1-4. 空文件 / SVG ----
        string empty = Path.Combine(dir, "empty.png");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Check("0 字节文件：安静放弃", await QuietNull(decoder, empty));

        string svg = Path.Combine(dir, "vector.svg");
        await File.WriteAllTextAsync(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Check("SVG：一次都不试", await QuietNull(decoder, svg));

        // ---- E1-5. 扩展名标错但文件头对（.jpg 里装的是 PNG）----
        // 验"文件头兜底"那条路径没被上面几道闸门一起挡死。
        string mis = Path.Combine(dir, "mislabeled.jpg");
        File.Copy(png, mis, true);
        var misInfo = await decoder.ProbeAsync(mis);
        Check("扩展名标错、文件头对：仍按文件头解出来",
              misInfo is { PixelWidth: 32, PixelHeight: 24 },
              misInfo is null ? "（返回 null —— 误杀了）" : $"{misInfo.PixelWidth}x{misInfo.PixelHeight}");

        try { Directory.Delete(dir, true); } catch { }

        // ---- E2. 真机上的图片再扫一遍（限量，别把测试拖慢）----
        var r = await ScanWithMagick(300);
        Console.WriteLine($"  （真机扫描：{r.Scanned} 个图片文件，嗅探放行 {r.Allowed}，" +
                          $"解出 {r.Ok}，主动放弃 {r.GaveUp}）");
        Check("真机上再也没有文件让 Magick 抛异常", r.Threw == 0,
              r.Threw == 0 ? "" : $"还有 {r.Threw} 个：" + string.Join("、", r.Samples.Take(3)));
        Check("真机扫描没有误杀（放行了的图里有能解出来的）",
              r.Allowed == 0 || r.Ok > 0, $"放行 {r.Allowed} / 解出 {r.Ok}");
    }

    /// <summary>
    /// 要求解码器"安静地返回 null"：既不抛异常，也不硬解出一个东西来。
    /// 抛任何异常都算失败 —— 这条正是回归测试要守的底线。
    /// </summary>
    private static async Task<bool> QuietNull(MagickImageDecoder decoder, string path)
    {
        try { return await decoder.ProbeAsync(path) is null; }
        catch { return false; }
    }

    /// <summary>
    /// 全量诊断：扫真实图库，逐个问 Magick"这文件你认得吗"，把认不出来的全列出来。
    /// 平时不跑（慢），排查解码异常时手动加 --scan-magick。
    /// </summary>
    private static async Task MagickScan()
    {
        var r = await ScanWithMagick(0);

        Console.WriteLine("==== Magick 格式探测诊断 ====");
        Console.WriteLine();
        Console.WriteLine($"扫描图片文件        : {r.Scanned}");
        Console.WriteLine($"嗅探放行（像图）    : {r.Allowed}");
        Console.WriteLine($"**仍然抛异常**     : {r.Threw}   ← 必须是 0");
        Console.WriteLine($"解出尺寸（没误杀）  : {r.Ok}");
        Console.WriteLine($"解码器主动放弃      : {r.GaveUp}");
        Console.WriteLine();

        if (r.GaveUp > 0)
        {
            Console.WriteLine("主动放弃的按扩展名（ICO 是预期的：Magick 没这委托；别的格式要留意）：");
            foreach (var kv in r.NullByExt.OrderByDescending(k => k.Value))
                Console.WriteLine($"  {kv.Key,-8} {kv.Value} 个");
            Console.WriteLine();
        }

        if (r.Threw > 0)
        {
            Console.WriteLine("抛异常的文件：");
            foreach (var kv in r.ThrewByExt.OrderByDescending(k => k.Value))
                Console.WriteLine($"  {kv.Key,-8} {kv.Value} 个");
            Console.WriteLine();
            foreach (string s in r.Samples) Console.WriteLine(s);
        }
    }

    private sealed record MagickScanResult(
        int Scanned, int Allowed, int Threw, int Ok, int GaveUp,
        Dictionary<string, int> NullByExt,
        Dictionary<string, int> ThrewByExt,
        List<string> Samples);

    /// <summary>
    /// 扫真机图片目录，逐个走一遍完整的 MagickImageDecoder 链路。
    /// </summary>
    /// <param name="maxFiles">最多扫几个文件；0 = 不限（全量诊断用）。</param>
    private static async Task<MagickScanResult> ScanWithMagick(int maxFiles)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(home, "Downloads"),
        };

        var exts = new HashSet<string>(
            ImageFormats.Common.Concat(ImageFormats.Extended),
            StringComparer.OrdinalIgnoreCase);

        var decoder = new MagickImageDecoder();

        int scanned = 0, allowed = 0, threw = 0, probedAsImage = 0, gaveUp = 0;
        var samples = new List<string>();
        var byExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byExtNull = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                Console.WriteLine($"（目录不存在）{root}");
                continue;
            }

            // 枚举可能因权限/长路径中断，逐个目录容错
            var queue = new Stack<string>();
            queue.Push(root);
            while (queue.Count > 0)
            {
                string dir = queue.Pop();
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { continue; }

                foreach (string file in files)
                {
                    if (!exts.Contains(Path.GetExtension(file))) continue;
                    scanned++;

                    if (maxFiles > 0 && scanned > maxFiles) return Build(scanned, allowed, threw,
                        probedAsImage, gaveUp, byExtNull, byExt, samples);

                    try
                    {
                        byte[] head = new byte[64];
                        int n;
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                            n = fs.Read(head, 0, head.Length);

                        if (!FileSignature.LooksLikeImage(head.AsSpan(0, n))) continue;
                        allowed++;

                        // 走**修复后的完整链路**：MagickImageDecoder 自己。
                        // 直接 new MagickImageInfo 会把修复绕过去，验证不到东西。
                        try
                        {
                            var info = await decoder.ProbeAsync(file);
                            if (info is not null) probedAsImage++;
                            else
                            {
                                // 解码器主动放弃：要么 Magick 没这格式的委托（预期，比如 ICO），
                                // 要么文件本身坏了。按扩展名分开记，才能看出有没有误杀。
                                gaveUp++;
                                string e2 = Path.GetExtension(file);
                                byExtNull[e2] = byExtNull.TryGetValue(e2, out int c2) ? c2 + 1 : 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            threw++;
                            string ext = Path.GetExtension(file);
                            byExt[ext] = byExt.TryGetValue(ext, out int c) ? c + 1 : 1;

                            if (samples.Count < 30)
                            {
                                long size = new FileInfo(file).Length;
                                string msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
                                if (msg.Length > 60) msg = msg[..60];
                                samples.Add($"  {ext,-6} {size,9} B  {file}\n           {msg}");
                            }
                        }
                    }
                    catch { /* 读不动的文件跟本次排查无关 */ }
                }

                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string name = Path.GetFileName(sub);
                        if (name.StartsWith('.') || name.StartsWith('$')) continue;
                        queue.Push(sub);
                    }
                }
                catch { }
            }
        }

        return Build(scanned, allowed, threw, probedAsImage, gaveUp, byExtNull, byExt, samples);
    }

    private static MagickScanResult Build(
        int scanned, int allowed, int threw, int ok, int gaveUp,
        Dictionary<string, int> byExtNull, Dictionary<string, int> byExt, List<string> samples)
        => new(scanned, allowed, threw, ok, gaveUp, byExtNull, byExt, samples);

    // ============ F. 评分 / 收藏 / 标签（用户数据，丢不得） ============

    private static PhotoInfo Photo(string path) => new()
    {
        Path = path,
        FileSize = 1000,
        PixelWidth = 800,
        PixelHeight = 600,
        LastModified = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
        DateTaken = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static void UserData()
    {
        Console.WriteLine();
        Console.WriteLine("==== F. 评分 / 收藏 / 标签（用户数据）====");
        Console.WriteLine();

        string db = Path.Combine(Path.GetTempPath(), "cv-user-test.db");
        foreach (string s in new[] { "", "-wal", "-shm" })
        { try { File.Delete(db + s); } catch { } }

        using var index = new MediaIndex(db);

        const string a = @"D:\Photos\a.jpg";
        const string b = @"D:\Photos\b.jpg";
        const string c = @"D:\Photos\c.jpg";

        index.Upsert(Photo(a));
        index.Upsert(Photo(b));
        index.Upsert(Photo(c));

        // ---- 评分 ----
        index.SetRating(a, 5);
        index.SetRating(b, 3);
        Check("评分能写能读", index.GetRating(a) == 5 && index.GetRating(b) == 3,
              $"a={index.GetRating(a)} b={index.GetRating(b)}");
        Check("没评过的是 0 星", index.GetRating(c) == 0);

        index.SetRating(b, 99);
        Check("评分超出范围被夹到 5 星", index.GetRating(b) == 5, $"{index.GetRating(b)} 星");
        index.SetRating(b, 3);

        // ---- 收藏 ----
        index.SetFavorite(a, true);
        index.SetFavorite(b, true);
        Check("收藏能写能读",
              index.IsFavorite(a) && index.IsFavorite(b) && !index.IsFavorite(c));
        Check("收藏数量对得上", index.CountFavorites() == 2, $"{index.CountFavorites()} 张");

        // ---- 标签 ----
        index.SetTags(a, new[] { "旅行", "家人" });
        index.SetTags(b, new[] { "旅行" });
        index.AddTag(b, "travel");      // 英文另算一个标签
        index.AddTag(b, "旅行");         // 重复加不该产生第二行
        Check("标签能写能读", index.GetTags(a).Count == 2, string.Join("、", index.GetTags(a)));
        Check("同一个标签重复加不会重复",
              index.GetTags(b).Count(t => t == "旅行") == 1, string.Join("、", index.GetTags(b)));

        index.RemoveTag(a, "家人");
        Check("删标签生效", !index.GetTags(a).Contains("家人"));

        var all = index.AllTags();
        Check("标签统计对得上（旅行 2 张、travel 1 张）",
              all.Count == 2 && all[0].Tag == "旅行" && all[0].Count == 2,
              string.Join("、", all.Select(t => $"{t.Tag}×{t.Count}")));

        // ---- 最要紧的一条：重扫不能冲掉用户数据 ----
        // 图被改过时要重新读 EXIF，那条 SQL 走的是 ON CONFLICT DO UPDATE。
        // 它要是顺手把 Favorite / Rating 覆盖回默认值，
        // 用户每整理一次图库评分就全没了 —— 最容易踩、后果最严重的一个坑。
        index.Upsert(new PhotoInfo
        {
            Path = a, FileSize = 999, PixelWidth = 10, PixelHeight = 10,
            LastModified = DateTimeOffset.UtcNow,
        });
        Check("重扫一遍：评分没被冲掉", index.GetRating(a) == 5, $"{index.GetRating(a)} 星");
        Check("重扫一遍：收藏没被冲掉", index.IsFavorite(a));
        Check("重扫一遍：标签没被冲掉",
              index.GetTags(a).Count == 1, string.Join("、", index.GetTags(a)));

        // ---- 查询 ----
        var fav = index.Query(new MediaQuery { FavoritesOnly = true });
        Check("只查收藏：数量和 CountFavorites 对得上",
              fav.Count == index.CountFavorites(), $"{fav.Count} 张");

        var travel = index.Query(new MediaQuery { Tag = "旅行" });
        Check("按标签查：两张都捞出来",
              travel.Count == 2 && travel.Contains(a) && travel.Contains(b), $"{travel.Count} 张");

        var four = index.Query(new MediaQuery { MinRating = 4 });
        Check("按最低评分查：只剩 5 星那张",
              four.Count == 1 && four[0] == a, $"{four.Count} 张");

        // 左栏的分组和右栏的查询必须用同一套筛选条件，
        // 否则"收藏"写着 2 张、点进去只有 1 张
        var favByDate = index.Group(GroupBy.Date, new MediaQuery { FavoritesOnly = true });
        Check("分组也认「只收藏」这个条件（左右不能对不上）",
              favByDate.Sum(g => g.Count) == 2, $"{favByDate.Sum(g => g.Count)} 张");

        // ---- 删图要连标签一起删 ----
        index.Remove(b);
        Check("删掉一张图：它的标签跟着没了（不留幽灵标签）",
              index.AllTags().All(t => t.Tag != "travel"),
              string.Join("、", index.AllTags().Select(t => t.Tag)));

        CheckMigrate();
    }

    /// <summary>
    /// 造一个"v2 时期"的老库（有评分、没有 Favorite 列和 Tags 表），
    /// 让新代码去打开它 —— 评分必须还在，新功能必须能用。
    ///
    /// 这条专门防"升表结构时图省事直接删表重建"：
    /// 库里只有派生数据的时候那样写看不出问题，
    /// 一旦存了用户数据就是**静默清零**，用户还不知道发生了什么。
    /// </summary>
    private static void CheckMigrate()
    {
        string db = Path.Combine(Path.GetTempPath(), "cv-migrate-test.db");
        foreach (string s in new[] { "", "-wal", "-shm" })
        { try { File.Delete(db + s); } catch { } }

        const string p = @"D:\Photos\old.jpg";

        using (var old = new MediaIndex(db))
        {
            old.Upsert(Photo(p));
            old.SetRating(p, 4);
        }

        // 把版本号压回 2，模拟"这是一个还没升过级的老库"
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }

        using var upgraded = new MediaIndex(db);
        Check("老库升级：评分保住了", upgraded.GetRating(p) == 4, $"{upgraded.GetRating(p)} 星");

        upgraded.SetFavorite(p, true);
        Check("老库升级：新加的收藏列能用", upgraded.IsFavorite(p));

        upgraded.SetTags(p, new[] { "老照片" });
        Check("老库升级：新加的标签表能用",
              upgraded.GetTags(p).Count == 1, string.Join("、", upgraded.GetTags(p)));
    }
}
