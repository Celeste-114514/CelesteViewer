using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteViewer.Services;

/// <summary>
/// 解码总入口：先试 WIC，WIC 搞不定再退到 Magick.NET。
///
/// 顺序不是随便排的：
///   · 扩展名明确属于"只有 Magick 能解"那一档（heic / psd / RAW），直接走 Magick，
///     别浪费一次注定失败的 WIC 尝试；
///   · 其它情况一律先走 WIC，失败了（文件损坏、扩展名骗人）再用 Magick 试一次。
///
/// 自带计数器，方便实测"到底有多少比例的图落到了慢路径"。
/// </summary>
public sealed class ImageDecodePipeline : IImageDecoder
{
    private readonly WicImageDecoder _wic = new();
    private readonly MagickImageDecoder _magick = new();

    public string Name => "Pipeline";

    public bool CanDecode(string extension)
        => _wic.CanDecode(extension) || _magick.CanDecode(extension);

    private int _wicHits;
    private int _magickFallbacks;
    private int _failures;

    /// <summary>WIC 直接搞定的次数。</summary>
    public int WicHits => Volatile.Read(ref _wicHits);

    /// <summary>靠 Magick.NET 解出来的次数（含"只有它能解"和"WIC 失败后救回来"两种）。</summary>
    public int MagickFallbacks => Volatile.Read(ref _magickFallbacks);

    /// <summary>两个都解不了的次数（坏图 / 不支持的格式）。</summary>
    public int Failures => Volatile.Read(ref _failures);

    public async Task<PhotoInfo?> ProbeAsync(
        string path,
        bool includeMetadata = false,
        CancellationToken ct = default)
    {
        string ext = Path.GetExtension(path);
        bool magickOnly = _magick.CanDecode(ext) && !_wic.CanDecode(ext);

        if (magickOnly)
        {
            var viaMagick = await _magick.ProbeAsync(path, includeMetadata, ct);
            if (viaMagick is not null) { CountMagick(); return viaMagick; }

            var fallback = await _wic.ProbeAsync(path, includeMetadata, ct);
            CountResult(fallback, wic: true);
            return fallback;
        }

        var viaWic = await _wic.ProbeAsync(path, includeMetadata, ct);
        if (viaWic is not null) { CountWic(); return viaWic; }

        var rescue = await _magick.ProbeAsync(path, includeMetadata, ct);
        CountResult(rescue, wic: false);
        return rescue;
    }

    public async Task<DecodedBitmap?> DecodeAsync(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken ct = default)
    {
        string ext = Path.GetExtension(path);
        bool magickOnly = _magick.CanDecode(ext) && !_wic.CanDecode(ext);

        if (magickOnly)
        {
            var viaMagick = await _magick.DecodeAsync(path, maxWidth, maxHeight, ct);
            if (viaMagick is not null) { CountMagick(); return viaMagick; }

            var fallback = await _wic.DecodeAsync(path, maxWidth, maxHeight, ct);
            CountResult(fallback, wic: true);
            return fallback;
        }

        var viaWic = await _wic.DecodeAsync(path, maxWidth, maxHeight, ct);
        if (viaWic is not null) { CountWic(); return viaWic; }

        var rescue = await _magick.DecodeAsync(path, maxWidth, maxHeight, ct);
        CountResult(rescue, wic: false);
        return rescue;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _wicHits, 0);
        Interlocked.Exchange(ref _magickFallbacks, 0);
        Interlocked.Exchange(ref _failures, 0);
    }

    private void CountWic() => Interlocked.Increment(ref _wicHits);
    private void CountMagick() => Interlocked.Increment(ref _magickFallbacks);

    private void CountResult(object? result, bool wic)
    {
        if (result is null) Interlocked.Increment(ref _failures);
        else if (wic) Interlocked.Increment(ref _wicHits);
        else Interlocked.Increment(ref _magickFallbacks);
    }
}
