using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CelesteGallery.Services;

/// <summary>截图编辑器里的一支笔。</summary>
public enum SnipTool
{
    /// <summary>不画，只用来点选/拖动已有标注。</summary>
    None = 0,

    /// <summary>自由画笔。走 PhotoMark 那套覆盖率算法，粗细均匀、边缘抗锯齿。</summary>
    Pen,

    /// <summary>矩形框（只描边，不填充）。</summary>
    Rect,

    /// <summary>椭圆框。</summary>
    Ellipse,

    /// <summary>带箭头的直线。</summary>
    Arrow,

    /// <summary>直线。</summary>
    Line,

    /// <summary>序号徽章：一个圆点里面写数字，用来标"第一步、第二步"。</summary>
    Number,

    /// <summary>文字。</summary>
    Text,

    /// <summary>马赛克（方块化打码）。</summary>
    Mosaic,

    /// <summary>模糊。</summary>
    Blur,
}

/// <summary>
/// 一条标注。
///
/// 存的是**矢量参数**（起止点、颜色、粗细、文字）而不是"画完的像素"，
/// 和 PhotoMark 的笔画一个道理：撤销就是把最后一条删掉、从原图重放，
/// 不用给每一步留一张整图快照（一张 4K 快照 33MB，存十步机器就爆了）。
/// 坐标一律是**图像像素**（不是屏幕、不是有效像素），窗口怎么缩放都不影响。
/// </summary>
public sealed class SnipAnnotation
{
    public SnipTool Tool;

    public double X0, Y0, X1, Y1;

    public byte R, G, B;

    /// <summary>线宽 / 笔尖直径（图像像素）。</summary>
    public double Width = 3;

    /// <summary>文字内容（Text 与 Number 用）。</summary>
    public string Text = "";

    /// <summary>字号（**有效像素**，画之前会乘缩放倍率换成图像像素）。</summary>
    public double FontSize = 20;

    /// <summary>字体。默认微软雅黑——中文界面下用它最稳，且系统一定自带。</summary>
    public string FontName = "Microsoft YaHei UI";

    public bool Bold;

    /// <summary>自由画笔的点列（只有 Pen 用）。</summary>
    public List<float> Xs = new();
    public List<float> Ys = new();

    /// <summary>马赛克粒度 / 模糊半径。</summary>
    public int Block = 10;

    /// <summary>这条标注盖住的范围（图像像素）。用来只刷新屏幕上那一小块。</summary>
    public (int X, int Y, int W, int H) Bounds()
    {
        if (Tool == SnipTool.Pen && Xs.Count > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < Xs.Count; i++)
            {
                if (Xs[i] < minX) minX = Xs[i];
                if (Xs[i] > maxX) maxX = Xs[i];
                if (Ys[i] < minY) minY = Ys[i];
                if (Ys[i] > maxY) maxY = Ys[i];
            }
            return Pad(minX, minY, maxX, maxY, Width);
        }

        if (Tool == SnipTool.Text)
        {
            // 文字的实际宽度要量过才知道，这里按字号粗估，宁可算大一点
            double est = Math.Max(10, Text.Length) * FontSize * 0.7;
            return Pad(X0, Y0, X0 + est, Y0 + FontSize * 1.4, Width);
        }

        return Pad(X0, Y0, X1, Y1, Width);
    }

    private static (int X, int Y, int W, int H) Pad(double x0, double y0, double x1, double y1, double w)
    {
        int a = (int)Math.Floor(Math.Min(x0, x1) - w - 2);
        int b = (int)Math.Floor(Math.Min(y0, y1) - w - 2);
        int c = (int)Math.Ceiling(Math.Max(x0, x1) + w + 2);
        int d = (int)Math.Ceiling(Math.Max(y0, y1) + w + 2);
        return (a, b, c - a, d - b);
    }
}

