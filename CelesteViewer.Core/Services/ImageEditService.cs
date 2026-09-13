using System;
using System.IO;
using ImageMagick;

namespace CelesteViewer.Services;

/// <summary>
/// 图片编辑：旋转、翻面、改尺寸、另存。
///
/// 落在 Core 里而不是 UI 层，是因为它只跟"像素"打交道 ——
/// 这样以后做批处理、做命令行工具都能直接复用，不用拖着一套界面。
///
/// 一律 **byte[] 进、byte[] 出**：
/// 编辑的中间结果就存在内存里（_editBytes），用户点"另存为"才落到磁盘，
/// 这样反复试效果不会在硬盘上堆一串临时文件，也不会误改原图。
///
/// 底层用 Magick.NET —— 它已经在项目里了（兜底解码器），
/// 做这些操作不需要再往程序里塞任何新东西。
/// </summary>
public static class ImageEditService
{
    /// <summary>JPEG 另存时的质量。95 基本看不出压缩痕迹，体积也还能接受。</summary>
    private const int JpegQuality = 95;

    /// <summary>
    /// 旋转。只接受 90 / 180 / 270，其它角度原样返回 ——
    /// 任意角度旋转会留下一圈画布，看图软件里几乎没人想要。
    /// </summary>
    public static byte[] Rotate(byte[] source, int degrees)
    {
        if (degrees != 90 && degrees != 180 && degrees != 270) return source;

        using var image = new MagickImage(source);
        image.Rotate((double)degrees);
        return image.ToByteArray(MagickFormat.Png);
    }

    /// <summary>翻面。<paramref name="horizontal"/> 为真时左右翻，否则上下翻。</summary>
    public static byte[] Flip(byte[] source, bool horizontal)
    {
        using var image = new MagickImage(source);
        if (horizontal) image.Flop();
        else image.Flip();
        return image.ToByteArray(MagickFormat.Png);
    }

    /// <summary>
    /// 裁剪。矩形按**像素**给，指的是原图坐标系里的位置。
    ///
    /// 越界一律夹回图内，不抛异常：界面上拖裁剪框时本来就容易出现
    /// "框比图还大"或者"边正好压在边界上"的情况，让调用方每次先去夹
    /// 反而更容易漏。夹完如果还是整张图，直接原样返回（等于没裁）。
    /// </summary>
    public static byte[] Crop(byte[] source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return source;

        using var image = new MagickImage(source);

        int iw = (int)image.Width;
        int ih = (int)image.Height;
        if (iw <= 0 || ih <= 0) return source;

        int left = Math.Clamp(x, 0, iw - 1);
        int top = Math.Clamp(y, 0, ih - 1);
        int w = Math.Min(width, iw - left);
        int h = Math.Min(height, ih - top);

        if (w <= 0 || h <= 0) return source;

        // 一点没裁就别走编解码了，几 MB 的图白跑一趟
        if (left == 0 && top == 0 && w == iw && h == ih) return source;

        image.Crop(new MagickGeometry(left, top, (uint)w, (uint)h));

        // Crop 只是把画布裁小，Magick 还会记着"原来的原点在哪"（page 偏移）。
        // 不重置的话存出来的 PNG 会带一段偏移（oFFs 块），别的软件打开会莫名其妙地歪。
        // 这个版本没有 +repage 那个方法，等价写法就是手工把 page 设成"原点在左上、尺寸就是现在这张"。
        image.Page = new MagickGeometry(0, 0, (uint)w, (uint)h);

        return image.ToByteArray(MagickFormat.Png);
    }

    /// <summary>
    /// 改尺寸。宽或高给 0 表示"另一个算了之后按比例来"，两个都给就按给的来
    /// （不强制保持比例 —— 用户可能在对话框里手动改了宽高）。
    /// </summary>
    public static byte[] Resize(byte[] source, int width, int height)
    {
        if (width <= 0 && height <= 0) return source;

        using var image = new MagickImage(source);

        if (width <= 0 || height <= 0)
        {
            // 只给了一边：另一边按原比例算
            double scale = width > 0
                ? width / (double)image.Width
                : height / (double)image.Height;
            width = Math.Max(1, (int)Math.Round(image.Width * scale));
            height = Math.Max(1, (int)Math.Round(image.Height * scale));
        }

        // Magick.NET 的 Resize(w, h) 默认**保持**宽高比（等于"塞进这个框里"），
        // 用户明明把"保持宽高比"取消勾选了却还是等比缩，会以为软件不听话。
        // 两个尺寸都给了就明确关掉等比，让它照着给的来。
        if (width > 0 && height > 0)
        {
            image.Resize(new MagickGeometry((uint)width, (uint)height)
            {
                IgnoreAspectRatio = true,
            });
        }
        else
        {
            image.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
        }

        return image.ToByteArray(MagickFormat.Png);
    }

