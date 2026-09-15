using System;
using System.Collections.Generic;

namespace CelesteGallery.Services;

/// <summary>标记用的笔。</summary>
public enum MarkTool
{
    /// <summary>圆头笔。边缘带一格抗锯齿，"画"用。</summary>
    Pen = 0,

    /// <summary>荧光笔。半透明的粗笔，正片叠底，压在字上字还看得见。</summary>
    Highlighter = 1,

    /// <summary>手写笔。硬边不抗锯齿，划得越快线越细 —— 有笔锋，像真笔。</summary>
    Nib = 2,

    /// <summary>橡皮。把标记擦回原图，不动底下的照片。</summary>
    Eraser = 3,
}

/// <summary>
/// 一条笔画。
///
/// 存的是**矢量点列**而不是"画完的位图"，有两个好处：
///   1. 撤销就是把最后一条删掉然后重放，不需要给每一步存一张快照
///      （一张 4000×3000 的快照就是 48MB，存十步机器就爆了）；
///   2. 点列用图像像素坐标记，缩放、改窗口都不影响，怎么晃都是同一笔。
/// </summary>
public sealed class MarkStroke
{
    public MarkTool Tool;

    /// <summary>笔尖直径（图像像素）。</summary>
    public double Width;

    /// <summary>颜色。荧光笔会拿它做正片叠底，所以默认给亮色。</summary>
    public byte R, G, B;

    /// <summary>点列的 X。和 <see cref="Ys"/> 一一对应。</summary>
    public readonly List<float> Xs = new();

    /// <summary>点列的 Y。</summary>
    public readonly List<float> Ys = new();

    public int Count => Xs.Count;

    public void Add(double x, double y)
    {
        Xs.Add((float)x);
        Ys.Add((float)y);
    }
}

/// <summary>一块矩形范围。界面拿它决定"只刷新屏幕上的哪一块"。</summary>
public readonly record struct MarkRect(int X0, int Y0, int X1, int Y1)
{
    /// <summary>空范围（没碰到任何像素）。</summary>
    public static readonly MarkRect Empty = new(0, 0, -1, -1);

    public bool IsEmpty => X1 < X0 || Y1 < Y0;
}

/// <summary>
/// 标记引擎：在**照片自己的像素**上画线。
///
/// 为什么不让标记单独占一层透明图层再合成：那样荧光笔做不了。
/// 荧光笔的本质是"正片叠底"—— 黄笔压在白纸上还是纸、压在黑字上还是字，
/// 靠的是拿底下的像素去乘。透明层底下是空的（全 0），乘出来一律是黑，
/// 只能退化成一块半透明的色条，压在深色照片上就糊成一片。
///
/// 所以这里直接改照片像素，另存一份 <c>origin</c> 当"橡皮擦回哪儿"的底。
/// 顺带的好处：退出标记模式时不用做任何合成，屏幕上看到的就是最终结果。
///
/// 一条笔画分两步处理，**不能省**：
///   1. 先按"覆盖率图"（单通道，逐像素取最大值）记下这条笔画盖住了哪些像素；
///   2. 再拿覆盖率把颜色混到画布上，每个像素只混一次。
///
/// 早先试过"沿线段每隔几个像素盖一个圆盘"的老办法，踩了个大坑：
/// 圆盘互相重叠，半透明的笔每重叠一次就再混一次 —— 荧光笔设 0.42 的透明度，
/// 一路盖过去实际能到 0.89，糊成一块不透明色块，字全被闷死在里面。
/// 取最大值而不是累加，就不会有这个问题：一条线压过同一个像素多少次，
/// 透明度都还是设定的那个值。
///
/// 实时绘制还有个更隐蔽的坑：鼠标拖动是一段一段传进来的。要是每来一段
/// 就直接往上混一次颜色，两根"胶囊"在接缝处重叠的那一小块就会被混两次 ——
/// 荧光笔立刻变成一串深色圆点（按 8px 一个点排下来）。
/// 所以实时绘制走 BeginStroke / AddPoint 这套：笔画开始时把画布存一份底片，
/// 之后每次都是"拿底片 + 当前覆盖率重算这一块"，和一次性画完整条的结果
/// 一模一样。
///
/// 全部是纯算术，不碰任何 native 库，因此能在控制台里单独压测。
/// </summary>
public static class PhotoMark
{
    /// <summary>笔尖直径的范围。UI 的滑块和引擎共用这两个数。</summary>
    public const double MinWidth = 2;
    public const double MaxWidth = 120;

