using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Views;

public sealed partial class CardCheckoutWindow : Window
{
    private static readonly TimeSpan AdapterCreationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NavigationOverlayTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NavigationCompletionTimeout = TimeSpan.FromSeconds(20);
    private readonly Uri? _checkoutUri;
    private readonly IExternalUriLauncher? _externalUriLauncher;
    private readonly IReadOnlyList<CheckoutWebViewProfile> _profiles = LinuxWebViewEnvironment.Selection.Profiles;
    private CardCheckoutWindowResult _closeResult = CardCheckoutWindowResult.Closed;
    private NativeWebView? _checkoutWebView;
    private CancellationTokenSource? _attemptCts;
    private CheckoutWebViewProfile _currentProfile;
    private int _profileIndex = -1;
    private int _attemptGeneration;
    private bool _adapterCreated;
    private bool _surfaceResetPending;
    private bool _surfaceResetCompleted;
    private bool _navigationRequested;
    private bool _navigationCompleted;

    internal CardCheckoutWindowResult CloseResult => _closeResult;

    public CardCheckoutWindow()
    {
        InitializeComponent();
    }

    public CardCheckoutWindow(CardCheckoutWindowRequest request, IExternalUriLauncher externalUriLauncher)
        : this()
    {
        _externalUriLauncher = externalUriLauncher;
        Opened += HandleOpened;
        Closed += HandleClosed;
        var amount = request.AmountUsd > 0
            ? $"{request.AmountUsd:0.##} {request.Currency ?? "USD"}"
            : request.Currency ?? "Card payment";
        var cycle = string.IsNullOrWhiteSpace(request.BillingCycle) ? "selected billing cycle" : request.BillingCycle;
        SummaryText.Text = $"Secure checkout for {cycle} Pro subscription ({amount}).";
        if (Uri.TryCreate(request.CheckoutUrl, UriKind.Absolute, out var checkoutUri) &&
            CheckoutUrlPolicy.IsAllowed(checkoutUri))
        {
            _checkoutUri = checkoutUri;
            CheckoutStatusText.Text = "Preparing secure checkout...";
            CheckoutStatusDetailText.Text = checkoutUri.Host;
            StartupDiagnostics.Log($"checkout-navigation-ready host={checkoutUri.Host}");
        }
        else
        {
            CheckoutStatusText.Text = "Checkout URL is invalid.";
            CheckoutStatusDetailText.Text = "Close this window and try the card payment again.";
        }
    }

    internal async Task CompletePaidAsync()
    {
        _closeResult = CardCheckoutWindowResult.Paid;
        CheckoutStatusOverlay.IsVisible = true;
        CheckoutStatusText.Text = "Payment confirmed";
        CheckoutStatusDetailText.Text = "Your Pro subscription is active. Returning to LibreGuard...";
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (IsVisible) Close(CardCheckoutWindowResult.Paid);
    }

    internal void ShowTerminalStatus(CardCheckoutWindowResult result)
    {
        _closeResult = result;
        CheckoutStatusOverlay.IsVisible = true;
        CheckoutStatusText.Text = result switch
        {
            CardCheckoutWindowResult.Failed => "Payment failed",
            CardCheckoutWindowResult.Canceled => "Checkout canceled",
            CardCheckoutWindowResult.Refunded => "Payment refunded",
            CardCheckoutWindowResult.TimedOut => "Still waiting for confirmation",
            _ => "Checkout status changed"
        };
        CheckoutStatusDetailText.Text = result == CardCheckoutWindowResult.TimedOut
            ? "Confirmation is taking longer than expected. You can close this window and check your account later."
            : "Your account was not upgraded. Close this window to try again.";
    }

    internal void CancelCheckout()
    {
        _closeResult = CardCheckoutWindowResult.Closed;
        if (IsVisible) Close();
    }

