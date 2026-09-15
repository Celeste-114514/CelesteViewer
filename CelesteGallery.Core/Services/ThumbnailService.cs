using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteGallery.Services;

/// <summary>
/// 缩略图服务。
///
/// 图片查看器快不快，九成取决于这个类的三个设计：
///
///   1. **缓存**（<see cref="_cache"/>）
///      已经解过的缩略图留在内存里，来回滚动不用重解。
///      按内存字节数淘汰（LRU），不是按张数 ——
///      因为 256×256 的缩略图一张就 256KB，按张数限制很容易爆到几个 G。
///
///   2. **并发闸门**（<see cref="_gate"/>）
///      解码是 CPU 密集的活，几百张一起上只会让每张都变慢、界面全程卡死。
///      这里限制同时只解若干个，多出来的排队。
///
///   3. **请求去重**（<see cref="_inFlight"/>）
///      快速滚动时，同一张图会被请求好几次（滚过去又滚回来、虚拟化回收再重建）。
///      没有去重的话就会重复解码同一张，白白浪费几倍 CPU。
///      这里让第二个请求直接复用第一个的那次解码，不重复开工。
///
/// 还有一个 <see cref="DiskThumbnailCache"/> 可选的第四层（磁盘缓存）：
/// 内存里没有、但上次运行留下过，就直接读文件，跳过解码。
/// 这是"关掉程序再打开同一目录，缩略图秒出"的来源。
/// </summary>
public sealed class ThumbnailService
{
    private readonly IImageDecoder _decoder;
    private readonly SemaphoreSlim _gate;
    private readonly long _maxBytes;
    private readonly DiskThumbnailCache? _disk;

    /// <summary>
    /// 超过这个边长就不写磁盘缓存了。
    /// 单图查看会请求到 8000px，一张的裸像素有 190MB，写盘纯属浪费。
    /// </summary>
    private readonly int _diskMaxSize;

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, Task<DecodedBitmap?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private long _currentBytes;

    private sealed class Entry
    {
        public required DecodedBitmap Bitmap { get; init; }
        public required LinkedListNode<string> Node { get; init; }
        public long Bytes { get; init; }
    }

    /// <param name="decoder">解码器，通常传 <see cref="ImageDecodePipeline"/>。</param>
    /// <param name="maxConcurrency">
    /// 同时解码的张数。默认 = CPU 核心数的一半，最少 2、最多 8。
    /// 设太高不会更快，只会把界面挤卡 —— 解码吃的是 CPU，不是 IO。
    /// </param>
    /// <param name="maxBytes">缓存占用的内存上限，默认 128 MB。</param>
    /// <param name="diskCache">
    /// 磁盘缓存，不传就是不启用。通常传 <see cref="DiskThumbnailCache.Shared"/>。
    /// </param>
    /// <param name="diskCacheMaxSize">超过这个边长的缩略图不写磁盘（默认 512）。</param>
    public ThumbnailService(
        IImageDecoder decoder,
        int maxConcurrency = 0,
        long maxBytes = 128L * 1024 * 1024,
        DiskThumbnailCache? diskCache = null,
        int diskCacheMaxSize = 512)
    {
        _decoder = decoder;
        _maxBytes = maxBytes;
        _disk = diskCache;
        _diskMaxSize = diskCacheMaxSize;

        if (maxConcurrency <= 0)
        {
            maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        }
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>磁盘缓存实例（没启用就是 null）。</summary>
    public DiskThumbnailCache? DiskCache => _disk;

    /// <summary>当前缓存了多少张缩略图。</summary>
    public int CachedCount
    {
        get { lock (_sync) return _cache.Count; }
    }

    /// <summary>当前缩略图缓存占了多少内存（字节）。</summary>
    public long CachedBytes => Interlocked.Read(ref _currentBytes);

    /// <summary>
    /// 取一张缩略图。命中缓存就直接返回（不占并发额度、不碰磁盘）。
    /// 打不开（坏图、不支持的格式）返回 null。
    /// </summary>
    /// <param name="size">缩略图的边长上限，比如 256。</param>
    public async Task<DecodedBitmap?> GetAsync(string path, int size, CancellationToken ct = default)
    {
        string key = KeyOf(path, size);

        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
                return hit.Bitmap;
            }
        }

