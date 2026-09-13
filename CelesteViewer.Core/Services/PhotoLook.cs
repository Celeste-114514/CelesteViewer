using System;

namespace CelesteViewer.Services;

/// <summary>黑白处理的方式。</summary>
public enum MonoMode
{
    /// <summary>保留颜色。</summary>
    None = 0,

    /// <summary>纯黑白。</summary>
    Mono = 1,

    /// <summary>银盐：高对比黑白，预设里会再叠颗粒。</summary>
    Silver = 2,
}

/// <summary>
/// 一套"画面风格"的全部参数。
///
/// 滤镜和手动调整用的是**同一份参数** —— 点滤镜就是把这整个结构换成预设值，
/// 拖滑块就是改其中某一项。这么设计有两个好处：
///   1. 用户先点滤镜再微调，是自然叠加，不会出现"滤镜把我的调整冲掉了"；
///   2. 滤镜预览缩略图和最终成图走的是同一段代码，所见即所得，不可能对不上。
///
/// 所有滑块项一律 -100 ~ +100（0 = 不动），负值表示"反方向"。
/// 结构是值类型，默认构造出来就是"原图"（全 0、Mono 为 None）。
/// </summary>
public struct LookSettings : IEquatable<LookSettings>
{
    // ===== 十项手动调整 =====

    /// <summary>亮度：整体加/减一个固定量（对暗部影响最明显）。</summary>
    public int Brightness;

    /// <summary>曝光：整体乘一个系数（±100 ≈ ±1 档，对亮部影响最明显）。</summary>
    public int Exposure;

    /// <summary>对比度：以中间灰为轴拉开 / 压缩明暗差距。</summary>
    public int Contrast;

    /// <summary>突出显示：只动亮部。正值提亮高光，负值压回去（救过曝）。</summary>
    public int Highlights;

    /// <summary>阴影：只动暗部。正值提亮暗部，负值把暗部压下去。</summary>
    public int Shadows;

    /// <summary>晕影：四角压暗（负值反过来提亮四角）。</summary>
    public int Vignette;

    /// <summary>饱和度：-100 完全去色，+100 加倍。</summary>
    public int Saturation;

    /// <summary>暖度：色温，正值偏黄、负值偏蓝。</summary>
    public int Warmth;

    /// <summary>浅色：色调，正值偏绿、负值偏品红。</summary>
    public int Tint;

    /// <summary>清晰度：中间调的局部对比。正值更"扎实"，负值发柔（柔雾）。</summary>
    public int Clarity;

    // ===== 滤镜用的额外几项（滑块上不出现）=====

    /// <summary>褪色：抬黑 + 压白，胶片的"灰"感。0 ~ 1。</summary>
    public double Fade;

    /// <summary>颗粒：0 ~ 1。</summary>
    public double Grain;

    /// <summary>分离色调：暗部压青、亮部压橙（电影感的底子）。0 ~ 1。</summary>
    public double Split;

    /// <summary>黑场：把黑点抬到这里（自动增强用）。0 ~ 0.45，0 = 不动。</summary>
    public double BlackLift;

    /// <summary>白场：把白点压下来多少（自动增强用）。0 ~ 0.45，0 = 不动。</summary>
    public double WhiteDrop;

    /// <summary>黑白方式。</summary>
    public MonoMode Mono;

    /// <summary>是不是"原图"（一项都没动）。是的话渲染时直接跳过整条流水线。</summary>
    public bool IsNeutral =>
        Brightness == 0 && Exposure == 0 && Contrast == 0 &&
        Highlights == 0 && Shadows == 0 && Vignette == 0 &&
        Saturation == 0 && Warmth == 0 && Tint == 0 && Clarity == 0 &&
        Fade <= 0 && Grain <= 0 && Split <= 0 &&
        BlackLift <= 0 && WhiteDrop <= 0 &&
        Mono == MonoMode.None;

