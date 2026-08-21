using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public interface IBackendApiClient
{
    void SetBearerToken(string? token);
    Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<ApiMessage> ResendConfirmationAsync(string email, CancellationToken cancellationToken);
    Task<EmailConfirmationStatus> CheckConfirmationAsync(string userId, CancellationToken cancellationToken);
    Task<LoginResponse> LoginAsync(string email, string password, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<LoginResponse> VerifyTwoFactorAsync(string email, string code, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<LoginResponse> VerifyRecoveryCodeAsync(string email, string recoveryCode, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<LoginResponse> RefreshAsync(string refreshToken, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<ApiMessage> LogoutAsync(string? refreshToken, CancellationToken cancellationToken);
    Task<TokenCheckResponse> CheckTokenAsync(CancellationToken cancellationToken);
    Task<ApiMessage> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);
    Task<ApiMessage> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
    Task<LoginResponse> LoginWithGoogleAsync(string token, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<LoginResponse> LoginWithGoogleCodeAsync(GoogleOAuthAuthorizationCode authorizationCode, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<ApiMessage> RemovePreAuthDeviceAsync(string email, string password, int deviceId, CancellationToken cancellationToken);
    Task<ApiMessage> RemovePreAuthOAuthDeviceAsync(string provider, string deviceRemovalToken, int deviceId, CancellationToken cancellationToken);
    Task<ApiMessage> RemovePreAuthOAuthDeviceWithCodeAsync(string provider, GoogleOAuthAuthorizationCode authorizationCode, int deviceId, CancellationToken cancellationToken);
    Task<LoginResponse> ExchangeOAuthTokenAsync(string email, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<LoginResponse> CompleteOAuthAsync(string email, string provider, DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<TwoFactorStatus> GetTwoFactorStatusAsync(CancellationToken cancellationToken);
    Task<TwoFactorSetup> SetupTwoFactorAsync(CancellationToken cancellationToken);
    Task<ApiMessage> EnableTwoFactorAsync(string code, CancellationToken cancellationToken);
    Task<ApiMessage> DisableTwoFactorAsync(CancellationToken cancellationToken);
    Task<ApiMessage> ResetTwoFactorAsync(CancellationToken cancellationToken);
    Task<RecoveryCodesResponse> GenerateRecoveryCodesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VpnServer>> GetServersAsync(CancellationToken cancellationToken);
    Task<VpnConfigResponse> GetVpnConfigAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken);
    Task<VpnConfigResponse> GetVpnConfigQueryAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken);
    Task<Stream> DownloadOpenVpnConfigAsync(int serverId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserCertificate>> GetCertificatesAsync(CancellationToken cancellationToken);
    Task<CertificateRequestResponse> RequestCertificateAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken);
    Task<CertificateJob> GetCertificateJobAsync(string jobId, CancellationToken cancellationToken);
    Task<UsageQuota> GetUsageQuotaAsync(CancellationToken cancellationToken);
    Task<UsageQuota> CanConnectAsync(CancellationToken cancellationToken);
    Task<SubscriptionStatus> GetSubscriptionStatusAsync(CancellationToken cancellationToken);
    Task<DnsPreferenceResponse> GetDnsPreferenceAsync(CancellationToken cancellationToken);
    Task<DnsPreferenceResponse> UpdateDnsPreferenceAsync(bool enabled, CancellationToken cancellationToken);
    Task<ServerAccessResponse> CanAccessServerAsync(int serverTier, CancellationToken cancellationToken);
    Task<SubscriptionDeviceRegistrationResponse> RegisterSubscriptionDeviceAsync(DeviceRegistrationPayload device, CancellationToken cancellationToken);
    Task<ApiMessage> RemoveSubscriptionDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task<CheckoutUrlResponse> GetCheckoutUrlAsync(string cycle, CancellationToken cancellationToken);
    Task<MoneroPriceResponse> GetMoneroPriceAsync(BillingCycle cycle, CancellationToken cancellationToken);
    Task<MoneroInvoiceResponse> CreateMoneroInvoiceAsync(BillingCycle cycle, CancellationToken cancellationToken);
    Task<MoneroStatusResponse> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken cancellationToken);
    Task<MoneroInvoiceResponse> GetLatestMoneroInvoiceAsync(CancellationToken cancellationToken);
    Task<CardCheckoutResponse> CreateCardCheckoutAsync(BillingCycle cycle, CancellationToken cancellationToken);
    Task<CardPaymentStatusResponse> GetCardPaymentStatusAsync(string transactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<ApiMessage> RemoveDeviceAsync(int id, CancellationToken cancellationToken);
    Task<ApiMessage> DeleteDeviceAsync(int id, CancellationToken cancellationToken);
    Task<ApiMessage> RemoveAllOtherDevicesAsync(CancellationToken cancellationToken);
    Task<ApiMessage> RemoveAllInactiveDevicesAsync(CancellationToken cancellationToken);
    Task<Stream> DownloadCertificateConfigAsync(int certificateId, CancellationToken cancellationToken);
    Task<Stream> DownloadCertificateAsync(int certificateId, CancellationToken cancellationToken);
}

public interface IGoogleOAuthService
{
    Task<GoogleOAuthAuthorizationCode> AuthenticateAsync(CancellationToken cancellationToken);
}

public interface ICardCheckoutWindowService
{
    bool IsCheckoutActive { get; }
    Task<CardCheckoutWindowResult> ShowCheckoutAsync(CardCheckoutWindowRequest request, CancellationToken cancellationToken);
    Task<CardCheckoutWindowResult> MonitorCheckoutAsync(string transactionId, CancellationToken cancellationToken);
    Task<ExternalUriLaunchResult> OpenInBrowserAsync(string checkoutUrl, CancellationToken cancellationToken);
    void CancelCheckout();
}

public sealed record CardCheckoutWindowRequest(
    string CheckoutUrl,
    string TransactionId,
    string? BillingCycle,
    decimal AmountUsd,
    string? Currency);

public enum CardCheckoutWindowResult
{
    Closed,
    Paid,
    Failed,
    Canceled,
    Refunded,
    TimedOut,
    Unavailable
}

public interface IExternalUriLauncher
{
    Task<ExternalUriLaunchResult> OpenAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed record ExternalUriLaunchResult(bool Success, string? ErrorMessage = null);

public sealed record GoogleOAuthAuthorizationCode(
    string ClientId,
    string Code,
    string RedirectUri,
    string CodeVerifier);

public interface IDeviceIdentityService
{
    Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken);
    Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken);
}

public interface IAuthSessionService
{
    AuthSession? CurrentSession { get; }
    Task SetSessionAsync(AuthSession session, CancellationToken cancellationToken);
    Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken);
    Task EnsureAuthenticatedAsync(CancellationToken cancellationToken);
    Task<bool> TryRefreshSessionAsync(CancellationToken cancellationToken);
    Task ClearSessionAsync(CancellationToken cancellationToken);
    Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken);
    Task<T> ExecuteAuthorizedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
    Task ExecuteAuthorizedAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken);
}

public interface IVpnConnectionService
{
    event EventHandler<VpnStatus>? StatusChanged;
    Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task ConnectAsync(VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
    Task<VpnProfile> ImportOrUpdateProfileAsync(VpnConfigResponse config, VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken);
}

public interface IVpnProfileConverter
{
    VpnProtocol Protocol { get; }
    Task<VpnProfile> ConvertAsync(VpnConfigResponse config, VpnServer server, CancellationToken cancellationToken);
}

public interface ILinuxPreflightService
{
    Task<LinuxPreflightResult> CheckAsync(VpnProtocol protocol, CancellationToken cancellationToken);
}

public interface IServerLatencyService
{
    Task<IReadOnlyDictionary<string, int>> MeasureLatenciesAsync(IReadOnlyList<VpnServer> servers, CancellationToken cancellationToken);
    IReadOnlyDictionary<string, int> GetCachedLatencies();
}

public interface IPublicIpResolver
{
    Task<string?> ResolveAsync(CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken);
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken);
}

public interface IDesktopNotificationService
{
    Task ShowAsync(string title, string body, CancellationToken cancellationToken);
}

public interface ILocalStatisticsStore
{
    string GetUserHash(AuthSession? session);
    Task<LocalStatisticsProfile> LoadProfileAsync(AuthSession? session, bool closeStaleActiveSession, CancellationToken cancellationToken);
    Task<LocalStatisticsProfile> StartSessionAsync(AuthSession? session, LocalVpnSession activeSession, CancellationToken cancellationToken);
    Task<LocalStatisticsProfile> RecordSnapshotAsync(AuthSession? session, TunnelTrafficSnapshot snapshot, DateTimeOffset observedAt, CancellationToken cancellationToken);
    Task<LocalStatisticsProfile> FinalizeActiveSessionAsync(AuthSession? session, DateTimeOffset endedAt, string finalStatus, CancellationToken cancellationToken);
    Task<string?> GetStatisticsPeriodAsync(AuthSession? session, CancellationToken cancellationToken);
    Task SetStatisticsPeriodAsync(AuthSession? session, string period, CancellationToken cancellationToken);
}

public interface IThemePreferenceService
{
    event EventHandler? PreferenceChanged;

    AppThemePreference CurrentPreference { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task SetPreferenceAsync(AppThemePreference preference, CancellationToken cancellationToken);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken);

    Task<ProcessResult> StartDetachedAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        => RunAsync(fileName, arguments, cancellationToken);
}

public interface IFileSavePickerService
{
    Task<FileSaveTarget?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken);
}

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

