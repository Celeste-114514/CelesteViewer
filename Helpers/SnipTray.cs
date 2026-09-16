using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace CelesteGallery.Helpers;

/// <summary>
/// 截图工具的"常驻入口"：托盘图标 + 全局热键。
///
/// 为什么需要它：截图要是只能从主界面里点按钮触发，那它就不是个工具，
/// 只是个功能。QQ / 微信截图之所以好用，是因为<b>随时按一下就出来</b>——
/// 哪怕主窗口被别的程序盖住、哪怕用户正在浏览器里。这需要两样东西：
/// 一个全局热键，和一个托盘图标（不然热键没人记得住，也没法退出）。
///
/// == 为什么要单开一条线程 ==
///
/// 全局热键和托盘图标都是"收到系统消息 → 干活"的模型，消息只能投递到
/// <b>创建窗口的那条线程的消息队列</b>上。WinUI 3 自己那条 UI 线程的消息循环
/// 是框架内部跑的，我们插不进自己的处理（没有窗口过程可以挂）。
///
/// 所以要另起一条线程：在建好的隐藏窗口上收 WM_HOTKEY / 托盘回调，
/// 真正要动界面的时候再通过 <see cref="DispatcherQueue"/> 扔回 UI 线程。
/// 好处是它完全不碰 WinUI 的消息循环，不可能把它搞坏。
///
/// 窗口用 <c>HWND_MESSAGE</c> 的"只收消息"窗口：它不占屏幕、不出现在
/// 任务栏和 Alt+Tab 里，纯粹是个收信箱。
/// </summary>
internal static class SnipTray
{
    /// <summary>热键候选（按顺序试，第一个能注册上的就用它）。</summary>
    private static readonly (uint Mods, uint Vk, string Text)[] Candidates =
    {
        (MOD_CONTROL | MOD_ALT, VK_A, "Ctrl + Alt + A"),
        (MOD_CONTROL | MOD_SHIFT, VK_A, "Ctrl + Shift + A"),
        (MOD_CONTROL | MOD_ALT, VK_S, "Ctrl + Alt + S"),
        (MOD_CONTROL | MOD_SHIFT, VK_S, "Ctrl + Shift + S"),
    };

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_A = 0x41;
    private const uint VK_S = 0x53;

    private const int WM_HOTKEY = 0x0312;
    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    private const int WM_DESTROY = 0x0002;

    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int CMD_SNIP = 1;
    private const int CMD_SHOW = 2;
    private const int CMD_EXIT = 3;

    private static Thread? _thread;
    private static IntPtr _hwnd = IntPtr.Zero;
    private static uint _hotkeyId;
    private static DispatcherQueue? _ui;
    private static Action? _onSnip;
    private static Action? _onShow;
    private static Action? _onExit;

    /// <summary>已经注册上的热键的文字（比如 "Ctrl + Alt + A"）。没起来时是空串。</summary>
    public static string HotkeyText { get; private set; } = "";

    /// <summary>启动失败的原因（给设置界面显示用）。成功时是 null。</summary>
    public static string? Failure { get; private set; }

    // WndProc 的委托必须自己拿住引用，不然 GC 一收，窗口过程就变成野指针了
    private static WndProcDelegate? _wndProc;
    private static IntPtr _hIcon = IntPtr.Zero;
    private static IntPtr _hMenu = IntPtr.Zero;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void Start(DispatcherQueue ui, Action onSnip, Action onShow, Action onExit)
    {
        if (_thread is not null) return;

        _ui = ui;
        _onSnip = onSnip;
        _onShow = onShow;
        _onExit = onExit;

        _thread = new Thread(Run)
        {
            IsBackground = true,          // 别拦着进程退出
            Name = "CelesteSnipTray",
        };
        _thread.Start();
    }

    /// <summary>收摊：注销热键、摘掉托盘图标、让消息循环退出。</summary>
    public static void Stop()
    {
        try
        {
            if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* 退出路上出错没意义，安静走 */ }
    }

    // ===================== 线程主体 =====================

    private static void Run()
    {
        try
        {
            _wndProc = WindowProc;

            IntPtr hInstance = GetModuleHandle(null);
            string className = "CelesteGallerySnipSink";

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = className,
            };

            if (RegisterClassEx(ref wc) == 0)
            {
                Failure = "注册窗口类失败";
                Services.StartupLog.Write("托盘/热键：注册窗口类失败，功能不可用");
                return;
            }

            // HWND_MESSAGE = (IntPtr)(-3)：只收消息、不上屏
            _hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0,
                                   new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Failure = "创建消息窗口失败";
                Services.StartupLog.Write("托盘/热键：建消息窗口失败，功能不可用");
                return;
            }

