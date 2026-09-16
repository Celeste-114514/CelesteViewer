using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CelesteGallery.Services;

/// <summary>
/// 屏幕上的一块矩形（**物理像素**，不是 WinUI 的有效像素）。
/// 多显示器时 X / Y 可以为负（副屏摆在主屏左边的情形）。
/// </summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// 抓下来的一帧画面。像素是 BGRA32、**从左上角开始按行排**（top-down），
/// 这样丢给 WinUI 的 SoftwareBitmap / 上层画图都不用再翻转一次。
/// </summary>
public sealed class CapturedFrame
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public double Scale { get; }   // 物理像素 ÷ 有效像素（1.0 = 100%，1.5 = 150% 缩放）

    public CapturedFrame(byte[] pixels, int width, int height, int stride, double scale)
    {
        Pixels = pixels; Width = width; Height = height; Stride = stride; Scale = scale;
    }

    /// <summary>整帧是不是全黑。抓屏失败（黑屏/权限不足）时几乎是必现的现象，值得单独测一下。</summary>
    public bool LooksBlank()
    {
        // 只抽样，别全扫 —— 一帧 4K 图有八百多万个点，全扫要花时间
        int step = Math.Max(1, (Width * Height) / 4096);
        int i = 0;
        while (i < Pixels.Length - 4)
        {
            if (Pixels[i] > 8 || Pixels[i + 1] > 8 || Pixels[i + 2] > 8) return false;
            i += step * 4;
        }
        return true;
    }
}

/// <summary>窗口列表里的一项（给"抓窗口"模式用）。</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }

    /// <summary>窗口标题。标题为空的（很多后台窗口）不值得出现在列表里。</summary>
    public string Title { get; init; } = "";

    /// <summary>可视区域矩形（物理像素）。用 DWM 的 EXTENDED_FRAME_BOUNDS，
    /// 比 GetWindowRect 准——后者会把阴影边框算进去，抓出来边上多一圈黑。</summary>
    public CaptureRect Bounds { get; init; }

    public override string ToString() => $"{Title} [{Bounds.Width}x{Bounds.Height}]";
}

