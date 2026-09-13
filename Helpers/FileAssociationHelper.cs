using System;
using System.Collections.Generic;
using System.IO;
using CelesteViewer.Services;
using Microsoft.Win32;

namespace CelesteViewer.Helpers;

/// <summary>
/// 文件关联：把图片格式的"双击打开"指向本程序。
///
/// 整套逻辑从 CelesteMusicPlayer 的 FileAssociationHelper 搬过来（那份已经跑过实机），
/// 只把格式清单换成本看图器认识的图片格式。刻意保留它三个关键设计：
///
///   1. **只写 HKCU**（当前用户），不碰 HKLM —— 不需要管理员权限，
///      也不会因为权限不够而静默失败。
///   2. **可关联的格式以 <see cref="ImageFormats"/> 为准**，和"双击能打开哪些格式"
///      共用同一份清单，不会出现"关联了却打不开"。
///   3. **解除时只删默认值指向本程序的键**，绝不把用户指向别的看图器的关联误删。
///
/// 还有一条必须留着的坑：MSIX 打包版 exe 不能做文件关联 ——
/// shell 拉起它时缺包标识，会静默退出（表现就是"双击没反应"）。
/// 所以 <see cref="Register"/> 一进门就先挡一道，见 <see cref="IsPackagedLayout"/>。
/// </summary>
public static class FileAssociationHelper
{
    public const string ProgId = "CelesteViewer.Image";

    /// <summary>
    /// 全部可关联的格式（常见 + Magick 兜底 + 可当一叠图翻的压缩包）。
    /// 和 ImageFormats 对齐，保证"能关联就一定打得开"。
    /// </summary>
    public static readonly string[] AllExtensions = BuildAllExtensions();

