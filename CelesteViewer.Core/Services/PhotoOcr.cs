using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CelesteViewer.Services;

/// <summary>
/// 一次识别的结果。成功和失败都走这个类型，调用方不用 try/catch。
/// </summary>
/// <param name="Ok">这次识别有没有跑成（不等于"认出了字"）。</param>
/// <param name="Text">识别出的全文，一行一段。认不出字时是空串。</param>
/// <param name="LineCount">认出几行。</param>
/// <param name="LanguageName">实际用了哪个语言包，显示给用户看。</param>
/// <param name="Message">失败原因，或者"没找到文字"这类说明。成功且有字时为 null。</param>
public sealed record OcrOutcome(
    bool Ok,
    string Text,
    int LineCount,
    string LanguageName,
    string? Message)
{
    public static OcrOutcome Fail(string message) => new(false, "", 0, "", message);
}

/// <summary>
/// 从图片里读文字（OCR）。
///
/// 用系统自带的 Windows.Media.Ocr，不自己塞模型：
///   · 零体积 —— 语言包归系统管（设置 → 时间和语言 → 语言和区域 →
///     某个语言的「可选语言功能」→ 光学字符识别），程序一个字节都不用带；
///   · 离线跑，不联网；
///   · WinRT 投影在项目的目标框架里本来就有，不用引任何包。
///
/// ⚠️ **必须在同一个 STA 线程（也就是 UI 线程）上调用，不要套 Task.Run。**
/// OcrEngine / SoftwareBitmap 都是非敏捷（non-agile）对象，只能在自己被创建的
/// 那个线程上用；丢到线程池上跨线程用会报 RPC_E_WRONG_THREAD (0x8001010E)，
/// 而且时有时无，极难查。
/// 好在 WinRT 的异步调用本来就是"别的线程干活、回调回调用线程"，
/// 真正耗时的部分走 await 让出，界面不会卡。
/// </summary>
public static class PhotoOcr
{
    /// <summary>送去识别之前，图片长边最多这么大。</summary>
    public const int MaxEdge = 2000;

    /// <summary>系统里有没有装 OCR 语言包。一个都没装时整个功能不可用。</summary>
    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>装了的语言包名字。拼"没装语言包"的提示时用得上。</summary>
    public static IReadOnlyList<string> AvailableLanguageNames
        => OcrEngine.AvailableRecognizerLanguages.Select(l => l.DisplayName).ToList();

    /// <summary>
    /// 认一张图（传进来的是**编码过的图片字节**，不是裸像素 ——
    /// 解码和缩放都在这里做，调用方只要把手上那份字节丢过来）。
    /// </summary>
    public static async Task<OcrOutcome> RecognizeAsync(
        byte[] encodedImage,
        CancellationToken ct = default)
    {
        if (encodedImage.Length == 0) return OcrOutcome.Fail("图片数据是空的。");

        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return OcrOutcome.Fail(IsAvailable
                ? "系统里的 OCR 语言包和当前语言对不上，换个语言再试。"
                : "系统里没有装任何 OCR 语言包。到「设置 → 时间和语言 → 语言和区域」，"
                  + "给中文或英文加上「可选语言功能 → 光学字符识别」，装好再试。");
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(encodedImage.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;
            if (w == 0 || h == 0) return OcrOutcome.Fail("这张图读不出尺寸。");

            // 识别耗时基本跟像素数成正比：一张 6000px 的图直接送进去要好几秒。
            // 缩到长边 2000 以内，对"截图 / 文档 / 招牌"这类场景几乎没有损失
            // （字本来就没那么小），顺带也避开了 OcrEngine.MaxImageDimension 的上限。
            double scale = Math.Min(1.0, MaxEdge / (double)Math.Max(w, h));

            using var bitmap = scale < 0.999
                ? await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, Math.Round(w * scale)),
                        ScaledHeight = (uint)Math.Max(1, Math.Round(h * scale)),
                        // 缩小一定要插值。直接丢像素的话小字会先被丢没，
                        // 而那正是最需要识别的部分
                        InterpolationMode = BitmapInterpolationMode.Fant,
                    },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                : await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

            ct.ThrowIfCancellationRequested();

            var result = await engine.RecognizeAsync(bitmap);
            string language = engine.RecognizerLanguage.DisplayName;

            var lines = result.Lines
                .Select(line => line.Text.Trim())
                .Where(text => text.Length > 0)
                .ToList();

            if (lines.Count == 0)
                return new OcrOutcome(true, "", 0, language, "这张图里没找到文字。");

            // 一行一段。OcrResult.Text 也能直接拿，但它把行拼成整段时
            // 空白的处理比较随意，自己拼出来的更好读、也更好复制
            string text = string.Join(Environment.NewLine, lines);
            return new OcrOutcome(true, text, lines.Count, language, null);
        }
        catch (OperationCanceledException)
        {
            throw;      // 取消不算失败，交给调用方
        }
        catch (Exception ex)
        {
            // 坏数据、不支持的格式、系统组件抽风 —— 一律当"没认成"，
            // 别让一个识别失败把整个看图页掀了
            return OcrOutcome.Fail(ex.Message);
        }
    }
}
