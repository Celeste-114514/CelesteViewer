using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteGallery.Services;

/// <summary>
/// 批量处理引擎（路线图第 7 步）。
///
/// 设计原则：
///   · **只新增输出、绝不偷偷改原图**。默认给输出文件加后缀（如 <c>_batch</c>），
///     或写到指定输出目录；除非用户明确勾选"覆盖原图"，否则原文件永远留在原地。
///     这是"每步能回退"在本项目里的落地 —— 批量跑错了，原图还在。
///   · **单张失败不影响其余**。每张图的结果（成功 / 失败原因）都收进 <see cref="BatchResult"/>，
///     不会一张崩了整个任务中断。
///   · **编辑算法只有一套**。旋转 / 翻转 / 调色一律走 <see cref="EditRenderer"/>，
///     和查看器、缩略图墙用的是同一个函数，绝不会出现"批量出来的图和单张编辑不一样"。
///   · **纯托管、无 UI 依赖**，可以直接丢进控制台压测（见 IndexHarness / _cvbatch）。
///
/// 五种操作各自独立成一次批处理（选一种、应用到全部选中图），
/// 不把多种操作揉在一个界面里 —— 用户脑子里的"批量"就是"对一堆图做同一件事"。
/// </summary>
public enum BatchOperation
{
    /// <summary>旋转 / 翻转（改几何）。</summary>
    RotateFlip,

    /// <summary>统一调色（套用同一套 <see cref="LookSettings"/>，不改几何）。</summary>
    Tone,

    /// <summary>改尺寸（等比缩到指定长边，或显式宽高）。</summary>
    Resize,

    /// <summary>格式转换（PNG / JPG / WebP …，内容不变）。</summary>
    Convert,

    /// <summary>批量重命名（按模式生成新文件名，内容不变）。</summary>
    Rename,
}

/// <summary>一次批量任务的全部参数。UI 把面板上的选择填进来，引擎只认这个对象。</summary>
public sealed class BatchOptions
{
    /// <summary>要做的操作。</summary>
    public BatchOperation Operation { get; set; } = BatchOperation.RotateFlip;

    // ---- 旋转 / 翻转 ----
    /// <summary>顺时针旋转角度，仅接受 0 / 90 / 180 / 270。</summary>
    public int Rotation { get; set; }
    /// <summary>水平翻转（左右镜像）。</summary>
    public bool FlipH { get; set; }
    /// <summary>垂直翻转（上下镜像）。</summary>
    public bool FlipV { get; set; }

    // ---- 调色（仅 Tone 操作使用）----
    /// <summary>统一套用的调色参数；<see cref="LookSettings"/> 默认即"不动"。</summary>
    public LookSettings Look { get; set; } = default;

    // ---- 改尺寸 ----
    /// <summary>等比缩到"长边 = 此值"（&gt;0 时生效，忽略显式宽高）。</summary>
    public int LongEdge { get; set; }
    /// <summary>显式宽度（与 <see cref="Height"/> 同时 &gt;0 时按给的来，不保持比例）。</summary>
    public int Width { get; set; }
    /// <summary>显式高度。</summary>
    public int Height { get; set; }

    // ---- 格式转换 ----
    /// <summary>目标扩展名（含点），如 <c>.png</c> / <c>.jpg</c> / <c>.webp</c>。</summary>
    public string TargetExtension { get; set; } = ".png";

    // ---- 重命名 ----
    /// <summary>重命名模式，支持 <c>{n}</c>(序号) / <c>{n:3}</c>(补零) / <c>{name}</c>(原名) / <c>{ext}</c>(原扩展名无点)。</summary>
    public string RenamePattern { get; set; } = "{n}_{name}";

    // ---- 输出 ----
    /// <summary>输出目录；null = 与原图同目录。</summary>
    public string? OutputDirectory { get; set; }
    /// <summary>输出文件名加的后缀（不改名时用来区分原图）。默认 <c>_batch</c>。</summary>
    public string Suffix { get; set; } = "_batch";
    /// <summary>是否允许覆盖已存在的输出文件。false 时遇到重名自动加" (n)"避让。</summary>
    public bool Overwrite { get; set; }
}

/// <summary>每张图处理完回传的进度。</summary>
public sealed class BatchProgress
{
    /// <summary>已完成数量（含失败）。</summary>
    public int Done { get; set; }
    /// <summary>总数量。</summary>
    public int Total { get; set; }
    /// <summary>当前正在处理的文件名（不含路径）。</summary>
    public string CurrentFile { get; set; } = "";
    /// <summary>上一张是否失败。</summary>
    public bool LastWasError { get; set; }
    /// <summary>上一张失败的原因（成功则为 null）。</summary>
    public string? LastError { get; set; }
}

