using System;
using System.Collections.Generic;

namespace CelesteViewer.Services;

/// <summary>擦掉之后那块地方填什么。</summary>
public enum EraseMode
{
    /// <summary>
    /// 智能填充：拿选区**外面**的像素一层层往里补，把选中的东西"抹掉"。
    /// 擦水印、擦掉画面里多余的杂物、擦掉照片上的一行字，用这个。
    /// </summary>
    Inpaint = 0,

    /// <summary>填成白色（做白底图、盖住敏感信息）。</summary>
    White = 1,

    /// <summary>填成透明（抠掉一块，存成 PNG 时才有意义）。</summary>
    Transparent = 2,
}

/// <summary>一块矩形区域（图像像素坐标，两端都含）。</summary>
public readonly struct EraseRect
{
    public readonly int X0, Y0, X1, Y1;

    public EraseRect(int x0, int y0, int x1, int y1)
    {
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
    }

    public static EraseRect Empty => new(0, 0, -1, -1);

    public bool IsEmpty => X1 < X0 || Y1 < Y0;

    public int Width => IsEmpty ? 0 : X1 - X0 + 1;
    public int Height => IsEmpty ? 0 : Y1 - Y0 + 1;
}

/// <summary>
/// 擦除引擎：把选中的一块**从照片上抹掉**。
///
/// 选区用多边形表示 —— 矩形就是四个角，套索就是随手画出来的任意形状，
/// 同一个算法通吃，省得写两套。
///
/// 三种填法里只有"智能填充"需要动脑子。做法叫**洋葱剥皮**：
/// 从选区边界开始，每一圈用"已经确定下来的邻居"取平均，算出这一圈该是什么颜色，
/// 然后往里推进一圈，直到填满。
///   好处一：用的是真实像素，颜色和纹理自然接得上，不像纯色块那么突兀；
///   好处二：一圈只碰一圈的像素，总计算量和选区面积成正比，
///          不管选区多大多小都不会突然变慢。
///
/// 早期想过"每一轮把整块选区扫一遍"（简单写法），那样会变成面积 × 圈数，
/// 选区一大就是几亿次运算，点一下要卡好几秒 —— 所以老老实实按圈推进。
///
/// 全部是纯算术，不碰 native 库，所以能在控制台里单独压测。
/// </summary>
public static class PhotoErase
{
    /// <summary>
    /// 擦除。<paramref name="xs"/> / <paramref name="ys"/> 是选区的顶点（图像像素坐标，
    /// 可以带小数，会自动做抗锯齿）。返回被改动的范围。
    /// </summary>
    public static EraseRect Fill(
        byte[] canvas, int width, int height,
        double[] xs, double[] ys, EraseMode mode)
    {
        if (width <= 0 || height <= 0) return EraseRect.Empty;
        if (canvas is null || canvas.Length < width * height * 4) return EraseRect.Empty;
        if (xs is null || ys is null) return EraseRect.Empty;

        int n = Math.Min(xs.Length, ys.Length);
        if (n < 3) return EraseRect.Empty;      // 两个点连不成面

        // 选区不能超出画面：超出部分直接切掉，不做报错（用户就是随手一拉）
        var box = Bounds(xs, ys, n, width, height);
        if (box.IsEmpty) return EraseRect.Empty;

        var coverage = BuildCoverage(xs, ys, n, width, height, box);

        switch (mode)
        {
            case EraseMode.White:
                SolidFill(canvas, width, coverage, box, 255, 255, 255, 255);
                break;

            case EraseMode.Transparent:
                TransparentFill(canvas, width, coverage, box);
                break;

            default:
                Inpaint(canvas, width, height, coverage, box);
                break;
        }

        return box;
    }

