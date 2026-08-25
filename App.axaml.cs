using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Libreguard.Vpn.Linux.Services;
using Libreguard.Vpn.Linux.ViewModels;
using Libreguard.Vpn.Linux.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Libreguard.Vpn.Linux;

public sealed partial class App : Application
{
    private ServiceProvider? _services;
    private IThemePreferenceService? _themePreferenceService;
    private TrayIcons? _trayIcons;
    private TrayIcon? _trayIcon;
    private MainViewModel? _trayViewModel;
    private PosixSignalRegistration? _sigIntRegistration;
    private PosixSignalRegistration? _sigTermRegistration;
    private PosixSignalRegistration? _sigHupRegistration;
    private int _vpnExitCleanupStarted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupDiagnostics.Log("framework-init-enter");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args: { } args } smokeDesktop &&
            args.Contains("--webview-smoke", StringComparer.Ordinal))
        {
            var smokeWindow = new WebViewSmokeWindow();
            smokeWindow.Completed += exitCode =>
            {
                Environment.ExitCode = exitCode;
                Dispatcher.UIThread.Post(() => smokeDesktop.Shutdown(exitCode));
            };
            smokeDesktop.MainWindow = smokeWindow;
            StartupDiagnostics.Log("webview-smoke-window-ready");
            base.OnFrameworkInitializationCompleted();
            return;
        }

        using (StartupDiagnostics.WatchStep("service-registry-build"))
        {
            _services = ServiceRegistry.Build();
        }

        using (StartupDiagnostics.WatchStep("register-process-exit-handlers"))
        {
            RegisterProcessExitHandlers();
        }

        using (StartupDiagnostics.WatchStep("resolve-theme-service"))
        {
            _themePreferenceService = _services.GetRequiredService<IThemePreferenceService>();
            _themePreferenceService.PreferenceChanged += HandleThemePreferenceChanged;
        }

        using (StartupDiagnostics.WatchStep("apply-theme-preference"))
        {
            ApplyThemePreference(_themePreferenceService.CurrentPreference);
            ActualThemeVariantChanged += HandleActualThemeVariantChanged;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainViewModel viewModel;
            using (StartupDiagnostics.WatchStep("resolve-main-view-model"))
            {
                viewModel = _services.GetRequiredService<MainViewModel>();
            }

            using (StartupDiagnostics.WatchStep("main-window-create"))
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel
                };
                _services.GetRequiredService<AvaloniaFileSavePickerService>().Owner = desktop.MainWindow;
            }

            desktop.MainWindow.Opened += HandleMainWindowOpened;

            try
            {
                using (StartupDiagnostics.WatchStep("tray-icon-setup"))
                {
                    ConfigureTrayIcon();
                }
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"tray-icon-setup-failed type={ex.GetType().Name}");
            }
        }

        StartupDiagnostics.Log("framework-init-exit");
        base.OnFrameworkInitializationCompleted();
    }

    private void HandleThemePreferenceChanged(object? sender, EventArgs e)
    {
        if (_themePreferenceService is null)
        {
            return;
        }

        ApplyThemePreference(_themePreferenceService.CurrentPreference);
    }

    private void HandleActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ThemePalette.Apply(this, ActualThemeVariant);
    }

    private void ApplyThemePreference(AppThemePreference preference)
    {
        RequestedThemeVariant = preference.ToRequestedThemeVariant();
        ThemePalette.Apply(this, ActualThemeVariant);
    }

    private void ConfigureTrayIcon()
    {
        if (_trayIcons is not null)
        {
            StartupDiagnostics.Log("tray-icon-skip-existing");
            return;
        }

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || _services is null)
        {
            StartupDiagnostics.Log("tray-icon-skip-no-desktop");
            return;
        }

        _trayViewModel = _services.GetRequiredService<MainViewModel>();
        _trayViewModel.PropertyChanged += HandleTrayViewModelChanged;
        _trayViewModel.ServerGroups.CollectionChanged += HandleTrayServerGroupsChanged;

        var trayIcon = new TrayIcon
        {
            ToolTipText = _trayViewModel.TrayToolTipText,
            IsVisible = true,
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://libreguard-vpn-linux/Resources/LibreGuard_logo_login_rounded12.png"))),
            Menu = BuildTrayMenu(desktop, _trayViewModel)
        };
        trayIcon.Clicked += (_, _) => ShowMainWindow(desktop);

        _trayIcon = trayIcon;
        _trayIcons = new TrayIcons { trayIcon };
        TrayIcon.SetIcons(this, _trayIcons);
        StartupDiagnostics.Log("tray-icon-ready");
    }

    private void HandleTrayViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trayIcon is null || _trayViewModel is null || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        if (e.PropertyName is not (nameof(MainViewModel.TrayToolTipText)
            or nameof(MainViewModel.TrayTopActionText)
            or nameof(MainViewModel.CanUseTrayServers)
            or nameof(MainViewModel.IsConnected)
            or nameof(MainViewModel.IsConnectRunning)
            or nameof(MainViewModel.IsQuickConnectRunning)))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _trayIcon.ToolTipText = _trayViewModel.TrayToolTipText;
            _trayIcon.Menu = BuildTrayMenu(desktop, _trayViewModel);
        });
    }

    private void HandleTrayServerGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_trayIcon is null || _trayViewModel is null || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _trayIcon.Menu = BuildTrayMenu(desktop, _trayViewModel));
    }

    private NativeMenu BuildTrayMenu(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel)
    {
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem
        {
            Header = viewModel.TrayTopActionText,
            Command = viewModel.IsConnected ? viewModel.DisconnectCommand : viewModel.QuickConnectCommand,
            IsEnabled = viewModel.IsConnected || viewModel.QuickConnectCommand.CanExecute(null)
        });

        var serversMenu = new NativeMenu();
        foreach (var group in viewModel.ServerGroups)
        {
            var countryMenu = new NativeMenu();
            foreach (var server in group.Servers)
            {
                countryMenu.Add(new NativeMenuItem
                {
                    Header = $"{server.CountryFlag} {server.DisplayName}",
                    Command = viewModel.ConnectToServerCommand,
                    CommandParameter = server,
                    IsEnabled = viewModel.CanUseTrayServer(server)
                });
            }

            var flag = group.Servers.FirstOrDefault()?.CountryFlag ?? "?";
            serversMenu.Add(new NativeMenuItem
            {
                Header = $"{flag} {group.Country} ({group.Count})",
                Menu = countryMenu,
                IsEnabled = viewModel.CanUseTrayServers && group.Servers.Any(viewModel.CanUseTrayServer)
            });
        }

        if (viewModel.ServerGroups.Count == 0)
        {
            serversMenu.Add(new NativeMenuItem
            {
                Header = "No servers loaded",
                IsEnabled = false
            });
        }

        menu.Add(new NativeMenuItem
        {
            Header = "Servers",
            Menu = serversMenu,
            IsEnabled = viewModel.CanUseTrayServers && viewModel.ServerGroups.Any(group => group.Servers.Any(viewModel.CanUseTrayServer))
        });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem
        {
            Header = "Exit",
            Command = new RelayCommand(_ => _ = ExitFromTrayAsync(desktop, viewModel))
        });

        return menu;
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is null)
        {
            return;
        }

        if (desktop.MainWindow.WindowState == WindowState.Minimized)
        {
            desktop.MainWindow.WindowState = WindowState.Normal;
        }

        desktop.MainWindow.Show();
        desktop.MainWindow.Activate();
    }

    private static async Task ExitFromTrayAsync(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel)
    {
        if (await viewModel.PrepareForExitAsync(CancellationToken.None))
        {
            desktop.Shutdown();
        }
    }

    private void HandleMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not Window window || _services is null)
        {
            return;
        }

        window.Opened -= HandleMainWindowOpened;

        var viewModel = window.DataContext as MainViewModel ?? _services.GetRequiredService<MainViewModel>();
        StartupDiagnostics.Log("main-window-opened");
        StartupDiagnostics.Log("vpn-startup-cleanup-launch");
        _ = Task.Run(() => TryCleanupVpnStateAsync("startup"));
        StartupDiagnostics.Log("theme-initialize-launch");
        _ = InitializeThemePreferenceAsync();
        StartupDiagnostics.Log("main-view-model-initialize-launch");
        _ = InitializeMainViewModelAsync(viewModel);
    }

    private async Task InitializeThemePreferenceAsync()
    {
        if (_themePreferenceService is null)
        {
            return;
        }

        try
        {
            using (StartupDiagnostics.WatchStep("theme-initialize-background"))
            {
                await _themePreferenceService.InitializeAsync(CancellationToken.None);
            }

            ApplyThemePreference(_themePreferenceService.CurrentPreference);
            StartupDiagnostics.Log("theme-initialize-complete");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"theme-initialize-failed type={ex.GetType().Name}");
        }
    }

    private async Task InitializeMainViewModelAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync();
            StartupDiagnostics.Log("main-view-model-initialize-complete");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"main-view-model-initialize-failed type={ex.GetType().Name}");
        }
    }

    private void RegisterProcessExitHandlers()
    {
        AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
        if (OperatingSystem.IsLinux())
        {
            _sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => TryCleanupVpnState("signal-int"));
            _sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => TryCleanupVpnState("signal-term"));
            _sigHupRegistration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => TryCleanupVpnState("signal-hup"));
        }
    }

    private void HandleProcessExit(object? sender, EventArgs e)
        => TryCleanupVpnState("process-exit");

    private void TryCleanupVpnState(string reason)
    {
        if (_services is null || Interlocked.Exchange(ref _vpnExitCleanupStarted, 1) != 0)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            StartupDiagnostics.Log($"vpn-cleanup-begin reason=\"{reason}\"");
            _services.GetRequiredService<IVpnConnectionService>()
                .ShutdownAsync(cts.Token)
                .GetAwaiter()
                .GetResult();
            StartupDiagnostics.Log($"vpn-cleanup-complete reason=\"{reason}\"");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"vpn-cleanup-failed reason=\"{reason}\" type={ex.GetType().Name}");
        }
    }

    private async Task TryCleanupVpnStateAsync(string reason)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            StartupDiagnostics.Log($"vpn-cleanup-begin reason=\"{reason}\"");
            await _services.GetRequiredService<IVpnConnectionService>().ShutdownAsync(cts.Token);
            StartupDiagnostics.Log($"vpn-cleanup-complete reason=\"{reason}\"");
        }
        catch (OperationCanceledException)
        {
            StartupDiagnostics.Log($"vpn-cleanup-timeout reason=\"{reason}\"");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"vpn-cleanup-failed reason=\"{reason}\" type={ex.GetType().Name}");
        }
    }
}
