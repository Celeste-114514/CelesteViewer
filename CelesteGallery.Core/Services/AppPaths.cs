using System;
using System.IO;

namespace CelesteGallery.Services;

/// <summary>
/// 应用叫什么名字、用户数据放在哪儿 —— 全项目**只在这一个文件里**写死。
///
/// 为什么要把这件事单独拎出来：以前 5 个类各写了一遍
/// <c>Path.Combine(LocalApplicationData, "本应用名")</c>，
/// 于是"改个应用名"要满仓库找字符串，漏一处就变成"数据劈成两个目录"——
/// 索引库在一个、缩略图缓存在另一个，排查起来极其费劲。
///
/// 改名的坑（这次就是从 CelesteViewer 改成 CelesteGallery）：
/// 数据目录里躺着 <c>library.db</c>，用户的**评分 / 收藏 / 标签全在里面**。
/// 直接把目录名换掉而不搬数据 = 静默清空用户资料，用户还不知道发生了什么。
/// 所以老目录还在、新目录还没有时，这里会**原地搬过去**。
/// </summary>
public static class AppPaths
{
    /// <summary>应用名。显示用的字面量也从这里取，别再手打。</summary>
    public const string AppName = "CelesteGallery";

    /// <summary>
    /// 改名前叫什么。老数据目录还挂着这个名，首次启动要搬。
    ///
    /// ⚠ **这一行永远不要跟着全仓库改名一起替换掉！**
    /// 2026-09-16 从 CelesteViewer 改名叫 CelesteGallery 时就踩过：
    /// 这条常量也被 `sed 's/CelesteViewer/CelesteGallery/g'` 扫了一遍，
    /// 于是"老目录"和"新目录"算出同一个路径 → 搬家分支永远不成立 →
    /// 程序在新目录里从零建库，用户原来那份 library.db（评分、收藏、标签）
    /// 就成了没人认识的孤儿。**改名的 sed 必须排除这一行。**
    /// 回归测试里有一条专门盯着"这两个常量不能相等"。
    /// </summary>
    public const string LegacyAppName = "CelesteViewer";

    private static readonly Lazy<string> LazyDataDir = new(ResolveDataDir);

    /// <summary>
    /// 用户数据目录。library.db / library.txt / settings.txt / ThumbCache / startup.log
    /// 全在这儿。首次访问时确定，之后不会变。
    /// </summary>
    public static string DataDir => LazyDataDir.Value;

    /// <summary>这次启动是不是刚从老名字的目录搬过来（给日志用）。</summary>
    public static bool MigratedFromLegacy { get; private set; }

    /// <summary>数据目录下的一个文件路径。</summary>
    public static string File(string name) => System.IO.Path.Combine(DataDir, name);

    /// <summary>数据目录下的一个子目录路径（不负责创建）。</summary>
    public static string Dir(string name) => System.IO.Path.Combine(DataDir, name);

    private static string ResolveDataDir()
    {
        string root;
        try { root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        catch { root = string.Empty; }
        if (string.IsNullOrEmpty(root)) root = System.IO.Path.GetTempPath();

        string current = System.IO.Path.Combine(root, AppName);
        string legacy = System.IO.Path.Combine(root, LegacyAppName);

        if (!Directory.Exists(current) && Directory.Exists(legacy))
        {
            try
            {
                // Directory.Move 是原子的：要么整体搬过去，要么原地不动，
                // 不会出现"搬到一半"的半残状态。
                Directory.Move(legacy, current);
                MigratedFromLegacy = true;
            }
            catch
            {
                // 搬不动（目录被别的进程占着、权限不足……）不是致命问题，
                // 下面会退回继续用老目录。**绝不能**因为搬不动就把老目录当不存在 ——
                // 那等于用户一改名就丢了全部评分和收藏。
            }
        }

        string pick = Directory.Exists(current) ? current
                    : Directory.Exists(legacy) ? legacy
                    : current;

        try { Directory.CreateDirectory(pick); } catch { }
        return pick;
    }
}
