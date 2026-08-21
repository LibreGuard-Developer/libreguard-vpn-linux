using System.IO;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;
using QRCoder;

namespace Libreguard.Vpn.Linux.ViewModels;

public sealed class TwoFactorSetupDialogViewModel : ObservableObject
{
    private readonly IAuthSessionService _authSession;
    private readonly IBackendApiClient _backend;
    private string _verificationCode = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isLoading;

    public TwoFactorSetupDialogViewModel(IAuthSessionService authSession, IBackendApiClient backend, TwoFactorSetup setup)
    {
        _authSession = authSession;
        _backend = backend;
        SharedKey = setup.SharedKey ?? setup.ManualEntryKey ?? string.Empty;
        AuthenticatorUri = setup.AuthenticatorUri ?? string.Empty;
        QrCodeImage = CreateQrCodeBitmap(AuthenticatorUri);
        EnableTwoFactorCommand = new AsyncCommand(EnableTwoFactorAsync, () => !IsLoading);
        CancelCommand = new RelayCommand(_ => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public Bitmap? QrCodeImage { get; }

    public string SharedKey { get; }

    public string AuthenticatorUri { get; }

    public ICommand EnableTwoFactorCommand { get; }

    public ICommand CancelCommand { get; }

    public string VerificationCode
    {
        get => _verificationCode;
        set => SetProperty(ref _verificationCode, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                (EnableTwoFactorCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task EnableTwoFactorAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(VerificationCode))
        {
            ErrorMessage = "Please enter the verification code.";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var response = await _authSession.ExecuteAuthorizedAsync(
                token => _backend.EnableTwoFactorAsync(VerificationCode.Trim(), token),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(response.Message))
            {
                ErrorMessage = string.Empty;
            }

            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static Bitmap? CreateQrCodeBitmap(string authenticatorUri)
    {
        if (string.IsNullOrWhiteSpace(authenticatorUri))
        {
            return null;
        }

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(authenticatorUri, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrData);
        var bytes = qrCode.GetGraphic(10);
        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
