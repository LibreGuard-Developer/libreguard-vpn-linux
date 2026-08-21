using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Libreguard.Vpn.Linux.ViewModels;

namespace Libreguard.Vpn.Linux.Views;

public sealed partial class MainWindow : Window
{
    private enum ConnectionVisualState
    {
        Idle,
        Preparing,
        Connected,
        Disconnecting
    }

    private MainViewModel? _viewModel;
    private INotifyPropertyChanged? _viewModelNotifier;
    private readonly DispatcherTimer _connectionAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _serverRefreshAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _sectionTransitionTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly TranslateTransform _mainContentTranslate = new(0, 0);
    private ConnectionVisualState _connectionVisualState = ConnectionVisualState.Idle;
    private DateTimeOffset _lastConnectionAnimationTick = DateTimeOffset.UtcNow;
    private DateTimeOffset _serverRefreshAnimationStartedAt;
    private DateTimeOffset _sectionTransitionStartedAt;
    private double _connectionAnimationPhase;
    private readonly RotateTransform _serverRefreshIconRotation = new(0);
    private ScaleTransform? _orbScale;
    private ScaleTransform? _glowScale;
    private bool _allowImmediateClose;
    private bool _isHandlingCloseRequest;

    public MainWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://libreguard-vpn-linux/Resources/LibreGuard_logo.png")));
        SizeChanged += HandleSizeChanged;
        Loaded += HandleLoaded;
        Closing += HandleClosing;
        Closed += HandleClosed;
        DataContextChanged += HandleDataContextChanged;
        _connectionAnimationTimer.Tick += HandleConnectionAnimationTick;
        _serverRefreshAnimationTimer.Tick += HandleServerRefreshAnimationTick;
        _sectionTransitionTimer.Tick += HandleSectionTransitionTick;
        InitializeConnectionTransforms();
        InitializeServerRefreshAnimation();
        InitializeSectionTransition();
    }

    private void HandleLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateLayoutMode();
        UpdateConnectionAnimationState(forceReset: true);
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowImmediateClose || _isHandlingCloseRequest || _viewModel is null || !_viewModel.IsExitConfirmationRequired)
        {
            return;
        }

        e.Cancel = true;
        _isHandlingCloseRequest = true;
        try
        {
            var dialog = new ExitConfirmationWindow();
            var confirmed = await dialog.ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            var isReadyToClose = await _viewModel.PrepareForExitAsync(CancellationToken.None);
            if (!isReadyToClose)
            {
                return;
            }

            _allowImmediateClose = true;
            Close();
        }
        finally
        {
            _isHandlingCloseRequest = false;
        }
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        StopConnectionAnimationTimer();
        StopServerRefreshAnimation();
        StopSectionTransition();
    }

    private void HandleSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateLayoutMode(Bounds.Width);
        }
    }

    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.TwoFactorSetupDialogRequested -= HandleTwoFactorSetupDialogRequested;
            _viewModel.TwoFactorDisableDialogRequested -= HandleTwoFactorDisableDialogRequested;
        }

        if (_viewModelNotifier is not null)
        {
            _viewModelNotifier.PropertyChanged -= HandleViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        _viewModelNotifier = _viewModel as INotifyPropertyChanged;

        if (_viewModel is not null)
        {
            _viewModel.TwoFactorSetupDialogRequested += HandleTwoFactorSetupDialogRequested;
            _viewModel.TwoFactorDisableDialogRequested += HandleTwoFactorDisableDialogRequested;
        }

        if (_viewModelNotifier is not null)
        {
            _viewModelNotifier.PropertyChanged += HandleViewModelPropertyChanged;
        }

        UpdateConnectionAnimationState(forceReset: true);
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel)
        {
            return;
        }

        UpdateConnectionAnimationState();
        if (e.PropertyName == nameof(MainViewModel.CurrentSection))
        {
            StartSectionTransition();
        }
    }

    private void InitializeSectionTransition()
    {
        if (MainContentHost is null)
        {
            return;
        }

        MainContentHost.RenderTransform = _mainContentTranslate;
        MainContentHost.Opacity = 1;
    }

    private void InitializeConnectionTransforms()
    {
        _orbScale = new ScaleTransform(1, 1);
        _glowScale = new ScaleTransform(1, 1);

        if (ConnectionRing is not null)
        {
            ConnectionRing.RenderTransform = null;
        }

        if (ConnectionOrb is not null)
        {
            ConnectionOrb.RenderTransform = _orbScale;
        }

        if (ConnectionGlow is not null)
        {
            ConnectionGlow.RenderTransform = _glowScale;
        }
    }

    private void InitializeServerRefreshAnimation()
    {
        if (ServerRefreshIcon is not null)
        {
            ServerRefreshIcon.RenderTransform = _serverRefreshIconRotation;
        }
    }

    private void HandleServerRefreshButtonClick(object? sender, RoutedEventArgs e)
    {
        _serverRefreshAnimationTimer.Stop();
        _serverRefreshIconRotation.Angle = 0;
        _serverRefreshAnimationStartedAt = DateTimeOffset.UtcNow;
        _serverRefreshAnimationTimer.Start();
    }

    private void HandleExternalLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string url || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/xdg-open")
            {
                ArgumentList = { url },
                UseShellExecute = false
            });
        }
        catch
        {
            // Ignore launch failures so a missing desktop opener does not block the UI.
        }
    }

    private void HandleServerRefreshAnimationTick(object? sender, EventArgs e)
    {
        const double animationDurationMilliseconds = 520.0;

        var elapsed = (DateTimeOffset.UtcNow - _serverRefreshAnimationStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / animationDurationMilliseconds, 0.0, 1.0);
        var easedProgress = 1.0 - Math.Pow(1.0 - progress, 3.0);

        _serverRefreshIconRotation.Angle = easedProgress * 360.0;

        if (progress >= 1.0)
        {
            StopServerRefreshAnimation();
        }
    }

    private void StopServerRefreshAnimation()
    {
        if (_serverRefreshAnimationTimer.IsEnabled)
        {
            _serverRefreshAnimationTimer.Stop();
        }

        _serverRefreshIconRotation.Angle = 0;
    }

    private void StartSectionTransition()
    {
        if (MainContentHost is null)
        {
            return;
        }

        if (MainContentScrollViewer is not null)
        {
            MainContentScrollViewer.Offset = new Vector(0, 0);
        }

        _sectionTransitionTimer.Stop();
        _sectionTransitionStartedAt = DateTimeOffset.UtcNow;
        MainContentHost.Opacity = 0;
        _mainContentTranslate.Y = 12;
        _sectionTransitionTimer.Start();
    }

    private void HandleSectionTransitionTick(object? sender, EventArgs e)
    {
        const double animationDurationMilliseconds = 220.0;

        if (MainContentHost is null)
        {
            StopSectionTransition();
            return;
        }

        var elapsed = (DateTimeOffset.UtcNow - _sectionTransitionStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / animationDurationMilliseconds, 0.0, 1.0);
        var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);

        MainContentHost.Opacity = eased;
        _mainContentTranslate.Y = (1.0 - eased) * 12.0;

        if (progress >= 1.0)
        {
            StopSectionTransition();
        }
    }

    private void StopSectionTransition()
    {
        if (_sectionTransitionTimer.IsEnabled)
        {
            _sectionTransitionTimer.Stop();
        }

        if (MainContentHost is not null)
        {
            MainContentHost.Opacity = 1;
        }

        _mainContentTranslate.Y = 0;
    }

    private void StartConnectionAnimationTimer()
    {
        if (!_connectionAnimationTimer.IsEnabled)
        {
            _lastConnectionAnimationTick = DateTimeOffset.UtcNow;
            _connectionAnimationTimer.Start();
        }
    }

    private void StopConnectionAnimationTimer()
    {
        if (_connectionAnimationTimer.IsEnabled)
        {
            _connectionAnimationTimer.Stop();
        }
    }

    private void HandleConnectionAnimationTick(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var deltaSeconds = Math.Max(0.0, (now - _lastConnectionAnimationTick).TotalSeconds);
        _lastConnectionAnimationTick = now;

        var visualState = ResolveVisualState(_viewModel);
        if (visualState != _connectionVisualState)
        {
            _connectionVisualState = visualState;
            _connectionAnimationPhase = 0;
            ApplyConnectionPalette(visualState);
        }

        _connectionAnimationPhase += deltaSeconds;
        ApplyConnectionMotion(visualState, _connectionAnimationPhase);
        if (!ShouldRunConnectionAnimation(visualState))
        {
            StopConnectionAnimationTimer();
        }
    }

    private void UpdateConnectionAnimationState(bool forceReset = false)
    {
        if (_viewModel is null)
        {
            return;
        }

        var visualState = ResolveVisualState(_viewModel);
        if (forceReset || visualState != _connectionVisualState)
        {
            _connectionVisualState = visualState;
            _connectionAnimationPhase = 0;
            _lastConnectionAnimationTick = DateTimeOffset.UtcNow;
            ApplyConnectionPalette(visualState);
            ApplyConnectionMotion(visualState, 0);
        }

        if (ShouldRunConnectionAnimation(visualState))
        {
            StartConnectionAnimationTimer();
        }
        else
        {
            StopConnectionAnimationTimer();
        }
    }

    private static bool ShouldRunConnectionAnimation(ConnectionVisualState visualState)
        => true;

    private static ConnectionVisualState ResolveVisualState(MainViewModel viewModel)
        => viewModel.IsConnectionDisconnecting
            ? ConnectionVisualState.Disconnecting
            : viewModel.IsConnectionConnecting
                ? ConnectionVisualState.Preparing
                : viewModel.IsConnectionConnected
                    ? ConnectionVisualState.Connected
                    : ConnectionVisualState.Idle;

    private void ApplyConnectionPalette(ConnectionVisualState visualState)
    {
        var palette = visualState switch
        {
            ConnectionVisualState.Preparing => (Accent: BrushFromArgb(0xFF, 0xF5, 0x9E, 0x0B), Light: BrushFromArgb(0x1A, 0xF5, 0x9E, 0x0B)),
            ConnectionVisualState.Connected => (Accent: BrushFromArgb(0xFF, 0x10, 0xB9, 0x81), Light: BrushFromArgb(0x1A, 0x10, 0xB9, 0x81)),
            ConnectionVisualState.Disconnecting => (Accent: BrushFromArgb(0xFF, 0x64, 0x74, 0x8B), Light: BrushFromArgb(0x1A, 0x64, 0x74, 0x8B)),
            _ => (Accent: BrushFromArgb(0xFF, 0x94, 0xA3, 0xB8), Light: BrushFromArgb(0x1A, 0x94, 0xA3, 0xB8))
        };

        if (ConnectionCardRoot is not null)
        {
            ConnectionCardRoot.BorderBrush = palette.Accent;
        }

        if (ConnectionGlow is not null)
        {
            ConnectionGlow.Fill = palette.Light;
        }

        if (ConnectionRing is not null)
        {
            ConnectionRing.Stroke = palette.Accent;
        }

        if (ConnectionOrb is not null)
        {
            ConnectionOrb.Fill = palette.Accent;
        }

        if (ConnectionCore is not null)
        {
            ConnectionCore.BorderBrush = palette.Accent;
        }

        if (ConnectionStatusTextBlock is not null)
        {
            ConnectionStatusTextBlock.Foreground = palette.Accent;
        }
    }

    private void ApplyConnectionMotion(ConnectionVisualState visualState, double phaseSeconds)
    {
        if (_orbScale is null || _glowScale is null || ConnectionRing is null || ConnectionGlow is null)
        {
            return;
        }

        switch (visualState)
        {
            case ConnectionVisualState.Preparing:
            {
                var pulse = 1.01 + Math.Sin(phaseSeconds * 4.0) * 0.01;
                _orbScale.ScaleX = 1.0;
                _orbScale.ScaleY = 1.0;
                _glowScale.ScaleX = 1.0 + Math.Sin(phaseSeconds * 1.8) * 0.025;
                _glowScale.ScaleY = _glowScale.ScaleX;
                ConnectionGlow.Opacity = 0.55;
                ConnectionRing.Opacity = 0.82;
                ConnectionRing.StrokeDashOffset = -phaseSeconds * 18.0;
                break;
            }
            case ConnectionVisualState.Connected:
            {
                var pulse = 1.0 + Math.Sin(phaseSeconds * 1.2) * 0.008;
                _orbScale.ScaleX = 1.0;
                _orbScale.ScaleY = 1.0;
                _glowScale.ScaleX = 1.0 + Math.Sin(phaseSeconds * 1.0) * 0.018;
                _glowScale.ScaleY = _glowScale.ScaleX;
                ConnectionGlow.Opacity = 0.42;
                ConnectionRing.Opacity = 0.68;
                ConnectionRing.StrokeDashOffset = -phaseSeconds * 5.0;
                break;
            }
            case ConnectionVisualState.Disconnecting:
            {
                var unwind = Math.Clamp(phaseSeconds / 0.7, 0.0, 1.0);
                var pulse = 1.0 - unwind * 0.03;
                _orbScale.ScaleX = 1.0;
                _orbScale.ScaleY = 1.0;
                _glowScale.ScaleX = 1.0 - unwind * 0.03;
                _glowScale.ScaleY = _glowScale.ScaleX;
                ConnectionGlow.Opacity = 0.38 - unwind * 0.08;
                ConnectionRing.Opacity = 0.52 - unwind * 0.16;
                ConnectionRing.StrokeDashOffset = phaseSeconds * 12.0;
                break;
            }
            default:
            {
                _orbScale.ScaleX = 1.0;
                _orbScale.ScaleY = 1.0;
                _glowScale.ScaleX = 1.0;
                _glowScale.ScaleY = 1.0;
                ConnectionGlow.Opacity = 0.26;
                ConnectionRing.Opacity = 0.20;
                ConnectionRing.StrokeDashOffset = phaseSeconds * 3.0;
                break;
            }
        }
    }

    private static SolidColorBrush BrushFromArgb(byte alpha, byte red, byte green, byte blue)
        => new(Color.FromArgb(alpha, red, green, blue));

    private async void HandleTwoFactorSetupDialogRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialogViewModel = _viewModel.CreateTwoFactorSetupDialogViewModel();
        var dialog = new TwoFactorSetupWindow
        {
            DataContext = dialogViewModel
        };

        var result = await dialog.ShowDialog<bool>(this);
        if (result)
        {
            _viewModel.CompleteTwoFactorSetupFlow();
            await _viewModel.RefreshCurrentAccountStateAsync();
            return;
        }

        _viewModel.CancelTwoFactorSetupFlow();
    }

    private async void HandleTwoFactorDisableDialogRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new TwoFactorDisableConfirmationWindow();
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed)
        {
            await _viewModel.ConfirmTwoFactorDisableAsync(CancellationToken.None);
            return;
        }

        _viewModel.CancelTwoFactorDisableFlow();
    }
}