    /// <summary>荧光笔的不透明度。太高会把底下的字盖掉，太低看不出颜色。</summary>
    public const double HighlighterAlpha = 0.42;

    /// <summary>
    /// 橡皮的不透明度。
    ///
    /// 必须是 1，而且橡皮的覆盖率必须是"非 0 即满"（硬边）—— 这两件事都是为了
    /// **擦得干净**。原因：往一个方向混合再往反方向混合**不是**原路返回，
    /// 半透明混两次会留下舍入残差。实测底 60 的像素上盖 50% 的白再擦 50%，
    /// 会停在 109 而不是 60（差 49/255），肉眼就是一圈淡淡的鬼影轮廓。
    /// 硬边 + 全透明度走的是"直接拷回原值"，一个字节都不会差。
    /// </summary>
    public const double EraserAlpha = 1.0;

    /// <summary>
    /// 橡皮的笔尖要**向外多半格**。
    ///
    /// 圆头笔的抗锯齿会在真正的笔尖外沿再铺一圈半透明的边（半径 +0.5）。
    /// 橡皮要是按同一个半径按满格去擦，那一圈就擦不掉，剩下一条细毛边。
    /// 外扩半格正好把圆头笔的整条外沿盖住。
    /// </summary>
    private const double EraserPad = 0.5;

    /// <summary>
    /// 覆盖率图、每行左右边界、每点半径、笔画开始时的那份底片。
    ///
    /// 做成线程静态的缓存，是因为一张 2K 图的底片就有 14MB：
    /// 每画一条笔画就 new 一个，画几十笔能造出几百 MB 垃圾，GC 会被拖垮。
    /// 标记本来就是单线程的界面操作，缓存一份反复用最省事。
    /// </summary>
    [ThreadStatic] private static byte[]? _coverage;
    [ThreadStatic] private static int[]? _rowMin;
    [ThreadStatic] private static int[]? _rowMax;
    [ThreadStatic] private static int[]? _strokeMin;
    [ThreadStatic] private static int[]? _strokeMax;
    [ThreadStatic] private static double[]? _radii;
    [ThreadStatic] private static byte[]? _strokeBase;

    /// <summary>当前这条笔画碰过的行范围（收尾时只清这一段的覆盖率）。</summary>
    [ThreadStatic] private static int _touchedY0;
    [ThreadStatic] private static int _touchedY1;

    // ===== 一把画完（重放 / 压测用）=====

    /// <summary>把一条笔画整条盖到画布上。<paramref name="origin"/> 只有橡皮用得上。</summary>
    public static void Apply(byte[] canvas, byte[] origin, int width, int height, MarkStroke stroke)
    {
        if (!Usable(canvas, origin, width, height, stroke)) return;

        BeginStroke(canvas, width, height, stroke.Count);
        for (int i = 0; i < stroke.Count; i++) AddPoint(canvas, origin, width, height, stroke, i);
        FinishStroke(canvas, origin, width, height, stroke);
    }

    // ===== 实时绘制：一段一段来（界面用）=====

    /// <summary>
    /// 开始一条新笔画。把当前画布存一份底片 —— 之后每段都靠它重算，
    /// 保证和"一次性画完整条"的结果一模一样。
    /// </summary>
    public static void BeginStroke(byte[] canvas, int width, int height, int pointCount)
    {
        if (width <= 0 || height <= 0) return;
        int need = width * height;
        if (canvas.Length < need * 4) return;

        Allocate(width, height, pointCount);

        Array.Copy(canvas, _strokeBase!, need * 4);
        _touchedY0 = int.MaxValue;
        _touchedY1 = -1;
    }

