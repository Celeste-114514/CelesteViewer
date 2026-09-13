using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace CelesteViewer.Controls;

/// <summary>
/// 能改鼠标指针的空容器。
///
/// 为什么要专门写一个类：WinUI 里 <c>UIElement.ProtectedCursor</c> 是 **protected** 的，
/// 页面（Page 的子类）只能改自己的指针，改不了子元素的 —— 而裁剪时指针要在
/// "斜向箭头 / 十字 / 平移"之间跟着鼠标位置变，必须能对某个元素单独设。
/// 想设就只能有个自己的子类，在类内部去碰这个 protected 属性。
///
/// 只做这一件事，别往里加别的东西 —— 它会被放在整个界面最上层盖住一切。
/// </summary>
public sealed class CursorLayer : Grid
{
    /// <summary>
    /// 指针是"每次鼠标移动"都要刷的，而 <c>InputSystemCursor.Create</c> 每次
    /// 都会新建一个对象。不缓存的话，一次拖动就能造出上千个指针对象，
    /// 白白给 GC 添活。按形状缓存一份，全程复用。
    /// </summary>
    private static readonly Dictionary<InputSystemCursorShape, InputSystemCursor> Cache = new();

    private InputSystemCursorShape? _applied;

    /// <summary>换指针。传 null 恢复默认箭头。</summary>
    public void SetShape(InputSystemCursorShape? shape)
    {
        if (_applied == shape) return;
        _applied = shape;

        if (shape is null)
        {
            ProtectedCursor = null;
            return;
        }

        if (!Cache.TryGetValue(shape.Value, out var cursor))
        {
            cursor = InputSystemCursor.Create(shape.Value);
            Cache[shape.Value] = cursor;
        }

        ProtectedCursor = cursor;
    }
}
