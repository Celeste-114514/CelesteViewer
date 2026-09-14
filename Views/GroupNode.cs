namespace CelesteViewer.Views;

/// <summary>
/// 左侧树上一个"分组条目"（按日期、按相机…时挂的那些行）。
///
/// 为什么不复用 <see cref="FolderNode"/>：
///   它没有"路径"这个东西 —— 一个日期组里的图可能散在十几个目录里，
///   硬塞一个路径进去，右键菜单就会拿它去"在资源管理器中打开"，那是错的。
///   分成两个类型之后，菜单按 Content 的实际类型决定给哪些操作，
///   不会把文件夹那套动作误用到分组上。
///
/// 和 FolderNode 一样保留 Label / Glyph / Path 三个同名成员：
///   树上两种条目共用同一个 DataTemplate，模板是按名字取值的，
///   名字对不上那一列就会空白（不报错，很难查）。
/// </summary>
public sealed class GroupNode
{
    /// <summary>分组值，比如 "2026-09"、"Canon EOS R6"。传回查询时用的就是它。</summary>
    public required string Key { get; init; }

    /// <summary>树上显示的名字（已包含数量）。</summary>
    public required string Label { get; init; }

    /// <summary>这个组里有多少张。</summary>
    public int Count { get; init; }

    /// <summary>是不是"全部"那一行。它是分组模式的根，点了等于不筛。</summary>
    public bool IsAll { get; init; }

    /// <summary>
    /// 图标。分组模式用文字符号区分维度：日历 / 相机 / 文件夹。
    /// 名字和 FolderNode.Glyph 一致，共用同一个模板。
    /// </summary>
    public string Glyph { get; init; } = "\uE8B7";

    /// <summary>
    /// 恒为空串。
    /// 模板里把它当悬浮提示用（{Binding Content.Path}），
    /// 分组没有路径，留空比显示一串假路径好。
    /// </summary>
    public string Path => string.Empty;

    public override string ToString() => Label;
}