    /// <summary>
    /// 追加笔画里的第 <paramref name="index"/> 个点，返回因此发生变化的像素范围
    /// （界面只刷这一块，不必整张图重传）。
    ///
    /// 第 0 个点先不画 —— 它到底多粗要等第二点到了才知道（手写笔的笔锋看的是步长），
    /// 先画一个"默认粗细"的圆点，等第二点一来就会发现起笔比别处胖一截。
    /// 只点一下不拖动的情形在 <see cref="FinishStroke"/> 里补上。
    /// </summary>
    public static MarkRect AddPoint(byte[] canvas, byte[] origin, int width, int height,
                                    MarkStroke stroke, int index)
    {
        if (!Usable(canvas, origin, width, height, stroke)) return MarkRect.Empty;
        if (index <= 0 || index >= stroke.Count) return MarkRect.Empty;

        // 实时绘制时笔画是**长出来**的，进来之前不知道最后会有多少个点，
        // 所以这里得能自己扩容（BeginStroke 只按当时的点数备了一份）
        if (_radii is null || _radii.Length <= index)
        {
            int want = Math.Max(index + 1, (_radii?.Length ?? 0) * 2);
            _radii = new double[want];
        }

        var radii = _radii;

        // StampSegment 收的是**段号**：段 k 从第 k-1 个点连到第 k 个点。
        // 所以新来的第 index 个点要盖的是第 index 段。
        radii[index] = RadiusAt(stroke, index);

        // 第二个点到了，起笔的半径现在才算得出来（手写笔的笔锋看的是步长，
        // 只有一个点时无从谈起），补算第 0 个点
        if (index == 1) radii[0] = RadiusAt(stroke, 0);

        return StampSegment(canvas, origin, width, height, stroke, index);
    }

    /// <summary>一条笔画画完了：补上"只点了一下"的圆点，然后把覆盖率清干净。</summary>
    public static void FinishStroke(byte[] canvas, byte[] origin, int width, int height, MarkStroke stroke)
    {
        if (!Usable(canvas, origin, width, height, stroke)) return;

        // 只点了一下：这时候才画得出那个圆点
        if (stroke.Count == 1)
        {
            _radii![0] = stroke.Width / 2;
            StampSegment(canvas, origin, width, height, stroke, 0);
        }

        ClearCoverage(width);
    }

    // ===== 内部 =====

    private static bool Usable(byte[] canvas, byte[] origin, int width, int height, MarkStroke stroke)
    {
        if (width <= 0 || height <= 0) return false;
        if (canvas.Length < width * height * 4) return false;
        if (origin.Length < width * height * 4) return false;
        if (stroke.Count == 0) return false;
        if (stroke.Width <= 0) return false;
        return true;
    }

    private static void Allocate(int width, int height, int points)
    {
        int need = width * height;
        if (_coverage is null || _coverage.Length < need) _coverage = new byte[need];
        if (_rowMin is null || _rowMin.Length < height) _rowMin = new int[height];
        if (_rowMax is null || _rowMax.Length < height) _rowMax = new int[height];
        if (_strokeMin is null || _strokeMin.Length < height) _strokeMin = new int[height];
        if (_strokeMax is null || _strokeMax.Length < height) _strokeMax = new int[height];
        if (_strokeBase is null || _strokeBase.Length < need * 4) _strokeBase = new byte[need * 4];

        int want = Math.Max(points, 64);
        if (_radii is null || _radii.Length < want) _radii = new double[want];
    }

    /// <summary>
    /// 某个点的笔尖半径。
    ///
    /// 手写笔按"走得快就细"算笔锋，参照速度取"几个笔尖宽"，
    /// 这样不管笔粗笔细，手感都一致。其余几支笔一律固定半径。
    /// </summary>
    private static double RadiusAt(MarkStroke stroke, int index)
    {
        double half = stroke.Width / 2;
        if (stroke.Tool != MarkTool.Nib) return half;

        // 第一个点的"步长"借用第二段：它自己没有上一段可比
        int j = index == 0 ? 1 : index;
        if (j >= stroke.Count) return half;

        double dx = stroke.Xs[j] - stroke.Xs[j - 1];
        double dy = stroke.Ys[j] - stroke.Ys[j - 1];
        double len = Math.Sqrt(dx * dx + dy * dy);

        double reference = Math.Max(2.0, stroke.Width * 0.5);
        double k = reference / (reference + len);   // 慢 → 1，快 → 0
        return half * (0.45 + 0.55 * k);
    }