    /// <summary>
    /// 默认勾选的一批。
    ///
    /// 为什么不默认全选：.tif 常常已经属于看图/扫描软件，.psd / RAW 属于修图软件，
    /// .zip 更是几乎人人都有专门的压缩工具。默认全勾等于一上来就抢别人的饭碗，
    /// 应该让用户明确点一下。
    /// </summary>
    public static readonly string[] RecommendedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".avif",
    };

    private static string[] BuildAllExtensions()
    {
        var list = new List<string>();
        list.AddRange(ImageFormats.Common);
        list.AddRange(ImageFormats.Extended);
        list.AddRange(ImageFormats.Archives);

        // 去重 + 排好序，界面上的顺序才稳定
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string ext in list)
        {
            if (seen.Add(ext)) result.Add(ext.ToLowerInvariant());
        }

        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }

    /// <summary>读出当前已经关联到本程序的扩展名（勾选框回显用）。</summary>
    public static List<string> GetAssociatedExtensions()
    {
        var result = new List<string>();
        try
        {
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (classes == null) return result;

            foreach (string ext in AllExtensions)
            {
                using RegistryKey? extKey = classes.OpenSubKey(ext);
                if (extKey?.GetValue(string.Empty) as string == ProgId)
                    result.Add(ext);
            }
        }
        catch (Exception caught)
        {
            StartupLog.Write("FileAssociationHelper.GetAssociatedExtensions", caught);
        }

        return result;
    }

    /// <summary>
    /// 只解除这几个扩展名的关联，保留 ProgId 本身和其它扩展名。
    ///
    /// 不能拿 <see cref="Unregister"/> 顶替：那个会把 ProgId 整个删掉，
    /// 连带刚注册好的格式一起失效。
    /// </summary>
    public static void UnregisterExtensions(IEnumerable<string> extensions)
    {
        try
        {
            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            foreach (string raw in extensions)
            {
                string ext = NormalizeExtension(raw);
                try
                {
                    using RegistryKey? extKey = classes.OpenSubKey(ext, writable: true);
                    if (extKey?.GetValue(string.Empty) as string == ProgId)
                        classes.DeleteSubKeyTree(ext, throwOnMissingSubKey: false);
                }
                catch (Exception caught)
                {
                    StartupLog.Write("FileAssociationHelper.UnregisterExtensions(" + ext + ")", caught);
                }
            }
        }
        catch (Exception caught)
        {
            StartupLog.Write("FileAssociationHelper.UnregisterExtensions", caught);
        }
    }

    public static void Register(string executablePath, IEnumerable<string>? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("找不到可执行文件。", executablePath);

        string exe = Path.GetFullPath(executablePath);

        if (IsPackagedLayout(exe))
        {
            throw new InvalidOperationException(
                "这个路径下的是打包（调试）版本，Windows 不允许它作为文件的默认打开程序，"
                + "双击会没有反应。请改用免安装版或安装后的正式版本。");
        }

        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        using RegistryKey progId = classes.CreateSubKey(ProgId);
        progId.SetValue(string.Empty, "CelesteViewer Image");
        progId.SetValue("FriendlyTypeName", "CelesteViewer Image");

        using RegistryKey defaultIcon = progId.CreateSubKey("DefaultIcon");
        defaultIcon.SetValue(string.Empty, exe + ",0");

        using RegistryKey shell = progId.CreateSubKey(@"shell\open");
        shell.SetValue(string.Empty, "Open with CelesteViewer");
        using RegistryKey command = shell.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");

        foreach (string raw in extensions ?? AllExtensions)
        {
            using RegistryKey extKey = classes.CreateSubKey(NormalizeExtension(raw));
            extKey.SetValue(string.Empty, ProgId);
        }
    }

    public static void Unregister(IEnumerable<string>? extensions = null)
    {
        using RegistryKey classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        foreach (string raw in extensions ?? AllExtensions)
        {
            string ext = NormalizeExtension(raw);
            try
            {
                using RegistryKey? extKey = classes.OpenSubKey(ext, writable: true);
                if (extKey?.GetValue(string.Empty) as string == ProgId)
                    classes.DeleteSubKeyTree(ext, throwOnMissingSubKey: false);
            }
            catch (Exception caught)
            {
                StartupLog.Write("FileAssociationHelper.Unregister(" + ext + ")", caught);
            }
        }

        try
        {
            classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        }
        catch (Exception caught)
        {
            StartupLog.Write("FileAssociationHelper.Unregister(ProgId)", caught);
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? progId = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}");
            return progId != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 注册表里文件关联当前指向的 exe（没注册过 / 读不出来返回 null）。
    ///
    /// 用途：判断"当前关联有没有指到一个用不了的程序上" ——
    /// 把关联手动设成调试（打包）版 exe 是"双击图片没反应"的典型原因，
    /// 界面据此给出明确提示，而不是让用户对着没反应干瞪眼。
    /// </summary>
    public static string? GetRegisteredExecutable()
    {
        try
        {
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ProgId}\shell\open\command");

            if (command?.GetValue(string.Empty) as string is not string line
                || string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            // 值形如 "C:\...\CelesteViewer.exe" "%1"：取引号里的那段
            string text = line.Trim();
            if (text.StartsWith('"'))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : null;
            }

            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }
        catch (Exception caught)
        {
            StartupLog.Write("FileAssociationHelper.GetRegisteredExecutable", caught);
            return null;
        }
    }

    /// <summary>
    /// exe 是不是 MSIX 打包布局里的可执行文件（判据：同目录有 AppxManifest.xml）。
    ///
    /// 打包版 exe 依赖"应用包标识"才能启动，Windows 无法通过
    /// shell\open\command 拉起它 —— 双击只会静默退出，没有任何反应。
    /// VS 里按 F5 跑出来的就是这种，所以它天然不能用来做文件关联。
    /// </summary>
    public static bool IsPackagedLayout(string executablePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "AppxManifest.xml"));
        }
        catch (Exception caught)
        {
            StartupLog.Write("FileAssociationHelper.IsPackagedLayout", caught);
            return false;
        }
    }

    private static string NormalizeExtension(string ext)
    {
        ext = ext.Trim();
        if (!ext.StartsWith('.')) ext = "." + ext;
        return ext.ToLowerInvariant();
    }
}
