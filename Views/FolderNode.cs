namespace CelesteViewer.Views;

/// <summary>
/// 左侧目录树上一个节点对应的文件夹。
///
/// 为什么把 Content 做成对象而不是直接塞一个字符串：
/// 光标悬停在条目上时要能显示**完整路径**（左栏只有 236px 宽，长路径会被截断），
/// 字符串内容没地方挂这个信息。做成对象之后，模板里
/// `{Binding Content.Label}` 显示名字、`{Binding Content.Path}` 当悬浮提示。
/// </summary>
public sealed class FolderNode
{
    /// <summary>文件夹完整路径。"图库"根节点是空串。</summary>
    public required string Path { get; init; }

    /// <summary>树上显示的名字（叶子目录名；根节点是"图库"）。</summary>
    public required string Label { get; init; }

    /// <summary>
    /// 是不是图库的直接条目。只有这些能"从图库中移除"；
    /// 从子目录展开出来的节点给的是"加入图库"。
    /// </summary>
    public bool InLibrary { get; init; }

    /// <summary>是不是"图库"根节点（没有路径、只当一个分组标题）。</summary>
    public bool IsRoot => Path.Length == 0;

    /// <summary>
    /// 树上这一行的图标。
    /// 名字和 <see cref="GroupNode.Glyph"/> 一致 —— 两种条目共用同一个
    /// DataTemplate，模板按名字取值，名字对不上图标就不显示。
    /// </summary>
    public string Glyph => "\uE8B7";

    public override string ToString() => Label;
}
