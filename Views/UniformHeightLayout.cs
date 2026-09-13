using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace CelesteViewer.Views;

/// <summary>
/// 等高布局（"展示方式 → 等高"用）。
///
/// 每行高度固定（<see cref="RowHeight"/>），每张图按自己的宽高比决定宽度，
/// 一行排不下就换到下一行 —— 视觉上就是 Lightroom / 相册那种"定高、变宽"的格子。
///
/// 虚拟化做法和方形（UniformGridLayout）一样：**只 realize 视口里的格子**。
/// 关键点：算位置时只读宽高比，不碰 UI 元素（那样会逼着把所有格子都建出来，
/// 上万张就卡死了）。宽高比通过 <see cref="AspectOf"/> 回调拿，
/// 数据源那边在缩略图解出来后回填，布局只在等高模式下随它重排。
/// </summary>
public sealed class UniformHeightLayout : VirtualizingLayout
{
    /// <summary>每行高度（也是每张图的显示高度）。</summary>
    public double RowHeight { get; set; } = 180;

    /// <summary>格子之间、行之间的间距。</summary>
    public double Spacing { get; set; } = 6;

    /// <summary>
    /// 第 i 个格子的宽高比（宽 / 高）。布局只靠它算宽度，不建 UI 元素。
    /// 返回不了就当 1.0（正方形占位），等真实值回填后再重排。
    /// </summary>
    public Func<int, double>? AspectOf { get; set; }

    private readonly List<Rect> _rects = new();
    private Size _total;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size available)
    {
        _rects.Clear();

        int n = context.ItemCount;
        double sp = Spacing;
        double rowH = RowHeight;
        double maxW = available.Width > 0 ? available.Width : double.MaxValue;

        double x = 0, y = 0;
        for (int i = 0; i < n; i++)
        {
            double aspect = AspectOf?.Invoke(i) ?? 1.0;
            double w = Math.Max(1, rowH * aspect);

            // 当前行放不下这张就换行（第一张永远不换行，免得一行只有一张还空一大截）
            if (x > 0 && x + w > maxW)
            {
                x = 0;
                y += rowH + sp;
            }

            _rects.Add(new Rect(x, y, w, rowH));
            x += w + sp;
        }

        double totalH = y + (x > 0 ? rowH : 0) + sp;
        _total = new Size(available.Width, totalH);
        return _total;
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size final)
    {
        int n = Math.Min(context.ItemCount, _rects.Count);
        for (int i = 0; i < n; i++)
        {
            // WinUI3 没有 GetElement。取"已经 realize 的"元素用
            // GetOrCreateElementAt(index, None) —— None = 没 realize 就返回 null、不新建，
            // 正是原来的语义：Arrange 阶段只摆已经存在的元素，不凭空造。
            var element = context.GetOrCreateElementAt(i, ElementRealizationOptions.None);
            if (element is not null) element.Arrange(_rects[i]);
        }

        return _total;
    }

    // 注：WinUI3 的 VirtualizingLayout 未暴露 GetElementsThatProvisionSpace 虚方法，故不重写。
    // 虚拟化仍由 MeasureOverride / ArrangeOverride 只 realize 视口内元素来保证，不受影响。

    /// <summary>数据源变了（换目录 / 重排）时让 ItemsRepeater 重新问一遍位置。</summary>
    public new void InvalidateMeasure()
    {
        base.InvalidateMeasure();
    }
}
