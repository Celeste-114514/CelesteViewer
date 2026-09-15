using System;
using System.Collections.Generic;
using System.IO;

namespace CelesteGallery.Services;

/// <summary>
/// 一个"社交软件缓存目录" —— 可以被收进图库、按来源分类的那种。
/// </summary>
public sealed class SocialCacheRoot
{
    /// <summary>
    /// 来源名，也是"按来源"分组时显示在左栏的文字（微信 / QQ / 企业微信）。
    /// 和 <see cref="SocialCacheDetector.SourceOf"/> 返回的是同一套字符串，
    /// 这样"探测到的目录"和"每张图算出来的来源"永远对得上。
    /// </summary>
    public string App { get; init; } = string.Empty;

    /// <summary>要加进图库的那个目录（已经挑好了，不是整个软件的数据目录）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>这个目录装的是什么，给人看的（"聊天图 / 表情 …"）。</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// 识别本机的社交软件缓存目录，并判断任意一张图属于哪个来源。
///
/// 为什么值得做：微信 / QQ / 企业微信 会把聊天里收发过的图缓存在本地固定位置，
/// 数量很大（本机实测 QQ 就二十多万张），但它们在资源管理器里几乎没法用 ——
/// 文件名是哈希、按账号和日期散在好几层目录里、而且一张 EXIF 都没有。
/// 收进图库之后，就能用"按日期""按来源"当成一个正常相册来浏览。
///
/// 铁律：**只读、只探测**。绝不移动、改名、删除任何缓存文件 ——
/// 那是别人软件的地盘，我们只是借来看。删了会影响微信/QQ 正常显示图片。
///
/// 为什么不做 .dat 解密：微信/QQ 的聊天图有一部分是加密的 .dat。
/// 解密属于逆向，风险和工作量都大，本轮先只收"本来就能读的图"。
/// </summary>
public static class SocialCacheDetector
{
    // 来源名。写成常量而不是到处散字符串，避免"微信"和"wechat"两套写法对不上。
    public const string WeChat = "微信";
    public const string QQ = "QQ";
    public const string WeCom = "企业微信";

    /// <summary>
    /// 探测本机存在的社交缓存目录。可能返回多条（比如 QQ 登过两个账号）。
    ///
    /// 目录不存在就跳过 —— 没装这个软件的人不该看到关于它的任何提示。
    /// 全部探测都是"看目录在不在"，不递归数文件，所以很快，放后台线程调一下即可。
    /// </summary>
    public static List<SocialCacheRoot> Detect()
    {
        var list = new List<SocialCacheRoot>();
        string docs = DocumentsPath();
        if (docs.Length == 0) return list;

        // ---- QQ（NT 版）：Documents\Tencent Files\<账号>\nt_qq\nt_data\Pic ----
        // 注意只取到 **Pic**，不要整个 nt_data：同级的 Emoji 是表情包（本机三万七千多张），
        // 收进来会把真正想看的聊天图淹掉。Pic 下面还按月分好了目录（Pic\2024-08），
        // 正好让"按日期"分类直接能用。
        foreach (string acct in SafeDirs(Path.Combine(docs, "Tencent Files")))
        {
            string pic = Path.Combine(acct, "nt_qq", "nt_data", "Pic");
            if (Directory.Exists(pic))
                list.Add(new SocialCacheRoot
                {
                    App = QQ,
                    Path = pic,
                    Detail = "聊天图片（不含表情包）",
                });
        }

        // ---- 微信 4.0：Documents\xwechat_files\<账号>_<后缀> ----
        foreach (string acct in SafeDirs(Path.Combine(docs, "xwechat_files")))
        {
            if (IsNonAccountDir(Path.GetFileName(acct))) continue;
            list.Add(new SocialCacheRoot
            {
                App = WeChat,
                Path = acct,
                Detail = "聊天图 / 朋友圈缓存",
            });
        }

        // ---- 微信 3.x（旧版）：Documents\WeChat Files\<账号> ----
        foreach (string acct in SafeDirs(Path.Combine(docs, "WeChat Files")))
        {
            if (IsNonAccountDir(Path.GetFileName(acct))) continue;
            list.Add(new SocialCacheRoot
            {
                App = WeChat,
                Path = acct,
                Detail = "聊天图 / 朋友圈缓存（旧版微信）",
            });
        }

        // ---- 企业微信：Documents\WXWork\<账号>\Cache\Image ----
        // 只取 Cache\Image，**不要**把整个账号目录收进来：
        // 同级的 Avator 是头像（本机三万四千多张），收进来只会把有用的图淹掉。
        foreach (string acct in SafeDirs(Path.Combine(docs, "WXWork")))
        {
            string img = Path.Combine(acct, "Cache", "Image");
            if (Directory.Exists(img))
                list.Add(new SocialCacheRoot
                {
                    App = WeCom,
                    Path = img,
                    Detail = "收到的图片",
                });
        }

        return list;
    }