            RegisterHotkeyOn(_hwnd);
            InstallTrayIcon(_hwnd);

            // 标准消息循环。GetMessage 返回 0 表示收到 WM_QUIT
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Cleanup();
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
            Services.StartupLog.Write("托盘/热键：线程挂了", ex);
        }
    }

    /// <summary>依次试几个候选热键，注册上第一个能用的。</summary>
    private static void RegisterHotkeyOn(IntPtr hwnd)
    {
        // 先注册一个已知的固定 id，注销时要用同一个
        for (uint i = 0; i < Candidates.Length; i++)
        {
            var (mods, vk, text) = Candidates[i];
            uint id = 0x5100 + i + 1;   // 随便挑个不会和系统冲突的 id 段

            if (RegisterHotKey(hwnd, id, mods, vk))
            {
                _hotkeyId = id;
                HotkeyText = text;
                Services.StartupLog.Write($"托盘/热键：全局热键已注册 → {text}");
                return;
            }
        }

        Services.StartupLog.Write("托盘/热键：所有候选热键都被别的程序占了（QQ 之类的截图工具常见），热键不可用");
    }

    private static void InstallTrayIcon(IntPtr hwnd)
    {
        try
        {
            string ico = AppIcon.IcoPath;
            if (System.IO.File.Exists(ico))
            {
                _hIcon = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_TIP | (NIF_ICON * (_hIcon != IntPtr.Zero ? 1u : 0u)),
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon,
                szTip = "CelesteGallery 截图",
            };

            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                Services.StartupLog.Write("托盘/热键：加托盘图标失败（不影响热键）");
            }
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("托盘/热键：加托盘图标出错（不影响热键）", ex);
        }
    }

    private static void Cleanup()
    {
        try
        {
            if (_hotkeyId != 0)
            {
                UnregisterHotKey(_hwnd, _hotkeyId);
                _hotkeyId = 0;
            }

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);

            if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
            if (_hMenu != IntPtr.Zero) { DestroyMenu(_hMenu); _hMenu = IntPtr.Zero; }
        }
        catch { /* 收摊阶段不抛 */ }
    }

    // ===================== 消息处理 =====================

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch ((int)msg)
            {
                case WM_HOTKEY:
                    Post(_onSnip);
                    return IntPtr.Zero;

                case WM_TRAY:
                {
                    int evt = (int)lParam & 0xFFFF;
                    if (evt is WM_RBUTTONUP or WM_CONTEXTMENU) ShowTrayMenu(hWnd);
                    else if (evt == WM_LBUTTONUP) Post(_onSnip);   // 左键直接截图
                    return IntPtr.Zero;
                }

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("托盘/热键：处理消息出错", ex);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void ShowTrayMenu(IntPtr hWnd)
    {
        if (_hMenu == IntPtr.Zero)
        {
            _hMenu = CreatePopupMenu();
            if (_hMenu == IntPtr.Zero) return;

            string snipLabel = string.IsNullOrEmpty(HotkeyText) ? "截图" : $"截图　（{HotkeyText}）";
            AppendMenu(_hMenu, MF_STRING, CMD_SNIP, snipLabel);
            AppendMenu(_hMenu, MF_STRING, CMD_SHOW, "显示主窗口");
            AppendMenu(_hMenu, MF_SEPARATOR, 0, "");
            AppendMenu(_hMenu, MF_STRING, CMD_EXIT, "退出 CelesteGallery");
        }

        if (!GetCursorPos(out POINT pt)) return;

        // SetForegroundWindow 不能省：菜单在"前台窗口不是本程序"的时候弹出来，
        // 点空白处它不会自己关掉，会一直挂在桌面上
        SetForegroundWindow(hWnd);

        int cmd = TrackPopupMenu(_hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                 pt.X, pt.Y, 0, hWnd, IntPtr.Zero);

        switch (cmd)
        {
            case CMD_SNIP: Post(_onSnip); break;
            case CMD_SHOW: Post(_onShow); break;
            case CMD_EXIT: Post(_onExit); break;
        }
    }

    /// <summary>把动作扔回 UI 线程执行。界面只能在那条线程上动。</summary>
    private static void Post(Action? action)
    {
        if (action is null) return;
        try
        {
            if (_ui is null) return;
            _ui.TryEnqueue(() =>
            {
                try { action(); }
                catch (Exception ex) { Services.StartupLog.Write("托盘/热键：执行动作出错", ex); }
            });
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("托盘/热键：派发到 UI 线程失败", ex);
        }
    }

    // ===================== Win32 =====================

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, uint id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, int id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y,
        int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type,
        int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