public sealed class FileSaveTarget(Stream stream, string displayPath) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public string DisplayPath { get; } = displayPath;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public interface INetworkManagerClient
{
    Task EnsureAvailableAsync(CancellationToken cancellationToken);
    Task ImportOpenVpnAsync(VpnProfile profile, CancellationToken cancellationToken);
    Task ImportIkeV2Async(VpnProfile profile, CancellationToken cancellationToken);
    Task ActivateAsync(VpnProfile profile, CancellationToken cancellationToken);
    Task DeactivateAsync(string profileName, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetActiveLibreGuardProfilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetLibreGuardProfilesAsync(CancellationToken cancellationToken);
    Task DisconnectLibreGuardProfilesAsync(CancellationToken cancellationToken);
    Task DeleteLibreGuardProfilesAsync(string? excludeProfileName, CancellationToken cancellationToken);
    Task CleanupLibreGuardArtifactsAsync(string? excludeProfileName, CancellationToken cancellationToken);
    Task DeleteLibreGuardProfileAsync(string profileName, CancellationToken cancellationToken);
    Task CleanupLibreGuardProfileArtifactsAsync(string profileName, CancellationToken cancellationToken);
    Task<string?> GetActiveDeviceNameAsync(string profileName, CancellationToken cancellationToken);
}

public interface ITunnelTrafficMonitor
{
    Task<TunnelTrafficSnapshot> StartSessionAsync(string profileName, CancellationToken cancellationToken);
    Task<TunnelTrafficSnapshot> RefreshAsync(CancellationToken cancellationToken);
    void Stop();
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed record LinuxPreflightCheck(string Name, bool IsPresent, bool IsRequired, string Message);

public sealed record LinuxPreflightResult(IReadOnlyList<LinuxPreflightCheck> Checks)
{
    public bool IsReady => Checks.All(check => !check.IsRequired || check.IsPresent);

    public string Summary => IsReady
        ? "Linux VPN dependencies are ready."
        : string.Join(Environment.NewLine, Checks.Where(check => check.IsRequired && !check.IsPresent).Select(check => check.Message));
}

public sealed class SessionExpiredException(string message) : Exception(message);