    public bool Equals(LookSettings other) =>
        Brightness == other.Brightness && Exposure == other.Exposure &&
        Contrast == other.Contrast && Highlights == other.Highlights &&
        Shadows == other.Shadows && Vignette == other.Vignette &&
        Saturation == other.Saturation && Warmth == other.Warmth &&
        Tint == other.Tint && Clarity == other.Clarity &&
        Math.Abs(Fade - other.Fade) < 1e-6 &&
        Math.Abs(Grain - other.Grain) < 1e-6 &&
        Math.Abs(Split - other.Split) < 1e-6 &&
        Math.Abs(BlackLift - other.BlackLift) < 1e-6 &&
        Math.Abs(WhiteDrop - other.WhiteDrop) < 1e-6 &&
        Mono == other.Mono;
}

/// <summary>
/// 画面风格引擎：把一套 <see cref="LookSettings"/> 应用到一段 BGRA 像素上。
///
/// 为什么自己写逐像素而不是调 Magick.NET：
///   - 十项调整里有五项（阴影 / 高光 / 晕影 / 颗粒 / 清晰度）本质是"按位置或
///     按邻域算"，用 Magick 的命令行式 API 拼出来要么做不到、要么靠猜参数；
///   - 自己写，滤镜预览和最终成图走的是同一段代码，不存在两处效果对不上；
///   - 纯算术，能在控制台里跑压测，不依赖任何 native 库。
///
/// 性能上的关键手法：曝光 / 亮度 / 褪色 / 对比 / 色温 / 色调这六项都是
/// "每个通道独立、跟位置无关"的，合并成三张 256 项的查找表（R/G/B 各一张），
/// 于是每个像素只剩下十几次乘加，而不是六次完整变换。
/// </summary>
public static class PhotoLook
{
    /// <summary>滑块范围。UI 和引擎共用，别两处各写各的。</summary>
    public const int SliderMin = -100;
    public const int SliderMax = 100;

    /// <summary>清晰度用的降采样倍率。缩到 1/4 再模糊，速度够快也不失真太多。</summary>
    private const int ClarityScale = 4;

    /// <summary>清晰度用的模糊半径（单位是降采样后的像素）。</summary>
    private const int ClarityRadius = 3;

    // ===== 滤镜预设 =====
    //
    // 命名走"胶片 / 电影"这一路，刻意不用 Windows 照片应用那套
    // （金色 / 辐射 / 平静 / 生动酷炫……），免得做出来的东西和它一模一样。
    // 每一项都对应一种一眼能认出来的调子，不塞"看着差不多"的重复项。

    /// <summary>所有滤镜。第一个永远是"原图"，UI 直接按顺序铺。</summary>
    public static readonly (string Name, LookSettings Look)[] Filters =
    {
        ("原图", default),

        ("柯达金", new LookSettings
        {
            Brightness = 4, Exposure = 4, Contrast = 10, Saturation = 16,
            Warmth = 26, Highlights = -6, Shadows = 6,
            Fade = 0.06, Grain = 0.05,
        }),

        ("富士青", new LookSettings
        {
            Brightness = 6, Contrast = -8, Saturation = -8, Warmth = -22, Tint = 4,
            Shadows = 12, Highlights = -4, Fade = 0.10,
        }),

        ("褪色", new LookSettings
        {
            Brightness = 4, Contrast = -14, Saturation = -18, Shadows = 10, Fade = 0.22,
        }),

        ("电影感", new LookSettings
        {
            Contrast = 22, Saturation = 8, Warmth = 6,
            Highlights = -10, Shadows = -8, Vignette = 18, Split = 0.85,
        }),

        ("浓郁", new LookSettings
        {
            Brightness = 2, Contrast = 16, Saturation = 34, Clarity = 12,
        }),

        ("奶油", new LookSettings
        {
            Brightness = 10, Contrast = -10, Saturation = -10, Warmth = 14,
            Highlights = -8, Shadows = 14, Fade = 0.16,
        }),

        ("蓝调", new LookSettings
        {
            Contrast = 10, Saturation = -6, Warmth = -34, Tint = -6, Shadows = 6,
        }),

        ("暖阳", new LookSettings
        {
            Brightness = 6, Exposure = 8, Contrast = 8, Saturation = 18, Warmth = 30,
        }),

        ("复古", new LookSettings
        {
            Contrast = 6, Saturation = -30, Warmth = 16,
            Fade = 0.26, Vignette = 26, Grain = 0.18,
        }),

        ("暗调", new LookSettings
        {
            Brightness = -10, Exposure = -8, Contrast = 24, Saturation = -14,
            Shadows = -10, Vignette = 20,
        }),

        ("柔雾", new LookSettings
        {
            Brightness = 6, Contrast = -12, Saturation = -6, Highlights = -6,
            Clarity = -30, Fade = 0.20,
        }),

        ("黑白", new LookSettings
        {
            Saturation = -100, Contrast = 10, Clarity = 8,
        }),

        ("银盐", new LookSettings
        {
            Mono = MonoMode.Silver, Contrast = 26, Vignette = 12, Grain = 0.22,
        }),
    };

