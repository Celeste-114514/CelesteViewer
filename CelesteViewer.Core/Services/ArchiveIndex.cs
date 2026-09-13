using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace CelesteViewer.Services;

/// <summary>
/// 压缩包（zip / cbz）内翻图的支持层。
///
/// 核心手法是**虚拟路径**：把压缩包内的图片表示成
/// <code>D:\漫画\第1卷.zip|vol01/003.jpg</code>
/// —— 压缩包路径 + 分隔符 + 包内条目名，拼成一个普通字符串。
///
/// 好处是上层几乎不用改：目录索引、翻页、预读、缩略图缓存全都只是在传字符串，
/// 它们继续把这个字符串当"一个文件路径"用就行，只在真正要读字节的地方
/// （解码器、缓存 key）才拆开看它是不是虚拟的。
///
/// 分隔符用 <c>|</c> 是因为**它在 Windows 文件名里是非法字符** ——
/// 真实磁盘路径不可能含它，所以"含不含 |"是一个可靠的判据，不会误判。
///
/// 目前只支持 zip / cbz（.NET 自带 System.IO.Compression，零额外体积）。
/// rar / 7z 需要引入额外解码库，等确定要支持再加。
/// </summary>
public static class ArchiveIndex
{
    /// <summary>虚拟路径里"压缩包路径"和"包内条目名"的分隔符。</summary>
    public const char Separator = '|';

    private static readonly HashSet<string> _archiveExts =
        new(ImageFormats.Archives, StringComparer.OrdinalIgnoreCase);

    /// <summary>这个文件是不是一个能打开的压缩包（只看扩展名，不打开验证）。</summary>
    public static bool IsArchiveFile(string? path)
        => !string.IsNullOrEmpty(path) && _archiveExts.Contains(Path.GetExtension(path));

    /// <summary>
    /// 这个路径是不是虚拟路径（指向压缩包内部）。
    /// 判据就是有没有分隔符 —— 真实路径里不可能有 <c>|</c>。
    /// </summary>
    public static bool IsVirtual(string? path)
        => !string.IsNullOrEmpty(path) && path.IndexOf(Separator) > 0;

    /// <summary>把压缩包路径和条目名拼成虚拟路径。</summary>
    public static string Make(string archivePath, string entryName)
        => archivePath + Separator + entryName;

    /// <summary>拆开虚拟路径。不是虚拟路径就返回 false。</summary>
    public static bool TrySplit(string path, out string archivePath, out string entryName)
    {
        archivePath = string.Empty;
        entryName = string.Empty;

        if (string.IsNullOrEmpty(path)) return false;

        int i = path.IndexOf(Separator);
        if (i <= 0 || i == path.Length - 1) return false;

        archivePath = path.Substring(0, i);
        entryName = path.Substring(i + 1);
        return true;
    }

    /// <summary>
    /// 取一个虚拟路径（或普通路径）对应的"磁盘上的那个文件"的信息。
    /// 虚拟路径指向的文件在磁盘上并不存在，所以要退回到压缩包本身。
    /// </summary>
    public static FileInfo? InfoOf(string path)
    {
        try
        {
            return IsVirtual(path) && TrySplit(path, out string zip, out _)
                ? new FileInfo(zip)
                : new FileInfo(path);
        }
        catch
        {
            // 路径含非法字符时 FileInfo 会抛，不能让它把调用方带崩
            return null;
        }
    }

    /// <summary>
    /// 列出压缩包里所有图片，返回**虚拟路径**列表，按自然排序排好。
    /// 打不开（不是 zip / 文件损坏 / 有密码）就返回空列表，不抛异常。
    /// </summary>
    public static List<string> ListImages(string archivePath)
    {
        var result = new List<string>();

        foreach (string name in EnumerateEntryNames(archivePath))
        {
            if (IsNoise(name)) continue;

            // 只认扩展名，和目录扫描的策略一致：不打开文件验证，
            // 免得一个几百页的漫画包光是枚举就卡几秒
            if (!ImageFormats.IsImage(Path.GetExtension(name))) continue;

            result.Add(Make(archivePath, name));
        }

        result.Sort(NaturalCompare);
        return result;
    }

