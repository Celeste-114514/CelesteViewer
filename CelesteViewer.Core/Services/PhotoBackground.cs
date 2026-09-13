using System;

namespace CelesteViewer.Services;

/// <summary>
/// 背景怎么处理。三个动作共用同一张遮罩（见 <see cref="BackgroundMask"/>）：
///   Blur    —— 遮罩里那块模糊掉（背景虚化），主体保持清楚；
///   Remove  —— 遮罩里那块变成透明（把背景抠掉）；
///   Replace —— 遮罩里那块换成纯色或另一张图（换背景）。
/// </summary>
public enum BackgroundAction
{
    Blur = 0,
    Remove = 1,
    Replace = 2,
}

/// <summary>
/// 一块矩形区域（像素编号，两端都含）。
///
/// 这一档里到处都要用：画笔画了哪一块 → 只重算那一块的覆盖率 → 只重画那一块 → 只上传那一块。
/// "只动变了的地方"是这一档能做实时预览的根本 —— 整幅重来一遍，一张两千万像素的图
/// 每帧就要几百毫秒，鼠标一拖就废了。
/// </summary>
public readonly record struct BgRect(int X0, int Y0, int X1, int Y1)
{
    public static BgRect Empty => new(0, 0, -1, -1);

    public bool IsEmpty => X1 < X0 || Y1 < Y0;

    public int Width => IsEmpty ? 0 : X1 - X0 + 1;

    public int Height => IsEmpty ? 0 : Y1 - Y0 + 1;

    /// <summary>往外扩一圈，再夹回画面内。羽化、模糊都要多带一点邻域。</summary>
    public BgRect Expand(int pad, int width, int height)
    {
        if (IsEmpty) return this;

        int x0 = Math.Max(0, X0 - pad);
        int y0 = Math.Max(0, Y0 - pad);
        int x1 = Math.Min(width - 1, X1 + pad);
        int y1 = Math.Min(height - 1, Y1 + pad);

        return x1 < x0 || y1 < y0 ? Empty : new BgRect(x0, y0, x1, y1);
    }

    public static BgRect Union(BgRect a, BgRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;

        return new BgRect(
            Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0),
            Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));
    }

    public static BgRect Whole(int width, int height) => new(0, 0, width - 1, height - 1);
}

/// <summary>
/// 一次背景处理的参数。
///
/// 模糊强度存的是**比例**（0~1）而不是像素数 ——
/// 预览是在工作分辨率上算的、正式应用是在全分辨率上算的，
/// 存像素数的话两边半径不一样，预览和结果就对不上了。
/// 存比例，换算时乘"短边"，任何分辨率下糊掉的比例都一样。
/// </summary>
public readonly struct BackgroundOptions
{
    public BackgroundAction Action { get; init; }

    /// <summary>模糊强度 0~1。0 就是不模糊。</summary>
    public double BlurStrength { get; init; }

    /// <summary>Replace 用纯色时的颜色。</summary>
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }

    /// <summary>Replace 用图片时的背景图（BGRA，按"铺满"缩放后居中裁）。给了图就不用纯色。</summary>
    public byte[]? ImageBgra { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    public bool HasImage => ImageBgra is not null && ImageWidth > 0 && ImageHeight > 0;
}

