using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteViewer.Services;

/// <summary>
/// 磁盘缩略图缓存 —— 「第二次打开同一批图，缩略图瞬间全出来」靠的就是它。
///
/// 只做内存缓存的话，关掉程序再打开，一个一万张的目录要重新解一万次
/// （按实测 2.6ms/张算就是 26 秒）。写进磁盘之后，第二次打开只是读文件，
/// 快十几倍，而且是不管重启多少次都有效。
///
/// 三个设计要点：
///
///   1. **存的是裸像素，不是 PNG/JPG**
///      缩略图的像素本来就躺在内存里，直接原样写盘最省事也最快 ——
///      省掉"编码再解码"这一来一回（编码一张 320px 的图要好几毫秒，
///      比读一次磁盘还慢）。代价是文件大一些，但磁盘比 CPU 便宜得多。
///
///   2. **文件名是靠内容算出来的哈希**
///      哈希里混进了「路径 + 目标尺寸 + 文件修改时间 + 文件大小」。
///      所以图片被改了、被替换了，哈希就变了，旧缓存自然失效 ——
///      不需要任何"清理过期缓存"的逻辑。
///
///   3. **按占用总量淘汰**
///      上限默认 512MB，超了就按"最久没被用过"删。不限制的话，
///      存几万张之后能把用户的 C 盘吃掉好几个 G。
/// </summary>
public sealed class DiskThumbnailCache
{
    // 文件头 16 字节：4 字节魔数 + 宽 + 高 + 标志位 + 保留
    private const int HeaderSize = 16;
    private static readonly byte[] Magic = { (byte)'C', (byte)'V', (byte)'T', (byte)'1' };

    private static readonly Lazy<DiskThumbnailCache> _shared = new(() => new DiskThumbnailCache());

    /// <summary>
    /// 全程序共用一个实例。
    /// 网格视图和单图查看各建一个的话，两边对"已占用多少"的记账会各算各的，
    /// 淘汰逻辑就乱了。
    /// </summary>
    public static DiskThumbnailCache Shared => _shared.Value;

    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _writeGate = new(2, 2);

    private long _currentBytes = -1;   // -1 表示还没统计过
    private int _writesSincePrune;

    private int _hits;
    private int _misses;

    /// <param name="directory">缓存目录，默认 %LOCALAPPDATA%\CelesteViewer\ThumbCache。</param>
    /// <param name="maxBytes">占用上限，默认 512MB。</param>
    public DiskThumbnailCache(string? directory = null, long maxBytes = 512L * 1024 * 1024)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteViewer", "ThumbCache");
        _maxBytes = maxBytes;