    /// <summary>
    /// 矩形选区的便捷写法。两点顺序随便给，内部会摆正。
    ///
    /// 参数说的是**像素编号**，两端都含：传 (100, 80, 140, 120) 就是
    /// "把第 100…140 列、第 80…120 行擦掉"，一共 41×41 个像素。
    ///
    /// 内部会换算成多边形。像素是有宽度的 —— 编号 100 的那一列占的是 100.0~101.0，
    /// 所以编号 x0~x1 对应 100.0 到 x1+1.0 这一段（左闭右开）。
    ///
    /// **这个换算不能省，也不能想当然**：早先直接把像素编号当多边形坐标用，
    /// 编号 100~140 实际只圈住了 100~139，最右一列、最下一行漏在了选区外面；
    /// 而智能填充恰恰是拿"选区外面的像素"往里补的 —— 结果就把要擦掉的东西自己
    /// 当成了参考色，越补越像它（擦白块补出一片灰白）。这个坑是实测踩出来的，别改回去。
    ///
    /// 套索（<see cref="Fill"/>）没有这个换算：用户自己画到哪里，就是哪里。
    /// </summary>
    public static EraseRect FillRect(
        byte[] canvas, int width, int height,
        double x0, double y0, double x1, double y1, EraseMode mode)
    {
        double left = Math.Min(x0, x1), right = Math.Max(x0, x1);
        double top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);

        // 像素编号 → 连续坐标：右 / 下各加一格，把编号两端的像素整个圈进来
        right += 1.0;
        bottom += 1.0;

        // 顺时针四个角
        double[] xs = { left, right, right, left };
        double[] ys = { top, top, bottom, bottom };