/// <summary>
/// 背景遮罩：每像素一个字节，0 = 不算背景，255 = 完全算背景，中间值 = 半透明过渡。
///
/// 它是"哪块要动"的唯一依据 —— 模糊、删除、替换都只看它。
/// 尺寸跟着**工作分辨率**走（也就是当前显示的那张位图），
/// 点"应用"时才放大到全分辨率（见 <see cref="PhotoBackground.UpscaleMask"/>）。
/// 这样编辑期间才能实时预览。
/// </summary>
public sealed class BackgroundMask
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public BackgroundMask(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        Width = width;
        Height = height;
        Data = new byte[width * height];
    }

    public void Clear() => Array.Clear(Data, 0, Data.Length);

    public void FillAll() => Array.Fill(Data, (byte)255);

    public void Invert()
    {
        for (int i = 0; i < Data.Length; i++) Data[i] = (byte)(255 - Data[i]);
    }

    /// <summary>一个像素都没选中就是空的。空的遮罩上做任何动作都是白跑一趟。</summary>
    public bool IsEmpty()
    {
        var d = Data;
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i] != 0) return false;
        }

        return true;
    }

    /// <summary>选中区域的包围盒。全是 0 就返回空。</summary>
    public BgRect Bounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (Data[row + x] == 0) continue;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0 ? BgRect.Empty : new BgRect(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// 在遮罩上盖一个圆（画笔）或者把一个圆擦掉（橡皮）。
    ///
    /// 盖用 max、擦用 min：一笔里圆圈反复叠的时候，中间不会越叠越"过"，
    /// 边缘那圈半透明的过渡也能保住。
    /// </summary>
    public BgRect PaintCircle(double cx, double cy, double radius, double softness, bool erase)
    {
        if (radius <= 0) return BgRect.Empty;

        // 柔和度：从外圈往里留出一段渐变的宽度。0 就是硬边圆
        double ramp = radius * Math.Clamp(softness, 0, 0.95);

        int x0 = (int)Math.Floor(cx - radius);
        int y0 = (int)Math.Floor(cy - radius);
        int x1 = (int)Math.Ceiling(cx + radius);
        int y1 = (int)Math.Ceiling(cy + radius);

        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > Width - 1) x1 = Width - 1;
        if (y1 > Height - 1) y1 = Height - 1;
        if (x1 < x0 || y1 < y0) return BgRect.Empty;

        for (int y = y0; y <= y1; y++)
        {
            int row = y * Width;
            double dy = y + 0.5 - cy;

            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= radius) continue;

                double t = ramp <= 0 ? 1.0 : Math.Clamp((radius - dist) / ramp, 0, 1);
                int cov = (int)(t * 255 + 0.5);
                if (cov <= 0) continue;

                int i = row + x;
                if (erase)
                {
                    int left = 255 - cov;
                    if (Data[i] > left) Data[i] = (byte)left;
                }
                else if (Data[i] < cov)
                {
                    Data[i] = (byte)cov;
                }
            }
        }

        return new BgRect(x0, y0, x1, y1);
    }

    /// <summary>
    /// 沿一条线段连续盖章。鼠标事件之间隔得挺远，连成串才自然 ——
    /// 不然快速划一笔会变成一串断开的珠子。
    /// </summary>
    public BgRect PaintSegment(
        double x0, double y0, double x1, double y1,
        double radius, double softness, bool erase)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);

        // 步长取半径的 1/3：重叠够密，看着是一条连续的带子
        double step = Math.Max(0.7, radius / 3.0);
        int n = (int)Math.Ceiling(len / step);

        var first = PaintCircle(x0, y0, radius, softness, erase);
        if (n <= 1) return first;

        // 第一个圆单独算过了，从 1 开始，免得同一个位置盖两遍
        for (int i = 1; i <= n; i++)
        {
            double t = i / (double)n;
            var r = PaintCircle(x0 + dx * t, y0 + dy * t, radius, softness, erase);
            first = BgRect.Union(first, r);
        }

        return first;
    }

    /// <summary>
    /// 魔棒：从 (sx, sy) 出发，把颜色相近且**连成一片**的像素都选上。
    ///
    /// 这是这一档里最有用的一个东西 —— 纯色 / 渐变 / 天空 / 影棚背景
    /// 都是一点就选中一大片，比拿画笔画半天强得多。复杂背景才需要画笔兜底。
    ///
    /// 结果是**并进**现有遮罩，不是替换：天空和地面可以点两次一起选上。
    /// </summary>
    public BgRect Wand(byte[] bgra, int width, int height, int sx, int sy, int tolerance)
    {
        if (bgra is null) return BgRect.Empty;
        if (width != Width || height != Height) return BgRect.Empty;
        if (sx < 0 || sy < 0 || sx >= width || sy >= height) return BgRect.Empty;
        if (bgra.Length < width * height * 4) return BgRect.Empty;

        int seed = (sy * width + sx) * 4;
        byte sb = bgra[seed], sg = bgra[seed + 1], sr = bgra[seed + 2], sa = bgra[seed + 3];

        int tol = Math.Clamp(tolerance, 0, 255);

        var visited = new byte[width * height];

        // 扫描线式漫水：一行一行往里填，比"每像素入栈"少一个数量级的压栈
        var stack = new System.Collections.Generic.Stack<int>();
        stack.Push(sy * width + sx);

        int minX = sx, minY = sy, maxX = sx, maxY = sy;

        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            if (visited[idx] != 0) continue;

            int y = idx / width;
            int x = idx - y * width;

            if (!Similar(bgra, (y * width + x) * 4, sb, sg, sr, sa, tol)) continue;

            int row = y * width;

            // 往左右两边铺到走不动为止
            int xl = x;
            while (xl > 0 && visited[row + xl - 1] == 0
                && Similar(bgra, (row + xl - 1) * 4, sb, sg, sr, sa, tol)) xl--;

            int xr = x;
            while (xr < width - 1 && visited[row + xr + 1] == 0
                && Similar(bgra, (row + xr + 1) * 4, sb, sg, sr, sa, tol)) xr++;

            for (int xx = xl; xx <= xr; xx++)
            {
                int i = row + xx;
                visited[i] = 1;
                if (Data[i] < 255) Data[i] = 255;
            }

            SeedRow(y - 1, xl, xr);
            SeedRow(y + 1, xl, xr);

            if (xl < minX) minX = xl;
            if (xr > maxX) maxX = xr;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return new BgRect(minX, minY, maxX, maxY);

        // 在邻行给"每一段连着可填的像素"留一个种子。
        //
        // ⚠️ 这里**不能**用 visited 判断"是不是一段的开头"：邻行此刻根本还没被访问过，
        // 全是 0，于是除了当前行的第一个列以外一个种子都不会入栈，
        // 邻行那一整段就被整段漏掉，漫水提前停住。
        // （这个坑是拿渐变背景 + 主体的真实形状顶出来的：纯色矩形测试里种子恰好对齐，看不出来。）
        // 正确做法是扫描时自己记住"上一格是不是候选"。
        void SeedRow(int ny, int xl, int xr)
        {
            if (ny < 0 || ny >= height) return;

            int baseIdx = ny * width;
            int from = Math.Max(0, xl - 1);      // 多带一格，斜着搭上的那一段也要能进来
            int to = Math.Min(width - 1, xr + 1);

            bool prevCandidate = false;

            for (int xx = from; xx <= to; xx++)
            {
                int i = baseIdx + xx;
                bool candidate = visited[i] == 0
                    && Similar(bgra, i * 4, sb, sg, sr, sa, tol);

                if (candidate && !prevCandidate) stack.Push(i);
                prevCandidate = candidate;
            }
        }
    }

    /// <summary>
    /// 把整张遮罩轻微柔化一下。给魔棒用完调一次 ——
    /// 魔棒是按颜色边界硬切出来的，直接拿去抠图会在边上留一圈锯齿。
    ///
    /// 只在点了魔棒之后调一次（不是每帧），所以整幅算也不心疼。
    /// </summary>
    public void Soften(int radius)
    {
        if (radius <= 0) return;

        int n = Width * Height;
        var mid = new byte[n];

        Blur.BoxHorizontal(Data, mid, Width, Height, 1, radius);
        Blur.BoxVertical(mid, Data, Width, Height, 1, radius);
    }

    /// <summary>
    /// 颜色够不够像。用四通道里**差得最多的那一个**当距离 ——
    /// 直觉上就是"任何一个通道都不能偏太多"，比欧氏距离好调。
    /// </summary>
    private static bool Similar(byte[] bgra, int i, byte b, byte g, byte r, byte a, int tol)
    {
        int db = bgra[i] - b;
        if (db < 0) db = -db;
        if (db > tol) return false;

        int dg = bgra[i + 1] - g;
        if (dg < 0) dg = -dg;
        if (dg > tol) return false;

        int dr = bgra[i + 2] - r;
        if (dr < 0) dr = -dr;
        if (dr > tol) return false;

        int da = bgra[i + 3] - a;
        if (da < 0) da = -da;

        return da <= tol;
    }
}