        try { Directory.CreateDirectory(_dir); }
        catch { /* 建不了目录就让所有读写都失败，不影响程序本身 */ }
    }

    public string DirectoryPath => _dir;

    /// <summary>缓存命中过多少次（本进程内）。</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>缓存里没有、需要真去解码的次数。</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>当前占用的磁盘字节数（首次调用会扫一遍目录）。</summary>
    public long CachedBytes
    {
        get
        {
            EnsureSized();
            return Interlocked.Read(ref _currentBytes);
        }
    }

    /// <summary>
    /// 试着从磁盘取一张。取到就返回，取不到返回 null（调用方接着走解码）。
    /// 任何异常都被吞掉 —— 缓存坏掉只应该导致"慢一点"，绝不该导致打不开图。
    /// </summary>
    public async Task<DecodedBitmap?> TryGetAsync(string path, int size, CancellationToken ct = default)
    {
        string key = KeyOf(path, size);
        if (key.Length == 0) return null;

        string file = Path.Combine(_dir, key + ".bin");

        try
        {
            if (!File.Exists(file))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            byte[] all = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            if (all.Length < HeaderSize) return null;

            for (int i = 0; i < Magic.Length; i++)
                if (all[i] != Magic[i]) return null;

            int width = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(8));
            bool premultiplied = (all[12] & 1) != 0;

            int need = width * height * 4;
            if (width <= 0 || height <= 0 || all.Length - HeaderSize != need) return null;

            byte[] pixels = new byte[need];
            Buffer.BlockCopy(all, HeaderSize, pixels, 0, need);

            // 显式更新"最后访问时间"，淘汰时才知道谁该先删。
            // 系统默认会关掉访问时间的记录，但显式设置是生效的。
            try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { }

            Interlocked.Increment(ref _hits);

            return new DecodedBitmap
            {
                Pixels = pixels,
                PixelWidth = width,
                PixelHeight = height,
                Premultiplied = premultiplied,
                DecoderName = "磁盘缓存",
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把一张缩略图写进磁盘。故意做成"发完就不管"的异步 ——
    /// 界面不该等磁盘写完才显示。
    /// </summary>
    public async Task StoreAsync(string path, int size, DecodedBitmap bitmap, CancellationToken ct = default)
    {
        string key = KeyOf(path, size);
        if (key.Length == 0) return;
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;

        int need = bitmap.PixelWidth * bitmap.PixelHeight * 4;
        if (bitmap.Pixels.Length < need) return;

        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string file = Path.Combine(_dir, key + ".bin");
                if (File.Exists(file)) return;

                int total = HeaderSize + need;
                byte[] buffer = GC.AllocateUninitializedArray<byte>(total);

                Buffer.BlockCopy(Magic, 0, buffer, 0, Magic.Length);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), bitmap.PixelWidth);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), bitmap.PixelHeight);
                buffer[12] = (byte)(bitmap.Premultiplied ? 1 : 0);
                Buffer.BlockCopy(bitmap.Pixels, 0, buffer, HeaderSize, need);

                // 先写临时文件再改名：中途断电/崩溃只会留下一个 .tmp，
                // 不会留下一个"看起来完整、其实只写了一半"的 .bin
                string tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, buffer, CancellationToken.None).ConfigureAwait(false);
                File.Move(tmp, file, overwrite: true);

                EnsureSized();
                Interlocked.Add(ref _currentBytes, total);

                // 每写 64 张检查一次容量，避免每写一张就扫一遍目录
                if (++_writesSincePrune >= 64)
                {
                    _writesSincePrune = 0;
                    PruneIfNeeded();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
        }
    }

    /// <summary>清空全部磁盘缓存。</summary>
    public void Clear()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(_dir, "*.bin"))
            {
                try { File.Delete(f); } catch { }
            }
            foreach (string f in Directory.EnumerateFiles(_dir, "*.tmp"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch
        {
        }

        Interlocked.Exchange(ref _currentBytes, 0);
    }

    // ===== 内部 =====

    /// <summary>
    /// 算缓存键。混进修改时间和文件大小是刻意的 ——
    /// 用户换了张图或者重新导出了一版，键就变了，旧缓存自动作废。
    /// </summary>
    private static string KeyOf(string path, int size)
    {
        try
        {
            // ⚠️ 不能用 new FileInfo(path)：虚拟路径（"包.zip|内页.jpg"）在磁盘上
            // 没有对应文件，Exists 恒为 false（甚至可能因非法字符抛异常），
            // 结果就是包内的图永远走不到磁盘缓存，每次翻页都重新解码。
            // ArchiveIndex.InfoOf 会退回去用压缩包本身的信息，缓存才生效。
            var fi = ArchiveIndex.InfoOf(path);
            if (fi is null || !fi.Exists) return "";

            string raw = $"{path}|{size}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
        }
    }

    private void EnsureSized()
    {
        if (Interlocked.Read(ref _currentBytes) >= 0) return;

        long total = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(_dir, "*.bin"))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch
        {
        }

        Interlocked.Exchange(ref _currentBytes, total);
    }

    private void PruneIfNeeded()
    {
        try
        {
            EnsureSized();
            if (Interlocked.Read(ref _currentBytes) <= _maxBytes) return;

            long target = (long)(_maxBytes * 0.85);
            var files = new List<(string Path, long Size, DateTime Used)>();

            foreach (string f in Directory.EnumerateFiles(_dir, "*.bin"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    // 访问时间可能因为系统设置而不更新，取"访问/写入"里较晚的那个兜底
                    DateTime used = fi.LastAccessTimeUtc > fi.LastWriteTimeUtc
                        ? fi.LastAccessTimeUtc
                        : fi.LastWriteTimeUtc;
                    files.Add((f, fi.Length, used));
                }
                catch { }
            }

            files.Sort((a, b) => a.Used.CompareTo(b.Used));   // 最久没用过的排最前

            long freed = 0;
            foreach (var f in files)
            {
                if (Interlocked.Read(ref _currentBytes) - freed <= target) break;
                try
                {
                    File.Delete(f.Path);
                    freed += f.Size;
                }
                catch { }
            }

            // 顺手清掉崩溃残留的半截文件
            try
            {
                foreach (string t in Directory.EnumerateFiles(_dir, "*.tmp"))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(t) > TimeSpan.FromHours(1))
                            File.Delete(t);
                    }
                    catch { }
                }
            }
            catch { }

            if (freed > 0) Interlocked.Add(ref _currentBytes, -freed);
        }
        catch
        {
        }
    }
}
