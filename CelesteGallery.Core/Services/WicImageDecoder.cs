using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace CelesteGallery.Services;

/// <summary>
/// 主解码器：Windows 自带的 WIC（Windows Imaging Component）。
///
/// 为什么把它当主路径：
///   1. 零额外体积 —— 它本来就是系统的一部分，不用往程序里塞任何 dll
///   2. 快 —— 系统级实现，JPEG 解码直接用硬件友好的路径
///   3. 免费处理了两件麻烦事：
///        · EXIF 方向自动转正（手机竖拍的照片不会躺下来）
///        · 色彩管理（广色域照片不会发灰或过饱和）
///
/// 它解不了的格式（heic / psd / 相机 RAW）会抛异常兜不住，
/// 由 <see cref="ImageDecodePipeline"/> 转给 Magick.NET。
///
/// ⚠️ 这里**刻意不用** `StorageFile.GetFileFromPathAsync` + `file.OpenAsync`。
/// 原因写在下面 <see cref="DecodeAsync"/> 的注释里 —— 那两行会让程序在启动阶段直接崩掉，
/// 别看着"更官方"就改回去。
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    public string Name => "WIC";

    private static readonly HashSet<string> _supported =
        new(ImageFormats.Common, StringComparer.OrdinalIgnoreCase);

    public bool CanDecode(string extension) => _supported.Contains(extension);

    /* ================================================================
       ⚠️ 为什么拿文件不用 WinRT 的 StorageFile（踩过的坑，别改回去）

       原来的写法是：
           var file  = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
           using var s = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
           var dec   = await BitmapDecoder.CreateAsync(s).AsTask(ct);

       症状：**启动阶段随机崩溃**，实测 6 次里崩 5 次，而且崩得干干净净 ——
       没有异常、没有堆栈、日志里连一行都不留（进程被直接干掉，try-catch 也接不住）。

       定位过程：在每一步之间插日志，发现最后一行永远是"进入解码"，
       下一行"拿到 StorageFile"从未出现 —— 崩点就在 `GetFileFromPathAsync`，
       而且日志里那个 `Environment.CurrentManagedThreadId` 是 **1**，也就是 UI 线程。

       为什么只有"双击图片打开"这条路径会崩：
         · 缩略图墙的解码发生在窗口完全就绪之后（ElementPrepared 是滚到才触发），没事；
         · 双击图片时，单图页在**窗口刚 Activate 的那一瞬间**就开始解码，
           WinRT 的文件代理正好和初始布局/首帧渲染挤在一起，进程直接没了。

       换成普通的 File.OpenRead + AsRandomAccessStream 之后：
         · 完全不碰 WinRT 文件代理，少了那层跨进程编组，也就没有这个冲突；
         · 顺带更快 —— 之前实测"每张探测约 2ms"，怀疑的瓶颈就是它；
         · 本地盘 / 网络盘 / 云盘同步目录的表现反而更一致。

       一句话：**读图片只需要一个文件流，不需要一个"文件对象"。**
       ================================================================ */

    public async Task<PhotoInfo?> ProbeAsync(
        string path,
        bool includeMetadata = false,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // ArchiveIndex.OpenSource 对虚拟路径（压缩包内的图）返回内存流，
            // 对普通路径就是 File.OpenRead。这里刻意不直接用 StorageFile，理由见上面的长注释。
            using Stream? source = ArchiveIndex.OpenSource(path);
            if (source is null) return null;

            // 内容根本不是图片就别去麻烦 WIC 了 —— 一次失败的 COM 解码尝试
            // 比读 64 字节文件头贵得多。理由见 FileSignature 的注释。
            if (!FileSignature.LooksLikeImage(source)) return null;

            using var randomAccess = source.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccess).AsTask(ct);

            int width = (int)decoder.OrientedPixelWidth;
            int height = (int)decoder.OrientedPixelHeight;
            if (width <= 0 || height <= 0) return null;

            // 虚拟路径在磁盘上没有对应的文件，InfoOf 会退回压缩包本身的信息
            var disk = ArchiveIndex.InfoOf(path);
            var info = new PhotoInfo
            {
                Path = path,
                FileSize = disk?.Exists == true ? disk.Length : 0,
                LastModified = disk?.Exists == true ? disk.LastWriteTimeUtc : default,
                PixelWidth = width,
                PixelHeight = height,
                DecoderName = Name,
            };

            // EXIF 要另外打开原文件读，包内的图没有独立文件，跳过
            if (includeMetadata && !ArchiveIndex.IsVirtual(path))
                info = MetadataService.ApplyExif(info, path);

            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<DecodedBitmap?> DecodeAsync(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // 流必须在整个解码期间活着：GetPixelDataAsync 是惰性的，
            // 它之后还要回头读这个流，所以 using 的作用域要罩到方法结束。
            using Stream? source = ArchiveIndex.OpenSource(path);
            if (source is null) return null;

            // 同 ProbeAsync：先花 64 字节确认这玩意儿像不像图，不是就别进 COM。
            if (!FileSignature.LooksLikeImage(source)) return null;

            using var randomAccess = source.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccess).AsTask(ct);

            int srcW = (int)decoder.OrientedPixelWidth;
            int srcH = (int)decoder.OrientedPixelHeight;
            if (srcW <= 0 || srcH <= 0) return null;

            // 注意：这里用的是 OrientedPixelWidth，也就是**转正之后**的宽高。
            // 竖拍照片在文件里是横着存的，用 PixelWidth 会算错缩放比。
            var transform = new BitmapTransform();
            int outW = srcW, outH = srcH;

            if (maxWidth > 0 || maxHeight > 0)
            {
                int limitW = maxWidth > 0 ? maxWidth : int.MaxValue;
                int limitH = maxHeight > 0 ? maxHeight : int.MaxValue;
                double scale = Math.Min(limitW / (double)srcW, limitH / (double)srcH);

                // 只缩不放：一张 200px 的小图要它显示成 800px 只会更糊
                if (scale < 1.0)
                {
                    outW = Math.Max(1, (int)Math.Round(srcW * scale));
                    outH = Math.Max(1, (int)Math.Round(srcH * scale));
                    transform.ScaledWidth = (uint)outW;
                    transform.ScaledHeight = (uint)outH;
                }
            }

            // 关键两步：
            //   RespectExifOrientation  → 按 EXIF 自动转正，不用自己写旋转
            //   ColorManageToSRgb       → 内嵌 ICC / Display P3 的照片颜色才对
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(ct);

            byte[] pixels = pixelData.DetachPixelData();

            // 少数解码器只会按 1/2、1/4、1/8 降采样，实际给的尺寸可能比请求的略小。
            // 用真实字节数把高度校正回来，避免 UI 拿到对不上的尺寸画出花屏。
            int expected = outW * outH * 4;
            if (pixels.Length < expected && outW > 0)
            {
                int realRows = pixels.Length / (outW * 4);
                if (realRows > 0) outH = realRows;
            }

            return new DecodedBitmap
            {
                Pixels = pixels,
                PixelWidth = outW,
                PixelHeight = outH,
                DecoderName = Name,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
