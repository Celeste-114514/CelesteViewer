using System;

namespace CelesteGallery.Services;

/// <summary>
/// 把 <see cref="PhotoEdits"/> 上的编辑参数**渲染成实际像素**（路线图第 6 步）。
///
/// 为什么放在 Core 而不是界面层：
///   · 全项目只有这一处知道"旋转 → 翻转 → 裁剪 → 调色"的正确顺序。
///     查看器、缩略图墙、直方图、将来做导出/批处理，谁要"编辑后的样子"都调这里，
///     不会出现"墙上和打开后不一样"这种最难查的 bug。
///   · 纯托管、不碰 WinUI 也不碰 Magick —— 可以直接丢进控制台压测（见 IndexHarness K 段）。
///
/// 全程**只读原像素、返回新数组**（除了"没有编辑"这一种情况会原样返回同一个对象）。
/// 调用方拿到的原图永远不变，这是"非破坏性"这四个字的落点。
/// </summary>
public static class EditRenderer
{
    /// <summary>
    /// 应用编辑参数。
    ///
    /// 参数为空或"等于没改"时**直接返回原对象**（同一个引用）——
    /// 这是常态（图库里绝大多数图没编辑过），省掉一次整图拷贝很有意义。
    /// 因此调用方**不要**在原地改返回值，需要改就先自己拷一份。
    /// </summary>
    public static DecodedBitmap Apply(DecodedBitmap source, PhotoEdits? edits)
    {
        if (edits is null) return source;

        PhotoEdits e = edits.Normalized();
        if (e.IsIdentity) return source;

        byte[] pixels = source.Pixels;
        int w = source.PixelWidth;
        int h = source.PixelHeight;

        // 数据本身不完整就什么都别做（宁可显示原图，也不能把画面算成垃圾）
        if (w <= 0 || h <= 0 || pixels.Length < (long)w * h * 4) return source;

        // 翻转是**原地**改数组的，而旋转 / 裁剪会新建数组。
        // 于是这里必须盯住一件事：手里的数组到底是不是调用方那份原图？
        // 是的话，动它之前先拷一份 —— 不然"非破坏性"就是一句空话。
        // （2026-09-16 单测抓到过：只调色不改几何时，原图被就地改掉了。）
        byte[] Own(byte[] current)
            => ReferenceEquals(current, source.Pixels) ? (byte[])current.Clone() : current;

        // 1) 几何：旋转 → 翻转
        if (e.Rotation != 0) (pixels, w, h) = Rotate(pixels, w, h, e.Rotation);
        if (e.FlipH) FlipHorizontal(pixels = Own(pixels), w, h);
        if (e.FlipV) FlipVertical(pixels = Own(pixels), w, h);

        // 2) 裁剪（坐标是"几何变换之后"那张图的，归一化）
        if (e.HasCrop) (pixels, w, h) = Crop(pixels, w, h, e);

        // 3) 调色（放在最后：它作用在最终取景上）。
        //
        // 直接调 PhotoLook —— 查看器"调整"面板走的是同一个函数，
        // 所以"面板里预览的样子"和"存下来再打开的样子"必然一致。
        if (e.HasTone) pixels = ApplyTone(pixels, w, h, e.Look, source);

        return new DecodedBitmap
        {
            Pixels = pixels,
            PixelWidth = w,
            PixelHeight = h,
            Premultiplied = source.Premultiplied,
            DecoderName = source.DecoderName,
        };
    }

    /// <summary>
    /// 编辑之后图有多大。给"另存为对话框默认尺寸"和状态栏用，
    /// 不必真的把像素算出来（几千像素的图算一次不便宜）。
    /// </summary>
    public static (int Width, int Height) ResultSize(int width, int height, PhotoEdits? edits)
    {
        if (edits is null || width <= 0 || height <= 0) return (width, height);

        PhotoEdits e = edits.Normalized();
        if (e.IsIdentity) return (width, height);

        int w = width, h = height;

        // 90 / 270 会把宽高换过来
        if (e.Rotation is 90 or 270) (w, h) = (h, w);

        if (e.HasCrop)
        {
            w = Math.Max(1, (int)Math.Round(e.CropW * w));
            h = Math.Max(1, (int)Math.Round(e.CropH * h));
        }

        return (w, h);
    }