/// <summary>
/// 截图标注的光栅化引擎：把一条 <see cref="SnipAnnotation"/> 画到 BGRA 像素上。
///
/// ⚠️ 像素约定：<b>BGRA、从左上角开始按行排、每行紧密排列（stride = 宽×4）、非预乘</b>。
/// 和 <see cref="CapturedFrame"/>、<see cref="PhotoMark"/> 完全一致，可以互相直接传。
///
/// 抗锯齿统一走"距离场 + 覆盖率"这一套：
///   1. 对每个候选像素算出它到图形<b>轮廓线</b>的有符号距离 d；
///   2. 覆盖率 = 半线宽 + 0.5 − |d|，夹到 [0,1]；
///   3. 按覆盖率把颜色混上去，每个像素<b>只混一次</b>。
///
/// 为什么不能"沿轮廓每隔一像素盖一个圆点"：圆点互相重叠，半透明的边会被反复混，
/// 线越粗越糊，还会出现一串深浅不一的圆斑。覆盖率法没有这个问题。
///
/// 文字那条路不一样——字形这东西没法手算，交给 GDI 去画（见 <see cref="GdiText"/>）。
/// 之所以不引第三方绘图库：GDI 是系统自带的，中文、字体、抗锯齿全都现成，
/// 换来的代价只是几十行 P/Invoke，比拖一个几 MB 的依赖进来划算。
/// </summary>
public static class SnapshotDraw
{
    /// <summary>序号徽章的直径（有效像素）。</summary>
    public const double BadgeSize = 26;

    /// <summary>马赛克默认粒度。</summary>
    public const int DefaultMosaicBlock = 10;

    /// <summary>
    /// 把一条标注画到画布上。
    /// </summary>
    /// <param name="canvas">工作画布（会被直接改）。</param>
    /// <param name="origin">原始像素，马赛克/模糊要用它当"底"，橡皮和重放也要。</param>
    /// <param name="w">画布宽。</param>
    /// <param name="h">画布高。</param>
    /// <param name="a">标注。</param>
    /// <param name="scale">图像像素 ÷ 有效像素。字号和徽章要按它放大，高分屏上才不会画小。</param>
    public static void Apply(byte[] canvas, byte[] origin, int w, int h, SnipAnnotation a, double scale = 1.0)
    {
        if (canvas.Length < w * h * 4 || origin.Length < w * h * 4) return;
        if (w <= 0 || h <= 0) return;
        double s = scale > 0 ? scale : 1.0;

        switch (a.Tool)
        {
            case SnipTool.Pen:
                DrawPen(canvas, origin, w, h, a);
                break;

            case SnipTool.Rect:
                StrokeRect(canvas, w, h, a);
                break;

            case SnipTool.Ellipse:
                StrokeEllipse(canvas, w, h, a);
                break;

            case SnipTool.Line:
                StrokeLine(canvas, w, h, a);
                break;

            case SnipTool.Arrow:
                StrokeLine(canvas, w, h, a);
                FillArrowHead(canvas, w, h, a);
                break;

            case SnipTool.Number:
                DrawBadge(canvas, w, h, a, s);
                break;

            case SnipTool.Text:
                DrawText(canvas, w, h, a, s);
                break;

            case SnipTool.Mosaic:
            case SnipTool.Blur:
                DrawObfuscate(canvas, origin, w, h, a);
                break;
        }
    }

    /// <summary>
    /// 重放：先把画布还原成原图，再把所有标注按顺序画回去。
    /// 撤销 / 改颜色 / 拖动标注之后都走这条路——简单、不会算错，
    /// 代价是标注多了会慢一点，但截图上的标注撑死几十条，感觉不到。
    /// </summary>
    public static void Replay(byte[] canvas, byte[] origin, int w, int h,
                              IReadOnlyList<SnipAnnotation> all, double scale = 1.0)
    {
        if (canvas.Length < w * h * 4 || origin.Length < w * h * 4) return;
        Buffer.BlockCopy(origin, 0, canvas, 0, Math.Min(canvas.Length, origin.Length));
        foreach (var a in all) Apply(canvas, origin, w, h, a, scale);
    }

    // ===================== 画笔 =====================

    /// <summary>
    /// 自由画笔直接复用 PhotoMark。
    ///
    /// 不自己再写一套的理由很实在：PhotoMark 已经处理好了"实时拖动时一段一段接上去
    /// 接缝处不能混两次"这个坑（半透明的笔一混两次就会变成一串深斑），
    /// 这套逻辑调了很久才对，重写一遍必踩同样的坑。
    /// </summary>
    private static void DrawPen(byte[] canvas, byte[] origin, int w, int h, SnipAnnotation a)
    {
        if (a.Xs.Count == 0) return;

        var stroke = new MarkStroke
        {
            Tool = MarkTool.Pen,
            Width = a.Width,
            R = a.R, G = a.G, B = a.B,
        };
        for (int i = 0; i < a.Xs.Count; i++) stroke.Add(a.Xs[i], a.Ys[i]);

        PhotoMark.Apply(canvas, origin, w, h, stroke);
    }