/// <summary>
/// 羽化后的覆盖率。和 <see cref="BackgroundMask"/> 一一对应，只是边缘软了一圈。
///
/// 单独拎出来当个缓冲，是因为**它能增量更新**：
/// 画笔动了哪一块，就只重算那一块（外加羽化要用的邻域），
/// 而不是整幅重新羽化一遍。整幅重算在五百万像素的工作画布上要二十多毫秒，
/// 鼠标一拖就掉帧；只算脏区是几十微秒。
/// </summary>
public sealed class BackgroundCoverage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    /// <summary>重算时用的临时缓冲。留着反复用，免得每帧分配几 MB 给 GC 添堵。</summary>
    private byte[] _tmp1 = Array.Empty<byte>();
    private byte[] _tmp2 = Array.Empty<byte>();

    public BackgroundCoverage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        Width = width;
        Height = height;
        Data = new byte[width * height];
    }

    public void Clear() => Array.Clear(Data, 0, Data.Length);

    /// <summary>
    /// 重算一块区域的覆盖率。<paramref name="area"/> 是"遮罩里动了的地方"。
    ///
    /// **两个区域要分清，这是这一段最容易写错的地方**：
    ///
    ///   * 真正需要写回的，是 area **往外扩 2×羽化半径** —— 羽化是加权平均，
    ///     改动会像水波一样往外传 2 圈（两遍盒式模糊各传一圈）。只写 area 本身，
    ///     晕圈就是上一次算的旧值，实际表现是"羽化只朝里长、朝外是硬边"，
    ///     抠图时能明显看到一圈毛边；
    ///   * 为了算对这些像素，还要再多读一圈遮罩。算的区域比写回的区域大一圈，
    ///     靠外那圈的夹边误差就落不到写回区里。
    ///
    /// 这两个圈没搞对的话，症状是"涂着涂着边缘变得不对劲"，而且越涂越明显 ——
    /// 探针第 11 组专门盯着这个。
    /// </summary>
    public void Rebuild(BackgroundMask mask, BgRect area, int feather)
    {
        if (area.IsEmpty) return;

        // 覆盖率真正会变的像素
        var affected = area.Expand(feather * 2, Width, Height);
        if (affected.IsEmpty) return;

        // 算这块需要的遮罩范围（多带一圈，免得夹边误差落进写回区）
        var box = affected.Expand(feather * 2 + 1, Width, Height);
        if (box.IsEmpty) return;

        int bw = box.Width;
        int bh = box.Height;

        EnsureBuffers(bw * bh);

        // 把遮罩那一块抠出来
        for (int y = 0; y < bh; y++)
        {
            int src = (box.Y0 + y) * Width + box.X0;
            Buffer.BlockCopy(mask.Data, src, _tmp1, y * bw, bw);
        }

        if (feather > 0)
        {
            // 两遍盒式模糊 ≈ 三角核，够像高斯了，而且每遍都是 O(1)/像素
            Blur.BoxHorizontal(_tmp1, _tmp2, bw, bh, 1, feather);
            Blur.BoxVertical(_tmp2, _tmp1, bw, bh, 1, feather);
            Blur.BoxHorizontal(_tmp1, _tmp2, bw, bh, 1, feather);
            Blur.BoxVertical(_tmp2, _tmp1, bw, bh, 1, feather);
        }

        int x0 = Math.Max(affected.X0, box.X0);
        int y0 = Math.Max(affected.Y0, box.Y0);
        int x1 = Math.Min(affected.X1, box.X1);
        int y1 = Math.Min(affected.Y1, box.Y1);

        for (int y = y0; y <= y1; y++)
        {
            int dst = y * Width + x0;
            int src = (y - box.Y0) * bw + (x0 - box.X0);
            Buffer.BlockCopy(_tmp1, src, Data, dst, x1 - x0 + 1);
        }
    }

    private void EnsureBuffers(int need)
    {
        if (_tmp1.Length < need) _tmp1 = new byte[need];
        if (_tmp2.Length < need) _tmp2 = new byte[need];
    }
}

