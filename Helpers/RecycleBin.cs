using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CelesteViewer.Services;

namespace CelesteViewer.Helpers;

/// <summary>
/// 把文件移到 Windows 回收站（而不是硬删），这样删错了能去回收站还原。
///
/// 走 Win32 的 <c>SHFileOperation</c>（FO_DELETE + FOF_ALLOWUNDO）：
/// 这是系统自带的"送回收站"原语，比自己搞一个本地 Trash 文件夹稳得多 ——
/// 还原时用户直接在资源管理器里右键"还原"就行，不用记我们自己的目录。
///
/// 为什么不直接 <c>File.Delete</c>：那是永久删除，删错一张照片就真没了。
/// "查找重复"这种批量操作，安全底线必须是"可恢复"。
/// </summary>
public static class RecycleBin
{
    // FO_DELETE = 删除
    private const uint FO_DELETE = 0x0003;
    // FOF_ALLOWUNDO        → 送到回收站而不是真删
    // FOF_NOCONFIRMATION   → 不再弹系统确认框（我们已经用自己的对话框确认过了）
    // FOF_SILENT           → 不显示进度条
    // FOF_NOERRORUI        → 出错也不弹系统框，错误我们自己报
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>
    /// 把一批文件送进回收站。
    /// </summary>
    /// <param name="paths">要送回收站的文件完整路径。</param>
    /// <returns>
    /// (成功数, 是否有被中止/失败的)。
    /// 返回前不抛异常（删文件失败不该让界面崩）。
    /// </returns>
    public static (int Moved, bool Aborted) Send(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (string p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p))
                list.Add(p);
        }

        if (list.Count == 0) return (0, false);

        // pFrom 必须是"每条路径 \0 结尾、整体再以 \0\0 收尾"的双空结尾字符串
        var sb = new StringBuilder();
        foreach (string p in list)
        {
            sb.Append(p);
            sb.Append('\0');
        }
        sb.Append('\0');

        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = sb.ToString(),
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };

        try
        {
            int hr = SHFileOperationW(ref op);
            // 0 = 全部成功；任何非 0 都算有失败/用户取消
            if (hr != 0) return (0, true);
            if (op.fAnyOperationsAborted != 0) return (0, true);
            return (list.Count, false);
        }
        catch (Exception ex)
        {
            StartupLog.Write("RecycleBin: 送回收站失败", ex);
            return (0, true);
        }
    }
}
