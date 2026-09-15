using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace CelesteGallery.Helpers;

/// <summary>
/// 应用图标的三处用途，都从这里取路径，别在别处拼字符串：
///
///   1. **exe 图标**（资源管理器里看到的那个）—— 由 csproj 的 ApplicationIcon 决定，
///      编译期就写进 exe 了，运行时改不了。
///   2. **窗口图标**（标题栏左上角 / 任务栏 / Alt+Tab）—— 运行时用 AppWindow.SetIcon
///      指定 Assets\AppIcon.ico。非打包程序不做这一步的话，任务栏上就是个白板图标。
///   3. **界面里画的小图标**（标题栏那一行文字前面的小图）—— Assets\TitleMark.png。
///
/// 为什么 2 和 3 用**两套图**（2026-09-16 改的）：
/// AppIcon.png 是"三张照片叠影 + 青蓝渐变"，给 256px 看的，层次很足；
/// 但标题栏上只有 **16 逻辑像素**，三张半透明的卡缩下去就糊成一坨浅蓝，
/// 用户的原话是"想要一个单独的相册，不要蓝色的元素"。
/// 所以标题栏改用 TitleMark.png：单张相框、纯白单色、形状为 16px 优化过。
/// 两张图同一个来源：icon_build\make_gallery_icon.py / make_title_mark.py。
/// </summary>
public static class AppIcon
{
    private static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>窗口图标用的 ico（多尺寸，含 16/24/32/48/64/128/256）。</summary>
    public static string IcoPath => Path.Combine(AssetsDir, "AppIcon.ico");

    /// <summary>大尺寸用的主图标 png（256px，带渐变和叠影）。</summary>
    public static string PngPath => Path.Combine(AssetsDir, "AppIcon.png");

    /// <summary>标题栏小图标（单张相框、纯白、16px 优化）。</summary>
    public static string TitleMarkPath => Path.Combine(AssetsDir, "TitleMark.png");

    /// <summary>
    /// 载入界面里那张小图。优先用标题栏专用的 TitleMark.png；
    /// 万一那个文件没被复制到输出目录（比如漏加了 csproj 的 Content），
    /// 退回主图标 —— 难看总比空白强。
    /// </summary>
    public static Task<ImageSource?> LoadTitleMarkAsync()
        => LoadAsync(File.Exists(TitleMarkPath) ? TitleMarkPath : PngPath);

    /// <summary>
    /// 载入大尺寸主图标（关于窗口、联系页之类要看细节的地方用）。
    /// 失败就返回 null —— 少个图标而已，绝不能因为它把整个程序拖崩。
    ///
    /// 这里刻意走 **文件流 + SetSourceAsync**，而不是 `new BitmapImage(new Uri(路径))`：
    /// 后者是非打包模式下最容易踩的那个坑 —— BitmapImage 是**惰性**加载的，
    /// 构造时不会报错，等到真要画了才去取文件，那时又拿不到正确的基路径，
    /// 结果是"构造函数返回了对象，但标题栏上什么都没有"，还查不到任何异常。
    /// 用流则是在这里就把字节读进来了，成功失败当场就知道。
    /// </summary>
    public static Task<ImageSource?> LoadPngAsync() => LoadAsync(PngPath);

    private static async Task<ImageSource?> LoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var bitmap = new BitmapImage();

            using (var file = File.OpenRead(path))
            using (var stream = file.AsRandomAccessStream())
            {
                await bitmap.SetSourceAsync(stream);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write($"AppIcon: 载入 {Path.GetFileName(path)} 失败", ex);
            return null;
        }
    }

    /// <summary>给窗口设图标（标题栏 + 任务栏 + Alt+Tab）。</summary>
    public static void ApplyToWindow(Microsoft.UI.Windowing.AppWindow? window)
    {
        if (window is null) return;

        try
        {
            string path = IcoPath;
            if (File.Exists(path)) window.SetIcon(path);
            else Services.StartupLog.Write($"AppIcon: 找不到 {path}，窗口图标保持默认");
        }
        catch (Exception ex)
        {
            // 设图标失败不影响使用，退化成默认图标
            Services.StartupLog.Write("AppIcon: 设置窗口图标失败", ex);
        }
    }
}