        Task<DecodedBitmap?> pending;
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(key, out pending!))
            {
                pending = DecodeAsync(path, size, key);
                _inFlight[key] = pending;
            }
        }

        try
        {
            // WaitAsync 让调用方能及时放弃等待（比如这一格已经滚出屏幕了）。
            // 注意：只是不等了，后台那次解码仍会跑完并写进缓存 ——
            // 反正用户很可能还会滚回来，白扔掉可惜。
            return await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 预读：把马上可能要看的几张提前解好。
    /// 典型用法是"当前这张的前后各 3 张"，让人按方向键翻页时几乎感觉不到延迟。
    /// 这里是故意不等结果的（fire and forget），失败也不影响任何东西。
    /// </summary>
    public void Prefetch(IEnumerable<string> paths, int size)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;

            string key = KeyOf(path, size);
            lock (_sync)
            {
                if (_cache.ContainsKey(key) || _inFlight.ContainsKey(key)) continue;
                _inFlight[key] = DecodeAsync(path, size, key);
            }
        }
    }

    /// <summary>
    /// 让某一张图的缓存失效（比如用户在别处改了这张图、或者转过角度）。
    /// 传 null 表示全部清空。
    /// </summary>
    public void Invalidate(string? path)
    {
        if (path is null)
        {
            Clear();
            return;
        }

        lock (_sync)
        {
            // 一个路径可能缓存了好几种尺寸，全部找出来一起丢掉
            List<string>? doomed = null;
            foreach (var kv in _cache)
            {
                if (kv.Key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                    (doomed ??= new()).Add(kv.Key);
            }
            if (doomed is null) return;

            foreach (string key in doomed)
                DropLocked(key);
        }
    }

    /// <summary>清空所有缓存。切换目录时如果图片特别多，可以调一下释放内存。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _cache.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private async Task<DecodedBitmap?> DecodeAsync(string path, int size, string key)
    {
        try
        {
            // 压缩包（zip / cbz）的封面：用包里第一张图。
            // 缓存 key 仍然按压缩包本身算 —— 它的修改时间和大小才是"内容变没变"的判据，
            // 而且这样包里第一页换了（重新压了一版）旧封面也会自动作废。
            string decodePath = path;
            if (ArchiveIndex.IsArchiveFile(path))
            {
                var inner = ArchiveIndex.ListImages(path);
                if (inner.Count > 0) decodePath = inner[0];
            }

            // 第一道：磁盘缓存。有就直接用，连解码器都不用碰。
            // 放在并发闸门外面是关键 —— 读磁盘不吃 CPU，
            // 让它们也去排队会白白拖慢"滚回来重新显示"这种场景。
            if (_disk is not null && size > 0 && size <= _diskMaxSize)
            {
                var fromDisk = await _disk.TryGetAsync(path, size, CancellationToken.None)
                                        .ConfigureAwait(false);
                if (fromDisk is not null)
                {
                    Store(key, fromDisk);
                    return fromDisk;
                }
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 这里用 None 而不是调用方的 token：
                // 既然已经开始解了，就把它解完写进缓存，
                // 半途而废的话前面那些 CPU 时间就全白花了。
                //
                // 外面套 Task.Run 是必须的：调用方十有八九是在 UI 线程上发起的，
                // 而"闸门正好空着"时 await 会**同步**完成，解码就落到 UI 线程上跑了 ——
                // 一张 2200×1200 的图要几十毫秒，界面就卡那么一下。
                // 显式扔到线程池去，UI 线程从头到尾不碰解码。
                var bitmap = await Task.Run(
                        () => _decoder.DecodeAsync(decodePath, size, size, CancellationToken.None))
                    .ConfigureAwait(false);
                if (bitmap is not null)
                {
                    Store(key, bitmap);

                    // 写回磁盘。故意不等它 —— 界面不该等磁盘写完才显示。
                    // 失败也无所谓，下次重新解一遍而已。
                    if (_disk is not null && size <= _diskMaxSize)
                        _ = _disk.StoreAsync(path, size, bitmap, CancellationToken.None);
                }

                return bitmap;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                _inFlight.Remove(key);
            }
        }
    }

    private void Store(string key, DecodedBitmap bitmap)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                return;
            }

            long bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
            var node = _lru.AddFirst(key);
            _cache[key] = new Entry { Bitmap = bitmap, Node = node, Bytes = bytes };
            _currentBytes += bytes;

            while (_currentBytes > _maxBytes && _lru.Last is not null)
            {
                string victim = _lru.Last.Value;
                DropLocked(victim);
            }
        }
    }

    private void DropLocked(string key)
    {
        if (!_cache.TryGetValue(key, out var entry)) return;
        _cache.Remove(key);
        _lru.Remove(entry.Node);
        _currentBytes -= entry.Bytes;
        if (_currentBytes < 0) _currentBytes = 0;
    }

    private static string KeyOf(string path, int size) => path + "|" + size.ToString();
}