    /// <summary>
    /// 把"第 index-1 点到第 index 点"这一段盖上去，并把受影响的像素重算一遍。
    /// index 为 0 时盖的是一个圆点 —— 单击走这条。
    /// </summary>
    private static MarkRect StampSegment(byte[] canvas, byte[] origin, int width, int height,
                                         MarkStroke stroke, int index)
    {
        var radii = _radii!;
        double ax, ay, bx, by, ra, rb;

        if (index <= 0)
        {
            ax = bx = stroke.Xs[0];
            ay = by = stroke.Ys[0];
            ra = rb = radii[0];
        }
        else
        {
            ax = stroke.Xs[index - 1]; ay = stroke.Ys[index - 1];
            bx = stroke.Xs[index]; by = stroke.Ys[index];
            ra = radii[index - 1]; rb = radii[index];
        }

        bool hard = stroke.Tool == MarkTool.Nib || stroke.Tool == MarkTool.Eraser;
        double hardPad = stroke.Tool == MarkTool.Eraser ? EraserPad : 0;

        var cov = _coverage!;
        var rowMin = _rowMin!;
        var rowMax = _rowMax!;

        double pad = Math.Max(ra, rb) + 1.0;

        // 这一段可能碰到的行。**必须夹进图内**再拿去当 rowMin/rowMax 的下标 ——
        // 笔压在画布边上时范围会超出去（往右画到最后一列、笔宽 30，
        // 右边界就跑到图外了），不夹会直接数组越界崩掉。
        int yA = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - pad));
        int yB = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(ay, by) + pad));
        if (yA > yB) return MarkRect.Empty;

        // 先按"还没碰过"初始化这一段的行
        for (int y = yA; y <= yB; y++)
        {
            rowMin[y] = int.MaxValue;
            rowMax[y] = -1;
        }

        Capsule(cov, rowMin, rowMax, width, height, ax, ay, bx, by, ra, rb, hard, hardPad);

        var rect = Rebake(canvas, origin, width, stroke, yA, yB);

        // 累积这一笔碰过的行范围，收尾时照它清覆盖率
        if (!rect.IsEmpty)
        {
            if (rect.Y0 < _touchedY0) _touchedY0 = rect.Y0;
            if (rect.Y1 > _touchedY1) _touchedY1 = rect.Y1;
        }

        return rect;
    }

    /// <summary>
    /// 把这一段范围内的像素，按"底片 + 当前覆盖率"重新算一遍，返回真正动过的范围。
    ///
    /// 关键在"从底片重算"而不是"在当前值上再混一次"：
    /// 接缝处覆盖率被后一段抬高时，只有从底片重算才不会把颜色叠两次。
    /// </summary>
    private static MarkRect Rebake(byte[] canvas, byte[] origin, int width, MarkStroke stroke,
                                   int yA, int yB)
    {
        var cov = _coverage!;
        var rowMin = _rowMin!;
        var rowMax = _rowMax!;
        var basePx = _strokeBase!;

        bool eraser = stroke.Tool == MarkTool.Eraser;
        double toolAlpha = stroke.Tool switch
        {
            MarkTool.Highlighter => HighlighterAlpha,
            MarkTool.Eraser => EraserAlpha,
            _ => 1.0,
        };

        int rx0 = int.MaxValue, ry0 = int.MaxValue, rx1 = -1, ry1 = -1;

        for (int y = yA; y <= yB; y++)
        {
            int lo = rowMin[y], hi = rowMax[y];
            if (hi < 0) continue;

            int row = y * width;

            for (int x = lo; x <= hi; x++)
            {
                int idx = row + x;
                byte c = cov[idx];
                if (c == 0) continue;

                double a = c * (toolAlpha / 255.0);
                if (a <= 0) continue;

                int i = idx * 4;

                if (eraser)
                {
                    // 还原照片本身（不是这条笔画的底片）——
                    // 所以橡皮擦的是标记，蹭不掉照片
                    canvas[i] = Mix(basePx[i], origin[i], a);
                    canvas[i + 1] = Mix(basePx[i + 1], origin[i + 1], a);
                    canvas[i + 2] = Mix(basePx[i + 2], origin[i + 2], a);
                }
                else if (stroke.Tool == MarkTool.Highlighter)
                {
                    // 正片叠底：先拿底片乘，再按覆盖率往目标色靠。
                    // 白底乘任何颜色 = 那个颜色（上色），黑字乘任何颜色 ≈ 黑（字保住了），
                    // 这就是荧光笔的观感。乘积不会超过 255，直接截断是安全的
                    byte br = basePx[i + 2], bg = basePx[i + 1], bb = basePx[i];
                    canvas[i] = Mix(bb, (byte)(bb * stroke.B / 255), a);
                    canvas[i + 1] = Mix(bg, (byte)(bg * stroke.G / 255), a);
                    canvas[i + 2] = Mix(br, (byte)(br * stroke.R / 255), a);
                }
                else
                {
                    // 圆头笔 / 手写笔：直接往目标色混
                    canvas[i] = Mix(basePx[i], stroke.B, a);
                    canvas[i + 1] = Mix(basePx[i + 1], stroke.G, a);
                    canvas[i + 2] = Mix(basePx[i + 2], stroke.R, a);
                }

                if (x < rx0) rx0 = x;
                if (x > rx1) rx1 = x;
                if (y < ry0) ry0 = y;
                if (y > ry1) ry1 = y;
            }
        }

        if (rx1 < 0) return MarkRect.Empty;
        return new MarkRect(rx0, ry0, rx1, ry1);
    }

    /// <summary>把用过的覆盖率清零，下一笔复用同一块内存。</summary>
    private static void ClearCoverage(int width)
    {
        if (_coverage is null || _rowMin is null || _rowMax is null ||
            _strokeMin is null || _strokeMax is null)
        {
            _touchedY0 = int.MaxValue;
            _touchedY1 = -1;
            return;
        }

        var cov = _coverage;
        var rowMin = _rowMin;
        var rowMax = _rowMax;
        var strokeMin = _strokeMin;
        var strokeMax = _strokeMax;

        if (_touchedY1 >= _touchedY0)
        {
            for (int y = _touchedY0; y <= _touchedY1; y++)
            {
                // 清的范围要用**整笔**的并集，不能用最后一段的 ——
                // 见 Capsule 里那段说明
                int lo = strokeMin[y], hi = strokeMax[y];
                if (hi >= 0)
                {
                    int row = y * width;
                    for (int x = lo; x <= hi; x++) cov[row + x] = 0;
                }

                rowMin[y] = int.MaxValue;
                rowMax[y] = -1;
                strokeMin[y] = int.MaxValue;
                strokeMax[y] = -1;
            }
        }

        _touchedY0 = int.MaxValue;
        _touchedY1 = -1;
    }

    /// <summary>
    /// 把一段"胶囊"（两端圆头的粗线）的覆盖率盖进覆盖率图，**取最大值**。
    ///
    /// 和"沿线段盖圆盘"是同一个形状，但每个像素只算一次到线段的真实距离，
    /// 所以不会在重叠处把透明度叠加起来。
    ///
    /// <paramref name="hard"/> 为真时不铺抗锯齿的半透明边，覆盖率非 0 即满。
    /// 手写笔要这样（硬边是它的手感），橡皮更要这样（见 EraserAlpha 的注释）。
    /// </summary>
    private static void Capsule(byte[] cov, int[] rowMin, int[] rowMax, int width, int height,
                                double ax, double ay, double bx, double by,
                                double ra, double rb, bool hard, double hardPad)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;

        double pad = Math.Max(ra, rb) + 1.0;

        int minX = (int)Math.Floor(Math.Min(ax, bx) - pad);
        int maxX = (int)Math.Ceiling(Math.Max(ax, bx) + pad);
        int minY = (int)Math.Floor(Math.Min(ay, by) - pad);
        int maxY = (int)Math.Ceiling(Math.Max(ay, by) + pad);

        if (minX < 0) minX = 0;
        if (minY < 0) minY = 0;
        if (maxX > width - 1) maxX = width - 1;
        if (maxY > height - 1) maxY = height - 1;
        if (minX > maxX || minY > maxY) return;

        // 退化成一个点（单击）：t 固定在 0，半径固定
        bool dot = len2 <= 1e-9;

        for (int y = minY; y <= maxY; y++)
        {
            double py = y + 0.5;
            int row = y * width;

            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5;

                double t;
                if (dot)
                {
                    t = 0;
                }
                else
                {
                    t = ((px - ax) * dx + (py - ay) * dy) / len2;
                    if (t < 0) t = 0;
                    else if (t > 1) t = 1;
                }

                double qx = ax + dx * t - px;
                double qy = ay + dy * t - py;
                double d2 = qx * qx + qy * qy;

                double r = ra + (rb - ra) * t;

                byte c;
                if (hard)
                {
                    // 硬边：比平方，省掉一次开方。手写笔和橡皮走的就是这条
                    double limit = r + hardPad;
                    c = d2 <= limit * limit ? (byte)255 : (byte)0;
                }
                else
                {
                    // 抗锯齿：圆心往外一格之内线性衰减
                    double outer = r + 0.5;
                    if (d2 >= outer * outer) continue;

                    double d = Math.Sqrt(d2);
                    double f = outer - d;
                    if (f >= 1) c = 255;
                    else if (f <= 0) continue;
                    else c = (byte)(f * 255 + 0.5);
                }

                if (c == 0) continue;

                int idx = row + x;
                if (c > cov[idx])
                {
                    cov[idx] = c;

                    // 两份左右边界都要记：
                    //   行内这份（rowMin/rowMax）只统计**当前这一段**，合成时照着它扫，
                    //   范围小、省时间；
                    //   整笔那份（strokeMin/strokeMax）把所有段并起来，收尾时照着它
                    //   清覆盖率 —— 只按最后一段清的话，前面各段留下的覆盖率会残留，
                    //   下一条笔画经过同一片区域时就会莫名其妙被"补画"上旧的一笔。
                    if (x < rowMin[y]) rowMin[y] = x;
                    if (x > rowMax[y]) rowMax[y] = x;

                    var smin = _strokeMin!;
                    var smax = _strokeMax!;
                    if (x < smin[y]) smin[y] = x;
                    if (x > smax[y]) smax[y] = x;
                }
            }
        }
    }

    /// <summary>按比例往目标值靠。t 为 0 原样返回，1 全取目标值。</summary>
    private static byte Mix(byte from, byte to, double t)
    {
        if (t <= 0) return from;
        if (t >= 1) return to;

        int v = (int)(from + (to - from) * t + 0.5);
        if (v < 0) return 0;
        if (v > 255) return 255;
        return (byte)v;
    }

    /// <summary>把画布擦回原点。换图、撤销到头都用它。</summary>
    public static void Reset(byte[] canvas, byte[] origin)
    {
        int n = Math.Min(canvas.Length, origin.Length);
        Array.Copy(origin, canvas, n);
    }

    /// <summary>
    /// 按笔画列表整个重画一遍。
    ///
    /// 撤销 / 重做都走这条路：与其维护"反向操作"，不如从干净的底子重放。
    /// 标记的笔画数一般就几条到几十条，重放一次远快于用户眨眼。
    /// </summary>
    public static void Replay(byte[] canvas, byte[] origin, int width, int height,
                              IReadOnlyList<MarkStroke> strokes)
    {
        Reset(canvas, origin);
        for (int i = 0; i < strokes.Count; i++) Apply(canvas, origin, width, height, strokes[i]);
    }

    /// <summary>UI 上摆的那几个颜色。都是压在照片上也看得清的高饱和色。</summary>
    public static readonly (string Name, byte R, byte G, byte B)[] Palette =
    {
        ("红", 0xE8, 0x3B, 0x3B),
        ("橙", 0xF0, 0x8A, 0x1E),
        ("黄", 0xF5, 0xD2, 0x1B),
        ("绿", 0x35, 0xC4, 0x59),
        ("青", 0x1E, 0xC4, 0xD6),
        ("蓝", 0x2D, 0x7C, 0xE8),
        ("紫", 0x9B, 0x5C, 0xE8),
        ("黑", 0x1A, 0x1A, 0x1A),
        ("白", 0xFF, 0xFF, 0xFF),
    };
}
