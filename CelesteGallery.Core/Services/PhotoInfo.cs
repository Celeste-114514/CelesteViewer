using System;

namespace CelesteGallery.Services;

/// <summary>
/// 一张图片的全部"可见信息"。
///
/// 这里只放**能拿来给人看**的东西：路径、体积、像素尺寸、拍摄参数。
/// 不放解码器内部状态、不放像素数据 —— 那些是 <see cref="DecodedBitmap"/> 的事。
///
/// 设计上刻意做成不可变（全是 init）：同一个文件的信息在缩略图列表、
/// 大图查看、参数面板之间传来传去，可变的话很容易出现"这边改了那边没改"。
/// </summary>
public sealed class PhotoInfo
{
    public string Path { get; init; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>文件体积（字节）。参数面板里显示成 KB / MB 由 UI 层决定。</summary>
    public long FileSize { get; init; }

    /// <summary>已经按 EXIF 方向转正后的宽（也就是说：这就是人眼看到的宽）。</summary>
    public int PixelWidth { get; init; }

    /// <summary>已经按 EXIF 方向转正后的高。</summary>
    public int PixelHeight { get; init; }

    public double Megapixels => PixelWidth * PixelHeight / 1_000_000.0;

    // ===== 拍摄参数（EXIF）。读不到就是 null，UI 直接不显示这一行 =====

    public DateTimeOffset? DateTaken { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensModel { get; init; }
    public string? FNumber { get; init; }
    public string? ExposureTime { get; init; }
    public string? IsoSpeed { get; init; }
    public string? FocalLength { get; init; }
    public string? ColorSpace { get; init; }

    // ===== 来源 =====

    /// <summary>
    /// 这张图最终是哪个解码器解出来的："WIC" 或 "Magick"。
    /// 显示在状态栏上，方便一眼看出有没有落到兜底路径（落到兜底意味着慢一些）。
    /// </summary>
    public string DecoderName { get; init; } = string.Empty;

    /// <summary>文件最后修改时间，缩略图缓存拿它当失效依据。</summary>
    public DateTimeOffset LastModified { get; init; }
}
