using Avalonia;

namespace DshDesktop;

internal class Program
{
    // 初始化前避免调用依赖 Avalonia 状态或 SynchronizationContext 的代码。
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
           .StartWithClassicDesktopLifetime(args);
    }

    // 设计器也使用此 Avalonia 配置入口。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
#if DEBUG
                     .WithDeveloperTools()
#endif
                     .WithInterFont()
                     .LogToTrace();
}