    /// <summary>
    /// 应用一整套风格。返回新的字节数组，<paramref name="src"/> 不会被改动。
    /// 风格是"原图"时直接把原数组返回去（连复制都省了）。
    /// </summary>
    public static byte[] Apply(byte[] src, int width, int height, LookSettings s)
    {
        int need = width * height * 4;
        if (width <= 0 || height <= 0 || src.Length < need) return src;
        if (s.IsNeutral) return src;

        byte[] dst = new byte[need];
        ApplyTone(src, dst, width, height, s);

        if (s.Clarity != 0)
            dst = ApplyClarity(dst, width, height, s.Clarity);

        if (s.Grain > 0)
            ApplyGrain(dst, width, height, s.Grain);

        return dst;
    }

    /// <summary>
    /// 第一遍：所有"跟位置无关 + 只跟亮度位置有关"的变换。
    /// 曝光 / 亮度 / 褪色 / 对比 / 色温 / 色调合成三张查找表，剩下的逐像素算。
    /// </summary>
    private static void ApplyTone(byte[] src, byte[] dst, int w, int h, LookSettings s)
    {
        Span<byte> lutR = stackalloc byte[256];
        Span<byte> lutG = stackalloc byte[256];
        Span<byte> lutB = stackalloc byte[256];
        BuildToneLut(lutR, lutG, lutB, s);

        float shadowAmt = s.Shadows / 100f * 0.35f;
        float highAmt = s.Highlights / 100f * 0.35f;
        float sat = (float)Math.Clamp(1 + s.Saturation / 100.0, 0.0, 3.0);
        float split = (float)s.Split;
        float vign = s.Vignette / 100f * 0.85f;
        bool mono = s.Mono != MonoMode.None;

        double invW = w > 1 ? 1.0 / (w - 1) : 0;
        double invH = h > 1 ? 1.0 / (h - 1) : 0;

        for (int y = 0; y < h; y++)
        {
            double dy = (y * invH) * 2 - 1;
            int row = y * w * 4;

            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                byte a = src[i + 3];

                // 全透明的像素没有颜色可言，原样搬过去，免得算出一层脏边
                if (a == 0)
                {
                    dst[i] = src[i];
                    dst[i + 1] = src[i + 1];
                    dst[i + 2] = src[i + 2];
                    dst[i + 3] = 0;
                    continue;
                }

                float r = lutR[src[i + 2]] / 255f;   // BGRA 里第 0 字节是 B、第 2 字节是 R
                float g = lutG[src[i + 1]] / 255f;
                float b = lutB[src[i]] / 255f;

                // 亮度：用来决定阴影 / 高光的权重
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                if (shadowAmt != 0)
                {
                    float ws = Clamp01((0.42f - lum) / 0.42f);
                    float d = shadowAmt * ws * ws * (3 - 2 * ws);   // smoothstep，边界不硬
                    r += d; g += d; b += d;
                    lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                }

                if (highAmt != 0)
                {
                    float wh = Clamp01((lum - 0.58f) / 0.42f);
                    float d = highAmt * wh * wh * (3 - 2 * wh);
                    r += d; g += d; b += d;
                    lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                }

                if (sat != 1f)
                {
                    r = lum + (r - lum) * sat;
                    g = lum + (g - lum) * sat;
                    b = lum + (b - lum) * sat;
                }

                if (split > 0)
                {
                    // 暗部压青、亮部压橙 —— 电影调色的底子
                    float ws = Clamp01((0.42f - lum) / 0.42f);
                    float wh = Clamp01((lum - 0.58f) / 0.42f);
                    r += split * (0.11f * wh - 0.05f * ws);
                    g += split * (0.03f * wh + 0.02f * ws);
                    b += split * (-0.07f * wh + 0.12f * ws);
                }

                if (mono)
                {
                    lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = g = b = lum;
                }

                if (vign != 0)
                {
                    double dx = (x * invW) * 2 - 1;
                    double rr = Math.Sqrt(dx * dx + dy * dy) / 1.41421356;
                    float t = SmoothStep(0.35f, 1.0f, (float)rr);
                    float k = 1 - vign * t;
                    r *= k; g *= k; b *= k;
                }

                dst[i] = ToByte(b);
                dst[i + 1] = ToByte(g);
                dst[i + 2] = ToByte(r);
                dst[i + 3] = a;
            }
        }
    }

    /// <summary>
    /// 把六个"每通道独立"的变换压成三张 256 项的表。
    /// 原先每像素要做 6 次完整计算，现在变一次查表 —— 这是速度的大头。
    /// </summary>
    private static void BuildToneLut(Span<byte> lutR, Span<byte> lutG, Span<byte> lutB, LookSettings s)
    {
        double black = Math.Clamp(s.BlackLift, 0, 0.45);
        double white = 1 - Math.Clamp(s.WhiteDrop, 0, 0.45);
        double range = Math.Max(0.1, white - black);

        double gain = Math.Pow(2, s.Exposure / 100.0);
        double lift = s.Brightness * 0.0015;
        double fade = Math.Clamp(s.Fade, 0, 1);
        double fadeMul = 1 - fade * 0.18;
        double fadeAdd = fade * 0.16;
        double contrast = 1 + s.Contrast / 125.0;

        double wm = s.Warmth / 100.0;
        double tn = s.Tint / 100.0;
        double rGain = 1 + wm * 0.13 - tn * 0.03;
        double gGain = 1 + wm * 0.02 + tn * 0.10;
        double bGain = 1 - wm * 0.13 - tn * 0.03;

        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;

            v = (v - black) / range;               // 黑场 / 白场
            v *= gain;                             // 曝光
            v += lift;                             // 亮度
            v = v * fadeMul + fadeAdd;             // 褪色
            v = (v - 0.5) * contrast + 0.5;        // 对比度

            lutR[i] = ToByte(v * rGain);
            lutG[i] = ToByte(v * gGain);
            lutB[i] = ToByte(v * bGain);
        }
    }

    /// <summary>
    /// 清晰度（局部对比）：把每个像素的亮度和"它周围的平均亮度"比一比，
    /// 差值按强度放大。正值让中间调更扎实，负值往模糊里混（就是柔雾）。
    ///
    /// 先把图缩到 1/4 再模糊 —— 清晰度本来就是中频效果，
    /// 用不着全分辨率，省掉四分之三的计算。
    /// </summary>
    private static byte[] ApplyClarity(byte[] src, int w, int h, int amount)
    {
        float[] blur = BuildBlurLuma(src, w, h, ClarityScale, ClarityRadius);
        int sw = Math.Max(1, (w + ClarityScale - 1) / ClarityScale);
        int sh = Math.Max(1, (h + ClarityScale - 1) / ClarityScale);

        var dst = new byte[src.Length];
        float k = amount / 100f * 0.9f;

        for (int y = 0; y < h; y++)
        {
            int sy = Math.Clamp(y / ClarityScale, 0, sh - 1);
            int row = y * w * 4;
            int srow = sy * sw;

            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int sx = Math.Clamp(x / ClarityScale, 0, sw - 1);

                float r = src[i + 2] / 255f;
                float g = src[i + 1] / 255f;
                float b = src[i] / 255f;

                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                float delta = (lum - blur[srow + sx]) * k;

                if (lum > 0.02f)
                {
                    // 按比例缩放三个通道，色相不会跑掉
                    float scale = Math.Clamp((lum + delta) / lum, 0f, 4f);
                    r *= scale; g *= scale; b *= scale;
                }
                else
                {
                    r += delta; g += delta; b += delta;
                }

                dst[i] = ToByte(b);
                dst[i + 1] = ToByte(g);
                dst[i + 2] = ToByte(r);
                dst[i + 3] = src[i + 3];
            }
        }

        return dst;
    }

    /// <summary>降采样 + 两次盒式模糊，得到一张"低频亮度图"。</summary>
    private static float[] BuildBlurLuma(byte[] src, int w, int h, int scale, int radius)
    {
        int sw = Math.Max(1, (w + scale - 1) / scale);
        int sh = Math.Max(1, (h + scale - 1) / scale);
        var luma = new float[sw * sh];

        for (int y = 0; y < sh; y++)
        {
            int y0 = y * scale;
            int y1 = Math.Min(h, y0 + scale);
            int stepY = Math.Max(1, (y1 - y0) / 2);

            for (int x = 0; x < sw; x++)
            {
                int x0 = x * scale;
                int x1 = Math.Min(w, x0 + scale);
                int stepX = Math.Max(1, (x1 - x0) / 2);

                double sum = 0;
                int n = 0;

                for (int yy = y0; yy < y1; yy += stepY)
                {
                    int row = yy * w * 4;
                    for (int xx = x0; xx < x1; xx += stepX)
                    {
                        int i = row + xx * 4;
                        sum += (0.2126 * src[i + 2] + 0.7152 * src[i + 1] + 0.0722 * src[i]) / 255.0;
                        n++;
                    }
                }

                luma[y * sw + x] = (float)(n > 0 ? sum / n : 0);
            }
        }

        BoxBlur(luma, sw, sh, radius);
        BoxBlur(luma, sw, sh, radius);
        return luma;
    }

    /// <summary>可分离的盒式模糊：先横后竖，各一遍。</summary>
    private static void BoxBlur(float[] data, int w, int h, int radius)
    {
        if (radius <= 0 || w <= 0 || h <= 0) return;

        var tmp = new float[data.Length];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double sum = 0;
                int n = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int xx = x + d;
                    if (xx < 0 || xx >= w) continue;
                    sum += data[row + xx];
                    n++;
                }
                tmp[row + x] = (float)(sum / n);
            }
        }

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                double sum = 0;
                int n = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int yy = y + d;
                    if (yy < 0 || yy >= h) continue;
                    sum += tmp[yy * w + x];
                    n++;
                }
                data[y * w + x] = (float)(sum / n);
            }
        }
    }

    /// <summary>
    /// 颗粒。用坐标做哈希而不是随机数 —— 同一张图每次算出来的颗粒位置一致，
    /// 拖滑块时画面不会"沙沙"地乱跳。
    /// </summary>
    private static void ApplyGrain(byte[] data, int w, int h, double amount)
    {
        float amp = (float)amount * 0.06f;

        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (data[i + 3] == 0) continue;

                int hash = (x * 73856093) ^ (y * 19349663);
                float n = (hash & 0xFFFF) / 32767.5f - 1f;      // -1 ~ 1
                float d = n * amp;

                data[i] = ToByte(data[i] / 255f + d);
                data[i + 1] = ToByte(data[i + 1] / 255f + d);
                data[i + 2] = ToByte(data[i + 2] / 255f + d);
            }
        }
    }

    /// <summary>
    /// 自动增强：量一下这张图的明暗分布，把黑场白场拉开，再补一点饱和和清晰度。
    ///
    /// 只改 BlackLift / WhiteDrop / Saturation / Clarity 四项，
    /// 所以按完之后十根滑块还是可以接着微调，不会被"自动"锁死。
    /// </summary>
    public static LookSettings AutoEnhance(byte[] src, int width, int height)
    {
        int total = width * height;
        if (total <= 0 || src.Length < total * 4) return default;

        // 采样：最多看 20 万个点，够统计了，大图也不会卡
        int stride = Math.Max(1, total / 200_000);

        var hist = new int[256];
        double satSum = 0;
        int satCount = 0;
        int samples = 0;

        for (int p = 0; p < total; p += stride)
        {
            int i = p * 4;
            if (src[i + 3] == 0) continue;

            byte r = src[i + 2], g = src[i + 1], b = src[i];
            int lum = (int)(0.2126 * r + 0.7152 * g + 0.0722 * b);
            hist[Math.Clamp(lum, 0, 255)]++;

            int max = Math.Max(r, Math.Max(g, b));
            if (max > 0) satSum += (max - Math.Min(r, Math.Min(g, b))) / (double)max;

            satCount++;
            samples++;
        }

        if (samples < 16) return default;

        // 取 0.5% 和 99.5% 分位，避开个别死黑 / 死白像素
        int lowCount = (int)(samples * 0.005);
        int highCount = (int)(samples * 0.995);

        int blackIdx = 0, whiteIdx = 255;
        int run = 0;

        for (int i = 0; i < 256; i++)
        {
            run += hist[i];
            if (run >= lowCount) { blackIdx = i; break; }
        }

        run = 0;
        for (int i = 0; i < 256; i++)
        {
            run += hist[i];
            if (run >= highCount) { whiteIdx = i; break; }
        }

        // 最多各动 20%，过了就不是"增强"而是"改图"了
        double blackLift = Math.Clamp(blackIdx / 255.0, 0, 0.20);
        double whiteDrop = Math.Clamp(1 - whiteIdx / 255.0, 0, 0.20);

        // 图本身已经拉得很开就别再动了
        if (whiteDrop < 0.01 && blackLift < 0.01)
        {
            blackLift = 0;
            whiteDrop = 0;
        }

        double meanSat = satCount > 0 ? satSum / satCount : 0.2;
        int saturation = meanSat < 0.15 ? 14 : 6;

        return new LookSettings
        {
            BlackLift = blackLift,
            WhiteDrop = whiteDrop,
            Saturation = saturation,
            Clarity = 12,
        };
    }

    /// <summary>
    /// 缩到指定边长以内（保持宽高比，不放大）。
    /// 给滤镜预览缩略图用 —— 14 张预览要是都拿原图去算，打开面板得卡一下。
    /// </summary>
    public static byte[] Downscale(byte[] src, int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0) return src;
        if (Math.Max(width, height) <= maxSide) return src;

        double scale = maxSide / (double)Math.Max(width, height);
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));

        var dst = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            int sy0 = y * height / h;
            int sy1 = Math.Max(sy0 + 1, (y + 1) * height / h);

            for (int x = 0; x < w; x++)
            {
                int sx0 = x * width / w;
                int sx1 = Math.Max(sx0 + 1, (x + 1) * width / w);

                double r = 0, g = 0, b = 0, a = 0;
                int n = 0;

                for (int yy = sy0; yy < sy1; yy++)
                {
                    int row = yy * width * 4;
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        int i = row + xx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                        n++;
                    }
                }

                int o = (y * w + x) * 4;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = (byte)(a / n);
            }
        }

        return dst;
    }

    // ===== 小工具 =====

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static float SmoothStep(float edge0, float edge1, float v)
    {
        float t = Clamp01((v - edge0) / (edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    private static byte ToByte(double v)
        => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5);

    private static byte ToByte(float v)
        => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5);
}
