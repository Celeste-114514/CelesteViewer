using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CelesteGallery.Helpers;

/// <summary>
/// 非打包程序（WindowsPackageType=None）里调系统文件/文件夹选择器的正确姿势。
///
/// ⚠️ 不改水瓶 味精：不把窗口句柄喂给选择器，对话框会<b>一闪就关</b>，
/// 表现像"点了没反应"，非常难查。
/// </summary>
public static class StoragePicker
{
    // 这个 COM 接口的 IID 是固定的。之所以不用现成的 C# 投影类型：
    // 不同版本的 CsWinRT 里它叫什么、在哪个命名空间都不一样，
    // 自己按 GUID 声明最稳，不依赖那个名字。
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3E68D4BD-7135-4D10-8015-9FBF3936F305")]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    private static void Attach(IntPtr hwnd, object picker)
    {
        if (hwnd == IntPtr.Zero) return;
        ((IInitializeWithWindow)(object)picker).Initialize(hwnd);
    }

    /// <summary>选一个文件夹。取消返回 null。</summary>
    public static async Task<StorageFolder?> PickFolderAsync(IntPtr hwnd)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            Attach(hwnd, picker);
            return await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("StoragePicker: 选目录失败 → " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 另存为。<paramref name="types"/> 是 (显示名, 扩展名) 列表，至少给一项。
    /// 取消返回 null。
    /// </summary>
    public static async Task<StorageFile?> PickSaveFileAsync(
        IntPtr hwnd,
        string suggestedName,
        IReadOnlyList<(string Name, string Extension)> types)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.SuggestedFileName = suggestedName;
            foreach (var (name, ext) in types)
            {
                var list = new List<string> { ext.StartsWith('.') ? ext : "." + ext };
                picker.FileTypeChoices.Add(name, list);
            }
            Attach(hwnd, picker);
            return await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("StoragePicker: 另存为失败 → " + ex.Message);
            return null;
        }
    }
}
