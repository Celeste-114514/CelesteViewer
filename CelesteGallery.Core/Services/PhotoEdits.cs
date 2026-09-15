using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CelesteGallery.Services;

/// <summary>
/// 一张图的**非破坏性编辑参数**（路线图第 6 步）。
///
/// 核心约定：**原图文件一个字节都不动**。用户做的旋转、翻转、裁剪、调色
/// 全部记在这里，显示时按参数实时把像素算出来，需要成文件时才在"另存为 /
/// 导出"里烘焙进去。这样：
///   · 后悔随时能退（把参数清空就回到原样，无损）
///   · 不占磁盘（不产生一堆"改过一次"的副本）
///   · 元数据、拍摄时间、原图质量都不会因为"编辑一下"被重编码糟蹋
///
/// 参数存在索引库（<c>library.db</c> 的 <c>Media.Edits</c> 列）里，
/// 跟着评分 / 收藏 / 标签走的是同一套逻辑：重扫磁盘不该丢用户的操作。
///
/// **应用的先后顺序是固定的**（<see cref="EditRenderer.Apply"/> 里实现）：
///   1. 旋转 → 2. 翻转 → 3. 裁剪 → 4. 调色
/// 裁剪用的是**旋转翻转之后**那张图的归一化坐标（0~1），
/// 所以要紧跟着几何变换；调色放最后，这样它作用在"最终取景"上。
/// </summary>
public sealed class PhotoEdits
{
    /// <summary>顺时针旋转角度，只允许 0 / 90 / 180 / 270（其它值会被规整）。</summary>
    public int Rotation { get; set; }

    /// <summary>水平翻转（左右镜像）。</summary>
    public bool FlipH { get; set; }

    /// <summary>垂直翻转（上下镜像）。</summary>
    public bool FlipV { get; set; }

    // ---- 裁剪：归一化到 0~1，相对"旋转翻转之后"的图 ----
    //
    // 为什么不用像素：同一张图在缩略图上裁和在 8000px 原图上裁，
    // 存像素值只要两边尺寸对不上就完全错位；归一化之后跟尺寸彻底解耦。
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropW { get; set; } = 1.0;
    public double CropH { get; set; } = 1.0;

