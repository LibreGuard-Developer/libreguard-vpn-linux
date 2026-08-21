using System.Net;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class AuthSessionServiceTests
{
    [Fact]
    public async Task TryRestoreSessionAsync_UsesStoredJwt_WhenTokenIsStillValid()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = _ => Task.FromResult(new TokenCheckResponse { IsValid = true, Email = "user@example.com" })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "valid-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var restored = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal("valid-jwt", service.CurrentSession?.Token);
        Assert.Equal("refresh-token", service.CurrentSession?.RefreshToken);
        Assert.Equal("user@example.com", service.CurrentSession?.Email);
        Assert.Equal(0, backend.RefreshCalls);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_ReportsCurrentAppVersionForLegacySession()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = _ => Task.FromResult(new TokenCheckResponse { IsValid = true, Email = "user@example.com" }),
            RegisterSubscriptionDeviceHandler = device => Task.FromResult(new SubscriptionDeviceRegistrationResponse
            {
                DeviceIdHash = device.DeviceId,
                IsNewDevice = false
            })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "valid-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        Assert.True(await service.TryRestoreSessionAsync(CancellationToken.None));
        Assert.Equal(1, backend.AppVersionReportCalls);
        Assert.Equal("Linux/1.1.17", backend.ReportedAppVersion);
        Assert.Equal("Linux/1.1.17", await secrets.GetAsync("reported-app-version", CancellationToken.None));

        Assert.True(await service.TryRestoreSessionAsync(CancellationToken.None));
        Assert.Equal(1, backend.AppVersionReportCalls);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_UsesPersistedEmail_WhenTokenCheckOmitsIt()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = _ => Task.FromResult(new TokenCheckResponse { IsValid = true, Email = null })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "valid-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        await secrets.SetAsync("account-email", "user@example.com", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var restored = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal("user@example.com", service.CurrentSession?.Email);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_UsesPersistedPlanType_WhenTokenIsStillValid()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = _ => Task.FromResult(new TokenCheckResponse { IsValid = true, Email = "user@example.com" })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "valid-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        await secrets.SetAsync("plan-type", "Pro", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var restored = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal("Pro", service.CurrentSession?.PlanType);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_Refreshes_WhenStoredJwtIsInvalid()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = bearer => Task.FromResult(new TokenCheckResponse
            {
                IsValid = bearer == "fresh-jwt",
                Email = bearer == "fresh-jwt" ? "user@example.com" : null
            }),
            RefreshHandler = (_, device) => Task.FromResult(new LoginResponse
            {
                Token = "fresh-jwt",
                RefreshToken = "fresh-refresh",
                Email = "user@example.com",
                UserId = "user-1",
                DeviceId = device.DeviceId
            })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "expired-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var restored = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal("fresh-jwt", await secrets.GetAsync("jwt-token", CancellationToken.None));
        Assert.Equal("fresh-refresh", await secrets.GetAsync("refresh-token", CancellationToken.None));
        Assert.Equal(1, backend.RefreshCalls);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_Refreshes_WhenTokenCheckThrowsUnauthorized()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = bearer => bearer == "fresh-jwt"
                ? Task.FromResult(new TokenCheckResponse { IsValid = true, Email = "user@example.com" })
                : Task.FromException<TokenCheckResponse>(new BackendApiException("401 Unauthorized", HttpStatusCode.Unauthorized)),
            RefreshHandler = (_, device) => Task.FromResult(new LoginResponse
            {
                Token = "fresh-jwt",
                RefreshToken = "fresh-refresh",
                Email = "user@example.com",
                UserId = "user-1",
                DeviceId = device.DeviceId
            })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "expired-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var restored = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal(1, backend.RefreshCalls);
        Assert.Equal("fresh-jwt", service.CurrentSession?.Token);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_ClearsSession_WhenRefreshFails()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = _ => Task.FromResult(new TokenCheckResponse { IsValid = false }),
            RefreshHandler = (_, _) => Task.FromException<LoginResponse>(new BackendApiException("Invalid refresh token.", HttpStatusCode.Unauthorized))
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "expired-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "expired-refresh", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var refreshed = await service.TryRefreshSessionAsync(CancellationToken.None);

        Assert.False(refreshed);
        Assert.Null(await secrets.GetAsync("jwt-token", CancellationToken.None));
        Assert.Null(await secrets.GetAsync("refresh-token", CancellationToken.None));
        Assert.Null(service.CurrentSession);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_DeduplicatesConcurrentRefreshCalls()
    {
        var backend = new FakeBackend();
        backend.CheckTokenHandler = bearer => Task.FromResult(new TokenCheckResponse
        {
            IsValid = bearer == "fresh-jwt",
            Email = bearer == "fresh-jwt" ? "user@example.com" : null
        });
        backend.RefreshHandler = async (_, device) =>
        {
            await Task.Delay(50);
            return new LoginResponse
            {
                Token = "fresh-jwt",
                RefreshToken = "fresh-refresh",
                Email = "user@example.com",
                UserId = "user-1",
                DeviceId = device.DeviceId
            };
        };

        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "expired-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var refreshes = await Task.WhenAll(
            service.TryRefreshSessionAsync(CancellationToken.None),
            service.TryRefreshSessionAsync(CancellationToken.None));

        Assert.All(refreshes, Assert.True);
        Assert.Equal(1, backend.RefreshCalls);
        Assert.Equal("fresh-jwt", await secrets.GetAsync("jwt-token", CancellationToken.None));
    }

    [Fact]
    public async Task TryRefreshSessionAsync_UsesPersistedPlanType_WhenRefreshResponseOmitsIt()
    {
        var backend = new FakeBackend
        {
            CheckTokenHandler = _ => Task.FromResult(new TokenCheckResponse { IsValid = false }),
            RefreshHandler = (_, device) => Task.FromResult(new LoginResponse
            {
                Token = "fresh-jwt",
                RefreshToken = "fresh-refresh",
                Email = "user@example.com",
                UserId = "user-1",
                DeviceId = device.DeviceId,
                PlanType = null
            })
        };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "expired-jwt", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        await secrets.SetAsync("plan-type", "Free", CancellationToken.None);
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);

        var refreshed = await service.TryRefreshSessionAsync(CancellationToken.None);

        Assert.True(refreshed);
        Assert.Equal("Free", service.CurrentSession?.PlanType);
    }

    [Fact]
    public async Task ClearSessionAsync_RemovesPersistedPlanType()
    {
        var backend = new FakeBackend();
        var secrets = new InMemorySecretStore();
        var service = new AuthSessionService(backend, new FakeDeviceIdentityService(), secrets);
        await service.SetSessionAsync(new AuthSession("token", "refresh", "user@example.com", "user-1", "device-1", 1, 3, "Pro"), CancellationToken.None);

        await service.ClearSessionAsync(CancellationToken.None);

        Assert.Null(await secrets.GetAsync("plan-type", CancellationToken.None));
        Assert.Null(service.CurrentSession);
    }

    private sealed class FakeBackend : IBackendApiClient
    {
        public Func<string?, Task<TokenCheckResponse>> CheckTokenHandler { get; set; } = _ => Task.FromResult(new TokenCheckResponse { IsValid = false });
        public Func<string, DeviceRegistrationPayload, Task<LoginResponse>> RefreshHandler { get; set; } = (_, _) => Task.FromResult(new LoginResponse());
        public Func<DeviceRegistrationPayload, Task<SubscriptionDeviceRegistrationResponse>> RegisterSubscriptionDeviceHandler { get; set; } = _ => Task.FromResult(new SubscriptionDeviceRegistrationResponse());

        public int RefreshCalls { get; private set; }
        public int AppVersionReportCalls { get; private set; }
        public string? ReportedAppVersion { get; private set; }
        public string? BearerToken { get; private set; }

        public void SetBearerToken(string? token) => BearerToken = token;
        public Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResendConfirmationAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EmailConfirmationStatus> CheckConfirmationAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginAsync(string email, string password, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> VerifyTwoFactorAsync(string email, string code, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> VerifyRecoveryCodeAsync(string email, string recoveryCode, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<LoginResponse> RefreshAsync(string refreshToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return await RefreshHandler(refreshToken, device);
        }
        public Task<ApiMessage> LogoutAsync(string? refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TokenCheckResponse> CheckTokenAsync(CancellationToken cancellationToken) => CheckTokenHandler(BearerToken);
        public Task<ApiMessage> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginWithGoogleAsync(string token, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginWithGoogleCodeAsync(GoogleOAuthAuthorizationCode authorizationCode, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemovePreAuthDeviceAsync(string email, string password, int deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemovePreAuthOAuthDeviceAsync(string provider, string idToken, int deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemovePreAuthOAuthDeviceWithCodeAsync(string provider, GoogleOAuthAuthorizationCode authorizationCode, int deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> ExchangeOAuthTokenAsync(string email, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> CompleteOAuthAsync(string email, string provider, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TwoFactorStatus> GetTwoFactorStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TwoFactorSetup> SetupTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> EnableTwoFactorAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> DisableTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResetTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RecoveryCodesResponse> GenerateRecoveryCodesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VpnServer>> GetServersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VpnConfigResponse> GetVpnConfigAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VpnConfigResponse> GetVpnConfigQueryAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadOpenVpnConfigAsync(int serverId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UserCertificate>> GetCertificatesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CertificateRequestResponse> RequestCertificateAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CertificateJob> GetCertificateJobAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UsageQuota> GetUsageQuotaAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UsageQuota> CanConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SubscriptionStatus> GetSubscriptionStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DnsPreferenceResponse> GetDnsPreferenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DnsPreferenceResponse> UpdateDnsPreferenceAsync(bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServerAccessResponse> CanAccessServerAsync(int serverTier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<SubscriptionDeviceRegistrationResponse> RegisterSubscriptionDeviceAsync(DeviceRegistrationPayload device, CancellationToken cancellationToken)
        {
            AppVersionReportCalls++;
            ReportedAppVersion = device.AppVersion;
            return await RegisterSubscriptionDeviceHandler(device);
        }
        public Task<ApiMessage> RemoveSubscriptionDeviceAsync(string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CheckoutUrlResponse> GetCheckoutUrlAsync(string cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroPriceResponse> GetMoneroPriceAsync(BillingCycle cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroInvoiceResponse> CreateMoneroInvoiceAsync(BillingCycle cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroStatusResponse> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroInvoiceResponse> GetLatestMoneroInvoiceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CardCheckoutResponse> CreateCardCheckoutAsync(BillingCycle cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CardPaymentStatusResponse> GetCardPaymentStatusAsync(string transactionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UserDevice>> GetDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveDeviceAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> DeleteDeviceAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveAllOtherDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveAllInactiveDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadCertificateConfigAsync(int certificateId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadCertificateAsync(int certificateId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeDeviceIdentityService : IDeviceIdentityService
    {
        public Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DeviceRegistrationPayload("device-1", "Linux/1.1.17", "key", "key-id", "RSA-OAEP-256"));

        public Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken)
            => Task.FromResult("passphrase");
    }
}
