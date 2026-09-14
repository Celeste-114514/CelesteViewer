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
        NaturalSort();
        await Signature();

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
}
