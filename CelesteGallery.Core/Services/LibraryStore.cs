using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CelesteGallery.Services;

/// <summary>
/// 图库：用户自己收进来的文件夹清单（左侧目录树里"图库"那一组）。
///
/// 为什么要单独存一份、而不是塞进 settings.txt：
/// 这里的值是**有序列表**，而 settings.txt 是 "键=值" 的平面文本，
/// 一个键存一个值。硬塞成 "A|B|C" 这种拼接串，遇到路径里带分隔符的就要转义，
/// 属于自找麻烦。一行一个路径的纯文本最省事，出问题用记事本也能看能改。
///
/// 存到 %LOCALAPPDATA%\CelesteGallery\library.txt。
/// 所有读写都吞异常 —— 图库丢了最多是"要重新加一次"，不该让软件用不了。
/// </summary>
public static class LibraryStore
{
    /// <summary>最多记这么多条。再多左侧那一栏就没法看了。</summary>
    public const int MaxEntries = 30;

    private static readonly object Sync = new();

    private static string FilePath => AppPaths.File("library.txt");

    /// <summary>
    /// 读出图库清单。
    ///
    /// 第一次运行（文件还不存在）时，用"桌面 / 图片 / 下载"垫底，
    /// 并且**立刻落盘** —— 这样用户如果把其中某个移除了，下次启动它不会又冒出来。
    /// 反过来，如果文件存在但是空的（用户把条目全删了），就尊重这个状态，不再塞默认值。
    /// </summary>
    public static List<string> Load()
    {
        lock (Sync)
        {
            string path = FilePath;

            if (!File.Exists(path))
            {
                var seeded = Defaults();
                SaveCore(seeded);
                return seeded;
            }

            var list = new List<string>();
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string item = line.Trim();
                    if (item.Length == 0) continue;

                    // 注意：List<T>.Contains 没有接受比较器的重载，
                    // 想按"忽略大小写"去重必须用 Exists
                    if (list.Exists(p => PathComparer.Equals(p, item))) continue;

                    list.Add(item);
                    if (list.Count >= MaxEntries) break;
                }
            }
            catch
            {
            }

            return list;
        }
    }

    /// <summary>加一个文件夹（已存在就提到最前面），返回新的清单。</summary>
    public static List<string> Add(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Load();

        lock (Sync)
        {
            string normalized = Normalize(folder);

            var list = Load();
            list.RemoveAll(p => PathComparer.Equals(p, normalized));
            list.Insert(0, normalized);

            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);

            SaveCore(list);
            return list;
        }
    }

    /// <summary>移出一个文件夹，返回新的清单。</summary>
    public static List<string> Remove(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Load();

        lock (Sync)
        {
            string normalized = Normalize(folder);

            var list = Load();
            list.RemoveAll(p => PathComparer.Equals(p, normalized));

            SaveCore(list);
            return list;
        }
    }

    /// <summary>整体替换（顺序调整之类）。</summary>
    public static void Save(IEnumerable<string> folders)
    {
        lock (Sync) SaveCore(new List<string>(folders));
    }

    private static void SaveCore(List<string> list)
    {
        try
        {
            File.WriteAllText(FilePath, string.Join(Environment.NewLine, list), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static List<string> Defaults()
    {
        var list = new List<string>();

        void Try(string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add(Normalize(path!));
        }

        Try(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Try(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Try(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        return list;
    }

    /// <summary>去掉结尾的反斜杠，否则 "D:\照片" 和 "D:\照片\" 会被当成两个。</summary>
    private static string Normalize(string folder)
        => Path.TrimEndingDirectorySeparator(folder.Trim());

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
}
