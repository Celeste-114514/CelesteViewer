using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace CelesteViewer.Helpers;

/// <summary>
/// 子窗口的定位：让它开在**主窗口正中间**，而不是屏幕正中间。
///
/// 为什么非要相对主窗口：主窗口经常不是全屏、也不在屏幕正中
/// （比如靠左半屏摆着看图）。这时候按屏幕居中，弹出的小窗会离主窗口老远，
/// 视线要横跨半块屏幕找它 —— 尤其"关于"这种点一下就关的轻窗口，体验很割裂。
///
/// 为什么不用 Win32 的 owner（SetWindowLong GWLP_HWNDPARENT）：
/// WinUI3 的非打包窗口一旦设了 owner，主窗口最小化时子窗口会跟着消失，
/// 而且某些情况下会连带抢焦点。这里只做"算坐标"，不建立窗口从属关系，最省心。
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// 先把窗口改到 <paramref name="width"/> × <paramref name="height"/>，
    /// 再挪到主窗口正中；算不出来（主窗口没了 / 显示器信息拿不到）就退回屏幕居中。
    /// </summary>
    public static void ResizeAndCenterOnOwner(AppWindow window, int width, int height)
    {
        try
        {
            window.Resize(new SizeInt32(width, height));
        }
        catch
        {
            // 尺寸设不动不影响后面定位
        }

        PointInt32? target = CenterOnOwner(width, height) ?? CenterOnScreen(window, width, height);
        if (target is null) return;

        try
        {
            window.Move(target.Value);
        }
        catch
        {
            // 挪不动就用系统默认位置，不影响看
        }
    }

    /// <summary>相对主窗口算居中坐标；拿不到主窗口就返回 null。</summary>
    private static PointInt32? CenterOnOwner(int width, int height)
    {
        try
        {
            if (global::CelesteViewer.App.Instance?.AppWindow is not AppWindow owner) return null;

            PointInt32 pos = owner.Position;
            SizeInt32 size = owner.Size;
            if (size.Width <= 0 || size.Height <= 0) return null;

            return ClampToWorkArea(
                owner.Id,
                pos.X + (size.Width - width) / 2,
                pos.Y + (size.Height - height) / 2,
                width,
                height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>退回方案：相对主窗口所在显示器（拿不到就主显示器）的工作区居中。</summary>
    private static PointInt32? CenterOnScreen(AppWindow window, int width, int height)
    {
        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            return new PointInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把坐标夹回工作区里。
    ///
    /// 主窗口贴着屏幕边摆（或比要弹的窗口还小）时，居中算出来会跑到屏幕外，
    /// 窗口就"不见了"。这里保证至少整个窗口都看得见。
    /// </summary>
    private static PointInt32 ClampToWorkArea(WindowId id, int x, int y, int width, int height)
    {
        PointInt32 raw = new(x, y);
        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;

            int maxX = work.X + work.Width - width;
            int maxY = work.Y + work.Height - height;

            // 窗口比工作区还大（极窄窗口 / 小屏）时夹不动，就贴着左上角放
            x = maxX < work.X ? work.X : Math.Clamp(x, work.X, maxX);
            y = maxY < work.Y ? work.Y : Math.Clamp(y, work.Y, maxY);

            return new PointInt32(x, y);
        }
        catch
        {
            return raw;
        }
    }
}
