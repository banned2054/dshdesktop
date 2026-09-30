using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DshDesktop.Services.Platform;

/// <summary>系统文件夹选择服务；仅视图 code-behind 调用，保留原命名空间兼容既有调用。</summary>
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

    /// <summary>两处工作区入口共用选择、登记和错误呈现流程。</summary>
    internal static async Task PickAndRegisterWorkspaceAsync(
        Visual owner, Func<string?, Task> registerWorkspace, Action<Exception> reportError)
    {
        try
        {
            var folder = await PickFolderAsync(owner, "选择要登记为工作区的文件夹");
            if (folder is not null) await registerWorkspace(folder);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            reportError(exception);
        }
    }
}