        return Fill(canvas, width, height, xs, ys, mode);
    }

    /// <summary>选区的包围盒，已经夹进画面内。</summary>
    private static EraseRect Bounds(double[] xs, double[] ys, int n, int width, int height)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            if (xs[i] < minX) minX = xs[i];
            if (xs[i] > maxX) maxX = xs[i];
            if (ys[i] < minY) minY = ys[i];
            if (ys[i] > maxY) maxY = ys[i];
        }

        // 往外放一格：抗锯齿采样会取到像素的四个角，边界像素也算得进来
        int x0 = (int)Math.Floor(minX) - 1;
        int y0 = (int)Math.Floor(minY) - 1;
        int x1 = (int)Math.Ceiling(maxX) + 1;
        int y1 = (int)Math.Ceiling(maxY) + 1;

        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > width - 1) x1 = width - 1;
        if (y1 > height - 1) y1 = height - 1;

        return new EraseRect(x0, y0, x1, y1);
    }

    /// <summary>
    /// 把多边形光栅化成一张"覆盖率"图（0~255）。
    ///
    /// 每个像素取四个采样点（四分之一格的位置），落在多边形里的点数决定覆盖率 ——
    /// 这样选区的边缘是一圈过渡，不会出现楼梯一样的锯齿。
    /// </summary>
    private static byte[] BuildCoverage(
        double[] xs, double[] ys, int n,
        int width, int height, EraseRect box)
    {
        var coverage = new byte[width * height];

        // 四个采样点
        double[] ox = { 0.25, 0.75, 0.25, 0.75 };
        double[] oy = { 0.25, 0.25, 0.75, 0.75 };

        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;
            for (int x = box.X0; x <= box.X1; x++)
            {
                int hits = 0;
                for (int s = 0; s < 4; s++)
                {
                    if (Inside(xs, ys, n, x + ox[s], y + oy[s])) hits++;
                }

                if (hits > 0) coverage[row + x] = (byte)(hits * 255 / 4);
            }
        }

        return coverage;
    }

    /// <summary>射线法判断点是否在多边形内。</summary>
    private static bool Inside(double[] xs, double[] ys, int n, double px, double py)
    {
        bool inside = false;

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double yi = ys[i], yj = ys[j];
            if ((yi > py) == (yj > py)) continue;

            double xi = xs[i], xj = xs[j];
            double cross = xi + (py - yi) / (yj - yi) * (xj - xi);
            if (px < cross) inside = !inside;
        }

        return inside;
    }

    // ===== 填纯色 / 填透明 =====

    private static void SolidFill(
        byte[] canvas, int width, byte[] coverage, EraseRect box,
        byte r, byte g, byte b, byte a)
    {
        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;
            for (int x = box.X0; x <= box.X1; x++)
            {
                int cov = coverage[row + x];
                if (cov == 0) continue;

                int i = (row + x) * 4;
                canvas[i] = Mix(canvas[i], b, cov);
                canvas[i + 1] = Mix(canvas[i + 1], g, cov);
                canvas[i + 2] = Mix(canvas[i + 2], r, cov);
                canvas[i + 3] = Mix(canvas[i + 3], a, cov);
            }
        }
    }

    private static void TransparentFill(byte[] canvas, int width, byte[] coverage, EraseRect box)
    {
        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;
            for (int x = box.X0; x <= box.X1; x++)
            {
                int cov = coverage[row + x];
                if (cov == 0) continue;

                int i = (row + x) * 4;
                // RGB 留着不动，只把透明度打下去：万一以后要还原，颜色还在
                canvas[i + 3] = (byte)(canvas[i + 3] * (255 - cov) / 255);
            }
        }
    }

    private static byte Mix(byte from, byte to, int cov)
        => (byte)((from * (255 - cov) + to * cov + 127) / 255);

    // ===== 智能填充 =====

    /// <summary>
    /// 洋葱剥皮式填充。详见类注释。
    /// </summary>
    private static void Inpaint(
        byte[] canvas, int width, int height,
        byte[] coverage, EraseRect box)
    {
        int w = box.Width;
        int h = box.Height;

        // 把区块单独拷出来算，免得下标里到处都是 +box.X0
        var r = new byte[w * h];
        var g = new byte[w * h];
        var b = new byte[w * h];

        for (int y = 0; y < h; y++)
        {
            int src = ((box.Y0 + y) * width + box.X0) * 4;
            for (int x = 0; x < w; x++, src += 4)
            {
                int i = y * w + x;
                b[i] = canvas[src];
                g[i] = canvas[src + 1];
                r[i] = canvas[src + 2];
            }
        }

        var mask = new byte[w * h];
        int remaining = 0;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (box.Y0 + y) * width + box.X0;
            for (int x = 0; x < w; x++)
            {
                if (coverage[srcRow + x] == 0) continue;
                mask[y * w + x] = 1;
                remaining++;
            }
        }

        if (remaining == 0) return;

        /*
          每个像素一个状态：
            0 = 还没定（选区里，等着补）
            1 = 已定（选区外面，或者已经被补好的）
            2 = 排在这一圈里（马上要补）
          用状态位而不是额外的集合来去重：一个像素会被上下左右好几个邻居"提名"，
          入队那一刻就把状态改成 2，第二次提名自然就被挡掉了 ——
          否则同一个像素会被算好几遍，remaining 也会减多，直接算错。
        */
        var state = new byte[w * h];
        for (int i = 0; i < state.Length; i++)
        {
            if (mask[i] == 0) state[i] = 1;
        }

        var ring = new List<int>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] == 0) continue;
                if (!HasKnownNeighbour(state, w, h, x, y)) continue;

                state[i] = 2;
                ring.Add(i);
            }
        }

        // 整张图都被选中时没有任何"外面"的像素可参考，那就什么都补不了。
        // 与其死循环，不如原样留着（用户会看到"没擦掉"，比卡死强）
        if (ring.Count == 0) return;

        var valueR = new List<double>();
        var valueG = new List<double>();
        var valueB = new List<double>();

        // 一圈一圈往里推
        while (ring.Count > 0 && remaining > 0)
        {
            valueR.Clear();
            valueG.Clear();
            valueB.Clear();

            // 先把整圈的颜色都算完，再统一落笔 ——
            // 边算边写的话，同一圈里后算的像素会把刚写进去的值当成"已知"，颜色就歪了
            for (int k = 0; k < ring.Count; k++)
            {
                int i = ring[k];
                int x = i % w, y = i / w;

                int sumR = 0, sumG = 0, sumB = 0, count = 0;

                if (x > 0 && state[i - 1] == 1) { sumB += b[i - 1]; sumG += g[i - 1]; sumR += r[i - 1]; count++; }
                if (x < w - 1 && state[i + 1] == 1) { sumB += b[i + 1]; sumG += g[i + 1]; sumR += r[i + 1]; count++; }
                if (y > 0 && state[i - w] == 1) { sumB += b[i - w]; sumG += g[i - w]; sumR += r[i - w]; count++; }
                if (y < h - 1 && state[i + w] == 1) { sumB += b[i + w]; sumG += g[i + w]; sumR += r[i + w]; count++; }

                if (count == 0)
                {
                    // 理论上进不来（入队前筛过）。真碰上了就沿用原色，
                    // 至少不会突然出现一个黑点
                    valueB.Add(b[i]); valueG.Add(g[i]); valueR.Add(r[i]);
                    continue;
                }

                valueB.Add((double)sumB / count);
                valueG.Add((double)sumG / count);
                valueR.Add((double)sumR / count);
            }

            for (int k = 0; k < ring.Count; k++)
            {
                int i = ring[k];
                b[i] = (byte)Math.Round(valueB[k]);
                g[i] = (byte)Math.Round(valueG[k]);
                r[i] = (byte)Math.Round(valueR[k]);

                state[i] = 1;
                remaining--;
            }

            // 下一圈：这一圈的邻居里还没定过的那些
            var next = new List<int>();
            for (int k = 0; k < ring.Count; k++)
            {
                int i = ring[k];
                int x = i % w, y = i / w;

                if (x > 0 && mask[i - 1] != 0 && state[i - 1] == 0) { state[i - 1] = 2; next.Add(i - 1); }
                if (x < w - 1 && mask[i + 1] != 0 && state[i + 1] == 0) { state[i + 1] = 2; next.Add(i + 1); }
                if (y > 0 && mask[i - w] != 0 && state[i - w] == 0) { state[i - w] = 2; next.Add(i - w); }
                if (y < h - 1 && mask[i + w] != 0 && state[i + w] == 0) { state[i + w] = 2; next.Add(i + w); }
            }

            ring = next;
        }

        // 补完之后再抹两遍：洋葱剥皮的填充是一圈圈同心色带，
        // 直接看有一点点"梯田"感，抹两下就化开了
        Smooth(r, w, h, mask, 2);
        Smooth(g, w, h, mask, 2);
        Smooth(b, w, h, mask, 2);

        // 按覆盖率混回原画（边缘那一圈是半透明的，混一下才自然）
        for (int y = 0; y < h; y++)
        {
            int dstRow = (box.Y0 + y) * width + box.X0;
            for (int x = 0; x < w; x++)
            {
                int cov = coverage[dstRow + x];
                if (cov == 0) continue;

                int i = y * w + x;
                int dst = (dstRow + x) * 4;
                canvas[dst] = Mix(canvas[dst], b[i], cov);
                canvas[dst + 1] = Mix(canvas[dst + 1], g[i], cov);
                canvas[dst + 2] = Mix(canvas[dst + 2], r[i], cov);
            }
        }
    }

    private static bool HasKnownNeighbour(byte[] state, int w, int h, int x, int y)
    {
        if (x > 0 && state[y * w + x - 1] == 1) return true;
        if (x < w - 1 && state[y * w + x + 1] == 1) return true;
        if (y > 0 && state[(y - 1) * w + x] == 1) return true;
        if (y < h - 1 && state[(y + 1) * w + x] == 1) return true;
        return false;
    }

    /// <summary>只擦选区内部的平滑，抹掉同心色带的台阶感。用两份缓冲做，避免顺序偏差。</summary>
    private static void Smooth(byte[] channel, int w, int h, byte[] mask, int passes)
    {
        var temp = new byte[channel.Length];

        for (int p = 0; p < passes; p++)
        {
            Array.Copy(channel, temp, channel.Length);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (mask[i] == 0) continue;

                    int sum = channel[i];
                    int count = 1;

                    if (x > 0) { sum += temp[i - 1]; count++; }
                    if (x < w - 1) { sum += temp[i + 1]; count++; }
                    if (y > 0) { sum += temp[i - w]; count++; }
                    if (y < h - 1) { sum += temp[i + w]; count++; }

                    channel[i] = (byte)((sum + count / 2) / count);
                }
            }
        }
    }
}
