using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Libreguard.Vpn.Linux.Views;

namespace Libreguard.Vpn.Linux.Services;

public sealed class AvaloniaCardCheckoutWindowService : ICardCheckoutWindowService
{
    private readonly IBackendApiClient _backend;
    private readonly IAuthSessionService _authSession;
    private readonly IExternalUriLauncher _externalUriLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _fastPollingDuration;
    private readonly TimeSpan _fastPollingInterval;
    private readonly TimeSpan _slowPollingInterval;
    private readonly TimeSpan _pollingTimeout;
    private CardCheckoutWindow? _activeDialog;
    private Task<CardCheckoutWindowResult>? _activeCheckoutTask;

    public bool IsCheckoutActive => _activeDialog is { IsVisible: true } && _activeCheckoutTask is { IsCompleted: false };

    public AvaloniaCardCheckoutWindowService(
        IBackendApiClient backend,
        IAuthSessionService authSession,
        IExternalUriLauncher externalUriLauncher)
        : this(
            backend,
            authSession,
            externalUriLauncher,
            TimeProvider.System,
            Task.Delay,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(15))
    {
    }

    internal AvaloniaCardCheckoutWindowService(
        IBackendApiClient backend,
        IAuthSessionService authSession,
        IExternalUriLauncher externalUriLauncher,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeSpan fastPollingDuration,
        TimeSpan fastPollingInterval,
        TimeSpan slowPollingInterval,
        TimeSpan pollingTimeout)
    {
        _backend = backend;
        _authSession = authSession;
        _externalUriLauncher = externalUriLauncher;
        _timeProvider = timeProvider;
        _delayAsync = delayAsync;
        _fastPollingDuration = fastPollingDuration;
        _fastPollingInterval = fastPollingInterval;
        _slowPollingInterval = slowPollingInterval;
        _pollingTimeout = pollingTimeout;
    }

