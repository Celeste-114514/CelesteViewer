using System;
using System.Linq;
using System.Threading.Tasks;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace CelesteGallery.Services;

/// <summary>
/// 读图片的拍摄参数（EXIF）。
///
/// 用 MetadataExtractor 而不是自己去解析 EXIF 二进制：
/// EXIF 的坑多到不值得自己趟 —— 大端小端、IFD 偏移、厂商私有区（MakerNote）里
/// 的镜头型号每个品牌写法都不一样。这个库已经把这些都处理好了。
///
/// 读不到的字段一律留 null，UI 那边直接不显示那一行，
/// 不要显示"未知"占位 —— 满屏的"未知"比空白更干扰。
/// </summary>
public static class MetadataService
{
    /// <summary>
    /// 把 EXIF 信息补进 <paramref name="info"/>，返回一个新对象（PhotoInfo 是不可变的）。
    /// 读失败（没有 EXIF、文件损坏、格式不支持）就原样返回，绝不抛异常。
    /// </summary>
    public static PhotoInfo ApplyExif(PhotoInfo info, string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            if (ifd0 is null && sub is null)
                return info;

            return new PhotoInfo
            {
                Path = info.Path,
                FileSize = info.FileSize,
                PixelWidth = info.PixelWidth,
                PixelHeight = info.PixelHeight,
                LastModified = info.LastModified,
                DecoderName = info.DecoderName,

                CameraMake = TryGet(ifd0, ExifDirectoryBase.TagMake),
                CameraModel = TryGet(ifd0, ExifDirectoryBase.TagModel),
                LensModel = TryGet(sub, ExifSubIfdDirectory.TagLensModel),
                FNumber = TryGet(sub, ExifDirectoryBase.TagFNumber),
                ExposureTime = TryGet(sub, ExifDirectoryBase.TagExposureTime),
                IsoSpeed = TryGet(sub, ExifDirectoryBase.TagIsoEquivalent),
                FocalLength = TryGet(sub, ExifDirectoryBase.TagFocalLength),
                ColorSpace = TryGet(sub, ExifDirectoryBase.TagColorSpace),
                DateTaken = TryGetDate(sub),
            };
        }
        catch
        {
            return info;
        }
    }

    /// <summary>
    /// 异步版本。EXIF 读取要碰磁盘，不要在 UI 线程上直接调同步版，
    /// 否则鼠标悬停一张图就卡一下。
    /// </summary>
    public static Task<PhotoInfo> ApplyExifAsync(PhotoInfo info, string path)
        => Task.Run(() => ApplyExif(info, path));

    private static string? TryGet(Directory? dir, int tag)
    {
        if (dir is null) return null;
        try
        {
            string? text = dir.GetDescription(tag);
            if (string.IsNullOrWhiteSpace(text)) return null;

            // MetadataExtractor 偶尔会返回类似 "Unknown (0x1234)" 的内容，
            // 这种等于没读到，别往界面上放。
            if (text.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                return null;

            return text;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetDate(ExifSubIfdDirectory? sub)
    {
        if (sub is null) return null;
        try
        {
            // 优先"拍摄时间"，没有再退回"数字化时间"
            if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var shot))
                return new DateTimeOffset(shot, TimeSpan.Zero);

            if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                return new DateTimeOffset(digitized, TimeSpan.Zero);

            return null;
        }
        catch
        {
            return null;
        }
    }
}
