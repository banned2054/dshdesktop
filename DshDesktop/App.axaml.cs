using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Services.Approvals;
using DshDesktop.Harness.Services.Changes;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Llm;
using DshDesktop.Harness.Services.Permissions;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Harness.Services.Settings;
using DshDesktop.Harness.Services.Workspaces;
using DshDesktop.Infrastructure.Services;
using DshDesktop.Infrastructure.Services.Backend;
using DshDesktop.Presentation.Views;
using DshDesktop.Services.Backend;
using DshDesktop.ViewModels;

namespace DshDesktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var                    configuration = DesktopBackendConfiguration.FromEnvironment();
            ISessionService        sessionService;
            IWorkspaceService      workspaceService;
            IBackendHostService    backendService;
            IToolApprovalService   toolApprovalService;
            IPermissionPresetService permissionPresetService;
            ISettingsService        settingsService;
            ICredentialsService     credentialService;
            ILlmCatalogService      llmCatalogService;
            IWorkspaceChangesService changesService;
            HarnessConnection?     connection      = null;
            var                    isSimulatedMode = true;

            // 置顶是本项目自有方案（本地配置文件持久化），与后端模式无关，两种模式共用一份。
            ISidebarPinService sidebarPinService = new SidebarPinService();

            if (configuration is { UseRealBackend: true, Options: { } options })
            {
                var hostService = new NodeBackendHostService(options);
                connection              = new HarnessConnection(hostService.StartAsync);
                sessionService          = new HarnessSessionService(connection, configuration.PreferredModel);
                workspaceService        = new HarnessWorkspaceService(connection);
                backendService          = hostService;
                toolApprovalService     = new HarnessToolApprovalService(connection);
                permissionPresetService = new HarnessPermissionPresetService(connection);
                // 设置域服务先行装配；设置界面落地时由 ViewModel 消费。
                settingsService         = new HarnessSettingsService(connection);
                credentialService       = new HarnessCredentialService(connection);
                llmCatalogService       = new HarnessLlmCatalogService(connection);
                // 文件改动域服务先行装配；改动卡片落地时由 ViewModel 消费。
                changesService          = new HarnessWorkspaceChangesService(connection);
                isSimulatedMode         = false;
            }
            else
            {
                var simulatedWorkspaces  = new SimulatedWorkspaceService();
                var simulatedSessions    = new SimulatedSessionService(simulatedWorkspaces.AddSession);
                var simulatedPermissions = new SimulatedPermissionPresetService();
                // 切换确认与真实后端同路径：经 permissions 投影回流（不乐观更新）。
                simulatedPermissions.PresetApplied += (_, applied) =>
                    simulatedSessions.PushPermission(applied.SessionId, applied.Preset);
                sessionService          = simulatedSessions;
                workspaceService        = simulatedWorkspaces;
                backendService          = new SimulatedBackendStatusService();
                toolApprovalService     = new SimulatedToolApprovalService();
                permissionPresetService = simulatedPermissions;
                settingsService         = new SimulatedSettingsService();
                credentialService       = new SimulatedCredentialsService();
                llmCatalogService       = new SimulatedLlmCatalogService();
                changesService          = new SimulatedWorkspaceChangesService();
            }

            var viewModel = new MainWindowViewModel(sessionService, backendService, workspaceService,
                                                    toolApprovalService,
                                                    isSimulatedMode,
                                                    action => Dispatcher.UIThread.Post(action),
                                                    permissionPresetService,
                                                    sidebarPinService,
                                                    settingsService,
                                                    credentialService,
                                                    // 设置面板的主题偏好回调：light/dark 原样应用，
                                                    // 其余（system/缺省）回退跟随系统。
                                                    preference => Application.Current!.RequestedThemeVariant =
                                                        preference == "light"
                                                            ? ThemeVariant.Light
                                                            : preference == "dark"
                                                                ? ThemeVariant.Dark
                                                                : null,
                                                    llmCatalogService);
            var mainWindow = new MainWindow(viewModel);
            if (configuration.ConfigurationError is { Length: > 0 } error)
            {
                viewModel.ShowStartupNotice(error);
            }

            desktop.MainWindow = mainWindow;
            var capturedConnection = connection;
            var capturedBackend    = backendService;
            var capturedWorkspaces = workspaceService;
            mainWindow.Closed += async (_, _) =>
            {
                await viewModel.DisposeAsync();
                if (capturedWorkspaces is IAsyncDisposable disposableWorkspaces)
                {
                    await disposableWorkspaces.DisposeAsync();
                }

                if (capturedConnection is not null)
                {
                    await capturedConnection.DisposeAsync();
                }

                await capturedBackend.DisposeAsync();
            };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