    /// <summary>
    /// 判断一个路径属于哪个来源；不属于任何社交缓存返回 null。
    ///
    /// 判据是路径里有没有那个软件的标志目录名。之所以用**逐段比较**而不是
    /// <c>Contains("Tencent Files")</c>：后者会被 "D:\我的备份\Tencent Files 备份"
    /// 这种同名文件夹骗到，而按路径段比就不会。
    ///
    /// 这个方法会被扫描时逐张调用（二十多万次），所以刻意做得极轻：
    /// 只切字符串，不碰文件系统、不分配大对象。
    /// </summary>
    public static string? SourceOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        foreach (string seg in Segments(path!))
        {
            if (IsSourceMarker(seg)) return AppOfMarker(seg);
        }

        return null;
    }

    /// <summary>
    /// 取出路径里的"账号"那一段，用来区分同一个软件的多个账号目录
    /// （本机就登过两个 QQ、两个企业微信）。
    ///
    /// 规则：标志目录名的**下一段**就是账号 ——
    /// <c>…\Tencent Files\178237225\nt_qq\nt_data\Pic</c> → <c>178237225</c>；
    /// <c>…\xwechat_files\wxid_ea0e…</c> → <c>wxid_ea0e…</c>。
    ///
    /// 拿不到就返回 null，调用方自己退回用目录名。
    /// </summary>
    public static string? AccountOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        string? prev = null;
        foreach (string seg in Segments(path!))
        {
            if (prev is not null && IsSourceMarker(prev)) return seg;
            prev = seg;
        }

        return null;
    }

    /// <summary>这个目录名是不是某个软件用来标记"我的数据在这"的那一层。</summary>
    private static bool IsSourceMarker(string seg)
        => seg.Equals("Tencent Files", StringComparison.OrdinalIgnoreCase)
        || seg.Equals("xwechat_files", StringComparison.OrdinalIgnoreCase)
        || seg.Equals("WeChat Files", StringComparison.OrdinalIgnoreCase)
        || seg.Equals("WXWork", StringComparison.OrdinalIgnoreCase);

    private static string AppOfMarker(string seg)
    {
        if (seg.Equals("Tencent Files", StringComparison.OrdinalIgnoreCase)) return QQ;
        if (seg.Equals("WXWork", StringComparison.OrdinalIgnoreCase)) return WeCom;
        return WeChat;
    }

    /// <summary>
    /// 判断一张图是不是社交缓存里的"噪声图" —— 表情包、头像、界面图标这类。
    /// 它们数量巨大（本机 QQ 表情包三万七千多张）、跟"照片"完全不搭，收进图库只会把真图淹掉。
    ///
    /// **只对社交缓存路径生效**：先确认路径确实在某个社交缓存树里，再看有没有噪声目录段。
    /// 所以你自己建一个叫 Emoji 的文件夹放照片，照样正常进库，不会被误伤。
    ///
    /// 正常路径上其实用不着它 —— 探测时就把图库指向干净目录了（QQ 指到 nt_data\Pic）。
    /// 这个函数是保险：万一你手动把整个 nt_data 或 WXWork 加进图库，表情包和头像也进不来。
    /// </summary>
    public static bool IsNoise(string? path)
    {
        if (SourceOf(path) is null) return false;

        foreach (string seg in Segments(path!))
        {
            // QQ：表情包 / 表情预览图 / 在线状态图标 / 会员图标
            if (seg.Equals("Emoji", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("EmojiCover", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("OnlineStatus", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("PrivilegeIcon", StringComparison.OrdinalIgnoreCase)) return true;
            // 头像。企业微信那边官方目录名就拼成 "Avator"（少个 a），按原样匹配
            if (seg.Equals("Avator", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("Avatar", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("AvatarCache", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ===== 内部小工具 =====

    /// <summary>把路径按两种分隔符切开，去掉空段，给逐段比较用。</summary>
    private static IEnumerable<string> Segments(string path)
    {
        // 手写切分而不是 Split(new[]{'\\','/'})：后者会一次性造出一个数组，
        // 这个方法要跑二十多万次，能省就省。
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            bool end = i == path.Length;
            if (!end && path[i] != '\\' && path[i] != '/') continue;

            if (i > start) yield return path.Substring(start, i - start);
            start = i + 1;
        }
    }

    /// <summary>列子目录；盘不在、没权限一律当"没有"，绝不抛。</summary>
    private static string[] SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 这些名字不是"某个账号的缓存目录"，是软件自己放公共数据的地方，跳过。
    /// （all_users / Backup / All Users / Applet）
    /// </summary>
    private static bool IsNonAccountDir(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        return name.Equals("all_users", StringComparison.OrdinalIgnoreCase)
            || name.Equals("All Users", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Backup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Applet", StringComparison.OrdinalIgnoreCase);
    }

    private static string DocumentsPath()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        catch { return string.Empty; }
    }
}
