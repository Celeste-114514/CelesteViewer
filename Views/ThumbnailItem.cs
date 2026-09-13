using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CelesteViewer.Views;

/// <summary>
/// 缩略图墙上的一个格子。
///
/// 关键点是**它自己不负责加载**：真正的加载由页面在格子出现在屏幕上时触发
/// （见 BrowserPage.Thumbs_ElementPrepared）。
/// 这样一万张图的目录也只会去解用户真正看到的那几十张 ——
/// 这是"大目录不卡"的核心，光靠列表虚拟化还不够，解码也得跟着虚拟化。
/// </summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _failed;
    private bool _selected;
    private bool _multiSelected;
    private double _aspect = 1.0;

    public required string Path { get; init; }
    public required string FileName { get; init; }

    /// <summary>
    /// 这张图相对于"当前正在浏览的目录"坐在哪个子目录里。
    /// 空字符串 = 就在当前目录下。只在开启"包含子文件夹"时才会被填上，
    /// 用来在格子角上标一下"它其实来自哪一层"，免得用户以为图丢了。
    /// </summary>
    public string SubFolder { get; init; } = "";

    /// <summary>没有子目录标记的格子不留任何多余元素。</summary>
    public Visibility SubFolderVisibility =>
        string.IsNullOrEmpty(SubFolder) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 已经有人给这张图排过队了。
    /// 虚拟化会反复回收再重建同一个位置的格子，没有这个标志会重复解码好几遍。
    /// </summary>
    public bool LoadRequested { get; set; }

    /// <summary>
    /// 宽高比（宽 / 高）。等高布局用它在"定高、变宽"的格子里摆正图片。
    /// 默认 1.0，缩略图解出来后回填真实值（等高模式下格子会按它变宽）。
    /// </summary>
    public double Aspect
    {
        get => _aspect;
        set
        {
            if (Math.Abs(_aspect - value) < 0.001) return;
            _aspect = value;
            Raise(nameof(Aspect));
        }
    }

    /// <summary>解好的缩略图。null = 还没解出来。</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            Raise(nameof(Thumbnail));
            Raise(nameof(PlaceholderVisibility));
        }
    }

    /// <summary>普通单选高亮（单击选中、双击打开）。</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Raise(nameof(SelectionOpacity));
        }
    }

    /// <summary>多选状态下这张被选进了集合。</summary>
    public bool MultiSelected
    {
        get => _multiSelected;
        set
        {
            if (_multiSelected == value) return;
            _multiSelected = value;
            Raise(nameof(MultiSelectedVisibility));
            Raise(nameof(SelectionOpacity));
        }
    }

    /// <summary>多选勾选角标：只在多选模式里、且这张被选中时才露出来。</summary>
    public Visibility MultiSelectedVisibility =>
        _multiSelected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 蓝色选中框的不透明度。普通单选或都选进多选集合都显示，
    /// 用不透明度切换省掉给每个格子分配画刷。
    /// </summary>
    public double SelectionOpacity => (_selected || _multiSelected) ? 1.0 : 0.0;

    /// <summary>还没解出来时显示的占位图标。</summary>
    public Visibility PlaceholderVisibility =>
        _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>解不出来（坏图 / 不支持的格式）。</summary>
    public Visibility FailedVisibility =>
        _failed ? Visibility.Visible : Visibility.Collapsed;

    public void MarkFailed()
    {
        if (_failed) return;
        _failed = true;
        Raise(nameof(FailedVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
