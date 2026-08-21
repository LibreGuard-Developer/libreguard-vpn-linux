using System.Net;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class VpnConnectionServiceTests
{
    [Fact]
    public async Task ConnectAsync_RequestsCertificate_WhenConfigIsMissing()
    {
        var backend = new FakeBackend();
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "token", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var network = new FakeNetworkManager();
        var converter = new FakeConverter();
        var deviceIdentity = new FakeDeviceIdentityService();
        var publicIpResolver = new FakePublicIpResolver("198.51.100.15");
        var service = new VpnConnectionService(
            backend,
            new AuthSessionService(backend, deviceIdentity, secrets),
            [converter],
            new FakePreflightService(),
            network,
            publicIpResolver);

        await service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None);

        Assert.Equal(2, backend.ConfigRequests);
        Assert.True(backend.CertificateRequested);
        Assert.True(network.Activated);
    }

    [Fact]
    public async Task ConnectAsync_PublishesClientIpAcrossConnectionStates()
    {
        var backend = new FakeBackend();
        backend.ConfigResponse = new VpnConfigResponse(true, "IKEv2", "NL", "10.0.0.1", "cert", "config", null, "198.51.100.15", "device", null);
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "token", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var network = new FakeNetworkManager();
        var converter = new FakeConverter();
        var deviceIdentity = new FakeDeviceIdentityService();
        var publicIpResolver = new FakePublicIpResolver("198.51.100.15");
        var service = new VpnConnectionService(
            backend,
            new AuthSessionService(backend, deviceIdentity, secrets),
            [converter],
            new FakePreflightService(),
            network,
            publicIpResolver);
        var statuses = new List<VpnStatus>();
        service.StatusChanged += (_, status) =>
        {
            statuses.Add(status);
        };

        await service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None);

        Assert.Contains(statuses, status => status.State == VpnConnectionState.Preparing && status.ClientPublicIp == "198.51.100.15");
        Assert.Contains(statuses, status => status.State == VpnConnectionState.Connecting && status.ClientPublicIp == "198.51.100.15");
        var connectedStatus = Assert.Single(statuses, status => status.State == VpnConnectionState.Connected);
        Assert.Equal("libreguard-ikev2-nl-1", connectedStatus.ActiveProfile);
        Assert.Equal("198.51.100.15", connectedStatus.ClientPublicIp);
        Assert.Equal("10.0.0.1", connectedStatus.ServerIp);
        Assert.NotNull(connectedStatus.ConnectedAt);
    }

    [Fact]
    public async Task ConnectAsync_FallsBackToConfigClientIp_WhenResolverFails()
    {
        var backend = new FakeBackend();
        backend.ConfigResponse = new VpnConfigResponse(true, "IKEv2", "NL", "10.0.0.1", "cert", "config", null, "203.0.113.99", "device", null);
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("jwt-token", "token", CancellationToken.None);
        await secrets.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var network = new FakeNetworkManager();
        var converter = new FakeConverter();
        var deviceIdentity = new FakeDeviceIdentityService();
        var publicIpResolver = new FakePublicIpResolver(null);
        var service = new VpnConnectionService(
            backend,
            new AuthSessionService(backend, deviceIdentity, secrets),
            [converter],
            new FakePreflightService(),
            network,
            publicIpResolver);
        VpnStatus? connectedStatus = null;
        service.StatusChanged += (_, status) =>
        {
            if (status.State == VpnConnectionState.Connected)
            {
                connectedStatus = status;
            }
        };

        await service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None);

        Assert.Equal("203.0.113.99", connectedStatus!.ClientPublicIp);
    }

    [Fact]
    public async Task ConnectAsync_CleansLibreGuardStateBeforeImportingNewProfile()
    {
        var backend = CreateReadyBackend();
        var network = new FakeNetworkManager();
        var service = CreateService(backend, network);

        await service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None);

        Assert.Equal(1, network.DisconnectLibreGuardProfilesCalls);
        Assert.Equal(1, network.DeleteLibreGuardProfilesCalls);
        Assert.Equal(1, network.CleanupLibreGuardArtifactsCalls);
        Assert.True(network.ImportIkeV2Called);
    }

    [Fact]
    public async Task DisconnectAsync_CleansLibreGuardStateWithoutActiveInMemoryProfile()
    {
        var backend = CreateReadyBackend();
        var network = new FakeNetworkManager();
        var service = CreateService(backend, network);

        await service.DisconnectAsync(CancellationToken.None);

        Assert.Equal(1, network.DisconnectLibreGuardProfilesCalls);
        Assert.Equal(1, network.DeleteLibreGuardProfilesCalls);
        Assert.Equal(1, network.CleanupLibreGuardArtifactsCalls);
    }

    [Fact]
    public async Task ConnectAsync_CleansUpImportedProfileWhenActivationFails()
    {
        var backend = CreateReadyBackend();
        var network = new FakeNetworkManager
        {
            ActivateException = new VpnConfigurationException("activation failed")
        };
        var service = CreateService(backend, network);
        var statuses = new List<VpnStatus>();
        service.StatusChanged += (_, status) => statuses.Add(status);

        await Assert.ThrowsAsync<VpnConfigurationException>(() => service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None));

        Assert.DoesNotContain(statuses, status => status.State == VpnConnectionState.Connected);
        Assert.Contains(statuses, status => status.State == VpnConnectionState.Disconnected
            && status.Message == "Connection failed. Network settings were restored.");
        Assert.Equal(1, network.DisconnectLibreGuardProfilesCalls);
        Assert.Equal(1, network.DeleteLibreGuardProfilesCalls);
        Assert.Equal(1, network.CleanupLibreGuardArtifactsCalls);
        Assert.Contains("libreguard-ikev2-nl-1", network.DeactivatedProfiles);
        Assert.Contains("libreguard-ikev2-nl-1", network.DeletedProfiles);
        Assert.Contains("libreguard-ikev2-nl-1", network.CleanedArtifactProfiles);
    }

    [Fact]
    public async Task ConnectAsync_CleansUpPartiallyImportedProfileWhenDnsConfigurationFails()
    {
        var backend = CreateReadyBackend();
        var network = new FakeNetworkManager
        {
            ImportException = new VpnConfigurationException("private DNS verification failed")
        };
        var service = CreateService(backend, network);

        var statuses = new List<VpnStatus>();
        service.StatusChanged += (_, status) => statuses.Add(status);

        await Assert.ThrowsAsync<VpnConfigurationException>(() => service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None));

        Assert.False(network.Activated);
        Assert.DoesNotContain(statuses, status => status.State == VpnConnectionState.Connected);
        Assert.Contains(statuses, status => status.State == VpnConnectionState.Disconnected
            && status.Message == "Connection failed. Network settings were restored.");
        Assert.Equal(1, network.DisconnectLibreGuardProfilesCalls);
        Assert.Equal(1, network.DeleteLibreGuardProfilesCalls);
        Assert.Equal(1, network.CleanupLibreGuardArtifactsCalls);
        Assert.Contains("libreguard-ikev2-nl-1", network.DeactivatedProfiles);
        Assert.Contains("libreguard-ikev2-nl-1", network.DeletedProfiles);
        Assert.Contains("libreguard-ikev2-nl-1", network.CleanedArtifactProfiles);
    }

    [Fact]
    public async Task ConnectAsync_CleansUpTheExactProfileWhenActivationIsCancelled()
    {
        var backend = CreateReadyBackend();
        var network = new FakeNetworkManager
        {
            ActivateException = new OperationCanceledException()
        };
        var service = CreateService(backend, network);
        var statuses = new List<VpnStatus>();
        service.StatusChanged += (_, status) => statuses.Add(status);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ConnectAsync(backend.Server, VpnProtocol.Ikev2, CancellationToken.None));

        Assert.DoesNotContain(statuses, status => status.State == VpnConnectionState.Connected);
        Assert.Contains(statuses, status => status.State == VpnConnectionState.Disconnected
            && status.Message == "Connection cancelled.");
        Assert.Contains("libreguard-ikev2-nl-1", network.DeactivatedProfiles);
        Assert.Contains("libreguard-ikev2-nl-1", network.DeletedProfiles);
        Assert.Contains("libreguard-ikev2-nl-1", network.CleanedArtifactProfiles);
    }

    [Fact]
    public async Task ShutdownAsync_PublishesDisconnectedStateAfterCleanup()
    {
        var backend = CreateReadyBackend();
        var network = new FakeNetworkManager();
        var service = CreateService(backend, network);
        VpnStatus? disconnected = null;
        service.StatusChanged += (_, status) =>
        {
            if (status.State == VpnConnectionState.Disconnected)
            {
                disconnected = status;
            }
        };

        await service.ShutdownAsync(CancellationToken.None);

        Assert.NotNull(disconnected);
        Assert.Equal(1, network.EnsureAvailableCalls);
        Assert.Equal(1, network.DisconnectLibreGuardProfilesCalls);
        Assert.Equal(1, network.DeleteLibreGuardProfilesCalls);
        Assert.Equal(1, network.CleanupLibreGuardArtifactsCalls);
    }

    private static FakeBackend CreateReadyBackend()
        => new()
        {
            ConfigResponse = new VpnConfigResponse(true, "IKEv2", "NL", "10.0.0.1", "cert", "config", null, null, "device", null)
        };

    private static VpnConnectionService CreateService(FakeBackend backend, FakeNetworkManager network, string? resolvedIp = "198.51.100.15")
    {
        var secrets = new InMemorySecretStore();
        secrets.SetAsync("jwt-token", "token", CancellationToken.None).GetAwaiter().GetResult();
        secrets.SetAsync("refresh-token", "refresh", CancellationToken.None).GetAwaiter().GetResult();
        var deviceIdentity = new FakeDeviceIdentityService();
        return new VpnConnectionService(
            backend,
            new AuthSessionService(backend, deviceIdentity, secrets),
            [new FakeConverter()],
            new FakePreflightService(),
            network,
            new FakePublicIpResolver(resolvedIp));
    }

    private sealed class FakeBackend : IBackendApiClient
    {
        public VpnServer Server { get; } = new(1, "NL", "10.0.0.1", "nl.example", "Netherlands", "Amsterdam", 100, "free", 1, 1, 443, true);
        public int ConfigRequests { get; private set; }
        public bool CertificateRequested { get; private set; }
        public VpnConfigResponse ConfigResponse { get; set; } = new(true, "IKEv2", "NL", "10.0.0.1", "cert", "config", null, null, "device", null);

        public void SetBearerToken(string? token) { }
        public Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResendConfirmationAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EmailConfirmationStatus> CheckConfirmationAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginAsync(string email, string password, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> VerifyTwoFactorAsync(string email, string code, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> VerifyRecoveryCodeAsync(string email, string recoveryCode, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> RefreshAsync(string refreshToken, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginWithGoogleAsync(string token, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginWithGoogleCodeAsync(GoogleOAuthAuthorizationCode authorizationCode, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemovePreAuthDeviceAsync(string email, string password, int deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemovePreAuthOAuthDeviceAsync(string provider, string idToken, int deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemovePreAuthOAuthDeviceWithCodeAsync(string provider, GoogleOAuthAuthorizationCode authorizationCode, int deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> ExchangeOAuthTokenAsync(string email, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> CompleteOAuthAsync(string email, string provider, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> LogoutAsync(string? refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TokenCheckResponse> CheckTokenAsync(CancellationToken cancellationToken) => Task.FromResult(new TokenCheckResponse { IsValid = true, Email = "user@example.com", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        public Task<ApiMessage> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TwoFactorStatus> GetTwoFactorStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TwoFactorSetup> SetupTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> EnableTwoFactorAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> DisableTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResetTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RecoveryCodesResponse> GenerateRecoveryCodesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VpnServer>> GetServersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VpnServer>>([Server]);
        public Task<VpnConfigResponse> GetVpnConfigQueryAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadOpenVpnConfigAsync(int serverId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UserCertificate>> GetCertificatesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UserCertificate>>([]);
        public Task<CertificateJob> GetCertificateJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(new CertificateJob { Id = int.Parse(jobId), Status = "Success", OutputCertificateId = 99 });
        public Task<UsageQuota> GetUsageQuotaAsync(CancellationToken cancellationToken) => Task.FromResult(new UsageQuota { BytesUsed = 0, BytesLimit = null, IsUnlimited = true });
        public Task<UsageQuota> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(new UsageQuota { Allowed = true, BytesUsed = 0, BytesLimit = null, IsUnlimited = true });
        public Task<SubscriptionStatus> GetSubscriptionStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 1, false, null));
        public Task<DnsPreferenceResponse> GetDnsPreferenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DnsPreferenceResponse> UpdateDnsPreferenceAsync(bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServerAccessResponse> CanAccessServerAsync(int serverTier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SubscriptionDeviceRegistrationResponse> RegisterSubscriptionDeviceAsync(DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveSubscriptionDeviceAsync(string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CheckoutUrlResponse> GetCheckoutUrlAsync(string cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroPriceResponse> GetMoneroPriceAsync(BillingCycle cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroInvoiceResponse> CreateMoneroInvoiceAsync(BillingCycle cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroStatusResponse> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroInvoiceResponse> GetLatestMoneroInvoiceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CardCheckoutResponse> CreateCardCheckoutAsync(BillingCycle cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CardPaymentStatusResponse> GetCardPaymentStatusAsync(string transactionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UserDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UserDevice>>([]);
        public Task<ApiMessage> RemoveDeviceAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> DeleteDeviceAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveAllOtherDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveAllInactiveDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadCertificateConfigAsync(int certificateId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadCertificateAsync(int certificateId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VpnConfigResponse> GetVpnConfigAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken)
        {
            ConfigRequests++;
            if (ConfigRequests == 1)
            {
                throw new BackendApiException("No certificate", HttpStatusCode.NotFound);
            }

            return Task.FromResult(ConfigResponse);
        }

        public Task<CertificateRequestResponse> RequestCertificateAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken)
        {
            CertificateRequested = true;
            return Task.FromResult(new CertificateRequestResponse { JobId = 1, Status = "Pending" });
        }
    }

    private sealed class FakeDeviceIdentityService : IDeviceIdentityService
    {
        public Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DeviceRegistrationPayload("device", "1.0.0", "key", "key-id", "RSA-OAEP-256"));

        public Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken)
            => Task.FromResult("pass");
    }

    private sealed class FakeConverter : IVpnProfileConverter
    {
        public VpnProtocol Protocol => VpnProtocol.Ikev2;
        public Task<VpnProfile> ConvertAsync(VpnConfigResponse config, VpnServer server, CancellationToken cancellationToken)
            => Task.FromResult(new VpnProfile(VpnProtocol.Ikev2, "libreguard-ikev2-nl-1", "/tmp/profile.sswan", null, "address=example", "10.0.0.1"));
    }

    private sealed class FakePreflightService : ILinuxPreflightService
    {
        public Task<LinuxPreflightResult> CheckAsync(VpnProtocol protocol, CancellationToken cancellationToken)
            => Task.FromResult(new LinuxPreflightResult([
                new LinuxPreflightCheck("test", IsPresent: true, IsRequired: true, "ok")
            ]));
    }

    private sealed class FakePublicIpResolver(string? result) : IPublicIpResolver
    {
        public Task<string?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeNetworkManager : INetworkManagerClient
    {
        public bool Activated { get; private set; }
        public bool ImportIkeV2Called { get; private set; }
        public int EnsureAvailableCalls { get; private set; }
        public int DisconnectLibreGuardProfilesCalls { get; private set; }
        public int DeleteLibreGuardProfilesCalls { get; private set; }
        public int CleanupLibreGuardArtifactsCalls { get; private set; }
        public List<string> DeactivatedProfiles { get; } = [];
        public List<string> DeletedProfiles { get; } = [];
        public List<string> CleanedArtifactProfiles { get; } = [];
        public Exception? ImportException { get; init; }
        public Exception? ActivateException { get; init; }
        public Task EnsureAvailableAsync(CancellationToken cancellationToken)
        {
            EnsureAvailableCalls++;
            return Task.CompletedTask;
        }
        public Task ImportOpenVpnAsync(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ImportIkeV2Async(VpnProfile profile, CancellationToken cancellationToken)
        {
            ImportIkeV2Called = true;
            if (ImportException is not null)
            {
                throw ImportException;
            }

            return Task.CompletedTask;
        }
        public Task ActivateAsync(VpnProfile profile, CancellationToken cancellationToken)
        {
            if (ActivateException is not null)
            {
                throw ActivateException;
            }

            Activated = true;
            return Task.CompletedTask;
        }
        public Task DeactivateAsync(string profileName, CancellationToken cancellationToken)
        {
            DeactivatedProfiles.Add(profileName);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> GetActiveLibreGuardProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["profile"]);
        public Task<IReadOnlyList<string>> GetLibreGuardProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["profile"]);
        public Task DisconnectLibreGuardProfilesAsync(CancellationToken cancellationToken)
        {
            DisconnectLibreGuardProfilesCalls++;
            return Task.CompletedTask;
        }
        public Task DeleteLibreGuardProfilesAsync(string? excludeProfileName, CancellationToken cancellationToken)
        {
            DeleteLibreGuardProfilesCalls++;
            return Task.CompletedTask;
        }
        public Task CleanupLibreGuardArtifactsAsync(string? excludeProfileName, CancellationToken cancellationToken)
        {
            CleanupLibreGuardArtifactsCalls++;
            return Task.CompletedTask;
        }
        public Task DeleteLibreGuardProfileAsync(string profileName, CancellationToken cancellationToken)
        {
            DeletedProfiles.Add(profileName);
            return Task.CompletedTask;
        }
        public Task CleanupLibreGuardProfileArtifactsAsync(string profileName, CancellationToken cancellationToken)
        {
            CleanedArtifactProfiles.Add(profileName);
            return Task.CompletedTask;
        }
        public Task<string?> GetActiveDeviceNameAsync(string profileName, CancellationToken cancellationToken) => Task.FromResult<string?>("lgvpn0");
    }
}
