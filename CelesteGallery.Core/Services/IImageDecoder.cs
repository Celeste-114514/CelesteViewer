using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteGallery.Services;

/// <summary>
/// 解码结果：一段裸像素 + 尺寸。
///
/// 刻意不返回 WinUI 的 SoftwareBitmap / ImageSource ——
/// 那些类型绑着 UI 线程，一旦混进来，这套解码代码就没法脱离界面单独跑测试了。
/// 现在这样，控制台程序可以直接 new 一个解码器开跑。
/// </summary>
public sealed class DecodedBitmap
{
    /// <summary>BGRA 排布，每行 4 字节对齐（stride = 宽 × 4）。</summary>
    public required byte[] Pixels { get; init; }

    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }

    /// <summary>
    /// alpha 通道是不是"预乘"的。
    ///
    /// 两个解码器的行为不一样：
    ///   WIC     → Premultiplied（每个颜色分量已经乘过 alpha）
    ///   Magick  → Straight（颜色分量是原值，没乘 alpha）
    /// 对不透明的照片来说两者完全一样，但对带半透明的 PNG / PSD，
    /// 弄反了边缘就会出现一圈黑边或者白边。所以必须把这个差别带出来，
    /// 让 UI 层建 SoftwareBitmap 时用对应的 BitmapAlphaMode。
    /// </summary>
    public bool Premultiplied { get; init; }

    public required string DecoderName { get; init; }
}

/// <summary>
/// 图片解码器的统一接口。
///
/// 目前两个实现：
///   - <see cref="WicImageDecoder"/>  Windows 自带，零体积，走主路径
///   - <see cref="MagickImageDecoder"/>  Magick.NET，兜底，慢一些但格式全
///
/// 上层（缩略图服务、大图查看）只认这个接口，不关心底下是谁解的。
/// 这样哪天要换成 SkiaSharp / libvips，只需要再写一个实现，别的代码一行不动。
/// </summary>
public interface IImageDecoder
{
    /// <summary>解码器名字，用来显示和排查（"WIC" / "Magick"）。</summary>
    string Name { get; }

    /// <summary>
    /// 这个解码器认不认这个扩展名。
    /// 注意这只是"快速判断"，不代表文件一定能打开（文件可能是坏的、或者扩展名是骗人的）。
    /// </summary>
    bool CanDecode(string extension);

    /// <summary>
    /// 只读取尺寸，不解码像素，默认也不读 EXIF。
    ///
    /// 关于 <paramref name="includeMetadata"/>：
    /// 加载一个几千张图的目录时，逐张读 EXIF 会把时间拖长好几倍，
    /// 而列表上其实只显示文件名和尺寸。所以默认不读，
    /// 只有真正要给人看参数了（选中某张、打开参数面板）才传 true。
    ///
    /// 打不开就返回 null，不抛异常 —— 列表里混进一张坏图不应该让整个列表挂掉。
    /// </summary>
    Task<PhotoInfo?> ProbeAsync(
        string path,
        bool includeMetadata = false,
        CancellationToken ct = default);

    /// <summary>
    /// 解码图片，缩放到不超过 maxWidth × maxHeight（保持宽高比，不会放大）。
    /// 传 0 或负数表示不限，按原尺寸解。
    /// </summary>
    Task<DecodedBitmap?> DecodeAsync(string path, int maxWidth, int maxHeight, CancellationToken ct = default);
}

/// <summary>
/// 支持哪些扩展名。
///
/// 分成两档是有意为之：
///   Common   WIC 能直接解的日常格式
///   Extended 只有 Magick.NET 能解的格式
/// 文件对话框、拖放筛选、目录扫描都看这里，不各自写一份字符串数组。
/// </summary>
public static class ImageFormats
{
    /// <summary>系统 WIC 原生支持的格式（主路径，最快）。</summary>
    public static readonly IReadOnlyCollection<string> Common = new[]
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib",
        ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp", ".hdp",
    };

    /// <summary>需要 Magick.NET 兜底的格式。</summary>
    public static readonly IReadOnlyCollection<string> Extended = new[]
    {
        ".heic", ".heif", ".avif", ".psd", ".psb", ".jxl",
        ".arw", ".cr2", ".cr3", ".nef", ".nrw", ".orf", ".rw2", ".raf", ".dng", ".pef", ".srw",
        ".tga", ".pcx", ".ppm", ".pgm", ".pbm", ".xcf", ".exr", ".hdr",
        // 注意：svg 故意不放进来。Magick.NET-Q8-AnyCPU 这个构建没带 SVG 委托，
        // svg 100% 解不开（抛 no decode delegate for this image format ''），
        // 放进来只会让图库里每张 svg 都显示成"打不开"还伴随一次异常。
        // 将来要支持 svg，得换带 SVG 委托的 Magick 构建，再把 ".svg" 加回来。
    };

    /// <summary>
    /// 不是图片、但可以当"一叠图"直接翻的压缩包。
    /// 刻意**不放进** <see cref="IsImage"/> 的判定里 —— 目录扫描时 zip 不该被当成一张图，
    /// 它只在"打开文件"这类主动入口里出现。
    /// </summary>
    public static readonly IReadOnlyCollection<string> Archives = new[] { ".zip", ".cbz" };

    private static readonly HashSet<string> _all =
        new(Common.Concat(Extended), StringComparer.OrdinalIgnoreCase);

    /// <summary>这个扩展名是不是图片（不管哪个解码器能解）。</summary>
    public static bool IsImage(string extension) => _all.Contains(extension);

    /// <summary>从路径取扩展名（带点，小写形式由调用方保证）。</summary>
    public static string ExtensionOf(string path) => System.IO.Path.GetExtension(path);
}
