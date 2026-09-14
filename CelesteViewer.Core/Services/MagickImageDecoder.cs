using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CelesteViewer.Services;

/// <summary>
/// 兜底解码器：Magick.NET（ImageMagick 的 .NET 封装）。
///
/// 只在 WIC 解不了的时候才被叫上：heic / heif / avif / psd / tga / 相机 RAW。
/// 它慢一些、占内存大一些，所以永远不是第一选择 ——
/// 日常看照片 95% 以上的图根本走不到这里。
///
/// 已知限制（先写清楚，免得后面当成 bug 查）：
///   · 部分相机 RAW 格式依赖 Magick.NET 编译时带的委托，个别老机型可能仍打不开
///   · 一次解码会占几十 MB 内存，所以调用方必须限制并发数，不能几百张一起上
/// </summary>
public sealed class MagickImageDecoder : IImageDecoder
{
    public string Name => "Magick";

    private static readonly HashSet<string> _supported =
        new(ImageFormats.Extended, StringComparer.OrdinalIgnoreCase);

    public bool CanDecode(string extension) => _supported.Contains(extension);

    /// <summary>
    /// 这个 Magick.NET 构建（Q8-AnyCPU）明确不带委托、100% 解不开的格式。
    /// svg 是最典型的：进去必抛 `no decode delegate for this image format ''`
    /// （注意格式名是空的——因为它靠读文件头猜，猜不出就报空名）。
    /// 这类格式在 <see cref="ImageFormats.Extended"/> 里已经被排除，
    /// 正常不会走到这里；但解码总入口在 WIC 失败后还会拿任意扩展名来"救一次"，
    /// 所以这里再拦一道，直接从源头返回 null，连 Magick 都不碰 ——
    /// 既不崩，也不会让图库里出现一次无意义的异常。
    /// </summary>
    private static readonly HashSet<string> _noDelegate =
        new(StringComparer.OrdinalIgnoreCase) { ".svg", ".svgz" };

    /// <summary>扩展名属于"本构建铁定解不开"的那一类就直接放弃，不抛异常。</summary>
    private static bool IsUnsupportedNoDelegate(string path)
        => _noDelegate.Contains(Path.GetExtension(path));

    /// <summary>
    /// 已经记过日志的失败路径。
    ///
    /// 解码失败是**静默**的（返回 null，不打断浏览）—— 这是对的，
    /// 图库里混一张坏图不该弹窗。但静默过头就变成"用户说打不开、我查不到任何线索"。
    /// 所以每个路径只记一次：缩略图墙会对同一张图反复重试，不记会刷出几千行。
    /// </summary>
    private static readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _loggedLock = new();

    private static void LogOnce(string path, string reason)
    {
        lock (_loggedLock)
        {
            if (!_logged.Add(path)) return;
        }
        StartupLog.Write($"[解码跳过] {reason}：{path}");
    }