    public async Task<CardCheckoutWindowResult> ShowCheckoutAsync(CardCheckoutWindowRequest request, CancellationToken cancellationToken)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await await Dispatcher.UIThread.InvokeAsync(
                async () => await ShowCheckoutAsync(request, cancellationToken),
                DispatcherPriority.Normal,
                cancellationToken);
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            StartupDiagnostics.Log("checkout-window-unavailable reason=no-main-window");
            return CardCheckoutWindowResult.Unavailable;
        }

        if (_activeCheckoutTask is { IsCompleted: false })
        {
            _activeDialog?.Activate();
            StartupDiagnostics.Log("checkout-window-reused");
            return await _activeCheckoutTask;
        }

        CardCheckoutWindow dialog;
        try
        {
            StartupDiagnostics.Log("checkout-window-create-started");
            dialog = new CardCheckoutWindow(request, _externalUriLauncher);
            StartupDiagnostics.Log("checkout-window-create-completed");
        }
        catch (Exception ex)
        {
            LogException("checkout-window-create-error", ex);
            return CardCheckoutWindowResult.Unavailable;
        }

        _activeDialog = dialog;
        _activeCheckoutTask = RunCheckoutAsync(dialog, owner, request.TransactionId, cancellationToken);
        return await _activeCheckoutTask;
    }

    public void CancelCheckout()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(CancelCheckout);
            return;
        }

        if (_activeDialog is not { IsVisible: true } dialog)
        {
            return;
        }

        StartupDiagnostics.Log("checkout-window-cancel-requested");
        dialog.CancelCheckout();
    }

    private async Task<CardCheckoutWindowResult> RunCheckoutAsync(
        CardCheckoutWindow dialog,
        Window owner,
        string transactionId,
        CancellationToken cancellationToken)
    {
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dialogCompletion = new TaskCompletionSource<CardCheckoutWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? closedHandler = null;
        closedHandler = (_, _) => dialogCompletion.TrySetResult(dialog.CloseResult);
        dialog.Closed += closedHandler;

        try
        {
            dialog.Show(owner);
            dialog.Activate();
            StartupDiagnostics.Log("checkout-window-shown mode=modeless");

            var monitorTask = MonitorCheckoutAsync(transactionId, monitorCts.Token);
            var completed = await Task.WhenAny(dialogCompletion.Task, monitorTask);

            if (completed == dialogCompletion.Task)
            {
                monitorCts.Cancel();
                return await dialogCompletion.Task;
            }

            var paymentResult = await monitorTask;
            if (paymentResult == CardCheckoutWindowResult.Paid)
            {
                await dialog.CompletePaidAsync();
                return CardCheckoutWindowResult.Paid;
            }

            dialog.ShowTerminalStatus(paymentResult);
            var closeResult = await dialogCompletion.Task;
            return closeResult == CardCheckoutWindowResult.Closed ? paymentResult : closeResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (dialog.IsVisible)
            {
                dialog.Close(CardCheckoutWindowResult.Closed);
            }

            return CardCheckoutWindowResult.Closed;
        }
        catch (Exception ex)
        {
            LogException("checkout-window-show-error", ex);
            if (dialog.IsVisible)
            {
                dialog.Close(CardCheckoutWindowResult.Unavailable);
            }

            return CardCheckoutWindowResult.Unavailable;
        }
        finally
        {
            monitorCts.Cancel();
            dialog.Closed -= closedHandler;
            if (ReferenceEquals(_activeDialog, dialog))
            {
                _activeDialog = null;
                _activeCheckoutTask = null;
                StartupDiagnostics.Log("checkout-window-session-ended");
            }
        }
    }

    public async Task<ExternalUriLaunchResult> OpenInBrowserAsync(string checkoutUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var uri) || !CheckoutUrlPolicy.IsAllowed(uri))
        {
            return new ExternalUriLaunchResult(false, "Checkout URL is invalid.");
        }

        return await _externalUriLauncher.OpenAsync(uri, cancellationToken);
    }

    public async Task<CardCheckoutWindowResult> MonitorCheckoutAsync(string transactionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
        {
            return CardCheckoutWindowResult.Unavailable;
        }

        var startedAt = _timeProvider.GetUtcNow();
        var deadline = startedAt + _pollingTimeout;
        var consecutiveErrors = 0;
        StartupDiagnostics.Log("checkout-payment-monitor-started");

        try
        {
            while (_timeProvider.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var status = await _authSession.ExecuteAuthorizedAsync(
                        token => _backend.GetCardPaymentStatusAsync(transactionId, token),
                        cancellationToken);
                    consecutiveErrors = 0;
                    var result = MapStatus(status.Status);
                    if (result != CardCheckoutWindowResult.Closed)
                    {
                        StartupDiagnostics.Log($"checkout-payment-terminal status={result}");
                        return result;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    StartupDiagnostics.Log($"checkout-payment-poll-error count={consecutiveErrors} type={ex.GetType().Name}");
                }

                var elapsed = _timeProvider.GetUtcNow() - startedAt;
                var delay = elapsed < _fastPollingDuration ? _fastPollingInterval : _slowPollingInterval;
                await _delayAsync(delay, cancellationToken);
            }

            StartupDiagnostics.Log("checkout-payment-terminal status=TimedOut");
            return CardCheckoutWindowResult.TimedOut;
        }
        finally
        {
            StartupDiagnostics.Log("checkout-payment-monitor-stopped");
        }
    }

    private static CardCheckoutWindowResult MapStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "paid" or "succeeded" => CardCheckoutWindowResult.Paid,
            "failed" => CardCheckoutWindowResult.Failed,
            "canceled" or "cancelled" => CardCheckoutWindowResult.Canceled,
            "refunded" => CardCheckoutWindowResult.Refunded,
            _ => CardCheckoutWindowResult.Closed
        };

    private static void LogException(string eventName, Exception ex)
        => StartupDiagnostics.Log($"{eventName} type={ex.GetType().Name}");
}
