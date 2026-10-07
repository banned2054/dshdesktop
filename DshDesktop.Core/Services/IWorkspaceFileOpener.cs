namespace DshDesktop.Core.Services;

/// <summary>Opens an existing workspace file in the operating system's default application.</summary>
public interface IWorkspaceFileOpener
{
    /// <summary>Open the absolute path supplied by the active session.</summary>
    /// <exception cref="FileNotFoundException">The declared file no longer exists.</exception>
    void Open(string absolutePath);
}