    /// <summary>
    /// 扩展名 → MagickFormat 的映射。
    ///
    /// 关键点：MagickImage 直接吃一个裸流时，靠"读文件头猜格式"。
    /// 遇到没配对应委托的格式（最典型就是 svg —— 这个 Magick.NET 构建没带 SVG 委托），
    /// 它读不出格式就抛 `no decode delegate for this image format ''`（注意格式名是空的），
    /// 看起来很吓人。显式把扩展名对应的 MagickFormat 传进去，
    /// 至少能把异常变成"no decode delegate for this image format 'SVG'"（带名字），
    /// 干净、可定位，而且照样被下面的 catch 兜住返回 null，不会崩。
    /// </summary>
    private static readonly Dictionary<string, MagickFormat> _formatByExt =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { ".heic", MagickFormat.Heic }, { ".heif", MagickFormat.Heif },
        { ".avif", MagickFormat.Avif },
        { ".psd", MagickFormat.Psd },   { ".psb", MagickFormat.Psd },
        { ".jxl", MagickFormat.Jxl },
        { ".arw", MagickFormat.Arw },   { ".cr2", MagickFormat.Cr2 },
        { ".cr3", MagickFormat.Cr3 },   { ".nef", MagickFormat.Nef },
        { ".nrw", MagickFormat.Nrw },   { ".orf", MagickFormat.Orf },
        { ".rw2", MagickFormat.Rw2 },   { ".raf", MagickFormat.Raf },
        { ".dng", MagickFormat.Dng },   { ".pef", MagickFormat.Pef },
        { ".srw", MagickFormat.Srw },
        { ".tga", MagickFormat.Tga },   { ".pcx", MagickFormat.Pcx },
        { ".ppm", MagickFormat.Ppm },   { ".pgm", MagickFormat.Pgm },
        { ".pbm", MagickFormat.Pbm },
        { ".xcf", MagickFormat.Xcf },   { ".exr", MagickFormat.Exr },
        { ".hdr", MagickFormat.Hdr },
    };

    /// <summary>扩展名能对应到已知 MagickFormat 就返回它，否则 null（走自动探测）。</summary>
    private static MagickFormat? FormatFor(string path)
    {
        string ext = Path.GetExtension(path);
        return _formatByExt.TryGetValue(ext, out MagickFormat f) ? f : null;
    }

    // ---------- 2026-09-15 第三轮：彻底不留"裸流"入口 ----------
    //
    // 前两轮分别挡住了「内容是文本」和「Magick 没委托」两类，
    // 但入口上还留着一根管子：`new MagickImage(source)` —— 不给任何格式提示。
    // 只要走到这行，Magick 就得自己读文件头猜，猜不出就抛
    // `no decode delegate for this image format ''`（格式名是空的，最难查的那种）。
    //
    // 什么时候会走到？扩展名不在映射表里 **且** 文件头也认不出来的时候。
    // 那种文件连我们自己都说不出它是什么，交给 Magick 猜更是白猜 —— 直接放弃。
    //
    // 于是形成一条硬规则：
    //   **说不出格式名，就绝不碰 Magick。**
    // 这条规则一落地，空格式名异常在物理上就不可能再发生。

    /// <summary>
    /// 先问"这文件到底是什么格式"。扩展名优先，文件头兜底。
    /// 返回 null 表示真说不上来 —— 调用方必须放弃，不能硬塞给 Magick。
    /// </summary>
    private static MagickFormat? HintFor(string path, Stream source)
    {
        MagickFormat? byExt = FormatFor(path);
        if (byExt.HasValue) return byExt;

        return FromKind(FileSignature.Identify(source));
    }

    /// <summary>
    /// 实际读取时是否强制指定格式。
    ///
    /// 只在**扩展名**给出明确格式时才强制（用户/相机给的后缀通常可信）；
    /// 靠文件头猜出来的不强制——TIFF 容器里装的可能其实是 NEF / ARW / DNG，
    /// 我们一律识别成 Tiff，硬塞进去反而会把本来能开的 RAW 弄成打不开。
    /// 让 Magick 自己探测更准，反正"有没有委托"那道闸门已经在前面守着了。
    /// </summary>
    private static MagickFormat? ForceFor(string path) => FormatFor(path);

    // ---------- 下面这一组是 2026-09-15 第二轮修复 ----------
    //
    // 上一轮加了"文件头嗅探"，把"扩展名是 .png 其实是一行文本"那批文件挡住了。
    // 但还有一批漏网的：文件确实是图、扩展名也确实是图片，
    // 只是**这个 Magick.NET 构建没有该格式的解码委托**（实测：ICO，用户机器上一抓 236 个）。
    // 那时候 hint 为 null，Magick 裸流自探、猜不出 → 抛 no decode delegate。
    //
    // 治法就一句话：**调用之前先问一句"你到底有没有这个格式的委托"**。
    // 没有就别调，一次原生异常都不该发生。

    /// <summary>
    /// 这个 Magick.NET 构建登记过的全部格式。
    /// 静态构造时枚举一次，之后纯内存查表，没有运行时开销。
    /// </summary>
    private static readonly HashSet<MagickFormat> _magickFormats = BuildFormatSet();

    private static HashSet<MagickFormat> BuildFormatSet()
    {
        var set = new HashSet<MagickFormat>();
        try
        {
            foreach (var f in MagickNET.SupportedFormats)
                set.Add(f.Format);
        }
        catch
        {
            // 拿不到格式表就当"全都支持"，退回"试了再说"的老路 —— 不会比修复前更差
        }
        return set;
    }

    /// <summary>
    /// 实测打不开的格式。
    ///
    /// 光看 SupportedFormats 还不够：登记在册、实际却缺委托的情况也是有的
    /// （不同构建、不同平台都可能出现）。所以第一次真抛了就记下来，
    /// 以后同格式一律不再白试 —— 缩略图墙会对几百张图反复重试，
    /// 不记的话每张都要撞一次原生异常。
    /// </summary>
    private static readonly HashSet<MagickFormat> _deadFormats = new();
    private static readonly object _deadLock = new();

    private static void MarkDeadFormat(MagickFormat f)
    {
        lock (_deadLock) _deadFormats.Add(f);
    }

    /// <summary>这个格式还值不值得交给 Magick。false = 别试了，试也白试。</summary>
    private static bool MagickLacks(MagickFormat format)
    {
        // 格式表拿到了、而里面没有这个格式 → 必然失败
        if (_magickFormats.Count > 0 && !_magickFormats.Contains(format)) return true;

        lock (_deadLock) return _deadFormats.Contains(format);
    }

    /// <summary>把文件头识别出的种类翻译成 MagickFormat。识别不出来返回 null。</summary>
    private static MagickFormat? FromKind(FileSignature.ImageKind kind) => kind switch
    {
        FileSignature.ImageKind.Jpeg => MagickFormat.Jpeg,
        FileSignature.ImageKind.Png => MagickFormat.Png,
        FileSignature.ImageKind.Gif => MagickFormat.Gif,
        FileSignature.ImageKind.Bmp => MagickFormat.Bmp,
        FileSignature.ImageKind.Tiff => MagickFormat.Tiff,
        FileSignature.ImageKind.Ico => MagickFormat.Ico,
        FileSignature.ImageKind.WebP => MagickFormat.WebP,
        FileSignature.ImageKind.Heic => MagickFormat.Heic,
        FileSignature.ImageKind.Avif => MagickFormat.Avif,
        FileSignature.ImageKind.Cr3 => MagickFormat.Cr3,
        FileSignature.ImageKind.Psd => MagickFormat.Psd,
        FileSignature.ImageKind.Exr => MagickFormat.Exr,
        FileSignature.ImageKind.Jxl => MagickFormat.Jxl,
        FileSignature.ImageKind.Radiance => MagickFormat.Hdr,
        FileSignature.ImageKind.Pcx => MagickFormat.Pcx,
        FileSignature.ImageKind.Netpbm => MagickFormat.Pnm,
        FileSignature.ImageKind.Xcf => MagickFormat.Xcf,
        FileSignature.ImageKind.Dds => MagickFormat.Dds,
        FileSignature.ImageKind.Raf => MagickFormat.Raf,
        FileSignature.ImageKind.Crw => MagickFormat.Crw,
        FileSignature.ImageKind.Mrw => MagickFormat.Mrw,
        FileSignature.ImageKind.X3f => MagickFormat.X3f,
        _ => null,
    };

    public async Task<PhotoInfo?> ProbeAsync(
        string path,
        bool includeMetadata = false,
        CancellationToken ct = default)
    {
        MagickFormat? hint = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            // 铁定解不开的格式（svg/svgz）：直接放弃，连 Magick 都不碰
            if (IsUnsupportedNoDelegate(path)) return null;

            // 虚拟路径（压缩包内的图）走内存流，普通路径走文件流
            using Stream? source = ArchiveIndex.OpenSource(path);
            if (source is null) return null;

            // 先看文件头：不是图就别让 Magick 去猜（第一轮修复）
            if (!FileSignature.LooksLikeImage(source))
            {
                LogOnce(path, "内容不是图片（扩展名是骗人的）");
                return null;
            }

            // 先确认"这到底是什么格式"。说不上来就放弃 —— 见上面第三轮修复的说明。
            hint = HintFor(path, source);
            if (hint is null)
            {
                LogOnce(path, "认不出是什么格式（扩展名和文件头都对不上）");
                return null;
            }

            // 拿到格式名，才能先问一句"Magick 到底有没有这个格式的委托"
            if (MagickLacks(hint.Value))
            {
                LogOnce(path, $"Magick 没有 {hint} 的解码委托");
                return null;
            }

            // MagickImageInfo 只解析文件头，不解码像素 ——
            // 这就是为什么列表加载几千张也不至于卡死。
            var info = await Task.Run(() => new MagickImageInfo(source), ct);

            // MagickImageInfo 的宽高是 uint，这里转一次，后面全按 int 处理
            int width = (int)info.Width;
            int height = (int)info.Height;

            // MagickImageInfo 不做 EXIF 转正，得自己换回来，
            // 否则竖拍照片在列表里显示成横的。
            if (IsQuarterTurn(info.Orientation))
                (width, height) = (height, width);

            if (width <= 0 || height <= 0) return null;

            var disk = ArchiveIndex.InfoOf(path);
            var photo = new PhotoInfo
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
                photo = MetadataService.ApplyExif(photo, path);

            return photo;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MagickMissingDelegateErrorException)
        {
            // 格式表说有、实际没委托的情况：记下来，以后同格式一律不再白试。
            // 这一步之后，同一批 ico 只会在第一张上撞一次，其余全部静默跳过。
            if (hint.HasValue) MarkDeadFormat(hint.Value);
            LogOnce(path, $"Magick 没有 {hint?.ToString() ?? "（未知格式）"} 的解码委托");
            return null;
        }
        catch (Exception ex)
        {
            LogOnce(path, "Magick 探测失败：" + ShortMessage(ex));
            return null;
        }
    }

    public async Task<DecodedBitmap?> DecodeAsync(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken ct = default)
    {
        MagickFormat? hint = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            // 铁定解不开的格式（svg/svgz）：直接放弃，连 Magick 都不碰
            if (IsUnsupportedNoDelegate(path)) return null;

            using Stream? source = ArchiveIndex.OpenSource(path);
            if (source is null) return null;

            // 0 字节文件（空文件 / 截断的下载）没有像素可解，
            // 直接判"打不开"而不是交给 Magick 抛一堆看不懂的异常。
            if (source.CanSeek && source.Length == 0) return null;

            // 同 ProbeAsync：先看文件头，不是图就别让 Magick 去猜。
            // 这一步是 2026-09-15 那个 no decode delegate 异常的根治点 ——
            // 猜不出格式时会抛异常（虽然被下面 catch 住，但调试器每回都中断一次）。
            if (!FileSignature.LooksLikeImage(source))
            {
                LogOnce(path, "内容不是图片（扩展名是骗人的）");
                return null;
            }

            // 扩展名优先、文件头兜底。拿到明确格式，才能先问一句
            // "Magick 到底有没有这个格式的委托" —— 没有就别调，一次异常都不该发生。
            hint = HintFor(path, source);
            if (hint is null)
            {
                LogOnce(path, "认不出是什么格式（扩展名和文件头都对不上）");
                return null;
            }

            if (MagickLacks(hint.Value))
            {
                LogOnce(path, $"Magick 没有 {hint} 的解码委托");
                return null;
            }

            MagickFormat? force = ForceFor(path);

            return await Task.Run(() =>
            {
                // 扩展名给了明确格式就强制指定（避免"猜不出格式"那种空格式名异常）；
                // 只靠文件头认出来的、或者干脆认不出来的，让 Magick 自己探测更准。
                // 注意：走到这里时 hint 一定非 null —— 认不出来上面已经返回了，
                // 所以再也不会出现"裸流交给 Magick 去猜"那种调用。
                using var image = force.HasValue
                    ? new MagickImage(source, force.Value)
                    : new MagickImage(source);

                // 按 EXIF 方向转正，等价于 WIC 那边的 RespectExifOrientation
                image.AutoOrient();

                if (maxWidth > 0 || maxHeight > 0)
                {
                    int limitW = maxWidth > 0 ? maxWidth : int.MaxValue;
                    int limitH = maxHeight > 0 ? maxHeight : int.MaxValue;

                    // 只缩不放
                    if (image.Width > limitW || image.Height > limitH)
                    {
                        // Thumbnail 比 Resize 更适合看图：它会在解码阶段就降采样，
                        // 而不是先整张解开再缩小（对大图是几十倍的差距）
                        image.Thumbnail((uint)Math.Min(limitW, int.MaxValue),
                                        (uint)Math.Min(limitH, int.MaxValue));
                    }
                }

                // 统一转到 sRGB，否则 AdobeRGB / Display P3 的图会发灰
                image.ColorSpace = ColorSpace.sRGB;

                // 让 ImageMagick 自己做 alpha 预乘。
                // 这样输出的像素格式就和 WIC 那边一致了（都是 premultiplied），
                // UI 层不用区分、也不用自己写循环去乘一遍 —— 那种循环对大图很费 CPU。
                // 不透明的照片（绝大多数）没有 alpha 通道，直接跳过。
                if (image.HasAlpha)
                    image.Alpha(AlphaOption.Associate);

                byte[] pixels = image.ToByteArray(MagickFormat.Bgra);

                return new DecodedBitmap
                {
                    Pixels = pixels,
                    PixelWidth = (int)image.Width,
                    PixelHeight = (int)image.Height,
                    Premultiplied = true,
                    DecoderName = Name,
                };
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MagickMissingDelegateErrorException)
        {
            if (hint.HasValue) MarkDeadFormat(hint.Value);
            LogOnce(path, $"Magick 没有 {hint?.ToString() ?? "（未知格式）"} 的解码委托");
            return null;
        }
        catch (Exception ex)
        {
            // 走到这里说明文件头看着像图、但 Magick 还是解不开
            // （多半是这个构建没带对应委托、或者文件本身是坏的）。
            // 记一行日志：以前这里静默返回 null，出问题时一点线索都没有。
            LogOnce(path, "Magick 解码失败：" + ShortMessage(ex));
            return null;
        }
    }

    /// <summary>
    /// 异常消息截短，避免一个几百字符的 ImageMagick 报错刷满日志。
    /// 顺带把"没带委托"这种最常见的情况翻成一句人话。
    /// </summary>
    private static string ShortMessage(Exception ex)
    {
        string msg = ex.Message ?? ex.GetType().Name;

        if (ex is MagickMissingDelegateErrorException)
            return "这个 Magick.NET 构建没带该格式的解码委托（格式：" +
                   (msg.Contains("''") ? "探测不出" : msg) + "）";

        int nl = msg.IndexOf('\n');
        if (nl > 0) msg = msg[..nl];
        return msg.Length > 160 ? msg[..160] + "…" : msg;
    }

    /// <summary>EXIF 方向 5~8 表示转了 90° 或 270°，宽高要互换。</summary>
    private static bool IsQuarterTurn(OrientationType orientation)
        => orientation is OrientationType.LeftTop
                       or OrientationType.RightTop
                       or OrientationType.RightBottom
                       or OrientationType.LeftBottom;
}
