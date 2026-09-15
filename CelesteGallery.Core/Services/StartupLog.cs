using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CelesteGallery.Services;

/// <summary>
/// 「黑匣子」日志。
///
/// 为什么需要它：WinUI3 程序如果在启动阶段崩掉（尤其是 CLR 层的问题），
/// 窗口根本不会出现，也看不到任何提示 —— 只能靠事前写下的日志来定位。
///
/// 设计上刻意做得极其保守：
///   - 所有写操作都吞异常（日志失败绝不能连累程序本身）
///   - 每次写入立刻 Flush，保证崩溃前的内容真正落盘
///   - 不依赖任何第三方库，只用了 System.IO
///
/// 日志位置：%LOCALAPPDATA%\CelesteGallery\startup.log（路径由 <see cref="AppPaths"/> 决定）
/// </summary>
public static class StartupLog
{
    private static readonly object Sync = new();
    private static string? _path;

    /// <summary>日志文件的完整路径（首次访问时确定）。</summary>
    public static string Path
    {
        get
        {
            if (_path is not null) return _path;

            try
            {
                _path = AppPaths.File("startup.log");
            }
            catch
            {
                // 连日志目录都建不了（极端情况），退到临时目录
                _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CelesteGallery-startup.log");
            }

            return _path;
        }
    }

    /// <summary>写一行。带时间戳和线程号，方便看崩在哪一步。</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:00}] {message}{Environment.NewLine}";
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 故意吞掉：日志失败不应该影响程序
        }
    }

    /// <summary>
    /// 记一个异常。**完整堆栈 + 每一层内部异常 + HResult** 都要记下来。
    ///
    /// 这里踩过一次坑：原来只打 <c>StackTrace.Split('\n')[0]</c>，
    /// 而 XamlParseException 这类从 WinRT 边界过来的异常经常**根本没有堆栈**，
    /// 结果日志里只剩一个 <c>"@ "</c>，等于白记。
    /// HResult 是关键线索 —— 它能区分"属性值非法""资源找不到""类型加载失败"。
    /// </summary>
    public static void Write(string context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(context).Append(" → ");

        Exception? cur = ex;
        int depth = 0;

        while (cur is not null && depth++ < 5)
        {
            if (depth > 1) sb.Append("  ← 内层异常 ");

            sb.Append(cur.GetType().FullName)
              .Append(" (HResult=0x").Append(cur.HResult.ToString("X8")).Append("): ")
              .Append(cur.Message)
              .Append("  [Data: ").Append(DescribeData(cur)).Append(']');

            if (!string.IsNullOrWhiteSpace(cur.StackTrace))
            {
                sb.AppendLine();
                sb.Append(cur.StackTrace.TrimEnd());
            }

            cur = cur.InnerException;
        }

        Write(sb.ToString());
    }

    /// <summary>
    /// 异常的 Data 字典里往往藏着最有用的东西（控件名、资源键、行号），
    /// 但 ToString() 默认不显示它。
    /// </summary>
    private static string DescribeData(Exception ex)
    {
        try
        {
            if (ex.Data is null || ex.Data.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (System.Collections.DictionaryEntry entry in ex.Data)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(entry.Key).Append('=').Append(entry.Value);
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>开一次新会话：写分隔线并记录基本环境信息。</summary>
    public static void BeginSession(string tag)
    {
        try
        {
            lock (Sync)
            {
                string header = $"{Environment.NewLine}==================== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {tag} ====================";
                File.AppendAllText(Path, header + Environment.NewLine, Encoding.UTF8);

                Write($"OS        : {Environment.OSVersion}");
                Write($"CLR       : {Environment.Version}");
                Write($"64-bit    : {Environment.Is64BitProcess}");
                Write($"命令行    : {Environment.CommandLine}");
                Write($"工作目录  : {Environment.CurrentDirectory}");
                // 数据目录每次启动都记一笔：改名之后要能一眼看出
                // "到底在写老目录还是新目录"，以及这次是不是刚搬过来的。
                Write(AppPaths.MigratedFromLegacy
                    ? $"数据目录  : {AppPaths.DataDir}（本次启动已从 {AppPaths.LegacyAppName} 迁移过来）"
                    : $"数据目录  : {AppPaths.DataDir}");

                // 刻意不去 typeof(Microsoft.UI.Xaml.Application)：
                // 那会加载 WinRT 投影类型，而「日志本身把程序搞崩」是最糟的情况。
                // 用 FileVersionInfo 读文件版本，纯文件操作，零风险。
                try
                {
                    string dir = AppContext.BaseDirectory;
                    foreach (string name in new[] { "Microsoft.WinUI.dll", "Microsoft.WindowsAppRuntime.dll", "WinRT.Runtime.dll" })
                    {
                        string full = System.IO.Path.Combine(dir, name);
                        if (File.Exists(full))
                        {
                            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(full);
                            Write($"组件 {name,-32} 文件版本 {vi.FileVersion} / 产品 {vi.ProductVersion}");
                        }
                        else
                        {
                            Write($"组件 {name,-32} 未找到");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Write("读取组件版本失败", ex);
                }
            }
        }
        catch
        {
        }
    }
}