/// <summary>
/// 背景处理引擎。
///
/// 全部是 byte[] 进 byte[] 出的纯计算，不碰界面，所以在探针工程里能直接压测。
/// 像素一律 **BGRA**（B=0 G=1 R=2 A=3），和 ImageEditService.LoadPixels 一个约定。
///
/// 性能上是这么分工的：
///
///   * **跟遮罩无关**的重活先算一次缓存起来 —— 模糊版整幅图就是这样。
///     模糊强度定了之后它是个常量，没道理每帧重算；
///   * 每帧只做"按覆盖率把两份像素混一混"，而且**只在脏区里做**。
///
/// 于是拖鼠标时每帧的开销是"几个像素级操作"，而不是"整幅模糊一遍"。
/// 这是这一档能做实时预览的根本原因，改的时候别把脏区那一层拆掉。
/// </summary>
public static class PhotoBackground
{
    /// <summary>
    /// 模糊强度（0~1）→ 像素半径。
    ///
    /// 乘的是**短边**：同一张照片在工作分辨率和全分辨率下算出来的半径不一样，
    /// 但"糊掉的比例"一样，看到的画面就一致。满强度是短边的 6%（够糊了）。
    /// </summary>
    public static double BlurRadius(double strength, int width, int height)
    {
        if (strength <= 0.001) return 0;

        int min = Math.Min(width, height);
        return Math.Max(0, strength * min * 0.06);
    }

