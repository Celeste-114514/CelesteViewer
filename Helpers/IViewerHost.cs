using System;
using Microsoft.UI.Xaml;

namespace CelesteGallery.Helpers;

/// <summary>
/// 承载单图查看页面的那一层（可能是主窗口，也可能是独立看图窗口）。
///
/// 为什么需要它：看图页要做几件"只有窗口能办"的事 ——
/// 全屏、把文件选择框挂到正确的窗口上、按返回时决定"退到缩略图墙"还是"关掉自己"。
/// 以前这些直接写死去找主窗口（App.Instance），一旦页面被放进另一个窗口，
/// 全屏就切到主窗口去了、选择框也弹在错误的窗口上。
///
/// 定义成接口之后，页面只认接口，谁承载它都行。
/// </summary>
public interface IViewerHost
{
    /// <summary>窗口句柄。文件选择框、打印这些系统对话框必须知道挂在哪个窗口上。</summary>
    IntPtr WindowHandle { get; }

    /// <summary>现在是不是全屏（Esc 要先退全屏还是先退页面，靠它判断）。</summary>
    bool IsFullScreen { get; }

    void ToggleFullScreen();

    /// <summary>
    /// 返回。主窗口的实现是"切回缩略图墙"，独立窗口的实现是"关掉自己"。
    /// </summary>
    void RequestBack();

    /// <summary>把页面顶部那一条设成窗口标题栏，让标题栏区域能拖动窗口。</summary>
    void SetTitleBar(UIElement element);
}
