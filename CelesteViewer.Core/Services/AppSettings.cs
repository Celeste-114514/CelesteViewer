using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CelesteViewer.Services;

/// <summary>
/// 一点简单的偏好设置，存在 %LOCALAPPDATA%\CelesteViewer\settings.txt。
///
/// 刻意不引配置库、也不搞 JSON —— 就一个 "键=值" 的文本文件。
/// 理由：需要记的东西目前只有两三个（上次打开的文件夹、缩略图大小），
/// 为了这点东西引一个依赖、加一层序列化，不划算。
///
/// 所有读写都吞异常：设置丢了最多是"没记住"，不该影响用软件。
/// </summary>
public static class AppSettings
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _cache;

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CelesteViewer");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "settings.txt");
        }
    }

    public static string? Get(string key)
    {
        lock (Sync)
        {
            EnsureLoaded();
            return _cache!.TryGetValue(key, out string? value) ? value : null;
        }
    }

    public static int GetInt(string key, int fallback)
    {
        string? raw = Get(key);
        return int.TryParse(raw, out int value) ? value : fallback;
    }

    public static bool GetBool(string key, bool fallback)
    {
        string? raw = Get(key);
        return bool.TryParse(raw, out bool value) ? value : fallback;
    }

    public static void Set(string key, string value)
    {
        lock (Sync)
        {
            EnsureLoaded();
            if (_cache!.TryGetValue(key, out string? old) && old == value) return;

            _cache[key] = value;
            Save();
        }
    }

    public static void Set(string key, int value) => Set(key, value.ToString());

    public static void Set(string key, bool value) => Set(key, value.ToString());

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;

        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(FilePath)) return;

            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                int split = line.IndexOf('=');
                if (split <= 0) continue;

                string key = line[..split].Trim();
                string value = line[(split + 1)..].Trim();
                if (key.Length > 0) _cache[key] = value;
            }
        }
        catch
        {
        }
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in _cache!)
                sb.Append(kv.Key).Append('=').AppendLine(kv.Value);

            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }
}