    // ===================== 形状 =====================

    private static void StrokeRect(byte[] canvas, int w, int h, SnipAnnotation a)
    {
        double cx = (a.X0 + a.X1) / 2.0, cy = (a.Y0 + a.Y1) / 2.0;
        double hw = Math.Abs(a.X1 - a.X0) / 2.0, hh = Math.Abs(a.Y1 - a.Y0) / 2.0;
        if (hw < 0.5 || hh < 0.5) return;

        Stroke(canvas, w, h, a, (x, y) =>
        {
            // 点到矩形边界的有符号距离（内部为负）。标准 box SDF。
            double qx = Math.Abs(x - cx) - hw;
            double qy = Math.Abs(y - cy) - hh;
            double ex = Math.Max(qx, 0), ey = Math.Max(qy, 0);
            return Math.Sqrt(ex * ex + ey * ey) + Math.Min(Math.Max(qx, qy), 0.0);
        }, Math.Min(a.X0, a.X1), Math.Min(a.Y0, a.Y1), Math.Max(a.X0, a.X1), Math.Max(a.Y0, a.Y1));
    }

    private static void StrokeEllipse(byte[] canvas, int w, int h, SnipAnnotation a)
    {
        double cx = (a.X0 + a.X1) / 2.0, cy = (a.Y0 + a.Y1) / 2.0;
        double rx = Math.Abs(a.X1 - a.X0) / 2.0, ry = Math.Abs(a.Y1 - a.Y0) / 2.0;
        if (rx < 0.5 || ry < 0.5) return;

        // 椭圆没有精确的 SDF，这个是常用的一次近似：
        // k 是"归一化半径"，乘短轴长度换算回像素。够画抗锯齿的边了。
        Stroke(canvas, w, h, a, (x, y) =>
        {
            double dx = (x - cx) / rx, dy = (y - cy) / ry;
            return (Math.Sqrt(dx * dx + dy * dy) - 1.0) * Math.Min(rx, ry);
        }, Math.Min(a.X0, a.X1), Math.Min(a.Y0, a.Y1), Math.Max(a.X0, a.X1), Math.Max(a.Y0, a.Y1));
    }

    private static void StrokeLine(byte[] canvas, int w, int h, SnipAnnotation a)
    {
        double dx = a.X1 - a.X0, dy = a.Y1 - a.Y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5) return;

        // 箭头：箭杆要缩短，不然会从箭尖戳出来一截
        double endX = a.X1, endY = a.Y1;
        if (a.Tool == SnipTool.Arrow)
        {
            double head = HeadLength(a);
            if (len > head * 0.6)
            {
                endX = a.X1 - dx / len * head * 0.75;
                endY = a.Y1 - dy / len * head * 0.75;
            }
        }