    // ---- 调色：全部是 -100 ~ +100 的"相对量"，0 = 不动 ----
    //
    // 用相对量而不是绝对像素值，好处是"忘记改过"不会毁图：
    // 0 就是原样，任何一项单独调都不会把别的项带偏。
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }

    /// <summary>色温：正数偏暖（加红减蓝），负数偏冷。</summary>
    public double Temperature { get; set; }

    /// <summary>什么都没改。</summary>
    public bool IsIdentity => !HasGeometry && !HasCrop && !HasTone;

    /// <summary>有旋转或翻转。</summary>
    public bool HasGeometry => Rotation != 0 || FlipH || FlipV;

    /// <summary>
    /// 裁过。用 1e-4 的容差判断，不是"全等"：
    /// 界面上拖裁剪框很难拖出正好 1.0，差个万分之一不该算"裁过"。
    /// </summary>
    public bool HasCrop
        => CropX > 1e-4 || CropY > 1e-4 || CropW < 1.0 - 1e-4 || CropH < 1.0 - 1e-4;

    /// <summary>调过色。</summary>
    public bool HasTone
        => Math.Abs(Brightness) > 0.01 || Math.Abs(Contrast) > 0.01
        || Math.Abs(Saturation) > 0.01 || Math.Abs(Temperature) > 0.01;

    /// <summary>
    /// 规范化：角度归到 0/90/180/270，裁剪框夹进图内，调色夹到 ±100。
    ///
    /// 一定要在存库**之前**调一次 —— 让库里的参数永远是"合法且唯一"的形态，
    /// 这样 <see cref="Serialize"/> 出来的字符串可以直接当缓存 key 用，
    /// 不会出现 "r=450" 和 "r=90" 这种同一效果两个 key 的情况。
    /// </summary>
    public PhotoEdits Normalized()
    {
        var e = new PhotoEdits
        {
            Rotation = ((Rotation % 360) + 360) % 360,
            FlipH = FlipH,
            FlipV = FlipV,
            Brightness = Clamp100(Brightness),
            Contrast = Clamp100(Contrast),
            Saturation = Clamp100(Saturation),
            Temperature = Clamp100(Temperature),
        };

        // 旋转 90/270 时归一化坐标的宽高含义会互换 —— 这个换算由调用方负责
        // （它才知道当前图是转之前还是转之后量的），这里只保证落在 0~1 内。
        e.CropX = Math.Clamp(CropX, 0, 1);
        e.CropY = Math.Clamp(CropY, 0, 1);
        e.CropW = Math.Clamp(CropW, 0.01, 1);
        e.CropH = Math.Clamp(CropH, 0.01, 1);

        // 右边/下边越界就往回缩，缩不掉就把起点也拉回来
        if (e.CropX + e.CropW > 1) e.CropX = Math.Max(0, 1 - e.CropW);
        if (e.CropY + e.CropH > 1) e.CropY = Math.Max(0, 1 - e.CropH);

        // 角度归整之后旋转不再是 90 的倍数以外的值，去掉不可能的组合
        if (e.Rotation % 90 != 0) e.Rotation = 0;

        return e;
    }

    private static double Clamp100(double v)
        => double.IsNaN(v) ? 0 : Math.Clamp(v, -100, 100);

    /// <summary>
    /// 序列化成一行文本，存进 <c>Media.Edits</c>。
    ///
    /// 格式：<c>r=90;fh=1;cx=0.1;cw=0.5;b=10;s=-20</c>（**只写非默认项**）。
    /// 不选 JSON 的理由和 AppSettings 一样：参数就这么几个，
    /// 一眼能读懂、能手改、出问题时用 sqlite 命令行直接看就知道对不对，
    /// 比引一层序列化划算。
    /// </summary>
    public string Serialize()
    {
        PhotoEdits e = Normalized();
        if (e.IsIdentity) return string.Empty;

        var sb = new StringBuilder();
        void Add(string key, string value)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(key).Append('=').Append(value);
        }
        void AddNum(string key, double value)
        {
            if (Math.Abs(value) < 1e-9) return;
            // 固定区域格式：逗号小数点（中文系统下 ToString 可能给出逗号，
            // 那样解析回来会当场崩），并且裁掉多余的 0
            Add(key, value.ToString("0.####", CultureInfo.InvariantCulture));
        }

        if (e.Rotation != 0) Add("r", e.Rotation.ToString(CultureInfo.InvariantCulture));
        if (e.FlipH) Add("fh", "1");
        if (e.FlipV) Add("fv", "1");

        // 裁剪按"四个值整体"写，缺一个都不是完整矩形
        if (e.HasCrop)
        {
            Add("cx", e.CropX.ToString("0.#####", CultureInfo.InvariantCulture));
            Add("cy", e.CropY.ToString("0.#####", CultureInfo.InvariantCulture));
            Add("cw", e.CropW.ToString("0.#####", CultureInfo.InvariantCulture));
            Add("ch", e.CropH.ToString("0.#####", CultureInfo.InvariantCulture));
        }

        AddNum("b", e.Brightness);
        AddNum("c", e.Contrast);
        AddNum("s", e.Saturation);
        AddNum("t", e.Temperature);

        return sb.ToString();
    }

    /// <summary>
    /// 解析 <see cref="Serialize"/> 写出来的字符串。
    ///
    /// 认不出来的段**直接跳过**，不抛异常：这一列的来源可能是手改过的库、
    /// 或者是将来某个版本写的多出来的键。为了几个未知的键让整张图打不开，
    /// 代价完全不成比例。
    /// </summary>
    public static PhotoEdits Parse(string? text)
    {
        var e = new PhotoEdits();
        if (string.IsNullOrWhiteSpace(text)) return e;

        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;

            string key = part[..eq].Trim().ToLowerInvariant();
            string raw = part[(eq + 1)..].Trim();

            switch (key)
            {
                case "r":
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
                        e.Rotation = r;
                    break;

                case "fh":
                    e.FlipH = raw is "1" or "true" or "True";
                    break;

                case "fv":
                    e.FlipV = raw is "1" or "true" or "True";
                    break;

                case "cx": e.CropX = ParseNum(raw, e.CropX); break;
                case "cy": e.CropY = ParseNum(raw, e.CropY); break;
                case "cw": e.CropW = ParseNum(raw, e.CropW); break;
                case "ch": e.CropH = ParseNum(raw, e.CropH); break;

                case "b": e.Brightness = ParseNum(raw, 0); break;
                case "c": e.Contrast = ParseNum(raw, 0); break;
                case "s": e.Saturation = ParseNum(raw, 0); break;
                case "t": e.Temperature = ParseNum(raw, 0); break;
            }
        }

        return e.Normalized();
    }

    private static double ParseNum(string raw, double fallback)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
           ? v : fallback;

    /// <summary>
    /// 短指纹（16 进制，8 位）。给缩略图缓存当 key 用 ——
    /// 文件名的字符合集受限制，不能直接拿 <see cref="Serialize"/> 那串（里面有 <c>;</c> 和 <c>=</c>）。
    ///
    /// 用 FNV-1a：几行代码、分布够均匀，不需要引 <c>System.Security.Cryptography</c>。
    /// 这里**不需要抗碰撞**（只是个缓存 key，撞了最多显示一张旧缩略图，重扫即可），
    /// 所以不用 SHA。
    /// </summary>
    public string Signature()
    {
        string text = Serialize();
        if (text.Length == 0) return "0";

        unchecked
        {
            const uint FnvOffset = 2166136261;
            const uint FnvPrime = 16777619;

            uint hash = FnvOffset;
            foreach (char ch in text)
            {
                hash ^= ch;
                hash *= FnvPrime;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>把另一份参数原样抄过来（界面上的"还原"用得上）。</summary>
    public void CopyFrom(PhotoEdits other)
    {
        Rotation = other.Rotation;
        FlipH = other.FlipH;
        FlipV = other.FlipV;
        CropX = other.CropX;
        CropY = other.CropY;
        CropW = other.CropW;
        CropH = other.CropH;
        Brightness = other.Brightness;
        Contrast = other.Contrast;
        Saturation = other.Saturation;
        Temperature = other.Temperature;
    }

    /// <summary>
    /// 给人看的一句话摘要（界面上"这张图改过什么"或日志里用）。
    /// 没编辑过返回空串。
    /// </summary>
    public string Describe()
    {
        PhotoEdits e = Normalized();
        if (e.IsIdentity) return string.Empty;

        var parts = new List<string>();
        if (e.Rotation != 0) parts.Add($"旋转 {e.Rotation}°");
        if (e.FlipH) parts.Add("左右翻转");
        if (e.FlipV) parts.Add("上下翻转");
        if (e.HasCrop) parts.Add($"裁剪 {e.CropW * 100:0}%×{e.CropH * 100:0}%");
        if (Math.Abs(e.Brightness) > 0.01) parts.Add($"亮度 {e.Brightness:+0;-0}");
        if (Math.Abs(e.Contrast) > 0.01) parts.Add($"对比度 {e.Contrast:+0;-0}");
        if (Math.Abs(e.Saturation) > 0.01) parts.Add($"饱和度 {e.Saturation:+0;-0}");
        if (Math.Abs(e.Temperature) > 0.01) parts.Add($"色温 {e.Temperature:+0;-0}");

        return string.Join(" · ", parts);
    }
}