    /// <summary>
    /// 把任意图片字节解成 **BGRA8** 原始像素（每像素 4 字节，蓝-绿-红-透明）。
    ///
    /// 滤镜和调色都在这一层像素上做：Magick 的 API 各家版本行为不一致，
    /// 自己拿着数组逐像素算，结果在任何机器上都是一样的，也方便单独压测。
    ///
    /// 拿不到就返回 null，不抛异常 —— 调用方只需要判断一下。
    /// </summary>
    public static byte[]? LoadPixels(byte[] source, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var image = new MagickImage(source);

            // 带 alpha 的图要先铺白底：滤镜的数学假设的是不透明像素，
            // 透明区域 RGB 是 0，直接算会发黑
            if (image.HasAlpha) image.ColorAlpha(new MagickColor(255, 255, 255));

            // 可能有 EXIF 方向标记（手机竖拍的照片常见），
            // 不处理的话拿到的像素会是"躺着的"，和用户看到的不一致
            image.AutoOrient();

            /*
              下面三行的**顺序不能改**，这是个很隐蔽的坑：

              PNG 里但凡颜色少（截图、图表、纯色背景），解码出来就是
              depth=1 的**索引色**（ColorType=Palette）。这时候只设
              ColorType=TrueColorAlpha 是不够的 —— depth 仍然是 1，
              导出时每像素只给 1~4 位，字节数会比"宽×高×4"少好几倍，
              后面所有逐像素的代码都会读到一堆垃圾，而且**不报错**。

              必须先 Depth=8（把每个通道撑到 8 位），再设色彩空间。
              实测：少了 Depth 这行，640×360 的纯色图只吐 115200 字节
              （正确值 921600，正好差 8 倍）。
            */
            image.Depth = 8;
            image.ColorSpace = ColorSpace.sRGB;
            image.ColorType = ColorType.TrueColorAlpha;

            width = (int)image.Width;
            height = (int)image.Height;

            // 直接要 BGRA：界面上用的就是 BGRA，省掉一次内存里的通道对调
            return image.ToByteArray(MagickFormat.Bgra);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 BGRA8 像素重新压成 PNG 字节。是 <see cref="LoadPixels"/> 的逆操作。
    /// </summary>
    public static byte[] FromPixels(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return Array.Empty<byte>();

        try
        {
            using var image = new MagickImage(bgra,
                new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            return image.ToByteArray(MagickFormat.Png);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>查一下这段字节图多大。打不开返回 (0, 0)，不抛异常。</summary>
    public static (int Width, int Height) SizeOf(byte[] source)
    {
        try
        {
            using var image = new MagickImage(source);
            return ((int)image.Width, (int)image.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 另存到指定路径。格式由扩展名决定（.jpg 存 JPEG、.png 存 PNG……）。
    ///
    /// 之所以不统一存 PNG：用户选了 .jpg 却得到一张几 MB 的 PNG，会觉得莫名其妙。
    /// </summary>
    public static bool Save(byte[] source, string destPath)
    {
        try
        {
            using var image = new MagickImage(source);

            // JPEG 不支持透明，带 alpha 的图直接存会变成黑底，
            // 所以先铺一层白底再存 —— 这是看图软件的一致做法
            if (HasJpegExtension(destPath) && image.HasAlpha)
            {
                image.ColorAlpha(new MagickColor(255, 255, 255));
            }

            image.Quality = JpegQuality;
            image.Write(destPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>常见的几个另存格式，给文件保存对话框用。</summary>
    public static readonly (string Name, string Extension)[] SaveFormats =
    {
        ("PNG 图像", ".png"),
        ("JPEG 图像", ".jpg"),
        ("BMP 图像", ".bmp"),
        ("TIFF 图像", ".tiff"),
        ("WebP 图像", ".webp"),
    };

    private static bool HasJpegExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
