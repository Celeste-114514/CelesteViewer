using System;
using System.Threading.Tasks;
using CelesteGallery.Views;
using Microsoft.UI.Xaml;

namespace CelesteGallery.Helpers;

/// <summary>
/// 启动一次截图的统一入口。
///
/// 主界面的"截图"按钮、托盘菜单、全局热键三条路都走这里 —— 三条路要做的
/// 事一模一样（藏自己 → 等一帧 → 抓屏 → 框选 → 完事把自己放回来），
/// 分散写三遍迟早会漏掉其中一步（最典型的就是忘了把自己藏起来，
/// 结果截出来的图右下角带着本程序的界面）。
/// </summary>
internal static class SnipLauncher
{
    /// <summary>藏掉自己之后等多久才抓屏。见下面注释。</summary>
    private const int HideSettleMs = 220;

    /// <summary>
    /// 藏起主窗口 → 抓屏框选 → 标注 → 结束之后把主窗口放回来。
    /// </summary>
    /// <param name="owner">
    /// 要藏起来的窗口（一般是主窗口）。传 null 就什么都不藏 ——
    /// 主窗口已经被关掉时（比如从托盘点截图）就是这种情形。
    /// </param>
    public static async void Launch(Window? owner)
    {
        IntPtr hwnd = IntPtr.Zero;

        try
        {
            if (owner is not null)
            {
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
                WindowForeground.Hide(hwnd);

                // 藏完必须等一小会儿再抓。ShowWindow 只是给系统发了个请求，
                // 窗口真的从屏幕上消失、DWM 把桌面重新合成一遍还要一两帧。
                // 不等的话，抓到的仍然是"带着自己界面的那一帧"。
                await Task.Delay(HideSettleMs);
            }

            IntPtr restore = hwnd;
            SnipWindow.Start(() =>
            {
                if (restore != IntPtr.Zero) WindowForeground.BringToFront(restore);
            });
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("启动截图失败", ex);
            if (hwnd != IntPtr.Zero) WindowForeground.BringToFront(hwnd);
        }
    }
}
