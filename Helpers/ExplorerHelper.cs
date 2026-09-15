using System;
using System.Diagnostics;

namespace CelesteGallery.Helpers;

/// <summary>
/// 把"在资源管理器里打开"这事儿包起来。
///
/// 直接 Process.Start("explorer.exe", 路径) 有坑：explorer 的参数解析很挑，
/// 路径带空格时报价方式不对会把路径截断（于是打开了"文档"）。
/// 所以统一走 ProcessStartInfo + 显式参数，并且 try-catch 兜住 ——
/// 打不开资源管理器不是什么大事，不该把软件搞崩。
/// </summary>
public static class ExplorerHelper
{
    /// <summary>打开一个文件夹。</summary>
    public static void OpenFolder(string folder)
    {
        Run($"\"{folder}\"");
    }

    /// <summary>打开文件所在的文件夹，并把这个文件选中。</summary>
    public static void RevealFile(string file)
    {
        // /select, 后面不能有空格，路径要带引号
        Run($"/select,\"{file}\"");
    }

    private static void Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write($"ExplorerHelper: 打开资源管理器失败 {arguments}", ex);
        }
    }
}
