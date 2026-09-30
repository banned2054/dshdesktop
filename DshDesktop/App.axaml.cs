using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Services.Approvals;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Permissions;
using DshDesktop.Harness.Services.Sessions;
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
            HarnessConnection?     connection      = null;
            var                    isSimulatedMode = true;

            if (configuration is { UseRealBackend: true, Options: { } options })
            {
                var hostService = new NodeBackendHostService(options);
                connection              = new HarnessConnection(hostService.StartAsync);
                sessionService          = new HarnessSessionService(connection, configuration.PreferredModel);
                workspaceService        = new HarnessWorkspaceService(connection);
                backendService          = hostService;
                toolApprovalService     = new HarnessToolApprovalService(connection);
                permissionPresetService = new HarnessPermissionPresetService(connection);
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
            }

            var viewModel = new MainWindowViewModel(sessionService, backendService, workspaceService,
                                                    toolApprovalService,
                                                    isSimulatedMode,
                                                    action => Dispatcher.UIThread.Post(action),
                                                    permissionPresetService);
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
