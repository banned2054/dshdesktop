using DshDesktop.Core.Services;
using System.Diagnostics;

namespace DshDesktop.Infrastructure.Services;

/// <summary>Opens a workspace file through the current operating system's file association.</summary>
public sealed class WorkspaceFileOpener : IWorkspaceFileOpener
{
    public void Open(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Expected an absolute workspace file path.", nameof(absolutePath));
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("The delivered file is missing or inaccessible.", absolutePath);

        Process.Start(new ProcessStartInfo(absolutePath) { UseShellExecute = true })?.Dispose();
    }
}