    /// <summary>羽化（0~1）→ 像素半径。0 就是硬边。</summary>
    public static int FeatherRadius(double feather, int width, int height)
    {
        if (feather <= 0.001) return 0;

        int min = Math.Min(width, height);
        return Math.Max(0, (int)Math.Round(feather * min * 0.008));
    }

    /// <summary>
    /// 整幅的模糊版。<paramref name="source"/> 不动，返回一份新的。
    ///
    /// 这是这一档最贵的一步（两千万像素要一秒上下），所以调用方应该把它缓存起来 ——
    /// 只有"模糊强度"变了才需要重算，拖鼠标、涂遮罩都不需要。
    /// </summary>
    public static byte[] BlurCopy(byte[] source, int width, int height, double strength)
    {
        int n = width * height;
        var dst = new byte[n * 4];
        Buffer.BlockCopy(source, 0, dst, 0, n * 4);

        double radius = BlurRadius(strength, width, height);
        int r = (int)Math.Round(radius);
        if (r <= 0) return dst;

        // 半径超过短边的一半就没意义了（整幅都成了一块色），夹一下省时间
        int cap = Math.Max(1, Math.Min(width, height) / 2);
        if (r > cap) r = cap;

        // 逐通道处理：临时内存从"两份整幅 BGRA"降到"两份单通道"，
        // 两千万像素上差 144MB
        var channel = new byte[n];
        var mid = new byte[n];

        for (int ch = 0; ch < 3; ch++)
        {
            for (int i = 0; i < n; i++) channel[i] = source[i * 4 + ch];

            Blur.BoxHorizontal(channel, mid, width, height, 1, r);
            Blur.BoxVertical(mid, channel, width, height, 1, r);
            Blur.BoxHorizontal(channel, mid, width, height, 1, r);
            Blur.BoxVertical(mid, channel, width, height, 1, r);

            for (int i = 0; i < n; i++) dst[i * 4 + ch] = channel[i];
        }

        return dst;
    }

    /// <summary>
    /// 把效果画到 <paramref name="dest"/> 上，**只动 <paramref name="area"/> 那一块**。
    ///
    /// ⚠️ 调用约定：<paramref name="area"/> **外面**的像素原样不动，得由调用方保证是对的
    /// （界面上是首尾相接的：这块的边界和上一块严丝合缝，整幅自然就都刷到了）。
    ///
    /// 区域内则是"先铺一遍原像素、再按覆盖率混合"，所以：
    ///   * 不用担心 dest 里原来是什么 —— 遮罩缩小之后留下的旧效果会被这一铺冲干净；
    ///   * <paramref name="dest"/> 可以和 <paramref name="source"/> 是同一个数组
    ///     （正式应用时就这么用，省一份整幅拷贝）。铺那一遍会自动跳过，
    ///     而混合是"读了自己马上写自己"、不依赖邻居，所以原地安全。
    ///
    /// 后面要是想把模糊挪进来（它要看一圈邻居），这两条就得重新想。
    /// </summary>
    /// <param name="coverage">
    /// 羽化后的覆盖率，长度必须是 宽×高。全 0 表示没选中任何东西。
    /// </param>
    /// <param name="blurred">
    /// 模糊版整幅像素（<see cref="BlurCopy"/> 的结果）。只有 模糊 这一档需要，
    /// 别的档传 null。
    /// </param>
    /// <returns>真的动了画面返回 true。</returns>
    public static bool Render(
        byte[] dest, byte[] source, int width, int height,
        byte[] coverage, BgRect area, BackgroundOptions options, byte[]? blurred)
    {
        if (width <= 0 || height <= 0) return false;
        if (dest.Length < width * height * 4) return false;
        if (source.Length < width * height * 4) return false;
        if (coverage.Length < width * height) return false;

        var box = area.Expand(0, width, height);
        if (box.IsEmpty) return false;

        // 先把这块铺回原样。少了这一步，遮罩缩小 / 擦掉之后，
        // 画面上那块的旧效果就留在原地不走了（dest 是长期复用的同一份缓冲）
        if (!ReferenceEquals(dest, source)) CopyRegion(source, dest, width, box);

        switch (options.Action)
        {
            case BackgroundAction.Blur:
                // 没给模糊图就什么都不做，由调用方去建。这里不偷偷算 ——
                // 一算就是几百毫秒，藏在"渲染"里调用方根本没法察觉
                if (blurred is null || blurred.Length < width * height * 4) return false;
                return Blend(dest, source, blurred, cover: coverage, width, box);

            case BackgroundAction.Remove:
                return RemoveAlpha(dest, source, coverage, width, box);

            default:
                if (options.HasImage) return ReplaceImage(dest, source, coverage, width, height, box, options);
                return ReplaceColor(dest, source, coverage, width, box, options.R, options.G, options.B);
        }
    }

