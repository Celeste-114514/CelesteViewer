using System;
using System.IO;

namespace CelesteViewer.Services;

/// <summary>
/// 文件头嗅探：看前几十个字节，判断"这东西到底像不像一张图"。
///
/// 为什么需要它（2026-09-15 用户实际踩到）：
/// 图库里混进了一批"扩展名是 .png / .exr、内容却是一行文本"的文件
/// （ffmpeg 测试仓库里的 md5 校验清单，扩展名跟着被校验的文件走）。
/// WIC 打不开 → 解码总入口会"救一次"把它交给 Magick.NET →
/// Magick 读文件头猜格式、猜不出就抛
///     `no decode delegate for this image format ''`（注意格式名是空的）
/// 外层虽然 catch 住了不会崩，但每有一张这样的图，
/// 调试器就中断一次、日志里多一条看不懂的异常，图库里还会留一张永远打不开的缩略图位。
///
/// 处理方式：交给解码器之前先看一眼文件头，不是图就直接放弃。
/// 这样异常根本不会发生，比"多 catch 一种异常"干净得多。
///
/// 判据刻意做成**宽进严出**：
///   1. 命中已知图片魔数        → 放行
///   2. 明显是文本（可打印字符） → 判掉（文本不可能是图）
///   3. 其它未知二进制           → 放行，交给解码器自己试
/// 第 3 条很重要：宁可让解码器失败一次被 catch 住，
/// 也不能因为不认识某个新格式（比如以后的新 RAW）就把用户的照片判成"不是图"。
/// </summary>
public static class FileSignature
{
    /// <summary>嗅探时读多少字节。64 足够覆盖所有常见魔数（最深的 ISOBMFF 也才到第 12 字节）。</summary>
    private const int SniffLength = 64;

    /// <summary>
    /// 看一眼文件头，判断该不该把它交给解码器。
    /// </summary>
    /// <param name="stream">已打开、位置在任意处的流（本方法会自己 Seek，用完恢复原位）。</param>
    /// <returns>true = 像是图片，值得试；false = 确定不是图，别浪费时间。</returns>
    public static bool LooksLikeImage(Stream? stream)
    {
        if (stream is null) return false;

        // 不可 Seek 的流（网络流、压缩包内的 deflate 流）没法回头读，
        // 嗅探会破坏它的位置 —— 这种情况不猜，直接放行交给解码器。
        if (!stream.CanSeek) return true;

        long saved = stream.Position;
        try
        {
            long available = stream.Length - saved;
            if (available < 8) return false;   // 连一个最短的文件头都不够

            int n = (int)Math.Min(SniffLength, available);
            Span<byte> head = stackalloc byte[64];
            head = head[..n];

            stream.Position = saved;
            int read = stream.Read(head);
            if (read < 8) return false;

            return Classify(head) != Kind.NotImage;
        }
        catch
        {
            // 读都读不出来（权限/坏扇区），别在这里纠结，交给解码器再失败一次
            return true;
        }
        finally
        {
            try { stream.Position = saved; } catch { /* 流已经废了，无所谓 */ }
        }
    }

    /// <summary>对一段内存里的文件头做同样的判断（测试用）。</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> head)
        => head.Length >= 8 && Classify(head) != Kind.NotImage;

    private enum Kind { Image, NotImage, Unknown }

    private static Kind Classify(ReadOnlySpan<byte> h)
    {
        // ---------- 1. 已知图片魔数白名单 ----------

        // JPEG / JFIF / EXIF
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return Kind.Image;

        // PNG
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == (byte)'P' && h[2] == (byte)'N' &&
            h[3] == (byte)'G' && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A)
            return Kind.Image;

        // GIF
        if (StartsWith(h, "GIF87a") || StartsWith(h, "GIF89a")) return Kind.Image;

        // BMP
        if (h[0] == (byte)'B' && h[1] == (byte)'M') return Kind.Image;