    /// <summary>
    /// 把包内某个条目的内容读成一个内存流。
    ///
    /// ⚠️ 为什么要整个读进内存，而不是直接把 <c>entry.Open()</c> 的流交出去：
    ///   1. 那个流是 DeflateStream，**不能 Seek**，而 WIC 解码要随机访问，
    ///      直接给它会导致解码失败或极慢；
    ///   2. ZipArchive 一 dispose，它的条目流就失效了 —— 解码器必须能独立持有流。
    ///
    /// 代价是内存：包内一张图解压后通常几 MB，看图时同时最多持有两三张，可以接受。
    /// 调用方负责 dispose 返回的流。
    /// </summary>
    public static MemoryStream? ReadEntry(string archivePath, string entryName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? entry = zip.GetEntry(entryName);
            if (entry is null) return null;

            long size = entry.Length;
            var buffer = size > 0 && size <= int.MaxValue ? new MemoryStream((int)size)
                                                           : new MemoryStream();
            using (Stream src = entry.Open())
            {
                src.CopyTo(buffer);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开一个源流：虚拟路径就从包里读，普通路径就开文件。调用方 dispose。</summary>
    public static Stream? OpenSource(string path)
    {
        if (IsVirtual(path))
            return TrySplit(path, out string zip, out string entry) ? ReadEntry(zip, entry) : null;

        try
        {
            return File.OpenRead(path);
        }
        catch
        {
            return null;
        }
    }

    // ===== 内部实现 =====

    /// <summary>读取所有条目名。会顺手处理中文文件名编码不对的情况。</summary>
    private static List<string> EnumerateEntryNames(string archivePath)
    {
        var names = TryReadNames(archivePath, null);
        if (names is null || names.Count == 0) return names ?? new List<string>();

        // 中文 zip 的坑：很多压缩软件（尤其老版 WinRAR / 好压）用 GBK 存文件名，
        // 但 zip 规范里没地方写"我用的什么编码"。.NET 默认按 UTF-8 解，
        // 解出来就是一堆 U+FFFD 替换字符 —— 这就是乱码的指纹。
        if (!HasGarbled(names)) return names;

        var gbk = TryReadNames(archivePath, Gb18030OrNull());
        return gbk is not null && !HasGarbled(gbk) ? gbk : names;
    }

    private static List<string>? TryReadNames(string archivePath, Encoding? encoding)
    {
        try
        {
            using FileStream fs = File.OpenRead(archivePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: encoding);

            var names = new List<string>(zip.Entries.Count);
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                string name = e.FullName;
                if (name.Length > 0) names.Add(name);
            }

            return names;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasGarbled(List<string> names)
    {
        foreach (string n in names)
        {
            if (n.IndexOf('\uFFFD') >= 0) return true;
        }

        return false;
    }

    private static Encoding? Gb18030OrNull()
    {
        try
        {
            // .NET 默认不带 GB18030，要装 System.Text.Encoding.CodePages 才有。
            // 没有就返回 null，退回 UTF-8 的结果 —— 文件名可能还是乱码，但不会崩。
            return Encoding.GetEncoding("GB18030");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 包里的"杂物"：目录条目、macOS 的 __MACOSX、点开头的隐藏文件。
    /// 这些在漫画包里很常见，混进列表会让翻页莫名多出几张空白。
    /// </summary>
    private static bool IsNoise(string name)
    {
        if (name.Length == 0) return false;
        if (name[name.Length - 1] == '/' || name[name.Length - 1] == '\\') return true;

        // 只看最后一段（目录部分含 "." 是正常的，比如 "第1卷./001.jpg"）
        int slash = name.LastIndexOfAny(new[] { '/', '\\' });
        string file = slash >= 0 ? name.Substring(slash + 1) : name;

        if (file.Length == 0) return true;
        if (file[0] == '.') return true;

        return name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase);
    }

    // ===== 自然排序（和 FolderIndex 同一套：跟资源管理器顺序一致）=====

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    private static int NaturalCompare(string a, string b)
    {
        try
        {
            int result = StrCmpLogicalW(a, b);
            if (result != 0) return result;
        }
        catch
        {
            // shlwapi 不可用（极少数精简系统）时退回普通比较
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
