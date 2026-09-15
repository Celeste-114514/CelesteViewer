using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CelesteGallery.Services;

/// <summary>
/// 一个目录里的图片清单，负责回答"上一张 / 下一张是哪张"。
///
/// 看起来简单，但有两个地方容易做错，这里都处理了：
///
///   1. **排序必须是"自然排序"**
///      如果按字符串比，"IMG_10.jpg" 会排在 "IMG_2.jpg" 前面（因为 '1' < '2'），
///      用户按方向键翻页时顺序就乱了。这里用 Windows 资源管理器同款的
///      StrCmpLogicalW，保证和资源管理器里看到的顺序完全一致。
///
///   2. **只认扩展名，不打开文件验证**
///      一个目录里几千张图，如果每张都打开确认是不是真图片，光是开关文件
///      就要几秒。这里只按扩展名过滤，万一混进一个假图片，
///      翻到它时显示"打不开"就行 —— 总比打开目录卡三秒好。
/// </summary>
public sealed class FolderIndex
{
    /// <summary>"连子目录一起收"时默认最多往下钻几层。</summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>
    /// "连子目录一起收"时默认最多收多少张。
    ///
    /// 必须有这个上限：勾上递归之后如果点到 C:\ 或者某个存了几十万张图的大目录，
    /// 光是把路径塞进列表就能吃掉几百 MB 内存，缩略图墙再快也救不回来。
    /// 撞到上限就停，并置 <see cref="Truncated"/>，由界面去提示用户。
    /// </summary>
    public const int DefaultMaxFiles = 20000;

    private readonly List<string> _files = new();
    private int _current = -1;

    /// <summary>
    /// 这次扫描要不要把压缩包（zip / cbz）也收进来。
    /// 只有缩略图墙需要；单图页翻页不需要（翻到 zip 会解不开）。
    /// 用字段而不是一路传参数，是为了不动 CollectFiles 那一串签名 ——
    /// 代价是每个加载入口都必须显式设一次它，漏设就会沿用上一次的值。
    /// </summary>
    private bool _includeArchives;

    /// <summary>当前目录里一共有多少张图。</summary>
    public int Count => _files.Count;

    /// <summary>这次加载是不是因为撞到张数上限而提前停了。</summary>
    public bool Truncated { get; private set; }

    /// <summary>当前是第几张（从 1 开始，用来显示 "12 / 340"）。没有就是 0。</summary>
    public int Position => _current < 0 ? 0 : _current + 1;

    /// <summary>当前这张的完整路径，没有就是 null。</summary>
    public string? CurrentPath => _current >= 0 && _current < _files.Count ? _files[_current] : null;

    /// <summary>当前所在目录。</summary>
    public string? Folder { get; private set; }

    /// <summary>
    /// 加载整个目录，光标停在第一张。
    /// 网格视图（缩略图墙）用这个；<see cref="LoadFromFile"/> 是给"双击某张图打开"用的。
    /// </summary>
    /// <param name="folder">要加载的目录。</param>
    /// <param name="includeSubfolders">
    /// 是否连下面所有子目录里的图片一起收。
    /// 照片按年月分成一层层子目录时，开着它才看得到全景。
    /// </param>
    /// <returns>找到的图片数量。</returns>
    public int LoadFolder(
        string folder,
        bool includeSubfolders = false,
        int maxDepth = DefaultMaxDepth,
        int maxFiles = DefaultMaxFiles,
        bool includeArchives = false,
        CancellationToken ct = default)
    {
        Folder = folder;
        _files.Clear();
        _current = -1;
        Truncated = false;
        _includeArchives = includeArchives;

        CollectFiles(folder, includeSubfolders, maxDepth, maxFiles, ct);

        _files.Sort(NaturalCompare);
        _current = _files.Count > 0 ? 0 : -1;

        return _files.Count;
    }

