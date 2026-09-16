using Microsoft.UI.Xaml;

namespace CelesteGallery.Views;

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
    /// <summary>文件夹完整路径。"图库"根节点、以及分区标题行是空串。</summary>
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
    /// 是不是"分区标题"行（目前只有"社交缓存"这一条）。
    ///
    /// 微信 / QQ / 企业微信的缓存目录动辄十几万张，跟桌面、图片、下载
    /// 这些正常文件夹混在一起既不好找也不好认，所以在它们上面加一行标题，
    /// 上面再画一条分隔线，视觉上分成两段。
    ///
    /// 标题行没有路径（<see cref="Path"/> 为空），所以点了不加载任何目录、
    /// 也不会被展开 —— 复用 <see cref="IsRoot"/> 的既有判断即可。
    /// </summary>
    public bool IsSection { get; init; }

    /// <summary>分区标题上方那条分隔线。普通条目不显示。</summary>
    public Visibility SectionDividerVisibility
        => IsSection ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 分区标题上面留出空隙，让"两段"看起来是分开的而不是挤在一起。
    /// 普通条目零边距，保持原来的行距。
    /// </summary>
    public Thickness SectionMargin
        => IsSection ? new Thickness(0, 12, 0, 2) : new Thickness(0);

    /// <summary>
    /// 树上这一行的图标。
    /// 名字和 <see cref="GroupNode.Glyph"/> 一致 —— 两种条目共用同一个
    /// DataTemplate，模板按名字取值，名字对不上图标就不显示。
    ///
    /// 分区标题用联系人图标（和"按来源"那个维度一致，语义都是"来自哪个软件"）。
    /// <see cref="IconOverride"/> 非空时优先用它 —— 目前只有固定的「截图」条目在用，
    /// 它得跟工具条上那个截图按钮同一个相机图标，用户才反应得过来"这条就是我的截图"。
    /// </summary>
    public string Glyph => !string.IsNullOrEmpty(IconOverride)
        ? IconOverride!
        : (IsSection ? "\uE716" : "\uE8B7");

    /// <summary>
    /// 指定这一行用什么图标。留空 = 按上面的默认规则。
    /// 只给"不是普通文件夹、但又有真实路径"的特殊条目用。
    /// </summary>
    public string? IconOverride { get; init; }

    /// <summary>
    /// 是不是本程序自己的截图目录那条固定条目。
    ///
    /// 它和图库里的普通条目有三点不一样，菜单要按这个分开处理：
    ///   · 它**不在** library.txt 里，所以没有"从图库中移除"这一说 ——
    ///     移除只是把它从清单里划掉，下次启动又会出现，纯属骗人；
    ///   · 用户手动把同一个目录加进图库时，我们要靠它去重（不然左栏出现两条一样的）；
    ///   · 它永远存在，删掉目录也还在（截图功能就在那儿）。
    /// </summary>
    public bool IsSnipFolder { get; init; }

    /// <summary>分区标题的字体小一号，看着像一段的标题而不是一个可点开的文件夹。</summary>
    public double LabelFontSize => IsSection ? 12.5 : 14;

    public override string ToString() => Label;
}