    private static void CopyRegion(byte[] source, byte[] dest, int width, BgRect box)
    {
        int bytes = box.Width * 4;

        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int offset = (y * width + box.X0) * 4;
            Buffer.BlockCopy(source, offset, dest, offset, bytes);
        }
    }

    /// <summary>按覆盖率把两份像素混起来：覆盖率 0 → 完全取 a，255 → 完全取 b。</summary>
    private static bool Blend(byte[] dest, byte[] a, byte[] b, byte[] cover, int width, BgRect box)
    {
        bool touched = false;

        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;

            for (int x = box.X0; x <= box.X1; x++)
            {
                int c = cover[row + x];
                if (c == 0) continue;

                int i = (row + x) * 4;
                dest[i] = Mix(a[i], b[i], c);
                dest[i + 1] = Mix(a[i + 1], b[i + 1], c);
                dest[i + 2] = Mix(a[i + 2], b[i + 2], c);
                dest[i + 3] = Mix(a[i + 3], b[i + 3], c);
                touched = true;
            }
        }

        return touched;
    }

    private static bool RemoveAlpha(byte[] dest, byte[] source, byte[] cover, int width, BgRect box)
    {
        bool touched = false;

        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;

            for (int x = box.X0; x <= box.X1; x++)
            {
                int c = cover[row + x];
                if (c == 0) continue;

                int i = (row + x) * 4;

                // 颜色三个通道原样留着：万一以后想还原还有救，
                // 而且半透明的边缘也得靠它，不然边缘会泛黑
                dest[i] = source[i];
                dest[i + 1] = source[i + 1];
                dest[i + 2] = source[i + 2];
                dest[i + 3] = Mix(source[i + 3], 0, c);
                touched = true;
            }
        }

        return touched;
    }

    private static bool ReplaceColor(
        byte[] dest, byte[] source, byte[] cover, int width, BgRect box,
        byte r, byte g, byte b)
    {
        bool touched = false;

        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;

            for (int x = box.X0; x <= box.X1; x++)
            {
                int c = cover[row + x];
                if (c == 0) continue;

                int i = (row + x) * 4;
                dest[i] = Mix(source[i], b, c);
                dest[i + 1] = Mix(source[i + 1], g, c);
                dest[i + 2] = Mix(source[i + 2], r, c);
                dest[i + 3] = Mix(source[i + 3], 255, c);
                touched = true;
            }
        }

        return touched;
    }

    /// <summary>
    /// 换成另一张图。背景图按"铺满"（cover）缩放后居中裁切 ——
    /// 用户选一张风景照当背景，不会想在两边看到空白。
    /// </summary>
    private static bool ReplaceImage(
        byte[] dest, byte[] source, byte[] cover, int width, int height, BgRect box, BackgroundOptions o)
    {
        byte[] img = o.ImageBgra!;
        int iw = o.ImageWidth, ih = o.ImageHeight;

        double scale = Math.Max(width / (double)iw, height / (double)ih);
        double offX = (width - iw * scale) / 2;
        double offY = (height - ih * scale) / 2;

        bool touched = false;

        for (int y = box.Y0; y <= box.Y1; y++)
        {
            int row = y * width;
            double sy = (y + 0.5 - offY) / scale - 0.5;

            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;

            int ya = Math.Clamp(y0, 0, ih - 1);
            int yb = Math.Clamp(y0 + 1, 0, ih - 1);

            for (int x = box.X0; x <= box.X1; x++)
            {
                int c = cover[row + x];
                if (c == 0) continue;

                double sx = (x + 0.5 - offX) / scale - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;

                int xa = Math.Clamp(x0, 0, iw - 1);
                int xb = Math.Clamp(x0 + 1, 0, iw - 1);

                int p00 = (ya * iw + xa) * 4;
                int p01 = (ya * iw + xb) * 4;
                int p10 = (yb * iw + xa) * 4;
                int p11 = (yb * iw + xb) * 4;

                int i = (row + x) * 4;

                for (int ch = 0; ch < 3; ch++)
                {
                    double top = img[p00 + ch] + (img[p01 + ch] - img[p00 + ch]) * fx;
                    double bot = img[p10 + ch] + (img[p11 + ch] - img[p10 + ch]) * fx;
                    double v = top + (bot - top) * fy;

                    dest[i + ch] = Mix(source[i + ch], (byte)Math.Clamp(v + 0.5, 0, 255), c);
                }

                dest[i + 3] = Mix(source[i + 3], 255, c);
                touched = true;
            }
        }

        return touched;
    }

    private static byte Mix(byte from, byte to, int cov)
        => (byte)((from * (255 - cov) + to * cov + 127) / 255);

    // ===== 缩放 =====

    /// <summary>
    /// 把遮罩放大到全分辨率（或者缩到工作分辨率）。
    ///
    /// 双线性：放大之后边缘本来就该是软的，正好把"屏幕上的一格"摊成全分辨率下的几格，
    /// 和用户看到的预览对得上。用最近邻会出现一整块方格子。
    /// </summary>
    public static BackgroundMask UpscaleMask(BackgroundMask mask, int width, int height)
    {
        var dst = new BackgroundMask(width, height);

        int sw = mask.Width, sh = mask.Height;
        byte[] src = mask.Data;

        if (sw == width && sh == height)
        {
            Buffer.BlockCopy(src, 0, dst.Data, 0, src.Length);
            return dst;
        }

        double kx = sw / (double)width;
        double ky = sh / (double)height;

        for (int y = 0; y < height; y++)
        {
            double sy = (y + 0.5) * ky - 0.5;
            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;

            int ya = Math.Clamp(y0, 0, sh - 1);
            int yb = Math.Clamp(y0 + 1, 0, sh - 1);

            int row = y * width;

            for (int x = 0; x < width; x++)
            {
                double sx = (x + 0.5) * kx - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;

                int xa = Math.Clamp(x0, 0, sw - 1);
                int xb = Math.Clamp(x0 + 1, 0, sw - 1);

                double top = src[ya * sw + xa] + (src[ya * sw + xb] - src[ya * sw + xa]) * fx;
                double bot = src[yb * sw + xa] + (src[yb * sw + xb] - src[yb * sw + xa]) * fx;
                double v = top + (bot - top) * fy;

                dst.Data[row + x] = (byte)Math.Clamp(v + 0.5, 0, 255);
            }
        }

        return dst;
    }

    /// <summary>
    /// 改图像尺寸（BGRA）。缩小用区域平均（点采样会丢出摩尔纹），
    /// 放大用双线性。用来把全分辨率像素降成工作分辨率。
    /// </summary>
    public static byte[] ResampleBgra(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return Array.Empty<byte>();
        if (src.Length < sw * sh * 4) return Array.Empty<byte>();

        var dst = new byte[dw * dh * 4];

        if (dw == sw && dh == sh)
        {
            Buffer.BlockCopy(src, 0, dst, 0, dw * dh * 4);
            return dst;
        }

        if (dw >= sw || dh >= sh)
        {
            // 放大：双线性
            for (int y = 0; y < dh; y++)
            {
                double sy = (y + 0.5) * sh / dh - 0.5;
                int y0 = (int)Math.Floor(sy);
                double fy = sy - y0;
                int ya = Math.Clamp(y0, 0, sh - 1);
                int yb = Math.Clamp(y0 + 1, 0, sh - 1);

                for (int x = 0; x < dw; x++)
                {
                    double sx = (x + 0.5) * sw / dw - 0.5;
                    int x0 = (int)Math.Floor(sx);
                    double fx = sx - x0;
                    int xa = Math.Clamp(x0, 0, sw - 1);
                    int xb = Math.Clamp(x0 + 1, 0, sw - 1);

                    int p00 = (ya * sw + xa) * 4;
                    int p01 = (ya * sw + xb) * 4;
                    int p10 = (yb * sw + xa) * 4;
                    int p11 = (yb * sw + xb) * 4;
                    int p = (y * dw + x) * 4;

                    for (int ch = 0; ch < 4; ch++)
                    {
                        double top = src[p00 + ch] + (src[p01 + ch] - src[p00 + ch]) * fx;
                        double bot = src[p10 + ch] + (src[p11 + ch] - src[p10 + ch]) * fx;
                        dst[p + ch] = (byte)Math.Clamp(top + (bot - top) * fy + 0.5, 0, 255);
                    }
                }
            }

            return dst;
        }

        // 缩小：每个目标像素取源图里对应的一小块做平均
        for (int y = 0; y < dh; y++)
        {
            int sy0 = y * sh / dh;
            int sy1 = Math.Max(sy0 + 1, (y + 1) * sh / dh);
            if (sy1 > sh) sy1 = sh;

            for (int x = 0; x < dw; x++)
            {
                int sx0 = x * sw / dw;
                int sx1 = Math.Max(sx0 + 1, (x + 1) * sw / dw);
                if (sx1 > sw) sx1 = sw;

                int b = 0, g = 0, r = 0, a = 0, n = 0;

                for (int yy = sy0; yy < sy1; yy++)
                {
                    int row = yy * sw * 4;
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        int p = row + xx * 4;
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                        a += src[p + 3];
                        n++;
                    }
                }

                if (n == 0) n = 1;

                int q = (y * dw + x) * 4;
                dst[q] = (byte)((b + n / 2) / n);
                dst[q + 1] = (byte)((g + n / 2) / n);
                dst[q + 2] = (byte)((r + n / 2) / n);
                dst[q + 3] = (byte)((a + n / 2) / n);
            }
        }

        return dst;
    }
}