/// <summary>
/// 屏幕抓取。全部走 Win32 GDI，纯托管调用、不依赖任何 UI 框架，
/// 所以能在命令行探针里单独压测（见 tools/IndexHarness 那套做法）。
/// </summary>
/// <remarks>
/// ⚠️ 坐标单位：本类对外**只认物理像素**。WinUI 那边拿到的鼠标坐标是"有效像素"，
/// 乘 <see cref="SystemScale"/> 换过来再用。别混，混了在高分屏上会整体偏移。
/// </remarks>
public static class ScreenCapture
{
    // ---- 常量 ----
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;   // 不加这个，抓半透明/分层的窗口会缺内容
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int CURSOR_SHOWING = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint uUsage);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(out CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);
        [DllImport("user32.dll")] public static extern IntPtr GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    /// <summary>
    /// 系统缩放倍率：物理像素 ÷ 有效像素。150% 缩放的高分屏上是 1.5。
    /// 取的是主屏的值——抓图这一层没那么多讲究，够用。
    /// </summary>
    public static double SystemScale
    {
        get
        {
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            try
            {
                int dpi = Native.GetDeviceCaps(dc, LOGPIXELSX);
                if (dpi is >= 96 and <= 480) return dpi / 96.0;
            }
            catch { /* 拿不到 DPI 就按 100% 走，别让整个功能挂掉 */ }
            finally { if (dc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, dc); }
            return 1.0;
        }
    }

    /// <summary>整个虚拟桌面（把所有显示器拼起来）的矩形，物理像素。</summary>
    public static CaptureRect VirtualScreen => new(
        Native.GetSystemMetrics(SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>抓指定矩形（物理像素）。宽高会被夹进虚拟桌面范围内。</summary>
    public static CapturedFrame? CaptureRegion(int x, int y, int width, int height, bool includeCursor = true)
    {
        if (width <= 0 || height <= 0) return null;

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            memDc = Native.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            bmp = Native.CreateCompatibleBitmap(screenDc, width, height);
            if (bmp == IntPtr.Zero) return null;

            IntPtr old = Native.SelectObject(memDc, bmp);
            try
            {
                // CAPTUREBLT 必须带上：不带的话分层窗口（比如开了硬件加速的浏览器）
                // 抓下来是一片黑。
                if (!Native.BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                    return null;

                if (includeCursor) DrawCursor(memDc, x, y);

                return ReadBitmap(memDc, bmp, width, height);
            }
            finally { Native.SelectObject(memDc, old); }
        }
        finally
        {
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>抓整个虚拟桌面。</summary>
    public static CapturedFrame? CaptureVirtualScreen(bool includeCursor = true)
    {
        var r = VirtualScreen;
        return CaptureRegion(r.X, r.Y, r.Width, r.Height, includeCursor);
    }

    /// <summary>
    /// 抓某个窗口。优先用 PrintWindow（即使窗口被别的窗口挡住也能抓到完整内容），
    /// 失败了再退回"按屏幕矩形硬抓"。
    /// </summary>
    public static CapturedFrame? CaptureWindow(IntPtr hwnd, bool includeCursor = false)
    {
        if (hwnd == IntPtr.Zero) return null;

        var rect = WindowBounds(hwnd);
        if (rect.IsEmpty) return null;

        // 窗口超出屏幕边缘时（最大化 / 故意拖到屏幕外），按可见部分裁一下，
        // 免得建一张比屏幕还大的位图白白吃内存。
        var vs = VirtualScreen;
        int x = Math.Max(rect.X, vs.X);
        int y = Math.Max(rect.Y, vs.Y);
        int right = Math.Min(rect.Right, vs.Right);
        int bottom = Math.Min(rect.Bottom, vs.Bottom);
        int w = right - x, h = bottom - y;
        if (w <= 0 || h <= 0) return null;

        IntPtr winDc = Native.GetWindowDC(hwnd);
        if (winDc != IntPtr.Zero)
        {
            IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                memDc = Native.CreateCompatibleDC(winDc);
                if (memDc != IntPtr.Zero)
                {
                    bmp = Native.CreateCompatibleBitmap(winDc, w, h);
                    if (bmp != IntPtr.Zero)
                    {
                        IntPtr old2 = Native.SelectObject(memDc, bmp);
                        try
                        {
                            if (Native.PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT))
                            {
                                // PrintWindow 抓出来的左上角对齐的是窗口客户区，
                                // 而我们要的是上面算出的可见区域，检查下有没有内容再决定用不用。
                                var f = ReadBitmap(memDc, bmp, w, h);
                                if (f is not null && !f.LooksBlank()) return f;
                            }
                        }
                        finally { Native.SelectObject(memDc, old2); }
                    }
                }
            }
            catch { /* PrintWindow 对某些窗口会抛，退回屏幕抓 */ }
            finally
            {
                if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
                if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
                Native.ReleaseDC(hwnd, winDc);
            }
        }

        // 退回方案：直接按矩形抓屏幕
        return CaptureRegion(x, y, w, h, includeCursor);
    }

    /// <summary>鼠标底下是哪个窗口（传物理像素坐标）。</summary>
    public static IntPtr WindowAt(int physicalX, int physicalY)
    {
        return Native.WindowFromPoint(new POINT { X = physicalX, Y = physicalY });
    }

    /// <summary>
    /// 列出"值得出现在窗口列表里"的顶层窗口：可见、非最小化、有标题、不是桌面<｜hy_place▁holder▁no▁813｜>序窗口。
    /// </summary>
    public static IReadOnlyList<WindowInfo> EnumerateWindows()
    {
        var list = new List<WindowInfo>();
        IntPtr shell = Native.GetShellWindow();
        IntPtr desktop = Native.GetDesktopWindow();
        uint selfPid = (uint)Environment.ProcessId;

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (Native.IsIconic(hwnd)) return true;              // 最小化的不列
                if (hwnd == shell || hwnd == desktop) return true;

                int len = Native.GetWindowTextLength(hwnd);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 1);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                // 窗口自己的区域 bounds（用来剔除 0 尺寸的幽灵窗口）
                var b = WindowBounds(hwnd);
                if (b.Width < 8 || b.Height < 8) return true;

                list.Add(new WindowInfo { Handle = hwnd, Title = title, Bounds = b });
            }
            catch { /* 单个窗口枚举炸了不影响其他的 */ }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>窗口的可视矩形（物理像素），用 DWM 的扩展边框，排除阴影。</summary>
    public static CaptureRect WindowBounds(IntPtr hwnd)
    {
        try
        {
            if (Native.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0
                && r.Right > r.Left && r.Bottom > r.Top)
            {
                return new CaptureRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        catch { /* DWM 拿不到就用老办法 */ }

        if (Native.GetWindowRect(hwnd, out RECT wr))
            return new CaptureRect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);

        return default;
    }

    // ---------- 内部实现 ----------

    private static CapturedFrame? ReadBitmap(IntPtr memDc, IntPtr bmp, int width, int height)
    {
        int stride = ((width * 32 + 31) / 32) * 4;   // 每行必须 4 字节对齐
        var pixels = new byte[stride * height];

        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,        // 负数 = 从上往下排，省掉一次翻转
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,         // BI_RGB
            biSizeImage = pixels.Length,
        };

        int got = Native.GetDIBits(memDc, bmp, 0, (uint)height, pixels, ref bi, 0);
        if (got == 0) return null;

        // ⚠️ 必须自己把 alpha 补成 255。
        //
        // GDI 抓屏给出的是"32 位 BGRA"，但那个第 4 字节在 BI_RGB 下是**未定义**的
        // （实测一律是 0）。把它当 alpha 用就会出大问题：
        //   · 交给 SoftwareBitmap(BitmapAlphaMode.Premultiplied) → 整张图判定为全透明，
        //     界面上什么都看不见，日志却一切正常（踩过这个坑）；
        //   · 交给 Magick 按 BGRA 解码 → 存出来的 PNG 是一片透明。
        // 抓屏结果本来就是不透明的，"补成 255"不是猜，是事实。
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        return new CapturedFrame(pixels, width, height, stride, SystemScale);
    }

    private static void DrawCursor(IntPtr memDc, int originX, int originY)
    {
        try
        {
            if (!Native.GetCursorInfo(out CURSORINFO ci)) return;
            if (ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;

            // 光标位置是屏幕坐标，画到目标 DC 里要减掉区域原点
            Native.DrawIcon(memDc, ci.ptScreenPos.X - originX, ci.ptScreenPos.Y - originY, ci.hCursor);
        }
        catch { /* 画光标失败不影响截图本身 */ }
    }
}