        // TIFF / 绝大多数相机 RAW（NEF / CR2 / ARW / DNG / ORF / RW2 都是 TIFF 容器）
        if (h[0] == (byte)'I' && h[1] == (byte)'I' && h[2] == 0x2A && h[3] == 0x00) return Kind.Image;
        if (h[0] == (byte)'M' && h[1] == (byte)'M' && h[2] == 0x00 && h[3] == 0x2A) return Kind.Image;

        // WEBP（RIFF....WEBP）
        if (StartsWith(h, "RIFF") && h.Length >= 12 &&
            h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P')
            return Kind.Image;

        // HEIC / HEIF / AVIF / CR3 —— 都是 ISOBMFF：偏移 4 处是 "ftyp"
        if (h.Length >= 12 && h[4] == (byte)'f' && h[5] == (byte)'t' &&
            h[6] == (byte)'y' && h[7] == (byte)'p')
            return Kind.Image;

        // ICO / CUR
        if (h[0] == 0x00 && h[1] == 0x00 && (h[2] == 0x01 || h[2] == 0x02) && h[3] == 0x00)
            return Kind.Image;

        // PSD / PSB
        if (StartsWith(h, "8BPS")) return Kind.Image;

        // OpenEXR
        if (h.Length >= 4 && h[0] == 0x76 && h[1] == 0x2F && h[2] == 0x31 && h[3] == 0x01)
            return Kind.Image;

        // JPEG XL：裸码流 FF 0A，或容器 00 00 00 0C "JXL " 0D 0A 87 0A
        if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0x0A) return Kind.Image;
        if (h.Length >= 12 && h[0] == 0x00 && h[1] == 0x00 && h[2] == 0x00 && h[3] == 0x0C &&
            h[4] == (byte)'J' && h[5] == (byte)'X' && h[6] == (byte)'L' && h[7] == 0x20)
            return Kind.Image;

        // Radiance HDR
        if (StartsWith(h, "#?RADIANCE") || StartsWith(h, "#?RGBE")) return Kind.Image;

        // PCX
        if (h[0] == 0x0A && h[1] <= 0x05) return Kind.Image;

        // Netpbm（P1~P6）。这几个是纯文本格式，必须放在"文本判负"之前判。
        if (h.Length >= 2 && h[0] == (byte)'P' && h[1] >= (byte)'1' && h[1] <= (byte)'6' &&
            (h.Length < 3 || h[2] == 0x0A || h[2] == 0x20 || h[2] == 0x09 || h[2] == 0x0D))
            return Kind.Image;

        // GIMP XCF
        if (StartsWith(h, "gimp xcf")) return Kind.Image;

        // DDS / QOI
        if (StartsWith(h, "DDS ") || StartsWith(h, "qoif")) return Kind.Image;

        // ---------- 2. 明显是文本 → 一定不是图 ----------
        // 这一条是本次修复真正要拦的：md5 校验清单、HTML 错误页、
        // git-lfs 指针、下载失败留下的报错页面，全是这种。
        if (LooksLikeText(h)) return Kind.NotImage;

        // ---------- 3. 不认识的二进制：放行，别误杀 ----------
        return Kind.Unknown;
    }

    /// <summary>
    /// 前 40 字节里几乎全是可打印 ASCII 或空白 → 判为文本。
    /// 允许极少量异常字节（比如 BOM、个别控制符），但不允许有成片的二进制。
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> h)
    {
        int n = Math.Min(40, h.Length);
        if (n < 8) return false;

        int printable = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = h[i];
            if (b >= 0x20 && b < 0x7F) printable++;        // 可打印 ASCII
            else if (b is 0x09 or 0x0A or 0x0D) printable++; // 制表 / 换行
            else if (b == 0x00) return false;              // 出现 NUL 基本就是二进制
        }

        // 95% 以上是可打印字符才判文本
        return printable >= n - (n / 20);
    }

    private static bool StartsWith(ReadOnlySpan<byte> h, string ascii)
    {
        if (h.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (h[i] != (byte)ascii[i]) return false;
        return true;
    }
}
