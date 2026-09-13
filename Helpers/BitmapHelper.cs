using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CelesteViewer.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace CelesteViewer.Helpers;

/// <summary>
/// 把 Core 层解出来的裸像素（DecodedBitmap）变成 WinUI 能显示的 ImageSource。
///
/// 这一层存在的唯一理由：Core 层刻意不碰任何 UI 类型（那样才能单独跑压测），
/// 所以"像素 → 屏幕"这最后一棒必须由主项目来接。
/// </summary>
public static class BitmapHelper
{
    /// <summary>
    /// 转成可以给 Image.Source 赋值的 SoftwareBitmapSource。
    /// 失败（尺寸非法、内存不够）返回 null，调用方自己决定显示什么。
    /// </summary>
    public static async Task<SoftwareBitmapSource?> ToSourceAsync(DecodedBitmap bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
        if (bitmap.Pixels.Length < bitmap.PixelWidth * bitmap.PixelHeight * 4) return null;

        try
        {
            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                BitmapAlphaMode.Premultiplied);

            softwareBitmap.CopyFromBuffer(bitmap.Pixels.AsBuffer());

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);

            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解码结果 + 尺寸，给"编辑完要立刻显示"这种场景用。</summary>
    public sealed record DecodedSource(SoftwareBitmapSource Source, int Width, int Height);

    /// <summary>
    /// 直接从一段图片字节（比如编辑后的 PNG）解出可显示的图。
    ///
    /// 编辑结果存在内存里，没必要为了显示它先写个临时文件 ——
    /// 临时文件还得操心什么时候删，而且连续旋转十次会写出十份垃圾。
    /// </summary>
    public static async Task<DecodedSource?> DecodeBytesAsync(byte[] bytes)
    {
        if (bytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            using var randomAccess = stream.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(randomAccess);

            // 统一转成预乘 alpha 的 BGRA：SoftwareBitmapSource 只认这一种，
            // 让解码器去转比我们自己转快，也不会出错
            using var software = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(software);

            return new DecodedSource(source, (int)software.PixelWidth, (int)software.PixelHeight);
        }
        catch
        {
            return null;
        }
    }
}
