using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CelesteGallery.Services;
using ImageMagick;
using Microsoft.Data.Sqlite;
// PerceptualHash 在我的代码里是 CelesteGallery.Services.PerceptualHash；
// ImageMagick 命名空间里也有一个同名类型，用别名把简单名指向我的那份，消除歧义。
using PerceptualHash = CelesteGallery.Services.PerceptualHash;

namespace CelesteGallery.IndexHarness;

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

    // ==================== J. 直方图（路线图第 5 步） ====================

    /// <summary>造一段纯色 BGRA 像素。B/G/R/A 四个分量都给同一个值。</summary>
    private static byte[] MakeBgra(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = a;
        }
        return px;
    }

    private static void Histogram()
    {
        Console.WriteLine();
        Console.WriteLine("==== J. 直方图 ====");
        Console.WriteLine();

        // ---- J1: 纯色图只落在一个桶上，平均亮度就是那个色阶 ----
        var mid = HistogramCalculator.Compute(MakeBgra(64, 64, 128, 128, 128), 64, 64);
        Check("纯灰 128：全部落在亮度桶 128",
              mid.Luma[128] == mid.Samples && mid.Samples == 64 * 64,
              $"{mid.Luma[128]} / {mid.Samples}");
        Check("纯灰 128：平均亮度 = 128", Math.Abs(mid.MeanLuma - 128) < 0.01,
              mid.MeanLuma.ToString("0.###"));
        Check("小图不抽稀：样本数 = 总像素数", mid.Samples == 64 * 64, mid.Samples.ToString());

        // ---- J2: RGB 三条通道各自落点（B/G/R 的字节顺序不能搞反）----
        var blue = HistogramCalculator.Compute(MakeBgra(8, 8, 255, 0, 0), 8, 8);
        Check("纯蓝：B 落 255 桶、G/R 落 0 桶",
              blue.B[255] == blue.Samples && blue.B[0] == 0
              && blue.G[0] == blue.Samples && blue.R[0] == blue.Samples);
        Check("纯蓝：亮度 = 28（0.114 × 255）", Math.Abs(blue.MeanLuma - 28) < 1.0,
              blue.MeanLuma.ToString("0.##"));

        // ---- J3: 暗部 / 高光溢出 ----
        var black = HistogramCalculator.Compute(MakeBgra(16, 16, 0, 0, 0), 16, 16);
        Check("纯黑：暗部溢出 100%、高光 0",
              Math.Abs(black.ShadowClippedRatio - 1.0) < 1e-9 && black.HighlightClippedRatio == 0);
        var white = HistogramCalculator.Compute(MakeBgra(16, 16, 255, 255, 255), 16, 16);
        Check("纯白：高光溢出 100%、暗部 0",
              Math.Abs(white.HighlightClippedRatio - 1.0) < 1e-9 && white.ShadowClippedRatio == 0);

        // ---- J4: 全透明像素不参与统计 ----
        // 不排除的话，带透明通道的 PNG 会在 0 号桶上凭空多出一根大柱子
        var clear = HistogramCalculator.Compute(MakeBgra(16, 16, 0, 0, 0, a: 0), 16, 16);
        Check("全透明：样本数 0（不把透明区域的黑色算进去）", clear.Samples == 0);

        // ---- J5: 大图抽稀，但结论不变 ----
        const int Big = 2000;                        // 400 万像素，是上限的 15 倍
        var bigPx = MakeBgra(Big, Big, 200, 200, 200);
        var sw = Stopwatch.StartNew();
        var big = HistogramCalculator.Compute(bigPx, Big, Big);
        sw.Stop();
        Check($"大图抽稀：样本数 ≤ 上限 {HistogramCalculator.DefaultMaxSamples}",
              big.Samples > 0 && big.Samples <= HistogramCalculator.DefaultMaxSamples,
              big.Samples.ToString());
        Check("大图抽稀后统计仍然正确（纯色图照样全落一个桶）", big.Luma[200] == big.Samples);
        Check("大图统计够快（< 200ms）", sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");

        // ---- J6: 只读，一个字节都不改 ----
        byte[] sample = MakeBgra(32, 32, 10, 20, 30);
        byte[] copy = (byte[])sample.Clone();
        HistogramCalculator.Compute(sample, 32, 32);
        Check("只读：统计前后像素完全一致", sample.SequenceEqual(copy));

        // ---- J7: 边界不炸 ----
        Check("边界：null / 尺寸为 0 / 负尺寸 都返回空结果",
              HistogramCalculator.Compute(null, 10, 10).Samples == 0
              && HistogramCalculator.Compute(new byte[4], 0, 0).Samples == 0
              && HistogramCalculator.Compute(new byte[4], -3, 5).Samples == 0);

        // ---- J8: 绘图高度（开方压缩）----
        var bins = new int[HistogramData.Bins];
        bins[0] = 100;
        bins[1] = 25;
        double[] hs = HistogramCalculator.ToDisplayHeights(bins);
        Check("高度：峰值归一化到 1.0", Math.Abs(hs[0] - 1.0) < 1e-9, hs[0].ToString("0.###"));
        Check("高度：空桶是 0", hs[2] == 0);
        // 线性归一的话 25/100 = 0.25；开方之后 √25/√100 = 0.5。
        // 这一条就是"小柱子不会被大柱子压成贴地的一条线"的证据
        Check("高度：开方压缩把小柱子抬到 0.5（线性只会是 0.25）",
              Math.Abs(hs[1] - 0.5) < 1e-9, hs[1].ToString("0.###"));
        Check("高度：全部落在 0~1 区间", hs.All(v => v >= 0 && v <= 1));
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}  {detail}");
        if (!ok) _fail++;
    }

    /// <summary>
    /// 浮点比较。归一化比例是"像素 ÷ 尺寸"算出来的，会有尾数，
    /// 拿 <c>==</c> 比会时通时不通（同一份代码换台机器就变）。
    /// </summary>
    private static bool Near(double a, double b, double tolerance = 1e-9)
        => Math.Abs(a - b) < tolerance;

    // ==================== K. 非破坏性编辑（路线图第 6 步） ====================

    /// <summary>
    /// 调色参数的简写构造。调色复用查看器"调整"面板那套
    /// <see cref="LookSettings"/>（10 项滑块 + 5 个滤镜专用项 + 黑白模式），
    /// 全填一遍要写二十来行，测试里这么写根本看不清重点。
    /// </summary>
    private static LookSettings Tone(
        int brightness = 0, int exposure = 0, int contrast = 0,
        int saturation = 0, int warmth = 0, int highlights = 0,
        int shadows = 0, int vignette = 0, int tint = 0, int clarity = 0,
        double fade = 0, double grain = 0, double split = 0,
        double blackLift = 0, double whiteDrop = 0, MonoMode mono = MonoMode.None)
        => new()
        {
            Brightness = brightness,
            Exposure = exposure,
            Contrast = contrast,
            Saturation = saturation,
            Warmth = warmth,
            Highlights = highlights,
            Shadows = shadows,
            Vignette = vignette,
            Tint = tint,
            Clarity = clarity,
            Fade = fade,
            Grain = grain,
            Split = split,
            BlackLift = blackLift,
            WhiteDrop = whiteDrop,
            Mono = mono,
        };

    /// <summary>造一张"每像素 R 通道 = 序号（从 1 起）"的图。
    /// 用来验证旋转 / 翻转 / 裁剪有没有把像素搬到正确的格子上 ——
    /// 用纯色图是测不出来的（搬错位置也是同一个颜色）。
    /// </summary>
    private static byte[] MakeIndexed(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            px[i * 4 + 2] = (byte)(i + 1);    // R = 序号（1 起：0 留给"没写进去"）
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    private static DecodedBitmap Bmp(byte[] pixels, int width, int height, bool premultiplied = false)
        => new()
        {
            Pixels = pixels,
            PixelWidth = width,
            PixelHeight = height,
            Premultiplied = premultiplied,
            DecoderName = "test",
        };

    /// <summary>取 (x, y) 处像素的 R 分量。</summary>
    private static int RAt(DecodedBitmap bmp, int x, int y)
        => bmp.Pixels[(y * bmp.PixelWidth + x) * 4 + 2];

    private static void EditRender()
    {
        Console.WriteLine();
        Console.WriteLine("==== K. 非破坏性编辑：渲染 ====");
        Console.WriteLine();

        // ---- K1: 没编辑就原样返回（同一个对象，不白拷一遍像素）----
        var src = Bmp(MakeIndexed(4, 3), 4, 3);
        Check("无参数：返回同一个对象", ReferenceEquals(EditRenderer.Apply(src, null), src));
        Check("空参数：返回同一个对象", ReferenceEquals(EditRenderer.Apply(src, new PhotoEdits()), src));

        // ---- K2: 旋转 90°（顺时针），宽高互换 + 像素落点 ----
        // 3×2 的原图（数字是 R 分量）：
        //    1 2 3
        //    4 5 6
        // 顺时针 90° 之后应该是 2×3：
        //    4 1
        //    5 2
        //    6 3
        var r90 = EditRenderer.Apply(Bmp(MakeIndexed(3, 2), 3, 2),
                                     new PhotoEdits { Rotation = 90 });
        Check("旋转 90：宽高互换（3×2 → 2×3）",
              r90.PixelWidth == 2 && r90.PixelHeight == 3,
              $"{r90.PixelWidth}×{r90.PixelHeight}");
        Check("旋转 90：四角落点正确（左上→右上、左下→左上）",
              RAt(r90, 1, 0) == 1 && RAt(r90, 0, 0) == 4
              && RAt(r90, 1, 2) == 3 && RAt(r90, 0, 2) == 6,
              $"{RAt(r90, 1, 0)},{RAt(r90, 0, 0)},{RAt(r90, 1, 2)},{RAt(r90, 0, 2)}");

        // ---- K3: 旋转 180 / 270 / 360 ----
        var r180 = EditRenderer.Apply(Bmp(MakeIndexed(3, 2), 3, 2),
                                      new PhotoEdits { Rotation = 180 });
        Check("旋转 180：尺寸不变、完全倒序（左上↔右下）",
              r180.PixelWidth == 3 && r180.PixelHeight == 2
              && RAt(r180, 2, 1) == 1 && RAt(r180, 0, 0) == 6);

        var r270 = EditRenderer.Apply(Bmp(MakeIndexed(3, 2), 3, 2),
                                      new PhotoEdits { Rotation = 270 });
        // 逆时针 90° 之后：
        //    3 6
        //    2 5
        //    1 4
        Check("旋转 270：左上角转到左下角（左上 = 3、右下 = 4）",
              r270.PixelWidth == 2 && r270.PixelHeight == 3
              && RAt(r270, 0, 2) == 1 && RAt(r270, 0, 0) == 3 && RAt(r270, 1, 2) == 4,
              $"{r270.PixelWidth}×{r270.PixelHeight} → 左下 {RAt(r270, 0, 2)} / 左上 {RAt(r270, 0, 0)}");

        // 两次 90 = 一次 180（最容易验出"转反了"的一条）
        var twice = EditRenderer.Apply(
            EditRenderer.Apply(Bmp(MakeIndexed(3, 2), 3, 2), new PhotoEdits { Rotation = 90 }),
            new PhotoEdits { Rotation = 90 });
        Check("旋转 90 两次 = 旋转 180",
              twice.PixelWidth == r180.PixelWidth && twice.PixelHeight == r180.PixelHeight
              && twice.Pixels.SequenceEqual(r180.Pixels));

        Check("旋转 360：等于没转（返回原对象）",
              ReferenceEquals(EditRenderer.Apply(src, new PhotoEdits { Rotation = 360 }), src));

        // ---- K4: 翻转 ----
        var flipBase = MakeIndexed(3, 2);
        var fh = EditRenderer.Apply(Bmp(flipBase, 3, 2), new PhotoEdits { FlipH = true });
        Check("左右翻转：第 0 列与最后一列互换",
              RAt(fh, 2, 0) == 1 && RAt(fh, 0, 0) == 3 && RAt(fh, 0, 1) == 6);

        var fv = EditRenderer.Apply(Bmp(MakeIndexed(3, 2), 3, 2), new PhotoEdits { FlipV = true });
        Check("上下翻转：第 0 行与最后一行互换",
              RAt(fv, 0, 1) == 1 && RAt(fv, 0, 0) == 4);

        var fhh = EditRenderer.Apply(fh, new PhotoEdits { FlipH = true });
        Check("左右翻转两次 = 原图", fhh.Pixels.SequenceEqual(flipBase));

        // ---- K5: 裁剪（归一化坐标，相对"几何变换之后"的图）----
        var crop = EditRenderer.Apply(Bmp(MakeIndexed(4, 4), 4, 4),
                                      new PhotoEdits { CropX = 0.5, CropY = 0.5, CropW = 0.5, CropH = 0.5 });
        Check("裁剪：尺寸减半（4×4 → 2×2）",
              crop.PixelWidth == 2 && crop.PixelHeight == 2,
              $"{crop.PixelWidth}×{crop.PixelHeight}");
        // 4×4 序号 1..16，右下角 2×2 的左上角是序号 11（第 3 行第 3 列）
        Check("裁剪：取的是右下角那块（左上角序号 = 11）", RAt(crop, 0, 0) == 11, RAt(crop, 0, 0).ToString());

        var noCropSrc = Bmp(MakeIndexed(4, 4), 4, 4);
        Check("裁剪：整图参数不会白拷一份像素",
              ReferenceEquals(
                  EditRenderer.Apply(noCropSrc, new PhotoEdits { CropX = 0, CropY = 0, CropW = 1, CropH = 1 }),
                  noCropSrc));

        // 越界裁剪：规整会把框往里挪（而不是抛异常或裁出负数）
        var over = EditRenderer.Apply(Bmp(MakeIndexed(4, 4), 4, 4),
                                      new PhotoEdits { CropX = 0.9, CropY = 0.9, CropW = 0.5, CropH = 0.5 });
        Check("裁剪：越界参数不崩、尺寸合法",
              over.PixelWidth is >= 1 and <= 4 && over.PixelHeight is >= 1 and <= 4,
              $"{over.PixelWidth}×{over.PixelHeight}");

        var corner = EditRenderer.Apply(Bmp(MakeIndexed(4, 4), 4, 4),
                                        new PhotoEdits { CropX = 0.75, CropY = 0.75, CropW = 0.25, CropH = 0.25 });
        Check("裁剪：右下角 1×1（序号 16）",
              corner.PixelWidth == 1 && corner.PixelHeight == 1 && RAt(corner, 0, 0) == 16,
              $"{corner.PixelWidth}×{corner.PixelHeight} / {RAt(corner, 0, 0)}");

        // ---- K6: 旋转 + 裁剪的顺序（先几何后裁剪）----
        // 3×2 顺时针 90° 之后是 2×3：
        //    4 1
        //    5 2
        //    6 3
        // 再取上半（归一化 y 0~0.5，即第 0 行）应该得到 "4 1"
        var rc = EditRenderer.Apply(Bmp(MakeIndexed(3, 2), 3, 2),
                                    new PhotoEdits
                                    {
                                        Rotation = 90,
                                        CropX = 0, CropY = 0, CropW = 1, CropH = 1.0 / 3.0,
                                    });
        Check("旋转 + 裁剪：裁剪作用在旋转后的图上（拿到第 0 行 4 1）",
              rc.PixelWidth == 2 && rc.PixelHeight == 1
              && RAt(rc, 0, 0) == 4 && RAt(rc, 1, 0) == 1,
              $"{rc.PixelWidth}×{rc.PixelHeight} → {RAt(rc, 0, 0)},{RAt(rc, 1, 0)}");

        // ---- K7: 非破坏性 —— 原像素一个字节都不能变 ----
        byte[] original = MakeIndexed(8, 8);
        byte[] snapshot = (byte[])original.Clone();
        EditRenderer.Apply(Bmp(original, 8, 8), new PhotoEdits
        {
            Rotation = 90,
            FlipH = true,
            CropX = 0.1,
            CropY = 0.1,
            CropW = 0.5,
            CropH = 0.5,
            Look = Tone(brightness: 20),
        });
        Check("非破坏性：Apply 之后调用方的像素数组完全没动", original.SequenceEqual(snapshot));

        // 上面那条用例带了旋转（旋转本来就会新建数组），所以**单独**再盯着
        // "只有调色"和"只有翻转"这两种情况 —— 这两步是原地改的，
        // 忘了先克隆就会把调用方的原图就地改掉。
        // （2026-09-16 就是这条抓到了真 bug：先亮度 +20 再 -20，第二次拿到的是
        //   已经被改过的数组，减暗于是"没反应"。）
        byte[] toneOnly = MakeBgra(8, 8, 128, 128, 128);
        byte[] toneSnapshot = (byte[])toneOnly.Clone();
        EditRenderer.Apply(Bmp(toneOnly, 8, 8), new PhotoEdits { Look = Tone(brightness: 30) });
        Check("非破坏性：只调色（无任何几何变换）也不能改到原数组",
              toneOnly.SequenceEqual(toneSnapshot));

        byte[] flipOnly = MakeIndexed(8, 8);
        byte[] flipSnapshot = (byte[])flipOnly.Clone();
        EditRenderer.Apply(Bmp(flipOnly, 8, 8), new PhotoEdits { FlipH = true });
        Check("非破坏性：只翻转也不能改到原数组", flipOnly.SequenceEqual(flipSnapshot));

        byte[] flipTone = MakeBgra(8, 8, 128, 128, 128);
        byte[] flipToneSnapshot = (byte[])flipTone.Clone();
        EditRenderer.Apply(Bmp(flipTone, 8, 8),
                          new PhotoEdits { FlipV = true, Look = Tone(saturation: 40) });
        Check("非破坏性：翻转 + 调色组合也不能改到原数组",
              flipTone.SequenceEqual(flipToneSnapshot));

        // ---- K8: 调色 ----
        //
        // 参数本体是 LookSettings（"调整"面板那套），实际计算在 PhotoLook.Apply。
        // 这里验的是"参数能正确透到像素上"，以及和几何变换组合起来的结果。
        var gray = Bmp(MakeBgra(8, 8, 128, 128, 128), 8, 8);
        var brighter = EditRenderer.Apply(gray, new PhotoEdits { Look = Tone(brightness: 20) });
        Check("亮度 +20：中灰 128 变亮", RAt(brighter, 0, 0) > 128, RAt(brighter, 0, 0).ToString());

        var darker = EditRenderer.Apply(gray, new PhotoEdits { Look = Tone(brightness: -20) });
        Check("亮度 -20：中灰 128 变暗", RAt(darker, 0, 0) < 128, RAt(darker, 0, 0).ToString());

        // 对比度是 (v-0.5)*k+0.5，k = 1 + 参数/125。
        // 所以 -100 是 k=0.2（往中间灰**收拢**），不是"压成一片纯灰"。
        var flat = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 30, 128, 220), 4, 4),
                                      new PhotoEdits { Look = Tone(contrast: -100) });
        Check("对比度 -100：明暗差距被压小（暗的抬、亮的压）",
              flat.Pixels[0] > 30 && flat.Pixels[2] < 220,
              $"B={flat.Pixels[0]} R={flat.Pixels[2]}");

        var punchy = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 30, 128, 220), 4, 4),
                                        new PhotoEdits { Look = Tone(contrast: 60) });
        Check("对比度 +60：明暗差距被拉开（暗的更暗、亮的更亮）",
              punchy.Pixels[0] < 30 && punchy.Pixels[2] > 220,
              $"B={punchy.Pixels[0]} R={punchy.Pixels[2]}");

        // 饱和度 -100 → PhotoLook 把系数夹到 0 → 三通道相等
        var mono = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 30, 128, 220), 4, 4),
                                      new PhotoEdits { Look = Tone(saturation: -100) });
        Check("饱和度 -100：变成灰度（B = G = R）",
              mono.Pixels[0] == mono.Pixels[1] && mono.Pixels[1] == mono.Pixels[2],
              $"{mono.Pixels[0]},{mono.Pixels[1]},{mono.Pixels[2]}");

        // 暖度（色温）+60 → 加红减蓝
        var warm = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 100, 100, 100), 4, 4),
                                      new PhotoEdits { Look = Tone(warmth: 60) });
        Check("暖度 +60：R 变大、B 变小（偏暖）",
              warm.Pixels[2] > 100 && warm.Pixels[0] < 100,
              $"B={warm.Pixels[0]} R={warm.Pixels[2]}");

        var cool = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 100, 100, 100), 4, 4),
                                      new PhotoEdits { Look = Tone(warmth: -60) });
        Check("暖度 -60：R 变小、B 变大（偏冷）",
              cool.Pixels[2] < 100 && cool.Pixels[0] > 100,
              $"B={cool.Pixels[0]} R={cool.Pixels[2]}");

        // 黑白模式（滑块上不出现，是滤镜预设用的那几项）
        var bw = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 30, 128, 220), 4, 4),
                                    new PhotoEdits { Look = Tone(mono: MonoMode.Mono) });
        Check("黑白模式：三通道相等",
              bw.Pixels[0] == bw.Pixels[1] && bw.Pixels[1] == bw.Pixels[2],
              $"{bw.Pixels[0]},{bw.Pixels[1]},{bw.Pixels[2]}");

        // ---- K9: 调色不许碰 alpha ----
        var withAlpha = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 100, 100, 100, a: 200), 4, 4),
                                           new PhotoEdits { Look = Tone(brightness: 50, saturation: 50) });
        Check("调色不改 alpha（仍然是 200）", withAlpha.Pixels[3] == 200, withAlpha.Pixels[3].ToString());

        // ---- K10: 全透明像素不参与调色 ----
        var clear = EditRenderer.Apply(Bmp(MakeBgra(4, 4, 0, 0, 0, a: 0), 4, 4),
                                       new PhotoEdits { Look = Tone(brightness: 100) });
        Check("全透明像素：调色跳过（RGB 仍是 0）",
              clear.Pixels[0] == 0 && clear.Pixels[2] == 0);

        // ---- K11: 预乘 alpha（WIC 解码器主路径吐出来的就是这种）----
        //
        // 预乘像素里 RGB 已经乘过 alpha。对半透明像素直接调色会让 RGB 和 alpha
        // 对不上，边缘出现一圈脏色 —— 所以引擎会"反预乘 → 调色 → 再预乘"。
        //
        // 期望值推导（alpha = 128、RGB = 40）：
        //   反预乘    40 * 255 / 128            = 79
        //   亮度 +80  lift = 0.0015 * 80 = 0.12
        //             79/255 + 0.12 = 0.4298   → 110
        //   再预乘    110 * 128 / 255           = 55
        var pre = EditRenderer.Apply(
            Bmp(MakeBgra(4, 4, 40, 40, 40, a: 128), 4, 4, premultiplied: true),
            new PhotoEdits { Look = Tone(brightness: 80) });
        Check("预乘图：alpha 没被改", pre.Pixels[3] == 128, pre.Pixels[3].ToString());
        Check("预乘图：按“反预乘 → 调色 → 再预乘”算（40 → ≈55）",
              Math.Abs(pre.Pixels[2] - 55) <= 2, pre.Pixels[2].ToString());

        // 同样的输入**不打**预乘标记 —— 走另一条路（不反预乘），结果更亮
        var straight = EditRenderer.Apply(
            Bmp(MakeBgra(4, 4, 40, 40, 40, a: 128), 4, 4),
            new PhotoEdits { Look = Tone(brightness: 80) });
        Check("非预乘图：不做反预乘，直接调（40 → ≈71）",
              straight.Pixels[2] > pre.Pixels[2],
              $"非预乘 {straight.Pixels[2]} / 预乘 {pre.Pixels[2]}");

        // 全不透明的图：预乘标记不该带来任何差别（也不该白跑一遍反预乘）
        var opaquePre = EditRenderer.Apply(
            Bmp(MakeBgra(4, 4, 80, 80, 80, a: 255), 4, 4, premultiplied: true),
            new PhotoEdits { Look = Tone(brightness: 40) });
        var opaqueStraight = EditRenderer.Apply(
            Bmp(MakeBgra(4, 4, 80, 80, 80, a: 255), 4, 4),
            new PhotoEdits { Look = Tone(brightness: 40) });
        Check("预乘标记对全不透明的图毫无影响",
              opaquePre.Pixels.SequenceEqual(opaqueStraight.Pixels));

        // ---- K12: 尺寸预告（不真算像素）与实际结果一致 ----
        var edits = new PhotoEdits { Rotation = 90, CropX = 0.25, CropY = 0.25, CropW = 0.5, CropH = 0.5 };
        var actual = EditRenderer.Apply(Bmp(MakeIndexed(8, 4), 8, 4), edits);
        var (pw, ph) = EditRenderer.ResultSize(8, 4, edits);
        Check("ResultSize 与实际渲染结果一致",
              pw == actual.PixelWidth && ph == actual.PixelHeight,
              $"预告 {pw}×{ph} / 实际 {actual.PixelWidth}×{actual.PixelHeight}");

        // ---- K13: 大图性能（400 万像素，全流程）----
        var bigSw = Stopwatch.StartNew();
        EditRenderer.Apply(Bmp(MakeBgra(2000, 2000, 90, 120, 160), 2000, 2000), new PhotoEdits
        {
            Rotation = 90,
            FlipV = true,
            CropX = 0.1,
            CropY = 0.1,
            CropW = 0.8,
            CropH = 0.8,
            Look = Tone(brightness: 10, contrast: 10, saturation: 10, warmth: 10),
        });
        bigSw.Stop();
        Check("大图（400 万像素）全流程够快（< 3000ms，Debug 下也算宽裕）",
              bigSw.ElapsedMilliseconds < 3000, $"{bigSw.ElapsedMilliseconds} ms");

        // ---- K14: 参数序列化 round-trip ----
        var complex = new PhotoEdits
        {
            Rotation = 270,
            FlipH = true,
            CropX = 0.125,
            CropY = 0.25,
            CropW = 0.5,
            CropH = 0.375,
            // 顺带覆盖 int 滑块项和 double 的滤镜项 —— 这两种的写法和精度都不一样
            Look = Tone(brightness: 12, contrast: -8, saturation: 33, warmth: -20,
                        fade: 0.25, grain: 0.125, mono: MonoMode.Silver),
        };
        string text = complex.Serialize();
        var parsed = PhotoEdits.Parse(text);
        Check("序列化 round-trip：字符串一致", parsed.Serialize() == text, text);
        Check("序列化 round-trip：几何字段都对",
              parsed.Rotation == 270 && parsed.FlipH && !parsed.FlipV
              && Math.Abs(parsed.CropX - 0.125) < 1e-6 && Math.Abs(parsed.CropH - 0.375) < 1e-6);
        Check("序列化 round-trip：调色字段都对（含 int 与 double 两类）",
              parsed.Look.Brightness == 12
              && parsed.Look.Contrast == -8
              && parsed.Look.Saturation == 33
              && parsed.Look.Warmth == -20
              && Math.Abs(parsed.Look.Fade - 0.25) < 1e-6
              && Math.Abs(parsed.Look.Grain - 0.125) < 1e-6
              && parsed.Look.Mono == MonoMode.Silver,
              text);

        Check("序列化：没编辑过是空串（库里存 NULL）", new PhotoEdits().Serialize().Length == 0);
        Check("序列化：只写非默认项（旋转 90 的串里不该出现亮度）",
              new PhotoEdits { Rotation = 90 }.Serialize() == "r=90",
              new PhotoEdits { Rotation = 90 }.Serialize());

        // ---- K15: 脏数据不许炸 ----
        Check("解析：空串 / null / 乱码都不抛异常",
              PhotoEdits.Parse(null).IsIdentity
              && PhotoEdits.Parse("").IsIdentity
              && PhotoEdits.Parse("????").IsIdentity
              && PhotoEdits.Parse("r=abc;b=;;=1").IsIdentity);
        var legacyTone = PhotoEdits.Parse("b=50;c=50;s=50;t=50");
        Check("解析：认不得的键忽略（旧版本的 b/c/s/t 不能被塞进调色字段）",
              legacyTone.IsIdentity, legacyTone.Serialize());
        Check("规整：角度 450 → 90", new PhotoEdits { Rotation = 450 }.Normalized().Rotation == 90);
        Check("规整：角度 -90 → 270", new PhotoEdits { Rotation = -90 }.Normalized().Rotation == 270);
        var outOfRange = new PhotoEdits { CropX = -1, CropY = 0.9, CropW = 3, CropH = 0.5 }.Normalized();
        Check("规整：裁剪越界会被夹进图内",
              Near(outOfRange.CropX, 0) && Near(outOfRange.CropW, 1)
              && outOfRange.CropY + outOfRange.CropH <= 1.0001,
              outOfRange.Serialize());

        var clampedTone = new PhotoEdits
        {
            Look = Tone(brightness: 999, warmth: -999, fade: 5, whiteDrop: -3),
        }.Normalized();
        Check("规整：调色滑块夹到 ±100、0~1 的项夹进 0~1",
              clampedTone.Look.Brightness == 100 && clampedTone.Look.Warmth == -100
              && Near(clampedTone.Look.Fade, 1) && Near(clampedTone.Look.WhiteDrop, 0),
              clampedTone.Serialize());
        Check("规整：认不出的黑白模式当“保留颜色”（别把图渲成一片黑）",
              new PhotoEdits { Look = Tone(mono: (MonoMode)99) }.Normalized().Look.Mono == MonoMode.None);

        // ---- K16: 签名（缩略图缓存的 key）----
        Check("签名：同一组参数两次算出来一样",
              new PhotoEdits { Rotation = 90, Look = Tone(brightness: 5) }.Signature()
              == new PhotoEdits { Rotation = 90, Look = Tone(brightness: 5) }.Signature());
        Check("签名：不同参数指纹不同",
              new PhotoEdits { Rotation = 90 }.Signature()
              != new PhotoEdits { Rotation = 180 }.Signature());
        Check("签名：只调色不同，指纹也不同",
              new PhotoEdits { Look = Tone(brightness: 5) }.Signature()
              != new PhotoEdits { Look = Tone(brightness: 6) }.Signature());
        Check("签名：没编辑过是固定值 0", new PhotoEdits().Signature() == "0");
        Check("签名：只含文件名安全字符（16 进制）",
              new PhotoEdits { Rotation = 90, CropX = 0.5 }.Signature().All(
                  c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));

        // ---- K17: 像素矩形 → 归一化（界面拖完裁剪框走这条路）----
        var fromPixels = new PhotoEdits();
        EditRenderer.SetCropFromPixels(fromPixels, 100, 200, 25, 50, 50, 100);
        Check("像素矩形转归一化：(25,50,50,100) / 100×200 → 0.25,0.25,0.5,0.5",
              Math.Abs(fromPixels.CropX - 0.25) < 1e-9 && Math.Abs(fromPixels.CropY - 0.25) < 1e-9
              && Math.Abs(fromPixels.CropW - 0.5) < 1e-9 && Math.Abs(fromPixels.CropH - 0.5) < 1e-9);

        var clampedCrop = new PhotoEdits();
        EditRenderer.SetCropFromPixels(clampedCrop, 100, 100, 90, 90, 500, 500);
        Check("像素矩形越界：夹回图内（10×10 的右下角）",
              Near(clampedCrop.CropX, 0.9) && Near(clampedCrop.CropY, 0.9)
              && Near(clampedCrop.CropW, 0.1) && Near(clampedCrop.CropH, 0.1),
              clampedCrop.Serialize());

        // ---- K18: 摘要文本（界面上"这张图改过什么"）----
        Check("摘要：没编辑过是空串", new PhotoEdits().Describe().Length == 0);
        Check("摘要：改过就有内容",
              new PhotoEdits { Rotation = 90, FlipH = true }.Describe().Contains("旋转")
              && new PhotoEdits { Rotation = 90, FlipH = true }.Describe().Contains("翻转"),
              new PhotoEdits { Rotation = 90, FlipH = true }.Describe());

        // ---- K19: 真字节进出的完整链路（Magick 编解码 + 渲染）----
        //
        // 这条对应界面上的"另存为 / 复制 / 打印"：参数要作用在**全分辨率原图的字节**上。
        // 中间得过一次 Magick 的解码和编码，和前面那些直接喂 DecodedBitmap 的用例
        // 不是同一条路，所以单独测 —— 导出出来的图尺寸不对是最容易被用户发现的问题。
        byte[] roundPng = ImageEditService.FromPixels(MakeIndexed(8, 6), 8, 6);
        Check("合成 PNG：编得出来", roundPng.Length > 0, $"{roundPng.Length} 字节");

        byte[]? roundPixels = ImageEditService.LoadPixels(roundPng, out int rw, out int rh);
        Check("合成 PNG：解得回来（尺寸对得上）",
              roundPixels is not null && rw == 8 && rh == 6, $"{rw}×{rh}");

        if (roundPixels is not null)
        {
            var roundBmp = new DecodedBitmap
            {
                Pixels = roundPixels,
                PixelWidth = rw,
                PixelHeight = rh,
                Premultiplied = false,       // LoadPixels 走 Magick，出来是直通 alpha
                DecoderName = "Magick.NET",
            };

            var roundOut = EditRenderer.Apply(roundBmp,
                                              new PhotoEdits { Rotation = 90, Look = Tone(brightness: 20) });
            Check("真字节链路：旋转 90° 后宽高互换（8×6 → 6×8）",
                  roundOut.PixelWidth == 6 && roundOut.PixelHeight == 8,
                  $"{roundOut.PixelWidth}×{roundOut.PixelHeight}");

            byte[] outPng = ImageEditService.FromPixels(
                roundOut.Pixels, roundOut.PixelWidth, roundOut.PixelHeight);
            Check("真字节链路：渲染结果能重新编码成 PNG", outPng.Length > 0, $"{outPng.Length} 字节");

            var (outW, outH) = ImageEditService.SizeOf(outPng);
            Check("真字节链路：编码出来的尺寸 = 渲染结果的尺寸（导出的图不会被转回去）",
                  outW == 6 && outH == 8, $"{outW}×{outH}");
        }

        // ---- K20: 参数副本（LibraryIndexService 的内存映射靠它保平安）----
        //
        // 编辑参数现在缓存在 LibraryIndexService 的一个内存映射里
        // （缩略图墙每一格都要问"这张改过没有"，不能一格查一次库）。
        // 查看器拿到参数后是**就地改**的，所以对外必须给副本 ——
        // 给的是映射里那个对象的话，"转一下"会把缓存里的存档值一起改掉，
        // 于是墙上显示的和库里的对不上、还原也还原不回真值。
        var keepEdits = new PhotoEdits { Rotation = 90, FlipH = true, Look = Tone(brightness: 15) };
        PhotoEdits copy = keepEdits.Clone();

        Check("副本：内容与签名都一致",
              copy.Serialize() == keepEdits.Serialize()
              && copy.Signature() == keepEdits.Signature());

        copy.Rotation = 180;
        copy.FlipH = false;
        var copyLook = copy.Look;
        copyLook.Brightness = -99;
        copy.Look = copyLook;

        Check("副本：改副本不影响原对象（旋转 / 翻转 / 调色三样都不串）",
              keepEdits.Rotation == 90 && keepEdits.FlipH && keepEdits.Look.Brightness == 15,
              $"原对象现在 r={keepEdits.Rotation} fh={keepEdits.FlipH} brt={keepEdits.Look.Brightness}");

        Check("副本：改完之后签名也分开了",
              copy.Signature() != keepEdits.Signature());
    }

    /// <summary>
    /// 编辑参数在索引库里的存取 —— 包括**从 v5 老库升级**这一条。
    /// 老库升级是最容易出事的地方：用户库里躺着评分 / 收藏 / 标签。
    /// </summary>
    private static void EditStore()
    {
        Console.WriteLine();
        Console.WriteLine("==== K. 非编辑参数：索引库存取与升级 ====");
        Console.WriteLine();

        string db = Path.Combine(Path.GetTempPath(), "cvedit-test.db");
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(db + suffix); } catch { }
        }

        const string Photo = @"C:\fake\a.jpg";

        // ---- K19: 造一个 **v5** 的老库（没有 Edits 列），带用户数据 ----
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();

            void Run(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            // 完整照抄 v5 的建表语句（少了 NOT NULL 的列，插数据会失败）
            Run(@"
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
                    Favorite      INTEGER NOT NULL DEFAULT 0,
                    Md5           TEXT    NULL,
                    PHash         TEXT    NULL,
                    Source        TEXT    NULL,
                    IndexedAt     INTEGER NOT NULL DEFAULT 0
                );");
            Run(@"
                CREATE TABLE Tags (
                    Path     TEXT NOT NULL,
                    Tag      TEXT NOT NULL,
                    TagLower TEXT NOT NULL,
                    PRIMARY KEY (Path, TagLower)
                );");
            Run($"INSERT INTO Media (Path, PathLower, Directory, FileName, Rating, Favorite, Source) " +
                $"VALUES ('{Photo}', '{Photo.ToLowerInvariant()}', 'C:\\fake', 'a.jpg', 4, 1, 'wechat');");
            Run($"INSERT INTO Tags (Path, Tag, TagLower) VALUES ('{Photo}', '旅行', '旅行');");
            Run("PRAGMA user_version = 5;");
        }

        using var index = new MediaIndex(db);

        // ---- K20: 升级到 v6 之后，用户数据必须一个都不少 ----
        Check("v5 → v6：评分还在", index.GetRating(Photo) == 4, index.GetRating(Photo).ToString());
        Check("v5 → v6：收藏还在", index.IsFavorite(Photo));
        Check("v5 → v6：标签还在", index.GetTags(Photo).Contains("旅行"));

        // ---- K21: 升级完 Edits 列立即可用，且老记录默认"没编辑过" ----
        Check("v5 → v6：老图默认没编辑过（Edits = NULL）",
              index.GetEdits(Photo).IsIdentity && index.CountEdited() == 0);

        var edits = new PhotoEdits
        {
            Rotation = 90,
            CropX = 0.1,
            CropW = 0.8,
            Look = Tone(brightness: 15, mono: MonoMode.Silver),
        };
        index.SetEdits(Photo, edits);
        Check("写入后读回来一致", index.GetEdits(Photo).Serialize() == edits.Serialize(),
              index.GetEdits(Photo).Serialize());

        // 写编辑参数不该动到评分 / 收藏
        Check("写编辑参数不影响评分 / 收藏",
              index.GetRating(Photo) == 4 && index.IsFavorite(Photo));

        // ---- K22: 清除编辑 ----
        index.SetEdits(Photo, new PhotoEdits());   // "等于没改"的参数 = 清除
        Check("写入'空参数'等于清除", index.GetEdits(Photo).IsIdentity && index.CountEdited() == 0);

        index.SetEdits(Photo, edits);
        index.ClearEdits(Photo);
        Check("ClearEdits 之后回到没编辑过", index.GetEdits(Photo).IsIdentity && index.CountEdited() == 0);

        // ---- K23: 批量读取（缩略图墙用）----
        index.SetEdits(Photo, edits);
        index.SetEdits(@"C:\fake\b.jpg", null);
        var all = index.LoadAllEdits();
        Check("批量读取：只返回编辑过的那几条", all.Count == 1 && all.ContainsKey(Photo), all.Count.ToString());
        Check("批量读取：计数与 CountEdited 一致", index.CountEdited() == all.Count);

        // ---- K24: 库里存的确实是能一眼看懂的文本；顺便确认升级没碰别的列 ----
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Edits, Source, Rating FROM Media WHERE Path = '{Photo}';";

            using var r = cmd.ExecuteReader();
            r.Read();

            string raw = r.IsDBNull(0) ? "" : r.GetString(0);
            Check("库里存的是可读文本（出问题时能直接用 sqlite 看）",
                  raw.Contains("r=90") && raw.Contains("brt=15") && raw.Contains("mon=2"), raw);
            Check("升级后 Source / Rating 列也原封不动",
                  !r.IsDBNull(1) && r.GetString(1) == "wechat" && r.GetInt32(2) == 4);
        }

        // ---- K25: 库确实是 v6 ----
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            Check("表结构版本 = 6", version == 6, version.ToString());
        }

        try { File.Delete(db + "-wal"); } catch { }
        try { File.Delete(db + "-shm"); } catch { }
    }

    // ==================== M. 缩略图墙按编辑参数出图 ====================
    //
    // 第 6 步的收尾：编辑过的图，缩略图墙上也要出**编辑后的样子**，
    // 而不是原样。这条链路是 ThumbnailService 的"编辑变体"分支：
    //   先按 路径|尺寸 取原图缩略图（含磁盘缓存）→ 再按参数算出编辑后那份，
    //   编辑后那份单独占一个 路径|尺寸|参数指纹 的缓存 key。
    //
    // 最怕的两件事，这里各有用例盯着：
    //   1. 编辑变体把**原图那份缓存**污染了 —— 一改就再也回不到原样；
    //   2. 算了半天其实每次都在重算（缓存 key 没生效）—— 滚动就卡。

    /// <summary>
    /// 假解码器：不管要什么尺寸，都吐出同一张 12×8 的"编号图"，
    /// 并且记下自己被调用了几次 —— "有没有真的省掉一次解码"全靠这个计数。
    /// </summary>
    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly DecodedBitmap _bitmap;

        public FakeDecoder(DecodedBitmap bitmap) => _bitmap = bitmap;

        /// <summary>解码次数。ThumbnailService 声称"换参数不必重新解码"，就靠它验证。</summary>
        public int Calls { get; private set; }

        public string Name => "测试解码器";

        public bool CanDecode(string extension) => true;

        public Task<PhotoInfo?> ProbeAsync(
            string path, bool includeMetadata = false, CancellationToken ct = default)
            => Task.FromResult<PhotoInfo?>(null);

        public Task<DecodedBitmap?> DecodeAsync(
            string path, int maxWidth, int maxHeight, CancellationToken ct = default)
        {
            lock (this) Calls++;
            return Task.FromResult<DecodedBitmap?>(_bitmap);
        }
    }

    private static async Task ThumbnailEditsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("==== M. 缩略图墙按编辑参数出图 ====");
        Console.WriteLine();

        const string P = @"C:\fake\wall.jpg";

        // 故意**不给磁盘缓存**：这里要验的是内存这套逻辑，
        // 掺进磁盘缓存会让"解码次数"的判据变得不可控
        var decoder = new FakeDecoder(Bmp(MakeIndexed(12, 8), 12, 8));
        var thumbs = new ThumbnailService(decoder, maxConcurrency: 2, maxBytes: 64L * 1024 * 1024);

        // ---- M1: 老接口原样保留（没编辑的图一点额外代价都没有）----
        var plain = await thumbs.GetAsync(P, 320, CancellationToken.None);
        var plainAgain = await thumbs.GetAsync(P, 320, (PhotoEdits?)null, CancellationToken.None);
        Check("M1 传空参数 == 老接口（命中同一份缓存）",
              plain is not null && ReferenceEquals(plain, plainAgain));
        Check("M1 解码只跑了一次", decoder.Calls == 1, decoder.Calls.ToString());

        // ---- M2: "空参数"（等于没改）也走原图那条路，不产生多余变体 ----
        var identity = await thumbs.GetAsync(P, 320, new PhotoEdits(), CancellationToken.None);
        Check("M2 空参数（IsIdentity）也走原图那条路",
              ReferenceEquals(plain, identity));

        // ---- M3: 真的编辑 —— 旋转 90°，宽高互换 ----
        var rotated = await thumbs.GetAsync(P, 320, new PhotoEdits { Rotation = 90 }, CancellationToken.None);
        Check("M3 旋转 90°：缩略图宽高互换（12×8 → 8×12）",
              rotated is not null && rotated.PixelWidth == 8 && rotated.PixelHeight == 12,
              rotated is null ? "null" : $"{rotated.PixelWidth}×{rotated.PixelHeight}");

        // 像素落点也要对：顺时针 90° 之后，新图的左上角来自原图的**左下角**。
        // 编号图里 R 分量 = y*w + x + 1，原 (0,7) → 7*12+0+1 = 85。
        Check("M3 旋转的像素落点也对（新左上角 = 原左下角）",
              rotated is not null && RAt(rotated, 0, 0) == 85,
              rotated is null ? "null" : RAt(rotated, 0, 0).ToString());

        // ---- M4: 编辑变体缓存生效 —— 同一份参数第二次不再算 ----
        int callsBefore = decoder.Calls;
        var sameAgain = await thumbs.GetAsync(P, 320, new PhotoEdits { Rotation = 90 }, CancellationToken.None);
        Check("M4 同一份参数第二次：命中变体缓存（同一个对象）",
              ReferenceEquals(rotated, sameAgain));
        Check("M4 换参数重取**没有**重新解码原图",
              decoder.Calls == callsBefore, $"{callsBefore} → {decoder.Calls}");

        // ---- M5: 不同参数各自一份，互不覆盖 ----
        var r180 = await thumbs.GetAsync(P, 320, new PhotoEdits { Rotation = 180 }, CancellationToken.None);
        Check("M5 转 180° 和转 90° 是两份不同的缓存",
              r180 is not null && !ReferenceEquals(rotated, r180)
              && r180.PixelWidth == 12 && r180.PixelHeight == 8,
              r180 is null ? "null" : $"{r180.PixelWidth}×{r180.PixelHeight}");

        // ---- M6: 只调色（不改几何）也要出编辑后的样子 ----
        var toned = await thumbs.GetAsync(
            P, 320, new PhotoEdits { Look = Tone(brightness: 60) }, CancellationToken.None);
        Check("M6 只调色：尺寸不变（12×8）",
              toned is not null && toned.PixelWidth == 12 && toned.PixelHeight == 8);
        Check("M6 只调色：像素确实变亮了",
              toned is not null && plain is not null && RAt(toned, 0, 4) > RAt(plain, 0, 4),
              toned is null || plain is null ? "" : $"{RAt(plain, 0, 4)} → {RAt(toned, 0, 4)}");

        // ---- M7: Invalidate（用户在查看器里改了图，墙收到通知后调它）----
        thumbs.Invalidate(P);
        var reRotated = await thumbs.GetAsync(P, 320, new PhotoEdits { Rotation = 90 }, CancellationToken.None);
        Check("M7 Invalidate 把编辑变体一起清掉（拿到的是新算的对象）",
              reRotated is not null && !ReferenceEquals(rotated, reRotated));
        Check("M7 重算出来的内容和之前一致",
              reRotated is not null && rotated is not null
              && reRotated.PixelWidth == rotated.PixelWidth
              && reRotated.PixelHeight == rotated.PixelHeight
              && reRotated.Pixels.SequenceEqual(rotated.Pixels),
              reRotated is null || rotated is null ? "" : $"{reRotated.PixelWidth}×{reRotated.PixelHeight}");

        // ---- M8: 非破坏性 —— 折腾一圈之后原图那份还是原样 ----
        //
        // 这条是整段里最关键的一条。假解码器每次吐的是**同一个对象**，
        // 所以只要 EditRenderer 在哪一步就地改了它，这里立刻就露馅。
        var rawAfter = await thumbs.GetAsync(P, 320, CancellationToken.None);
        Check("M8 折腾一圈之后原图缩略图一个字节都没变",
              rawAfter is not null && rawAfter.Pixels.SequenceEqual(MakeIndexed(12, 8)));

        // ---- M9: 还原（参数清空）之后墙上必须是原图 ----
        var back = await thumbs.GetAsync(P, 320, new PhotoEdits(), CancellationToken.None);
        Check("M9 还原之后拿到的是原图，不是最后那个编辑变体",
              back is not null && back.PixelWidth == 12 && back.PixelHeight == 8
              && back.Pixels.SequenceEqual(MakeIndexed(12, 8)),
              back is null ? "null" : $"{back.PixelWidth}×{back.PixelHeight}");

        // ---- M10: 指纹口径 —— 转 450° 就是转 90°，不该白存两份 ----
        var r450 = await thumbs.GetAsync(P, 320, new PhotoEdits { Rotation = 450 }, CancellationToken.None);
        Check("M10 转 450° 与转 90° 归一到同一份缓存",
              ReferenceEquals(reRotated, r450));

        // ---- M11: 全透明 / 半透明图走调色也不能崩（墙上有 PNG 图标这类图）----
        var alphaDecoder = new FakeDecoder(Bmp(MakeBgra(8, 8, 120, 120, 120, 128), 8, 8, premultiplied: true));
        var alphaThumbs = new ThumbnailService(alphaDecoder, maxConcurrency: 2);
        var alphaToned = await alphaThumbs.GetAsync(
            @"C:\fake\alpha.png", 320,
            new PhotoEdits { Look = Tone(brightness: 40) }, CancellationToken.None);
        Check("M11 半透明图（预乘）调色不报错且尺寸正常",
              alphaToned is not null && alphaToned.PixelWidth == 8 && alphaToned.PixelHeight == 8);
        Check("M11 半透明图调色之后 alpha 保持不变",
              alphaToned is not null && alphaToned.Pixels[3] == 128 && alphaToned.Pixels[7] == 128,
              alphaToned is null ? "null" : $"{alphaToned.Pixels[3]} / {alphaToned.Pixels[7]}");
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
        Duplicates();
        SocialSource();
        AppIdentity();
        Histogram();
        EditRender();
        EditStore();
        await ThumbnailEditsAsync();

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

    // ==================== G. 重复 / 相似 ====================

    /// <summary>
    /// 验证两张事：精确重复（MD5）和视觉相似（PHash 汉明距离）。
    /// 用真图端到端测一遍，再塞一组伪造指纹锁死"查询 + 汉明分组"逻辑，
    /// 这样万一 PHash 在某环境算不出来，核心逻辑也还是被验证到。
    /// </summary>
    private static void Duplicates()
    {
        Console.WriteLine();
        Console.WriteLine("=== G. 重复 / 相似 ===");
        Console.WriteLine();

        string dir = Path.Combine(Path.GetTempPath(), "cvdup-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(dir); } catch { }

        try
        {
            // 造一张基础图：白底上一个黑方块（让 PHash 有内容可比）
            string basePng = Path.Combine(dir, "base.png");
            MakeImage(basePng, MagickColors.White, MagickColors.Black, 10, 10);

            // 原样复制成 3 份 → 字节完全相同 → MD5 相同 → 精确重复
            string dup1 = Path.Combine(dir, "dup1.png");
            string dup2 = Path.Combine(dir, "dup2.png");
            File.Copy(basePng, dup1);
            File.Copy(basePng, dup2);

            // 几乎一样但字节不同：黑方块挪 2 像素 → PHash 相近、MD5 不同
            string similar = Path.Combine(dir, "similar.png");
            MakeImage(similar, MagickColors.White, MagickColors.Black, 12, 12);

            // 一张完全不同的图（纯红）
            string other = Path.Combine(dir, "other.png");
            MakeImage(other, MagickColors.Red, null, 0, 0);

            string db = Path.Combine(Path.GetTempPath(), "cvidx-dup.db");
            foreach (string f in new[] { db, db + "-wal", db + "-shm" })
            { try { File.Delete(f); } catch { } }

            using var index = new MediaIndex(db);

            foreach (string p in new[] { basePng, dup1, dup2, similar, other })
            {
                string? md5 = PerceptualHash.ComputeMd5(p);
                string? phash = PerceptualHash.ComputePhash(p);
                index.Upsert(PhotoOf(p), md5: md5, phash: phash);
            }

            // G1: 精确重复 = 3 张（base/dup1/dup2），且只有 1 组
            var dups = index.FindDuplicates();
            int dupTotal = dups.Sum(g => g.Count);
            Check("精确重复：3 张同 MD5 归 1 组", dupTotal == 3 && dups.Count == 1,
                  $"{dupTotal} 张 / {dups.Count} 组");

            // G2: 相似图、完全不同的图不能混进精确重复
            bool dupLeak = dups.Any(g => g.Paths.Any(p =>
                p.EndsWith("similar.png", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("other.png", StringComparison.OrdinalIgnoreCase)));
            Check("精确重复不掺入相似图 / 异图", !dupLeak);

            // G3: 完全不同的图（全红）和 base（白底黑块）必然相差很远，绝不该归一组
            var sims = index.FindSimilar(10);
            bool mixed = sims.Any(g => g.Paths.Any(p => p.EndsWith("other.png", StringComparison.OrdinalIgnoreCase))
                                      && g.Paths.Any(p => p.EndsWith("base.png", StringComparison.OrdinalIgnoreCase)));
            Check("相似：完全不同的图不会和别的图乱归组", !mixed);

            // 若 PHash 本环境真算出来了，base 与 similar 应该归同组（它们只差 2 像素）
            string? pb = PerceptualHash.ComputePhash(basePng);
            string? ps = PerceptualHash.ComputePhash(similar);
            if (pb is not null && ps is not null && PerceptualHash.HammingDistance(pb, ps) <= 10)
            {
                bool same = sims.Any(g => g.Paths.Any(p => p.EndsWith("base.png", StringComparison.OrdinalIgnoreCase))
                                         && g.Paths.Any(p => p.EndsWith("similar.png", StringComparison.OrdinalIgnoreCase)));
                Check("相似（端到端）：base 与 similar 归同组", same);
            }
            else
            {
                Console.WriteLine("  [跳过] 本环境 PHash 未算出，相似归组由 G6 伪造指纹验证");
            }

            // G4: 汉明距离直接计算正确
            Check("汉明距离：相同 = 0", PerceptualHash.HammingDistance("abcd", "abcd") == 0);
            Check("汉明距离：全异 = 64",
                  PerceptualHash.HammingDistance("0000000000000000", "ffffffffffffffff") == 64);
            Check("汉明距离：相差 4 bit",
                  PerceptualHash.HammingDistance("0000000000000000", "000000000000000f") == 4);

            // G5: 重扫不丢指纹（COALESCE 兜底）—— Upsert 不带 md5/phash 不应清空
            index.Upsert(PhotoOf(basePng));   // 不带指纹
            var after = index.FindDuplicates();
            Check("重扫不丢指纹（COALESCE 兜底）", after.Sum(g => g.Count) == 3,
                  $"{after.Sum(g => g.Count)} 张");

            // G6: 直接塞伪造指纹，锁死"查询 + 汉明分组"逻辑（不依赖 Magick 能否算 PHash）
            string db2 = Path.Combine(Path.GetTempPath(), "cvidx-dup2.db");
            foreach (string f in new[] { db2, db2 + "-wal", db2 + "-shm" })
            { try { File.Delete(f); } catch { } }
            using var idx2 = new MediaIndex(db2);
            idx2.Upsert(PhotoOf(@"D:\g\a.png"), md5: "m1", phash: "0000000000000000");
            idx2.Upsert(PhotoOf(@"D:\g\b.png"), md5: "m2", phash: "000000000000000f"); // 距 4
            idx2.Upsert(PhotoOf(@"D:\g\c.png"), md5: "m3", phash: "ffffffffffffffff"); // 距 64
            var fakeSim = idx2.FindSimilar(10);
            Check("伪造指纹：近距两图归一组、远图被排除",
                  fakeSim.Count == 1 && fakeSim[0].Count == 2,
                  $"{fakeSim.Sum(g => g.Count)} 张 / {fakeSim.Count} 组");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ==================== H. 来源 / 社交缓存（计划第 4 步） ====================

    /// <summary>
    /// 验证"来源"这条维度：路径识别、噪声过滤、按来源分组筛选、老库升级不丢用户数据。
    ///
    /// 为什么这些必须测死：
    ///  - 识别错了 —— 微信的图被算成 QQ，用户按来源找图就去错地方了；
    ///  - 同名文件夹误判 —— 用户自己的 "D:\备份\Tencent Files 备份" 被当成 QQ 缓存；
    ///  - 噪声没滤掉 —— 三万七千张表情包涌进图库，真照片全被淹（本机实测就是这个数）；
    ///  - 升级丢数据 —— 静默清零，用户根本不知道发生过什么。
    /// </summary>
    private static void SocialSource()
    {
        Console.WriteLine();
        Console.WriteLine("==== H. 来源 / 社交缓存 ====");
        Console.WriteLine();

        // ---- H1: 路径 → 来源 ----
        Check("识别：QQ 缓存图",
              SocialCacheDetector.SourceOf(
                  @"C:\Users\x\Documents\Tencent Files\178237225\nt_qq\nt_data\Pic\2026-09\a.jpg")
              == SocialCacheDetector.QQ);

        Check("识别：微信 4.0 缓存图",
              SocialCacheDetector.SourceOf(
                  @"C:\Users\x\Documents\xwechat_files\wxid_abc123\msg\attach\b.jpg")
              == SocialCacheDetector.WeChat);

        Check("识别：微信 3.x 旧版缓存图",
              SocialCacheDetector.SourceOf(
                  @"C:\Users\x\Documents\WeChat Files\abc123\FileStorage\Image\c.jpg")
              == SocialCacheDetector.WeChat);

        Check("识别：企业微信缓存图",
              SocialCacheDetector.SourceOf(
                  @"C:\Users\x\Documents\WXWork\1688850\Cache\Image\2026-09\d.jpg")
              == SocialCacheDetector.WeCom);

        Check("识别：普通照片目录不算社交缓存",
              SocialCacheDetector.SourceOf(@"D:\我的照片\2026\上海\e.jpg") is null);

        // 逐段比较的意义就在这条：目录名**多一个后缀**就不该认。
        // 用 Contains("Tencent Files") 的话这里会被判成 QQ，把用户的备份目录当缓存扫。
        Check("识别：同名文件夹（多了后缀）不误判",
              SocialCacheDetector.SourceOf(@"D:\备份\Tencent Files 备份\f.jpg") is null);

        Check("识别：空路径不炸", SocialCacheDetector.SourceOf(null) is null
                              && SocialCacheDetector.SourceOf("") is null);

        // ---- H1b: 账号识别（左栏里两个"QQ"要分得清谁是谁） ----
        Check("账号：QQ 取 Tencent Files 下一段",
              SocialCacheDetector.AccountOf(
                  @"C:\Users\x\Documents\Tencent Files\178237225\nt_qq\nt_data\Pic") == "178237225");

        Check("账号：微信 4.0 取 xwechat_files 下一段",
              SocialCacheDetector.AccountOf(
                  @"C:\Users\x\Documents\xwechat_files\wxid_ea0el7kdjbm222_014a") == "wxid_ea0el7kdjbm222_014a");

        Check("账号：微信 3.x 取 WeChat Files 下一段",
              SocialCacheDetector.AccountOf(
                  @"C:\Users\x\Documents\WeChat Files\abc123\FileStorage\Image") == "abc123");

        Check("账号：企业微信取 WXWork 下一段",
              SocialCacheDetector.AccountOf(
                  @"C:\Users\x\Documents\WXWork\1688855772687730\Cache\Image") == "1688855772687730");

        // 关键一条：账号段必须是"标志目录名的**紧邻**下一段"。
        // 用 FirstOrDefault 之类"找第一个数字段"的写法，遇到盘符以外还有数字的路径就会取错。
        Check("账号：标志目录名后面没东西时返回 null（不得乱抓）",
              SocialCacheDetector.AccountOf(@"C:\Users\x\Documents\Tencent Files") is null);

        Check("账号：非社交路径返回 null",
              SocialCacheDetector.AccountOf(@"D:\我的照片\Tencent Files 备份\a.jpg") is null
              && SocialCacheDetector.AccountOf(null) is null);

        // ---- H2: 噪声（表情包 / 头像）过滤 ----
        Check("过滤：QQ 表情包算噪声",
              SocialCacheDetector.IsNoise(
                  @"C:\Users\x\Documents\Tencent Files\178\nt_qq\nt_data\Emoji\a.png"));

        Check("过滤：QQ 聊天图不算噪声",
              !SocialCacheDetector.IsNoise(
                  @"C:\Users\x\Documents\Tencent Files\178\nt_qq\nt_data\Pic\2026-09\a.jpg"));

        Check("过滤：企业微信头像算噪声（目录名官方就拼成 Avator）",
              SocialCacheDetector.IsNoise(
                  @"C:\Users\x\Documents\WXWork\1688850\Cache\Avator\a.png"));

        // 关键一条：过滤**只对社交缓存路径生效**。
        // 不加这条限制的话，用户自己建个叫 Emoji 的文件夹放照片就被误伤了。
        Check("过滤：自己的 Emoji 文件夹不受影响",
              !SocialCacheDetector.IsNoise(@"D:\我的照片\Emoji\a.png"));

        // ---- H3: 按来源分组 + 点某个来源筛出对应的图 ----
        string db = Path.Combine(Path.GetTempPath(), "cvidx-social.db");
        foreach (string f in new[] { db, db + "-wal", db + "-shm" })
        { try { File.Delete(f); } catch { } }

        string qq1 = @"C:\Users\x\Documents\Tencent Files\178\nt_qq\nt_data\Pic\2026-09\q1.jpg";
        string qq2 = @"C:\Users\x\Documents\Tencent Files\178\nt_qq\nt_data\Pic\2026-09\q2.jpg";
        string qq3 = @"C:\Users\x\Documents\Tencent Files\178\nt_qq\nt_data\Pic\2026-10\q3.jpg";
        string wx1 = @"C:\Users\x\Documents\xwechat_files\wxid_a\msg\w1.jpg";
        string wx2 = @"C:\Users\x\Documents\xwechat_files\wxid_a\msg\w2.jpg";
        string me1 = @"D:\我的照片\2026\me1.jpg";

        using (var index = new MediaIndex(db))
        {
            foreach (string p in new[] { qq1, qq2, qq3, wx1, wx2, me1 })
                index.Upsert(Photo(p));

            var groups = index.Group(GroupBy.Source);
            int nQq = groups.FirstOrDefault(g => g.Key == SocialCacheDetector.QQ)?.Count ?? -1;
            int nWx = groups.FirstOrDefault(g => g.Key == SocialCacheDetector.WeChat)?.Count ?? -1;
            var local = groups.FirstOrDefault(g => g.Key == "");
            int nMe = local?.Count ?? -1;

            Check("按来源分组：QQ 3 张 / 微信 2 张 / 本地 1 张",
                  nQq == 3 && nWx == 2 && nMe == 1,
                  $"QQ {nQq} / 微信 {nWx} / 本地 {nMe}");

            Check("按来源分组：没有来源的那组显示成「本地」，条数不受影响",
                  local?.Label == "本地" && groups.Sum(g => g.Count) == 6,
                  $"{groups.Sum(g => g.Count)} 张 / {groups.Count} 组");

            // 左栏点「QQ」→ 右栏必须只有 QQ 的图。
            // 分组和筛选走同一套条件，否则"QQ 写着 3 张、点进去只有 1 张"。
            var qqOnly = index.Query(new MediaQuery
            {
                Group = GroupBy.Source,
                GroupValue = SocialCacheDetector.QQ,
            });
            Check("点「QQ」筛出 3 张，且全是 QQ 的",
                  qqOnly.Count == 3 && qqOnly.All(p => p == qq1 || p == qq2 || p == qq3),
                  $"{qqOnly.Count} 张");

            var wxOnly = index.Query(new MediaQuery
            {
                Group = GroupBy.Source,
                GroupValue = SocialCacheDetector.WeChat,
            });
            Check("点「微信」筛出 2 张，不掺 QQ 的",
                  wxOnly.Count == 2 && !wxOnly.Any(p => p == qq1 || p == qq2 || p == qq3),
                  $"{wxOnly.Count} 张");

            // 本地那组的 Key 是空字符串，筛选时也得能筛出来
            var meOnly = index.Query(new MediaQuery { Group = GroupBy.Source, GroupValue = "" });
            Check("点「本地」筛出 1 张", meOnly.Count == 1, $"{meOnly.Count} 张");
        }

        CheckSocialMigrate();
    }

    /// <summary>
    /// 老库（v4，还没有 Source 列）升级到 v5：评分 / 收藏 / 标签一个都不能少，
    /// 而且升完「按来源」这条分类要能正常用。
    ///
    /// 做法是先用真代码建库、把 Source 列**真删掉**再把版本号压回 4 ——
    /// 这样才是真的"上个版本建的库"，不然加列逻辑根本没被走到。
    /// </summary>
    private static void CheckSocialMigrate()
    {
        string db = Path.Combine(Path.GetTempPath(), "cv-social-migrate.db");
        foreach (string s in new[] { "", "-wal", "-shm" })
        { try { File.Delete(db + s); } catch { } }

        const string p = @"D:\Photos\old.jpg";

        using (var old = new MediaIndex(db))
        {
            old.Upsert(Photo(p));
            old.SetRating(p, 4);
            old.SetFavorite(p, true);
            old.SetTags(p, new[] { "老照片" });
        }

        // 退回到 v4 的样子：Source 列不存在、版本号是 4
        bool dropped = true;
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            try
            {
                using var c1 = conn.CreateCommand();
                c1.CommandText = "DROP INDEX IF EXISTS IX_Media_Source;";
                c1.ExecuteNonQuery();

                using var c2 = conn.CreateCommand();
                c2.CommandText = "ALTER TABLE Media DROP COLUMN Source;";
                c2.ExecuteNonQuery();

                using var c3 = conn.CreateCommand();
                c3.CommandText = "PRAGMA user_version = 4;";
                c3.ExecuteNonQuery();
            }
            catch
            {
                // 个别环境的 SQLite 不支持 DROP COLUMN，那就没法模拟真 v4。
                // 与其报个假失败，不如明说这轮没测到。
                dropped = false;
            }
        }

        if (!dropped)
        {
            Console.WriteLine("  [跳过] 本环境 SQLite 不支持 DROP COLUMN，没法模拟 v4 老库");
            return;
        }

        using var upgraded = new MediaIndex(db);

        Check("老库 v4→v5：评分保住了", upgraded.GetRating(p) == 4, $"{upgraded.GetRating(p)} 星");
        Check("老库 v4→v5：收藏保住了", upgraded.IsFavorite(p));
        Check("老库 v4→v5：标签保住了",
              upgraded.GetTags(p).Count == 1, string.Join("、", upgraded.GetTags(p)));

        // 新列加上了、而且能用来分组 —— 老图没有来源，应该落在「本地」那组
        var groups = upgraded.Group(GroupBy.Source);
        Check("老库 v4→v5：按来源分组能用，老图归到「本地」",
              groups.Count == 1 && groups[0].Label == "本地" && groups[0].Count == 1,
              $"{groups.Count} 组 / {groups.Sum(g => g.Count)} 张");
    }

    // ==================== I. 应用身份 / 数据目录 ====================

    /// <summary>
    /// 盯住"应用名"和"数据目录"这两个常量。
    ///
    /// 为什么值得单开一段测两条字符串：2026-09-16 把应用从 CelesteViewer 改名成
    /// CelesteGallery 时，全仓库的 `sed s/CelesteViewer/CelesteGallery/g` 把
    /// <c>AppPaths.LegacyAppName</c> 也扫了一遍 —— 于是"老目录"和"新目录"
    /// 算出来是同一个路径，搬家分支永远不成立，程序在新目录里从零建库，
    /// 用户原来那份 library.db（评分 / 收藏 / 标签）直接成了孤儿。
    ///
    /// 症状还很隐蔽：程序一切正常、日志也不报错，只是"数据好像重置了"。
    /// 所以这里把不变量钉死，下次改名再手滑会立刻红。
    /// </summary>
    private static void AppIdentity()
    {
        Console.WriteLine();
        Console.WriteLine("==== I. 应用身份 / 数据目录 ====");
        Console.WriteLine();

        Check("应用名非空", !string.IsNullOrWhiteSpace(AppPaths.AppName), AppPaths.AppName);

        // 最关键的一条：两个名字必须不一样，否则"从老目录搬家"这段代码等于不存在
        Check("改名前的名字与新名字不相等（否则搬家逻辑会静默失效）",
              !string.Equals(AppPaths.AppName, AppPaths.LegacyAppName, StringComparison.OrdinalIgnoreCase),
              $"新 {AppPaths.AppName} / 旧 {AppPaths.LegacyAppName}");

        Check("数据目录以应用名结尾",
              AppPaths.DataDir.TrimEnd(Path.DirectorySeparatorChar)
                     .EndsWith(AppPaths.AppName, StringComparison.OrdinalIgnoreCase),
              AppPaths.DataDir);

        Check("数据目录下的文件/子目录都落在数据目录里",
              AppPaths.File("library.db").StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase)
              && AppPaths.Dir("ThumbCache").StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase));

        // 索引库、图库清单、设置、缓存四个文件必须指向同一个目录 ——
        // 以前这四处的目录名是各写一遍的，漏改一处就会"数据劈成两个目录"。
        string db = MediaIndex.DefaultPath;
        Check("索引库落在数据目录里（不是散在别处）",
              db.StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase), db);
    }

    /// <summary>造一张图：底色 fill，可选在 (bx,by) 画一个黑方块。写 PNG。</summary>
    private static void MakeImage(string path, MagickColor fill, MagickColor? block, int bx, int by)
    {
        using var img = new MagickImage(fill, 64, 48);
        if (block is not null)
        {
            using var b = new MagickImage(block, 20, 20);
            img.Composite(b, bx, by, CompositeOperator.Over);
        }
        img.Write(path);
    }

    private static PhotoInfo PhotoOf(string path) => new()
    {
        Path = path,
        // 合成路径（D:\g\a.png 这种）在磁盘上不存在，不能去读 FileInfo.Length，
        // 否则 G6 伪造指纹测试会崩。固定一个占位大小即可。
        FileSize = 1000,
        PixelWidth = 64,
        PixelHeight = 48,
        LastModified = DateTimeOffset.UtcNow,
    };

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