/// <summary>整次批处理的结果汇总。</summary>
public sealed class BatchResult
{
    /// <summary>总张数。</summary>
    public int Total { get; set; }
    /// <summary>成功张数。</summary>
    public int Succeeded { get; set; }
    /// <summary>失败张数。</summary>
    public int Failed { get; set; }
    /// <summary>失败的文件路径列表。</summary>
    public List<string> FailedFiles { get; } = new();
}

public static class BatchEditService
{
    /// <summary>
    /// 对一组路径跑批量任务。
    ///
    /// 每张图在后台线程处理（Magick 是 CPU 密集）；<paramref name="ct"/> 在两张图之间检查，
    /// 触发取消时抛出 <see cref="OperationCanceledException"/>。单张异常被吞掉并记入结果，
    /// 不会中断整个任务。
    /// </summary>
    public static async Task<BatchResult> RunAsync(
        IReadOnlyList<string> paths, BatchOptions opt,
        IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var result = new BatchResult { Total = paths.Count };

        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string src = paths[i];
            string name = Path.GetFileName(src);
            string? err = null;

            try
            {
                await Task.Run(() => ProcessOne(src, i, opt), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { err = ex.Message; }

            if (err is null) result.Succeeded++;
            else { result.Failed++; result.FailedFiles.Add(src); }

            progress?.Report(new BatchProgress
            {
                Done = i + 1,
                Total = paths.Count,
                CurrentFile = name,
                LastWasError = err is not null,
                LastError = err,
            });
        }

        return result;
    }

    // ===== 单张处理：按操作分派 =====

    private static void ProcessOne(string src, int index, BatchOptions opt)
    {
        if (!File.Exists(src)) throw new FileNotFoundException("源文件不存在", src);

        switch (opt.Operation)
        {
            case BatchOperation.Rename:
                RenameOne(src, index, opt);
                return;
            case BatchOperation.Convert:
                ConvertOne(src, opt);
                return;
            case BatchOperation.Resize:
                ResizeOne(src, opt);
                return;
            default: // RotateFlip / Tone —— 都走"编辑渲染"这条路
                EditOne(src, opt);
                return;
        }
    }

    // ===== 旋转 / 翻转 / 调色：解码 → EditRenderer → 重编码 =====

    private static void EditOne(string src, BatchOptions opt)
    {
        byte[] bytes = File.ReadAllBytes(src);
        byte[]? px = ImageEditService.LoadPixels(bytes, out int w, out int h);
        if (px is null || w <= 0 || h <= 0)
            throw new InvalidOperationException("无法解码像素");

        var edits = BuildEdits(opt);
        var bmp = new DecodedBitmap
        {
            Pixels = px,
            PixelWidth = w,
            PixelHeight = h,
            // Magick 解出来的是 Straight（非预乘）像素，
            // EditRenderer 只在"预乘 + 含半透明"时才走反预乘分支，这里给 false 正好。
            Premultiplied = false,
            DecoderName = "BatchEdit",
        };

        DecodedBitmap edited = EditRenderer.Apply(bmp, edits);
        if (ReferenceEquals(edited.Pixels, bmp.Pixels) && edits.IsIdentity)
        {
            // 等于没改（比如 Tone 但调色全 0）：原样拷贝一份到输出，保持行为一致
            if (opt.Operation == BatchOperation.Tone && edits.IsIdentity)
                throw new InvalidOperationException("调色参数为空，跳过");
        }

        byte[] outBytes = ImageEditService.FromPixels(edited.Pixels, edited.PixelWidth, edited.PixelHeight);
        if (outBytes.Length == 0) throw new InvalidOperationException("重编码失败");

        string dest = ComposeDest(src, opt, Path.GetExtension(src));
        ImageEditService.Save(outBytes, dest);
    }

    private static PhotoEdits BuildEdits(BatchOptions opt)
    {
        var e = new PhotoEdits
        {
            Rotation = opt.Rotation,
            FlipH = opt.FlipH,
            FlipV = opt.FlipV,
        };
        if (opt.Operation == BatchOperation.Tone) e.Look = opt.Look;
        return e.Normalized();
    }

    // ===== 改尺寸 =====

    private static void ResizeOne(string src, BatchOptions opt)
    {
        byte[] bytes = File.ReadAllBytes(src);
        (int w, int h) = ImageEditService.SizeOf(bytes);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("无法读取尺寸");

        int tw, th;
        if (opt.LongEdge > 0)
        {
            // 等比：只给长边，短边按比例算（Resize 收到一边为 0 时自己按比例）
            if (w >= h) { tw = opt.LongEdge; th = 0; }
            else { tw = 0; th = opt.LongEdge; }
        }
        else
        {
            tw = opt.Width;
            th = opt.Height;
            if (tw <= 0 && th <= 0) throw new InvalidOperationException("未指定目标尺寸");
        }

        byte[] resized = ImageEditService.Resize(bytes, tw, th);
        if (resized.Length == 0) throw new InvalidOperationException("缩放失败");

        string dest = ComposeDest(src, opt, Path.GetExtension(src));
        ImageEditService.Save(resized, dest);
    }

    // ===== 格式转换 =====

    private static void ConvertOne(string src, BatchOptions opt)
    {
        byte[] bytes = File.ReadAllBytes(src);
        string dest = ComposeDest(src, opt, opt.TargetExtension);
        if (!ImageEditService.Save(bytes, dest))
            throw new InvalidOperationException("转换失败");
    }

    // ===== 重命名（纯文件系统移动，不动像素）=====

    private static void RenameOne(string src, int index, BatchOptions opt)
    {
        string dir = opt.OutputDirectory ?? Path.GetDirectoryName(src) ?? ".";
        string base0 = Path.GetFileNameWithoutExtension(src);
        string ext = Path.GetExtension(src);
        string newBase = Sanitize(FormatRename(opt.RenamePattern, index, base0, ext));
        if (string.IsNullOrWhiteSpace(newBase)) newBase = base0;

        string dest = UniquePath(Path.Combine(dir, newBase + ext), opt.Overwrite);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新文件名与原名相同");

        File.Move(src, dest);
    }

    // ===== 输出路径拼接 =====

    /// <summary>
    /// 算出输出文件的完整路径。
    /// 重命名操作不走这里（它自己决定新名），其余操作：
    ///   目录 = OutputDirectory ?? 原目录
    ///   基名 = 原名 + Suffix（重命名模式除外）
    ///   扩展名 = 调用方给的 <paramref name="ext"/>
    /// </summary>
    private static string ComposeDest(string src, BatchOptions opt, string ext)
    {
        string dir = opt.OutputDirectory ?? Path.GetDirectoryName(src) ?? ".";
        string base0 = Path.GetFileNameWithoutExtension(src);
        string baseName = base0 + (opt.Suffix ?? "");
        string dest = Path.Combine(dir, baseName + ext);
        return UniquePath(dest, opt.Overwrite);
    }

    /// <summary>
    /// 处理输出重名：覆盖模式直接返回（Magick 的 Write 会覆盖）；
    /// 否则自动加 " (n)" 直到不冲突，绝不悄悄吃掉用户已有的文件。
    /// </summary>
    private static string UniquePath(string dest, bool overwrite)
    {
        if (overwrite) return dest;

        if (!File.Exists(dest)) return dest;

        string dir = Path.GetDirectoryName(dest) ?? ".";
        string name = Path.GetFileNameWithoutExtension(dest);
        string ext = Path.GetExtension(dest);

        for (int k = 1; ; k++)
        {
            string cand = Path.Combine(dir, $"{name} ({k}){ext}");
            if (!File.Exists(cand)) return cand;
        }
    }

    // ===== 重命名模式格式化 =====

    private static string FormatRename(string pattern, int index, string baseName, string ext)
    {
        // {n} 用 1 基序号（第 1 张 = 1），更符合用户对"第几张"的直觉
        int n = index + 1;
        var sb = new StringBuilder();
        int i = 0;

        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '{')
            {
                int e = pattern.IndexOf('}', i);
                if (e < 0) { sb.Append(pattern, i, pattern.Length - i); break; }
                string tok = pattern.Substring(i + 1, e - i - 1);
                sb.Append(ExpandToken(tok, n, baseName, ext));
                i = e + 1;
            }
            else { sb.Append(c); i++; }
        }

        return sb.ToString();
    }

    private static string ExpandToken(string tok, int n, string baseName, string ext)
    {
        if (tok == "name") return baseName;
        if (tok == "ext") return ext.TrimStart('.');
        if (tok.StartsWith("n", StringComparison.Ordinal))
        {
            // {n:3} → 补零到 3 位；{n} → 不补零
            int width = 0;
            if (tok.Length > 1 && tok[1] == ':') int.TryParse(tok.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
            return width > 0 ? n.ToString("D" + width, CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
        }
        return ""; // 不认识的令牌原样丢弃，避免输出怪字符
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(bad.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }
}
