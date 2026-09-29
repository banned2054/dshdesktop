using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DshDesktop.Utils;

/// <summary>系统文件夹选择对话框的小封装；无状态，仅视图 code-behind 调用。</summary>
public static class FolderPicker
{
    /// <summary>
    ///     打开单个文件夹选择对话框并返回所选本地路径；TopLevel 缺失、用户取消或
    ///     选择非文件系统位置时返回 null。
    /// </summary>
    public static async Task<string?> PickFolderAsync(Visual owner, string title)
    {
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = title,
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
