using System;

namespace CelesteGallery.Services;

/// <summary>
/// 一张图的直方图统计结果：亮度 + RGB 三条通道。
///
/// 每个数组都是 256 个桶，下标就是 0~255 的色阶，值是落在这个色阶上的**采样像素个数**。
/// </summary>
public sealed class HistogramData
{
    public const int Bins = 256;

    /// <summary>亮度（Rec.601 加权：0.299R + 0.587G + 0.114B）。</summary>
    public required int[] Luma { get; init; }

    public required int[] R { get; init; }
    public required int[] G { get; init; }
    public required int[] B { get; init; }

    /// <summary>实际参与统计的像素数（大图会抽稀，所以不等于原图总像素数）。</summary>
    public required long Samples { get; init; }

    /// <summary>平均亮度 0~255。判断"这张是不是整体偏暗"最快的一个数。</summary>
    public double MeanLuma { get; init; }

    /// <summary>暗部溢出比例（亮度 ≤2 的像素占比，0~1）。</summary>
    public double ShadowClippedRatio { get; init; }

    /// <summary>高光溢出比例（亮度 ≥253 的像素占比，0~1）。</summary>
    public double HighlightClippedRatio { get; init; }

    /// <summary>没有像素（空图/全透明）时给界面一个可用的空结果。</summary>
    public static HistogramData Empty => new()
    {
        Luma = new int[Bins],
        R = new int[Bins],
        G = new int[Bins],
        B = new int[Bins],
        Samples = 0,
    };
}

/// <summary>
/// 直方图计算。
///
/// 为什么要单独拎成 Core 里的纯函数：它只碰一段 byte[]，不碰任何 UI 类型，
/// 所以可以拉到控制台测试里跑（IndexHarness 的 J 段就是干这个的）。
///
/// **只读**：只统计、不改一个字节，也不写任何文件 —— 直方图是"看"，
/// 不是编辑，绝不能反过来动用户的原图。
/// </summary>
public static class HistogramCalculator
{
    /// <summary>
    /// 默认最多统计多少个像素。
    ///
    /// 一张 6000×4000 的图有两千四百万像素，逐像素过一遍在大图上要几十毫秒，
    /// 而且**对直方图形状几乎没有影响** —— 分布是要靠"样本够多"来稳定的，
    /// 而不是靠"每个像素都数到"。二十六万样本已经足够稳。
    /// </summary>
    public const int DefaultMaxSamples = 1 << 18;   // 262144

    /// <summary>
    /// 统计一张 BGRA 位图。
    ///
    /// <paramref name="bgra"/> 的排布按 <see cref="DecodedBitmap.Pixels"/>：
    /// 每像素 4 字节 B、G、R、A，每行紧密排布（stride = 宽 × 4）。
    ///
    /// 大图按**等步长抽稀**（行列同一个步长），也就是在一张均匀网格上取样。
    /// 不用"缩略图 + 面积平均"是因为直方图只要分布，抽稀的结果和全量几乎重合，
    /// 却省掉一次完整的缩放运算；直方图本来就不关心"某个像素邻域的平均色"。
    ///
    /// 完全不透明的像素（alpha &lt; 8）不参与统计 —— 带透明通道的 PNG
    /// 如果把透明区域的黑色也算进去，图上会凭空多出一根 0 的大柱子。
    /// </summary>
    public static HistogramData Compute(
        byte[]? bgra, int width, int height, int maxSamples = DefaultMaxSamples)
    {
        if (bgra is null || width <= 0 || height <= 0) return HistogramData.Empty;

        var luma = new int[HistogramData.Bins];
        var rBins = new int[HistogramData.Bins];
        var gBins = new int[HistogramData.Bins];
        var bBins = new int[HistogramData.Bins];

        long total = (long)width * height;
        int step = 1;
        if (maxSamples > 0 && total > maxSamples)
        {
            double ratio = (double)total / maxSamples;
            step = (int)Math.Ceiling(Math.Sqrt(ratio));
            if (step < 1) step = 1;
        }

        long samples = 0;
        long lumaSum = 0;
        long dark = 0;
        long bright = 0;

        for (int y = 0; y < height; y += step)
        {
            long rowStart = (long)y * width * 4;

            for (int x = 0; x < width; x += step)
            {
                long i = rowStart + (long)x * 4;
                if (i + 3 >= bgra.Length) break;

                byte bb = bgra[i];
                byte gg = bgra[i + 1];
                byte rr = bgra[i + 2];
                byte aa = bgra[i + 3];

                if (aa < 8) continue;

                rBins[rr]++;
                gBins[gg]++;
                bBins[bb]++;

                // Rec.601 亮度的整数写法：0.299R + 0.587G + 0.114B，
                // 三个系数乘 256 分别是 76.5 / 150.3 / 29.2，取整后总和 256，
                // 右移 8 位就是结果 —— 比浮点快，且不会有累积误差。
                int y8 = (77 * rr + 150 * gg + 29 * bb) >> 8;
                if (y8 > 255) y8 = 255;

                luma[y8]++;
                lumaSum += y8;
                samples++;

                if (y8 <= 2) dark++;
                else if (y8 >= 253) bright++;
            }
        }

        if (samples == 0) return HistogramData.Empty;

        return new HistogramData
        {
            Luma = luma,
            R = rBins,
            G = gBins,
            B = bBins,
            Samples = samples,
            MeanLuma = (double)lumaSum / samples,
            ShadowClippedRatio = (double)dark / samples,
            HighlightClippedRatio = (double)bright / samples,
        };
    }

    /// <summary>
    /// 把桶计数换算成 0~1 的绘图高度。
    ///
    /// 这里做了**开方压缩**（gamma = 0.5），不是直接按最大值归一：
    /// 一张大面积纯色的照片会让某一个柱子高出其它柱子一两个数量级，
    /// 直接归一的话其余 255 个桶会被压成贴着底的一条线，等于什么都没画。
    /// 开方之后大小柱子的差距被压缩到可读的范围，形状还保持原样。
    /// </summary>
    public static double[] ToDisplayHeights(int[] bins)
    {
        if (bins is null || bins.Length == 0) return Array.Empty<double>();

        var heights = new double[bins.Length];

        // 先用开方找峰值，再按这个峰值归一 —— 顺序不能反：
        // 先归一后开方的话，峰值会被抬到 1.0 以上然后被裁掉。
        double peak = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            double v = Math.Sqrt(bins[i]);
            if (v > peak) peak = v;
        }

        if (peak <= 0) return heights;

        for (int i = 0; i < bins.Length; i++)
            heights[i] = Math.Sqrt(bins[i]) / peak;

        return heights;
    }
}