    /// <summary>
    /// 加载指定文件所在的整个目录，并把光标停在这个文件上。
    /// 典型场景：双击某张图打开程序，按方向键应该从这张开始往两边翻。
    /// </summary>
    /// <returns>目录里找到的图片数量。</returns>
    public int LoadFromFile(string filePath)
    {
        string? folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)) return 0;

        // 虚拟路径（压缩包内的某一页）没有"所在目录"这个概念，
        // 它的"目录"就是那个压缩包本身 —— 所以先加载整个包，再定位到这一页。
        if (ArchiveIndex.IsVirtual(filePath)) return LoadFromVirtual(filePath);

        Folder = folder;
        _files.Clear();
        _current = -1;
        Truncated = false;
        _includeArchives = false;   // 单图页翻页列表里不要混进压缩包

        CollectFiles(folder);
        _files.Sort(NaturalCompare);

        _current = _files.FindIndex(
            p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));

        // 找不到的情况（比如文件刚被删掉）就停在开头，不要停在 -1 卡死
        if (_current < 0) _current = _files.Count > 0 ? 0 : -1;

        return _files.Count;
    }

    /// <summary>
    /// 加载一个压缩包（zip / cbz）里的所有图片，光标停在第一页。
    ///
    /// 列表里存的是虚拟路径（"包路径|包内条目名"），
    /// 所以后面翻页、预读这些现成逻辑一行都不用改 —— 它们只是在传字符串。
    /// </summary>
    /// <returns>包内找到的图片数量。不是压缩包/打不开/里面没有图片都会返回 0。</returns>
    public int LoadArchive(string archivePath)
    {
        Folder = archivePath;
        _files.Clear();
        _current = -1;
        Truncated = false;
        _includeArchives = false;

        _files.AddRange(ArchiveIndex.ListImages(archivePath));
        _current = _files.Count > 0 ? 0 : -1;

        return _files.Count;
    }

    /// <summary>
    /// 定位到压缩包内的指定一页（比如从缩略图墙双击进来的）。
    /// 找不到那一页就停在第一页，不要停在 -1 卡死。
    /// </summary>
    private int LoadFromVirtual(string virtualPath)
    {
        if (!ArchiveIndex.TrySplit(virtualPath, out string archivePath, out _)) return 0;

        int count = LoadArchive(archivePath);

        _current = _files.FindIndex(
            p => string.Equals(p, virtualPath, StringComparison.OrdinalIgnoreCase));

        if (_current < 0) _current = _files.Count > 0 ? 0 : -1;

        return count;
    }

    /// <summary>
    /// 收集图片路径。<paramref name="recursive"/> 为假时只看这一层，
    /// 为真时按**广度优先**往下钻 —— 同层的先收完再下一层，
    /// 这样万一被 <paramref name="maxFiles"/> 截断，拿到的是"比较浅的那批"，
    /// 而不是一路钻到底只收了某个深层子目录。
    /// </summary>
    private void CollectFiles(
        string? folder,
        bool recursive = false,
        int maxDepth = DefaultMaxDepth,
        int maxFiles = DefaultMaxFiles,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(folder)) return;

        if (!recursive)
        {
            AddFilesIn(folder, ct);
            return;
        }

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((folder, 0));

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested) return;

            if (_files.Count >= maxFiles)
            {
                Truncated = true;
                return;
            }

            var (dir, depth) = queue.Dequeue();
            AddFilesIn(dir, ct);

            if (depth >= maxDepth) continue;

            try
            {
                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        var attr = File.GetAttributes(d);
                        // 隐藏 / 系统目录直接跳过，不然会钻进 $RECYCLE.BIN 之类的地方
                        if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                            continue;

                        queue.Enqueue((d, depth + 1));
                    }
                    catch { }
                }
            }
            catch
            {
                // 权限不足的目录很常见，跳过就行
            }
        }
    }

    /// <summary>把这一层里的图片收进来。只按扩展名认，不打开文件验证。</summary>
    private void AddFilesIn(string folder, CancellationToken ct)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (ct.IsCancellationRequested) return;

                string ext = Path.GetExtension(path);

                // 压缩包要显式要求才收：只有缩略图墙（浏览页）需要它。
                // 单图页翻页时如果列表里混进一个 zip，翻到它会解不开、
                // 显示成"打不开的图" —— 所以默认一律不收。
                if (ImageFormats.IsImage(ext) ||
                    (_includeArchives && ArchiveIndex.IsArchiveFile(path)))
                    _files.Add(path);
            }
        }
        catch
        {
            // 权限不足 / 目录被删，都当"没有图片"处理
        }
    }

    /// <summary>
    /// 从某个根目录往下找第一个"真的有图片"的目录。
    ///
    /// 为什么需要它：Windows 的"图片"文件夹顶层通常是空的 ——
    /// 照片都在"图片\截图"、"图片\2026\..."之类的子目录里。
    /// 首次启动直接把用户扔到一个空白的缩略图墙上，看着就像软件坏了。
    ///
    /// 只往下找 <paramref name="maxDepth"/> 层，并且找到就立刻返回 ——
    /// 不能让"启动"这件事卡在一整个磁盘的递归扫描上。
    /// </summary>
    /// <returns>找到的目录；都没有就返回 null。</returns>
    public static string? FindFolderWithImages(string root, int maxDepth = 2)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

        if (HasAnyImage(root)) return root;

        return SearchDeeper(root, 1, maxDepth);
    }

    private static string? SearchDeeper(string folder, int depth, int maxDepth)
    {
        if (depth > maxDepth) return null;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(folder);
        }
        catch
        {
            return null;
        }

        var children = new List<string>();
        foreach (string d in dirs)
        {
            try
            {
                var attr = File.GetAttributes(d);
                if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                    continue;
                children.Add(d);
            }
            catch { }
        }

        children.Sort((a, b) => CompareNatural(Path.GetFileName(a), Path.GetFileName(b)));

        // 先扫一层（浅的优先），都没有再往下钻
        foreach (string d in children)
        {
            if (HasAnyImage(d)) return d;
        }

        foreach (string d in children)
        {
            string? deeper = SearchDeeper(d, depth + 1, maxDepth);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    /// <summary>这个目录里有没有图片。找到一个就返回，不枚举完。</summary>
    private static bool HasAnyImage(string folder)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (ImageFormats.IsImage(Path.GetExtension(path)))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>往后翻一张。已经是最后一张就返回 false。</summary>
    public bool MoveNext()
    {
        if (_current < 0 || _current + 1 >= _files.Count) return false;
        _current++;
        return true;
    }

    /// <summary>往前翻一张。已经是第一张就返回 false。</summary>
    public bool MovePrevious()
    {
        if (_current <= 0) return false;
        _current--;
        return true;
    }

    /// <summary>直接跳到第 n 张（从 0 开始）。</summary>
    public bool MoveTo(int index)
    {
        if (index < 0 || index >= _files.Count) return false;
        _current = index;
        return true;
    }

    /// <summary>
    /// 当前这张前后各 <paramref name="radius"/> 张的路径，用来预读。
    /// 预读做得好，用户按方向键翻页时几乎感觉不到延迟。
    /// </summary>
    public List<string> Neighbors(int radius)
    {
        var result = new List<string>();
        if (_current < 0) return result;

        for (int offset = -radius; offset <= radius; offset++)
        {
            if (offset == 0) continue;
            int index = _current + offset;
            if (index >= 0 && index < _files.Count)
                result.Add(_files[index]);
        }

        return result;
    }

    /// <summary>整个清单，给列表界面用。</summary>
    public IReadOnlyList<string> All => _files;

    // ===== 自然排序 =====

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    /// <summary>
    /// 自然排序的比较函数，公开出来给目录树用（文件夹名也要 IMG_2 排在 IMG_10 前面）。
    /// </summary>
    public static int CompareNatural(string a, string b) => NaturalCompare(a, b);

    private static int NaturalCompare(string a, string b)
    {
        try
        {
            return StrCmpLogicalW(a, b);
        }
        catch
        {
            // 万一系统 API 不可用（极小概率），退回普通字符串比较，
            // 顺序可能不完美，但不至于崩
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
