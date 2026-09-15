using System;
using System.IO;
using System.Security.Cryptography;
using ImageMagick;

namespace CelesteViewer.Services;

/// <summary>
/// 图片指纹。两种，各管各的：
///   <see cref="ComputeMd5"/>  —— 文件内容的 MD5。哈希相同 = 字节级完全一样 = 精确重复（快，读字节即可）。
///   <see cref="ComputePhash"/> —— 感知哈希 dHash。内容相近 = 视觉相似（要解码缩略后算，慢一些）。
///
/// 为什么需要两种：单靠 MD5 只能找"一模一样"的副本；
/// 截图存成不同格式、缩放过、加了水印这种"看起来一样但字节不同"的，
/// 得靠 PHash 的汉明距离来捞（见 <see cref="MediaIndex.FindSimilar"/>）。
///
/// 两种哈希失败（格式不支持、文件损坏）都返回 null，调用方把它当"没指纹"处理，
/// 绝不抛异常 —— 指纹是辅助能力，不能因为它让主流程（扫描/查重）崩掉。
/// </summary>
public static class PerceptualHash
{
    /// <summary>文件 MD5 → 32 位小写 hex。读不了（锁文件/损坏）返回 null。</summary>
    public static string? ComputeMd5(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(fs));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 感知哈希 dHash：解码 → 缩到 9×8 → 灰度 → 相邻列差分，得到 64 位。
    /// 内容相近的图哈希也相近（汉明距离小）。解不开/不支持的格式返回 null。
    ///
    /// 缩到 9×8（宽比高多 1 列）是 dHash 的标准做法：
    /// 8 行 × 每行的 8 对相邻列比较 = 64 位。变形拉伸无所谓，dHash 本就容忍尺寸/比例差异。
    /// </summary>
    public static string? ComputePhash(string path)
    {
        // 先问一句"Magick 认不认得这格式" —— 认不出就别碰，避免原生异常（见 MagickImageDecoder 第三轮修复）。
        if (!MagickImageDecoder.CanDecodeFile(path)) return null;

        try
        {
            MagickFormat? hint = MagickImageDecoder.FormatForPath(path);
            using var image = hint.HasValue
                ? new MagickImage(path, hint.Value)
                : new MagickImage(path);

            // 缩到 9×8（宽比高多 1 列，给列间差分留位置），转灰度取亮度通道
            image.Thumbnail(9, 8);
            image.Grayscale();

            byte[]? data = image.GetPixels().ToArray();
            if (data is null || data.Length < 9 * 8) return null;

            // 标准 dHash：每行的 8 对相邻列比大小，共 8 行 × 8 位 = 64 位。左亮于右记 1。
            ulong hash = 0;
            int bit = 63;
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    int left = data[row * 9 + col];
                    int right = data[row * 9 + col + 1];
                    if (left > right) hash |= (1UL << bit);
                    bit--;
                }
            }

            return hash.ToString("x16");
        }
        catch
        {
            // 任一环节（解码/缩放/取像素）失败都静默返回 null，
            // 绝不抛异常 —— 指纹是辅助能力，不能让查重主流程崩。
            return null;
        }
    }

    /// <summary>两个 64 位哈希的汉明距离（不同 bit 数）。距离越小越相似，0 = 完全相同。</summary>
    public static int HammingDistance(string a, string b)
    {
        if (!TryParseHex(a, out ulong ha) || !TryParseHex(b, out ulong hb))
            return int.MaxValue;

        ulong x = ha ^ hb;
        int count = 0;
        while (x != 0) { x &= x - 1; count++; }   // 数 1 的个数（Brian Kernighan 算法，popcount）
        return count;
    }

    private static bool TryParseHex(string s, out ulong v)
    {
        if (string.IsNullOrEmpty(s)) { v = 0; return false; }
        try { v = Convert.ToUInt64(s, 16); return true; }
        catch { v = 0; return false; }
    }
}
