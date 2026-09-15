using System;
using System.Runtime.InteropServices;

namespace CelesteGallery.Helpers;

/// <summary>
/// 把鼠标指针藏起来（配合自绘的笔尖圆圈用）。
///
/// 为什么需要它：WinUI3 里能换的系统指针只有
/// <c>InputSystemCursorShape</c> 里那几个现成的形状，
/// **没有"不显示"这一项**。而画图的时候要用一个空心圆来表示笔尖有多粗，
/// 光留一个十字在那儿，粗细根本看不出来 —— 所以只能自己把系统指针藏掉，
/// 用画出来的圈代替。
///
/// ⚠️ 这是**全局**状态（不是某个控件的属性），所以：
///   - 只允许在"进标记模式"和"出标记模式"各调一次，别在鼠标移动里反复调；
///   - 退出时**一定**要还原。忘了还原的话，用户的鼠标指针会一直看不见 ——
///     那是比"看不到笔尖"严重得多的事故。
///
/// 实现上不靠"调了几次 ShowCursor"来记账（那个计数从哪开始是不确定的），
/// 而是每次先问系统"当前指针到底显示着没有"（<c>GetCursorInfo</c>），
/// 再朝目标状态一步一步挪。这样无论进来时是什么状态，出去时都能对上账。
/// </summary>
internal static class MouseCursor
{
    private const int CursorShowing = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Handle;
        public NativePoint ScreenPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ShowCursor(bool show);

    /// <summary>系统指针现在看得见吗？问不到就当它是看得见的（宁可多减一次）。</summary>
    private static bool IsShowing()
    {
        try
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info)) return true;
            return (info.Flags & CursorShowing) != 0;
        }
        catch
        {
            // 理论上不会走到这儿。真出意外就当"看得见"，
            // 让后面的隐藏循环至少跑一次，别把隐藏这件事静默吞掉
            return true;
        }
    }

    /// <summary>藏起来。已经在隐藏状态就什么都不做。</summary>
    public static void Hide()
    {
        if (!IsShowing()) return;
        for (int i = 0; i < 64 && IsShowing(); i++) ShowCursor(false);
    }

    /// <summary>还原成看得见。已经在显示状态就什么都不做。</summary>
    public static void Show()
    {
        for (int i = 0; i < 64 && !IsShowing(); i++) ShowCursor(true);
    }
}