/// <summary>
/// 盒式模糊。单独拎出来是因为有三个地方要用它：
/// 整幅模糊、遮罩羽化、遮罩柔化。
///
/// 用滑动窗口做，每个像素只加一次减一次，**跟半径多大没关系** ——
/// 半径 200 和半径 2 一样快。不然大半径的实时预览根本没戏。
/// </summary>
internal static class Blur
{
    /// <summary>
    /// 横向。<paramref name="stride"/> 是源数组的通道间隔
    /// （单通道传 1）。越界一律夹到边上，相当于边缘拉伸，
    /// 不然画面四边会因为"窗口缩水"而变暗。
    /// </summary>
    public static void BoxHorizontal(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        if (radius <= 0) return;

        int window = radius * 2 + 1;
        int half = window / 2;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int sum = 0;

            // 先把 x=0 处的窗口铺满
            for (int d = -radius; d <= radius; d++)
            {
                int xx = d < 0 ? 0 : (d >= width ? width - 1 : d);
                sum += src[(row + xx) * stride];
            }

            for (int x = 0; x < width; x++)
            {
                dst[(row + x) * stride] = (byte)((sum + half) / window);

                int add = x + radius + 1;
                int rem = x - radius;
                if (add >= width) add = width - 1;
                if (rem < 0) rem = 0;

                sum += src[(row + add) * stride] - src[(row + rem) * stride];
            }
        }
    }

    public static void BoxVertical(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        if (radius <= 0) return;

        int window = radius * 2 + 1;
        int half = window / 2;

        for (int x = 0; x < width; x++)
        {
            int sum = 0;

            for (int d = -radius; d <= radius; d++)
            {
                int yy = d < 0 ? 0 : (d >= height ? height - 1 : d);
                sum += src[(yy * width + x) * stride];
            }

            for (int y = 0; y < height; y++)
            {
                dst[(y * width + x) * stride] = (byte)((sum + half) / window);

                int add = y + radius + 1;
                int rem = y - radius;
                if (add >= height) add = height - 1;
                if (rem < 0) rem = 0;

                sum += src[(add * width + x) * stride] - src[(rem * width + x) * stride];
            }
        }
    }
}
