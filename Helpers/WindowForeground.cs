using System;
using System.Runtime.InteropServices;

namespace CelesteGallery.Helpers;

/// <summary>
/// 把一个窗口抬到 z 序最上面，并让它拿到前台（键盘焦点）。
///
/// == 为什么需要它 ==
///
/// WinUI3 的 <c>Window.Activate()</c> 对**第一个**窗口是好使的，
/// 但对"程序已经在跑、用户又从主窗口里点开一个新窗口"这条路，它经常不管用：
/// 新窗口确实建出来了、内容也在渲染，但 z 序上**落在主窗口后面**。
/// 表现就是用户说的那句 —— "打开图片，新窗口会居于主程序后面"，
/// 从外面看像是没打开，其实窗口已经在那儿了。
///
/// 实测（`_cvzorder.py`）：主窗口 1920×1023、看图窗口 2048×1183，
/// 看图窗口建完之后仍排在主窗口**下面**。所以这里补一次 Win32 的硬抬升。
///
/// 三个调用顺序不能省：
///   1. 最小化了先还原 —— 没还原的话抬上来的还是那个最小化的框；
///   2. <c>SetWindowPos(HWND_TOP)</c> 改 z 序 —— 这一步才真正把它插到最上面；
///   3. <c>SetForegroundWindow</c> 抢前台 —— 光改 z 序，键盘焦点还在原来的窗口上，
///      用户敲键盘会发现"键没反应"。
///
/// 用 <c>HWND_TOP</c>（不是 <c>HWND_TOPMOST</c>）是有意的：
/// 只要求"插到这一摞的最上面"，不要求"永远压在别人头上"。
/// 用 TOPMOST 的话窗口会一直盖着别的程序，那是流氓软件的行为。
/// </summary>
internal static class WindowForeground
{
    private const int SW_RESTORE = 9;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>插到 z 序顶部。0 就是 HWND_TOP 的值。</summary>
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    /// <summary>抬到最前 + 抢到前台。抬不起来就安静地算了。</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // 只对"被最小化了"才 SW_RESTORE。
            // 无条件还原的话，用户最大化的窗口会被这一下压回普通大小。
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 抬不起来不是致命错：窗口已经建出来了，用户点一下也能用。
            // 这里绝不能抛出去 —— 调用点在构造函数里，抛出去就是"程序启动即崩"。
        }
    }
}