    /// <summary>
    /// 从像素矩形倒推归一化裁剪参数（界面拖完裁剪框调它）。
    ///
    /// 尺寸参数指的是**旋转翻转之后**那张图 —— 和 <see cref="Apply"/> 里裁剪那一步
    /// 看到的是同一张，口径不一致就会裁到别的地方去。
    /// </summary>
    public static void SetCropFromPixels(PhotoEdits edits, int width, int height,
                                         int x, int y, int cropWidth, int cropHeight)
    {
        if (edits is null || width <= 0 || height <= 0) return;

        int left = Math.Clamp(x, 0, width - 1);
        int top = Math.Clamp(y, 0, height - 1);
        int w = Math.Clamp(cropWidth, 1, width - left);
        int h = Math.Clamp(cropHeight, 1, height - top);

        edits.CropX = left / (double)width;
        edits.CropY = top / (double)height;
        edits.CropW = w / (double)width;
        edits.CropH = h / (double)height;
    }

    // ===== 几何 =====

    /// <summary>
    /// 旋转（顺时针 <paramref name="degrees"/> 度，只接受 90 / 180 / 270）。
    /// 90 / 270 会把宽高换过来。
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) Rotate(
        byte[] src, int w, int h, int degrees)
    {
        if (degrees == 180)
        {
            // 180° = 整块按像素倒序（第 i 个像素换到第 n-1-i 个位置），
            // 不用双重循环逐点搬，缓存友好得多
            var flipped = new byte[src.Length];
            int n = w * h;

            for (int i = 0; i < n; i++)
            {
                int s = i * 4;
                int d = (n - 1 - i) * 4;
                flipped[d] = src[s];
                flipped[d + 1] = src[s + 1];
                flipped[d + 2] = src[s + 2];
                flipped[d + 3] = src[s + 3];
            }

            return (flipped, w, h);
        }

        int nw = h, nh = w;
        var dst = new byte[src.Length];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // 90：左上角转到右上角；270（= 逆时针 90）：左上角转到左下角。
                // 这两个式子是"顺时针旋转"的标准换元，别凭感觉改。
                int nx, ny;
                if (degrees == 90) { nx = h - 1 - y; ny = x; }
                else { nx = y; ny = w - 1 - x; }

                int s = (y * w + x) * 4;
                int d = (ny * nw + nx) * 4;

                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }

        return (dst, nw, nh);
    }

    private static void FlipHorizontal(byte[] px, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;

            // 左右对调只需走到一半，中间那列自己跟自己换是无用功
            for (int x = 0; x < w / 2; x++)
            {
                int a = (row + x) * 4;
                int b = (row + (w - 1 - x)) * 4;
                (px[a], px[b]) = (px[b], px[a]);
                (px[a + 1], px[b + 1]) = (px[b + 1], px[a + 1]);
                (px[a + 2], px[b + 2]) = (px[b + 2], px[a + 2]);
                (px[a + 3], px[b + 3]) = (px[b + 3], px[a + 3]);
            }
        }
    }

    private static void FlipVertical(byte[] px, int w, int h)
    {
        int stride = w * 4;

        for (int y = 0; y < h / 2; y++)
        {
            int top = y * stride;
            int bottom = (h - 1 - y) * stride;

            for (int i = 0; i < stride; i++)
            {
                (px[top + i], px[bottom + i]) = (px[bottom + i], px[top + i]);
            }
        }
    }

    private static (byte[] Pixels, int Width, int Height) Crop(
        byte[] src, int w, int h, PhotoEdits e)
    {
        int x0 = (int)Math.Round(e.CropX * w);
        int y0 = (int)Math.Round(e.CropY * h);
        int cw = (int)Math.Round(e.CropW * w);
        int ch = (int)Math.Round(e.CropH * h);

        // 越界一律夹回来，不抛异常：拖裁剪框时"框比图大"是常态
        x0 = Math.Clamp(x0, 0, w - 1);
        y0 = Math.Clamp(y0, 0, h - 1);
        cw = Math.Clamp(cw, 1, w - x0);
        ch = Math.Clamp(ch, 1, h - y0);

        // 一点没裁就别拷了（归一化取整之后可能出现"等于整图"）
        if (x0 == 0 && y0 == 0 && cw == w && ch == h) return (src, w, h);

        var dst = new byte[cw * ch * 4];

        for (int y = 0; y < ch; y++)
        {
            Array.Copy(
                src, ((y0 + y) * w + x0) * 4,
                dst, y * cw * 4,
                cw * 4);
        }

        return (dst, cw, ch);
    }

    // ===== 调色 =====

    /// <summary>
    /// 调色。逻辑本体在 <see cref="PhotoLook"/>，这里只处理**预乘 alpha** 这一个坑。
    ///
    /// 坑是这样的：WIC 解码器（主路径）吐出来的像素是**预乘**的 ——
    /// 每个颜色分量已经乘过 alpha。对这种图直接改 RGB，RGB 和 alpha 就对不上了，
    /// 半透明的边缘立刻出现一圈黑边 / 白边。
    ///
    /// 处理办法：先"反预乘"把颜色还原成真实值，调完再乘回去。
    /// **只有确实存在半透明像素时才走这条路** —— 照片的绝大多数是全不透明的，
    /// 那种情况一次扫描就跳过，代价可以忽略。
    /// </summary>
    private static byte[] ApplyTone(
        byte[] pixels, int w, int h, LookSettings look, DecodedBitmap source)
    {
        // 动手改之前先确认这份数组是自己的。
        // （只调色、没做任何几何变换时，pixels 到现在还指着调用方的原图。）
        byte[] Own(byte[] p) => ReferenceEquals(p, source.Pixels) ? (byte[])p.Clone() : p;

        if (source.Premultiplied && HasTranslucent(pixels, w, h))
        {
            byte[] working = Own(pixels);
            UnPremultiply(working, w, h);

            byte[] toned = PhotoLook.Apply(working, w, h, look);
            if (!ReferenceEquals(toned, working)) working = toned;

            Premultiply(working, w, h);
            return working;
        }

        byte[] result = PhotoLook.Apply(pixels, w, h, look);

        // PhotoLook 自己会新建数组（不动入参）。万一它在某种情况下把原数组
        // 原样返回（像素长度对不上时就会），这里不能把调用方的原图当成结果交出去 ——
        // 调用方以为拿到的是"编辑后的新图"，回头再改它就会改到原图。
        return ReferenceEquals(result, source.Pixels) ? (byte[])result.Clone() : result;
    }

    /// <summary>
    /// 图里有没有**半透明**像素（alpha 落在 1~254 之间）。
    /// 全 0 和全 255 都不算：全透明像素 PhotoLook 会原样搬走，全不透明压根没有预乘问题。
    /// </summary>
    private static bool HasTranslucent(byte[] px, int w, int h)
    {
        int n = w * h;

        for (int i = 0; i < n; i++)
        {
            byte a = px[i * 4 + 3];
            if (a != 0 && a != 255) return true;
        }

        return false;
    }

    /// <summary>
    /// 预乘 → 直通：<c>存的值 = 真实颜色 × alpha / 255</c>，反过来就是除回去。
    ///
    /// 除法会有 1~2 级的舍入误差，但和"半透明边缘一整圈黑边"比起来，
    /// 轻重完全不成比例。
    /// </summary>
    private static void UnPremultiply(byte[] px, int w, int h)
    {
        int n = w * h;

        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int a = px[o + 3];

            // 全透明（没有颜色可言）和全不透明（等于乘 1）都不用换算
            if (a == 0 || a == 255) continue;

            px[o] = (byte)Math.Min(255, px[o] * 255 / a);
            px[o + 1] = (byte)Math.Min(255, px[o + 1] * 255 / a);
            px[o + 2] = (byte)Math.Min(255, px[o + 2] * 255 / a);
        }
    }

    /// <summary>直通 → 预乘：见 <see cref="UnPremultiply"/>，这是它的逆运算。</summary>
    private static void Premultiply(byte[] px, int w, int h)
    {
        int n = w * h;

        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int a = px[o + 3];

            if (a == 0 || a == 255) continue;

            px[o] = (byte)(px[o] * a / 255);
            px[o + 1] = (byte)(px[o + 1] * a / 255);
            px[o + 2] = (byte)(px[o + 2] * a / 255);
        }
    }
}