    private void HandleEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (!ReferenceEquals(sender, _checkoutWebView)) return;
        try
        {
            LinuxWebViewEnvironment.Configure(e, _currentProfile);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"checkout-webview-environment-error type={ex.GetType().Name}");
            Dispatcher.UIThread.Post(() => FallbackFromProfile("environment-error"));
        }
    }

    private void HandleAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (!ReferenceEquals(sender, _checkoutWebView) || _adapterCreated) return;
        _adapterCreated = true;
        var adapter = _checkoutWebView?.AdapterInfo?.ToString() ?? e.TryGetPlatformHandle()?.GetType().Name ?? "unknown";
        StartupDiagnostics.Log(
            $"checkout-webview-adapter-created profile={LinuxWebViewEnvironment.ProfileName(_currentProfile)} adapter={Sanitize(adapter)}");

        if (_attemptCts is { } attemptCts)
        {
            _ = WatchNavigationAsync(_attemptGeneration, attemptCts.Token);
        }

        if (_currentProfile == CheckoutWebViewProfile.GtkOffscreenCompatibility && !_surfaceResetCompleted)
        {
            _surfaceResetCompleted = true;
            _surfaceResetPending = true;
            if (_checkoutWebView is { } webView) webView.IsVisible = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || _checkoutWebView is not { } current) return;
                current.IsVisible = true;
                _surfaceResetPending = false;
                StartupDiagnostics.Log("checkout-webview-gtk-surface-reset");
                RequestNavigation();
            }, DispatcherPriority.Loaded);
            return;
        }

        RequestNavigation();
    }

    private void HandleOpened(object? sender, EventArgs e)
    {
        StartupDiagnostics.Log("checkout-window-opened");
        if (_checkoutUri is null)
        {
            return;
        }

        // The GTK adapter must not be created while this Window is still being
        // constructed. In restrictive X11 VMs that can synchronously wait for a
        // native surface and prevent both the window and browser fallback from
        // ever becoming interactive.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && _profileIndex < 0)
            {
                ActivateNextProfile("window-opened");
            }
        }, DispatcherPriority.Loaded);
    }

    private void HandleNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _checkoutWebView)) return;
        if (!CheckoutUrlPolicy.IsAllowedResource(e.Request))
        {
            e.Cancel = true;
            StartupDiagnostics.Log(
                $"checkout-navigation-blocked scheme={e.Request?.Scheme ?? "unknown"} host={e.Request?.Host ?? "unknown"}");
            return;
        }
        if (IsAuxiliaryResourceNavigation(e.Request))
        {
            StartupDiagnostics.Log($"checkout-navigation-resource-started host={e.Request?.Host ?? "unknown"}");
            return;
        }

        if (_navigationCompleted) return;
        CheckoutStatusOverlay.IsVisible = true;
        CheckoutStatusText.Text = "Loading secure checkout...";
        CheckoutStatusDetailText.Text = e.Request?.Host ?? _checkoutUri?.Host ?? string.Empty;
        StartupDiagnostics.Log($"checkout-navigation-started host={e.Request?.Host ?? "unknown"}");
    }

    private void HandleNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _checkoutWebView)) return;
        if (IsAuxiliaryResourceNavigation(e.Request))
        {
            StartupDiagnostics.Log($"checkout-navigation-resource-completed host={e.Request?.Host ?? "unknown"} success={e.IsSuccess}");
            return;
        }

        StartupDiagnostics.Log($"checkout-navigation-completed host={e.Request?.Host ?? "unknown"} success={e.IsSuccess}");
        if (e.IsSuccess)
        {
            _navigationCompleted = true;
            _attemptCts?.Cancel();
            CheckoutStatusOverlay.IsVisible = false;
            return;
        }

        FallbackFromProfile("navigation-failed");
    }

    private void HandleNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        if (!ReferenceEquals(sender, _checkoutWebView)) return;
        if (e.Request is { } target && CheckoutUrlPolicy.IsAllowed(target) && _checkoutWebView is { } webView)
        {
            StartupDiagnostics.Log($"checkout-new-window-redirected host={target.Host}");
            webView.Source = target;
            _navigationRequested = true;
            _navigationCompleted = false;
            e.Handled = true;
        }
    }

    private async void HandleBrowserClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_checkoutUri is null || _externalUriLauncher is null)
        {
            ShowBrowserFallback("Browser checkout is unavailable.", "Close this window and use Continue in Browser in LibreGuard.");
            return;
        }

        try
        {
            var result = await _externalUriLauncher.OpenAsync(_checkoutUri, CancellationToken.None);
            if (result.Success)
            {
                StartupDiagnostics.Log("checkout-browser-launch-success");
                if (_currentProfile == CheckoutWebViewProfile.Browser || _checkoutWebView is null)
                {
                    ShowBrowserFallback(
                        "Checkout opened in your browser.",
                        "LibreGuard is still monitoring this payment automatically.");
                }
                else
                {
                    CheckoutStatusOverlay.IsVisible = false;
                }
                return;
            }

            StartupDiagnostics.Log("checkout-browser-launch-failed");
            ShowBrowserFallback("Browser checkout could not be opened.", result.ErrorMessage);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"checkout-browser-launch-error type={ex.GetType().Name}");
            ShowBrowserFallback("Browser checkout could not be opened.");
        }
    }

    private void HandleCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(_closeResult);

    private void HandleClosed(object? sender, EventArgs e)
    {
        _attemptCts?.Cancel();
        _attemptCts?.Dispose();
        StartupDiagnostics.Log($"checkout-window-closed result={_closeResult}");
    }

    private void ActivateNextProfile(string reason)
    {
        _profileIndex++;
        if (_profileIndex >= _profiles.Count)
        {
            ShowBrowserFallback("Embedded checkout is unavailable.");
            return;
        }

        _currentProfile = _profiles[_profileIndex];
        StartupDiagnostics.Log(
            $"checkout-webview-profile-activated profile={LinuxWebViewEnvironment.ProfileName(_currentProfile)} reason={Sanitize(reason)}");
        if (_currentProfile == CheckoutWebViewProfile.Browser)
        {
            RemoveCurrentWebView();
            ShowBrowserFallback("Embedded checkout is unavailable.");
            return;
        }

        RemoveCurrentWebView();
        ResetAttemptState();
        var webView = new NativeWebView();
        webView.EnvironmentRequested += HandleEnvironmentRequested;
        webView.AdapterCreated += HandleAdapterCreated;
        webView.NavigationStarted += HandleNavigationStarted;
        webView.NavigationCompleted += HandleNavigationCompleted;
        webView.NewWindowRequested += HandleNewWindowRequested;
        _checkoutWebView = webView;
        CheckoutWebViewHost.Content = webView;
        _attemptCts = new CancellationTokenSource();
        var generation = ++_attemptGeneration;
        _ = WatchAttemptAsync(generation, _attemptCts.Token);
        RequestNavigation();
    }

    private async Task WatchAttemptAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AdapterCreationTimeout, cancellationToken);
            if (generation != _attemptGeneration || !IsVisible) return;
            if (!_adapterCreated)
            {
                StartupDiagnostics.Log("checkout-webview-adapter-timeout");
                FallbackFromProfile("adapter-timeout");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WatchNavigationAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NavigationOverlayTimeout, cancellationToken);
            if (generation != _attemptGeneration || !IsVisible || _navigationCompleted) return;
            StartupDiagnostics.Log("checkout-navigation-overlay-timeout");
            CheckoutStatusOverlay.IsVisible = false;

            await Task.Delay(NavigationCompletionTimeout - NavigationOverlayTimeout, cancellationToken);
            if (generation != _attemptGeneration || !IsVisible || _navigationCompleted) return;
            if (_adapterCreated)
            {
                StartupDiagnostics.Log("checkout-navigation-timeout");
                FallbackFromProfile("navigation-timeout");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FallbackFromProfile(string reason)
    {
        if (_currentProfile == CheckoutWebViewProfile.Browser) return;
        StartupDiagnostics.Log(
            $"checkout-webview-profile-failed profile={LinuxWebViewEnvironment.ProfileName(_currentProfile)} reason={Sanitize(reason)}");
        ActivateNextProfile(reason);
    }

    private void RequestNavigation()
    {
        if (_navigationRequested || _checkoutUri is null || !IsVisible || _surfaceResetPending || _checkoutWebView is null) return;
        _navigationRequested = true;
        _navigationCompleted = false;
        CheckoutStatusOverlay.IsVisible = true;
        CheckoutStatusText.Text = "Loading secure checkout...";
        CheckoutStatusDetailText.Text = _checkoutUri.Host;
        StartupDiagnostics.Log(
            $"checkout-navigation-requested profile={LinuxWebViewEnvironment.ProfileName(_currentProfile)} host={_checkoutUri.Host}");
        _checkoutWebView.Source = _checkoutUri;
    }

    private void ResetAttemptState()
    {
        _adapterCreated = false;
        _surfaceResetPending = false;
        _surfaceResetCompleted = false;
        _navigationRequested = false;
        _navigationCompleted = false;
    }

    private void RemoveCurrentWebView()
    {
        _attemptCts?.Cancel();
        _attemptCts?.Dispose();
        _attemptCts = null;
        if (_checkoutWebView is not { } webView) return;
        webView.EnvironmentRequested -= HandleEnvironmentRequested;
        webView.AdapterCreated -= HandleAdapterCreated;
        webView.NavigationStarted -= HandleNavigationStarted;
        webView.NavigationCompleted -= HandleNavigationCompleted;
        webView.NewWindowRequested -= HandleNewWindowRequested;
        CheckoutWebViewHost.Content = null;
        _checkoutWebView = null;
    }

    private void ShowBrowserFallback(string heading, string? detail = null)
    {
        CheckoutStatusOverlay.IsVisible = true;
        CheckoutStatusText.Text = heading;
        CheckoutStatusDetailText.Text = detail ?? "Use Continue in Browser to complete checkout.";
    }

    private static bool IsAuxiliaryResourceNavigation(Uri? request)
        => CheckoutUrlPolicy.IsAuxiliaryResource(request);

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '-').Replace('"', '\'');
}