        Stroke(canvas, w, h, a, (x, y) => DistToSegment(x, y, a.X0, a.Y0, endX, endY),
               Math.Min(a.X0, endX), Math.Min(a.Y0, endY), Math.Max(a.X0, endX), Math.Max(a.Y0, endY));
    }

    private static void FillArrowHead(byte[] canvas, int w, int h, SnipAnnotation a)
    {
        double dx = a.X1 - a.X0, dy = a.Y1 - a.Y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5) return;

        double ux = dx / len, uy = dy / len;
        double head = HeadLength(a);
        double half = head * 0.42;

        // 箭尖 + 往后缩一个头长、左右各张开 half 的两个点，构成等腰三角形
        double tipX = a.X1, tipY = a.Y1;
        double baseX = a.X1 - ux * head, baseY = a.Y1 - uy * head;
        double lx = baseX - uy * half, ly = baseY + ux * half;
        double rx = baseX + uy * half, ry = baseY - ux * half;

        int bx0 = (int)Math.Floor(Math.Min(Math.Min(tipX, lx), rx)) - 2;
        int by0 = (int)Math.Floor(Math.Min(Math.Min(tipY, ly), ry)) - 2;
        int bx1 = (int)Math.Ceiling(Math.Max(Math.Max(tipX, lx), rx)) + 2;
        int by1 = (int)Math.Ceiling(Math.Max(Math.Max(tipY, ly), ry)) + 2;
        bx0 = Math.Max(0, bx0); by0 = Math.Max(0, by0);
        bx1 = Math.Min(w - 1, bx1); by1 = Math.Min(h - 1, by1);

        // 三角形用 2×2 超采样取覆盖率：区域内每个像素采 4 个点，
        // 命中几个就是几分之几。比算距离简单，斜边的抗锯齿效果也够看。
        for (int y = by0; y <= by1; y++)
        {
            for (int x = bx0; x <= bx1; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < 2; sy++)
                {
                    for (int sx = 0; sx < 2; sx++)
                    {
                        double px = x + 0.25 + sx * 0.5;
                        double py = y + 0.25 + sy * 0.5;
                        if (InTriangle(px, py, tipX, tipY, lx, ly, rx, ry)) hit++;
                    }
                }
                if (hit == 0) continue;
                Blend(canvas, w, h, x, y, hit / 4.0, a.R, a.G, a.B);
            }
        }
    }

    private static double HeadLength(SnipAnnotation a)
        => Math.Max(8, a.Width * 3.2);

    // ===================== 序号徽章 =====================

    private static void DrawBadge(byte[] canvas, int w, int h, SnipAnnotation a, double scale)
    {
        double r = BadgeSize * scale / 2.0;
        double cx = a.X0, cy = a.Y0;
        if (r < 2) return;

        // 先铺一个实心圆（覆盖率按"到圆心的距离"算，边缘抗锯齿）
        byte fr = 230, fg = 60, fb = 60;   // 数字序号统一用红色，醒目、和 QQ 一致
        if (a.Tool == SnipTool.Number && (a.R != 0 || a.G != 0 || a.B != 0))
        {
            fr = a.R; fg = a.G; fb = a.B;
        }

        int x0 = Math.Max(0, (int)Math.Floor(cx - r - 2));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r - 2));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r + 2));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + r + 2));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                double cov = r + 0.5 - d;
                if (cov <= 0) continue;
                Blend(canvas, w, h, x, y, Math.Min(1, cov), fr, fg, fb);
            }
        }

        // 再在圆心写数字。白字，字号约为圆直径的 0.62，居中靠量出来的文字尺寸。
        string text = string.IsNullOrEmpty(a.Text) ? "1" : a.Text;
        double fontPx = r * 1.24;
        var size = MeasureText(text, (int)Math.Round(fontPx), a.FontName, true);
        GdiText(canvas, w, h, text,
                (int)Math.Round(cx - size.Width / 2.0),
                (int)Math.Round(cy - size.Height / 2.0),
                (int)Math.Round(fontPx), 255, 255, 255, a.FontName, true);
    }

    // ===================== 文字 =====================

    private static void DrawText(byte[] canvas, int w, int h, SnipAnnotation a, double scale)
    {
        if (string.IsNullOrEmpty(a.Text)) return;
        int fontPx = (int)Math.Max(6, Math.Round(a.FontSize * scale));
        GdiText(canvas, w, h, a.Text, (int)Math.Round(a.X0), (int)Math.Round(a.Y0),
                fontPx, a.R, a.G, a.B, a.FontName, a.Bold);
    }

    // ===================== 马赛克 / 模糊 =====================

    private static void DrawObfuscate(byte[] canvas, byte[] origin, int w, int h, SnipAnnotation a)
    {
        // 必须先夹进画布范围再算宽高：负坐标（从画面外开始拖）会让后面的
        // BlockCopy 拿到负偏移直接抛异常，也会让宽高和 Crop 裁出来的实际块对不上。
        int x = Math.Max(0, (int)Math.Floor(Math.Min(a.X0, a.X1)));
        int y = Math.Max(0, (int)Math.Floor(Math.Min(a.Y0, a.Y1)));
        int x2 = Math.Min(w, (int)Math.Ceiling(Math.Max(a.X0, a.X1)));
        int y2 = Math.Min(h, (int)Math.Ceiling(Math.Max(a.Y0, a.Y1)));
        int rw = x2 - x, rh = y2 - y;
        if (rw <= 0 || rh <= 0) return;

        var rect = new CaptureRect(x, y, rw, rh);

        // 这两个效果都要求"从原图取色"，不能在已经打过码的画布上再打一次
        // （二次马赛克会越打越糊，撤销也撤不干净）。所以先把这一块还原成原图。
        var src = new CapturedFrame(origin, w, h, w * 4, 1.0);
        var work = new CapturedFrame(canvas, w, h, w * 4, 1.0);
        var piece = SnapshotEffects.Crop(src, rect);
        if (piece is null) return;

        // 把还原后的这一块拷回画布
        for (int row = 0; row < piece.Height; row++)
        {
            Buffer.BlockCopy(piece.Pixels, row * piece.Stride,
                             canvas, ((y + row) * w + x) * 4, piece.Stride);
        }

        if (a.Tool == SnipTool.Mosaic) SnapshotEffects.Mosaic(work, rect, a.Block);
        else SnapshotEffects.BoxBlur(work, rect, Math.Max(1, a.Block / 2), 2);
    }

    // ===================== 通用：按距离场描边 =====================

    private static void Stroke(byte[] canvas, int w, int h, SnipAnnotation a,
                               Func<double, double, double> dist,
                               double bx0, double by0, double bx1, double by1)
    {
        double half = Math.Max(0.5, a.Width / 2.0);
        int pad = (int)Math.Ceiling(half + 2);

        int x0 = Math.Max(0, (int)Math.Floor(bx0) - pad);
        int y0 = Math.Max(0, (int)Math.Floor(by0) - pad);
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(bx1) + pad);
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(by1) + pad);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double d = dist(x + 0.5, y + 0.5);
                double cov = half + 0.5 - Math.Abs(d);
                if (cov <= 0) continue;
                Blend(canvas, w, h, x, y, cov > 1 ? 1 : cov, a.R, a.G, a.B);
            }
        }
    }

    /// <summary>把一个像素按覆盖率混上颜色。源是不透明的画笔，所以是标准的 src-over。</summary>
    private static void Blend(byte[] px, int w, int h, int x, int y, double cov, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        if (cov <= 0) return;
        if (cov > 1) cov = 1;

        int i = (y * w + x) * 4;
        px[i] = (byte)(px[i] + (b - px[i]) * cov);
        px[i + 1] = (byte)(px[i + 1] + (g - px[i + 1]) * cov);
        px[i + 2] = (byte)(px[i + 2] + (r - px[i + 2]) * cov);
        px[i + 3] = 255;
    }

    private static double DistToSegment(double px, double py,
                                        double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len2 = dx * dx + dy * dy;
        if (len2 <= 0) return Math.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0));

        double t = ((px - x0) * dx + (py - y0) * dy) / len2;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        double cx = x0 + t * dx, cy = y0 + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static bool InTriangle(double px, double py,
                                   double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    // ===================== GDI 文字 =====================

    /// <summary>
    /// 量一段文字占多大（像素）。排版（居中、换行）要用。
    /// 量不出来返回 (0,0)，调用方自己兜底，别抛。
    /// </summary>
    public static (int Width, int Height) MeasureText(string text, int fontPx, string fontName, bool bold)
    {
        if (string.IsNullOrEmpty(text) || fontPx <= 0) return (0, 0);

        IntPtr screen = IntPtr.Zero, dc = IntPtr.Zero, font = IntPtr.Zero;
        try
        {
            screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return (0, 0);
            dc = Native.CreateCompatibleDC(screen);
            if (dc == IntPtr.Zero) return (0, 0);

            font = MakeFont(fontPx, fontName, bold);
            IntPtr old = Native.SelectObject(dc, font);
            try
            {
                if (!Native.GetTextExtentPoint32(dc, text, text.Length, out Native.SIZE sz)) return (0, 0);
                return (sz.cx, sz.cy);
            }
            finally { Native.SelectObject(dc, old); }
        }
        catch { return (0, 0); }
        finally
        {
            if (font != IntPtr.Zero) Native.DeleteObject(font);
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// 用 GDI 把一行文字画进 BGRA 画布。
    ///
    /// 流程是"把画布像素借给 GDI，让 GDI 直接在上面写，再拿回来"：
    ///   1. 建一块和画布同尺寸的 DIB（top-down，高度取负）；
    ///   2. 把画布像素拷进去；
    ///   3. 选字体、设透明背景、写字；
    ///   4. 拷回画布。
    ///
    /// 为什么必须<b>拷进 DIB 再写</b>而不是拿个空位图画完再合成：
    /// GDI 的抗锯齿文字是"拿覆盖率跟背景像素混"出来的，背景必须是真实内容，
    /// 空位图上背景全 0，混出来是一圈黑边。
    /// </summary>
    private static void GdiText(byte[] canvas, int w, int h, string text,
                                int x, int y, int fontPx,
                                byte r, byte g, byte b, string fontName, bool bold)
    {
        if (string.IsNullOrEmpty(text) || fontPx <= 0) return;
        if (canvas.Length < w * h * 4) return;

        IntPtr screen = IntPtr.Zero, dc = IntPtr.Zero, dib = IntPtr.Zero, font = IntPtr.Zero;
        try
        {
            screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return;
            dc = Native.CreateCompatibleDC(screen);
            if (dc == IntPtr.Zero) return;

            var bmi = default(Native.BITMAPINFO);
            bmi.Header.biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>();
            bmi.Header.biWidth = w;
            bmi.Header.biHeight = -h;          // 负 = top-down，和我们画布的行序一致
            bmi.Header.biPlanes = 1;
            bmi.Header.biBitCount = 32;
            bmi.Header.biCompression = Native.BI_RGB;

            dib = Native.CreateDIBSection(dc, ref bmi, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;

            Marshal.Copy(canvas, 0, bits, w * h * 4);
            IntPtr oldBmp = Native.SelectObject(dc, dib);

            font = MakeFont(fontPx, fontName, bold);
            IntPtr oldFont = Native.SelectObject(dc, font);

            try
            {
                Native.SetBkMode(dc, Native.TRANSPARENT);
                // COLORREF 是 0x00BBGGRR，和我们的 BGRA 字节序正好反过来
                Native.SetTextColor(dc, (uint)((b << 16) | (g << 8) | r));
                Native.TextOut(dc, x, y, text, text.Length);
            }
            finally
            {
                Native.SelectObject(dc, oldFont);
                Native.SelectObject(dc, oldBmp);
            }

            Marshal.Copy(bits, canvas, 0, w * h * 4);
        }
        catch { /* 写字失败就当没写，别把整张截图搞没 */ }
        finally
        {
            if (font != IntPtr.Zero) Native.DeleteObject(font);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static IntPtr MakeFont(int fontPx, string fontName, bool bold)
    {
        // 高度取负 = "我要的是字符高度这么多像素"，而不是"含行距的单元格高度"；
        // 取正数在不同字体下会莫名其妙小一圈。
        return Native.CreateFont(-fontPx, 0, 0, 0,
                                 bold ? Native.FW_BOLD : Native.FW_NORMAL,
                                 0, 0, 0, Native.DEFAULT_CHARSET,
                                 Native.OUT_TT_PRECIS, Native.CLIP_DEFAULT_PRECIS,
                                 Native.ANTIALIASED_QUALITY,
                                 Native.DEFAULT_PITCH, fontName);
    }

    private static class Native
    {
        public const int BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;
        public const int TRANSPARENT = 1;
        public const int DEFAULT_CHARSET = 1;
        public const int OUT_TT_PRECIS = 4;
        public const int CLIP_DEFAULT_PRECIS = 0;
        public const int ANTIALIASED_QUALITY = 4;
        public const int DEFAULT_PITCH = 0;
        public const int FW_NORMAL = 400;
        public const int FW_BOLD = 700;

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER Header;
            public uint bmiColors;   // 32bpp 不需要调色板，占位即可
        }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFont(int cHeight, int cWidth, int cEscapement,
            int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut,
            uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality,
            uint iPitchAndFamily, string pszFaceName);

        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool TextOut(IntPtr hdc, int x, int y, string text, int c);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextExtentPoint32(IntPtr hdc, string text, int c, out SIZE size);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
                                                     out IntPtr ppvBits, IntPtr hSection, uint offset);
    }
}
