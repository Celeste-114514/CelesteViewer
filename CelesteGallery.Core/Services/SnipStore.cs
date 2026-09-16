using System;
using System.IO;

namespace CelesteGallery.Services;

/// <summary>
/// 本程序自己截的图放哪儿 —— 全项目**只在这一个文件里**拼这个路径。
///
/// 放在"图片"文件夹下，而不是我们自己的数据目录（%LOCALAPPDATA%）：
/// 用户截完图之后多半想拿它干点什么（发给别人、拖进别的软件、用资源管理器翻），
/// 藏在 AppData 里等于每次都要先问一遍"在哪儿"。
/// 图片库是大家本来就熟悉的地方，而且它自动进 Windows 的"图片"库，一举两得。
///
/// 左栏那条固定的「截图」条目（见 BrowserPage.BuildFolderTree）指的也是这里，
/// 两个地方共用 <see cref="RootPath"/>，不会出现"存的是一处、看的是另一处"。
/// </summary>
public static class SnipStore
{
    /// <summary>
    /// 文件夹名。带程序名是故意的 —— 一个人图片文件夹里可能有好几个软件建的目录，
    /// 只叫"截图"根本认不出是谁建的。
    /// </summary>
    public const string FolderName = "CelesteGallery 截图";

    private static readonly object Sync = new();

    private static readonly Lazy<string> LazyRoot = new(Resolve);

    /// <summary>
    /// 存好一张截图之后发一下，参数是写好的完整路径。
    ///
    /// 谁在听：缩略图墙。用户截完图回到主界面，如果左栏还没出现「截图」那条，
    /// 或者正停在截图文件夹里，就得靠这个通知去补一下 ——
    /// 否则会看到"我刚存的图呢？"（其实在盘上，只是墙没刷新）。
    ///
    /// 刻意用静态事件而不是让编辑器直接持有 BrowserPage：
    /// 编辑器是独立窗口，不该知道主界面长什么样；而且托盘/热键截的图
    /// 走的是另一条入口，那里根本拿不到主界面对象。
    /// </summary>
    public static event Action<string>? Saved;

    /// <summary>截图目录的完整路径。这个值算出来就不会变。</summary>
    public static string RootPath => LazyRoot.Value;

    /// <summary>
    /// 确保目录存在再返回路径。首次截图、以及画左栏那一条之前都会先叫一下它 ——
    /// 目录不存在时 <c>Directory.EnumerateFiles</c> 会抛，左栏点进去也会没反应。
    /// 建不出来（盘只读、权限不足）不是致命错误：调用方拿到的仍是一个合法路径，
    /// 保存时自己会失败并报错。
    /// </summary>
    public static string EnsureRoot()
    {
        try { Directory.CreateDirectory(RootPath); } catch { }
        return RootPath;
    }

    /// <summary>这个路径是不是截图目录本身（用来在左栏里避免出现两条重复的条目）。</summary>
    public static bool IsRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(path!),
            Path.TrimEndingDirectorySeparator(RootPath),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 存一张截图，返回写好的完整路径；失败返回 null。
    ///
    /// 文件名用 <c>截图_20260916_134512.png</c>：时间排在中间，在资源管理器里
    /// 按名称排序就等于按时间排序，翻起来最顺手。
    /// 同一秒里连截两张（手快、或者自动化脚本）会撞名，撞了就往后缀 _2、_3……
    /// 绝不覆盖 —— 覆盖掉的那张可能已经是用户唯一的一份。
    /// </summary>
    public static string? Save(byte[] png)
    {
        if (png is null || png.Length == 0) return null;

        lock (Sync)
        {
            try
            {
                EnsureRoot();

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(RootPath, $"截图_{stamp}.png");

                for (int i = 2; File.Exists(path) && i < 1000; i++)
                    path = Path.Combine(RootPath, $"截图_{stamp}_{i}.png");

                if (!ImageEditService.Save(png, path)) return null;

                // 通知放在锁里发是安全的：订阅方（缩略图墙）只做了
                // "把这个活丢回 UI 线程"，不会反过来再调回本类，
                // 不存在自己等自己（重入锁）的死局。
                try { Saved?.Invoke(path); } catch { }

                return path;
            }
            catch (Exception ex)
            {
                StartupLog.Write("SnipStore: 保存截图失败", ex);
                return null;
            }
        }
    }

    private static string Resolve()
    {
        string root = string.Empty;
        try { root = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); }
        catch { }

        // 极端情况下（配置文件损坏、被重定向到不存在的盘）拿不到"图片"路径，
        // 退回自己的数据目录 —— 至少图不会丢，只是位置没那么顺手。
        if (string.IsNullOrEmpty(root)) return AppPaths.Dir(FolderName);

        return Path.Combine(root, FolderName);
    }
}
