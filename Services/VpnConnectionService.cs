using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class VpnConnectionService(
    IBackendApiClient backend,
    IAuthSessionService authSession,
    IEnumerable<IVpnProfileConverter> converters,
    ILinuxPreflightService preflightService,
    INetworkManagerClient networkManager,
    IPublicIpResolver publicIpResolver) : IVpnConnectionService
{
    private readonly Dictionary<VpnProtocol, IVpnProfileConverter> _converters = converters.ToDictionary(converter => converter.Protocol);
    private VpnStatus _status = new(VpnConnectionState.Disconnected, null, "Disconnected");

    public event EventHandler<VpnStatus>? StatusChanged;

    public Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(_status);

    public async Task ConnectAsync(VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken)
    {
        string? activeProfileName = null;
        string? resolvedClientIp = null;
        SetStatus(VpnConnectionState.Preparing, null, "Checking account and preparing VPN profile...");
        try
        {
            await CleanupLibreGuardStateAsync(cancellationToken);

            var preflight = await preflightService.CheckAsync(protocol, cancellationToken);
            if (!preflight.IsReady)
            {
                throw new VpnConfigurationException(preflight.Summary);
            }

            await authSession.EnsureAuthenticatedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var quota = await authSession.ExecuteAuthorizedAsync(backend.CanConnectAsync, cancellationToken);
            if (!quota.CanConnect)
            {
                throw new VpnConfigurationException(quota.Message ?? "Your current quota does not allow a new VPN connection.");
            }

            var subscription = await authSession.ExecuteAuthorizedAsync(backend.GetSubscriptionStatusAsync, cancellationToken);
            if (!subscription.IsActive)
            {
                throw new VpnConfigurationException(subscription.Message ?? "Your subscription is not active.");
            }

            SetStatus(VpnConnectionState.Preparing, null, "Resolving current public IP...");
            resolvedClientIp = await ResolveClientPublicIpAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SetStatus(VpnConnectionState.Preparing, null, "Downloading VPN configuration...");
            var config = await LoadConfigWithCertificateFallbackAsync(server, protocol, cancellationToken);
            resolvedClientIp ??= config.ClientIp;
            cancellationToken.ThrowIfCancellationRequested();

            SetStatus(
                VpnConnectionState.Preparing,
                null,
                "Installing local VPN profile...",
                connectedAt: null,
                clientPublicIp: resolvedClientIp,
                serverIp: server.ServerIp);
            activeProfileName = ProfileNames.For(server, protocol);
            var profile = await CreateProfileAsync(config, server, protocol, cancellationToken);
            if (!string.Equals(profile.ProfileName, activeProfileName, StringComparison.Ordinal))
            {
                throw new VpnConfigurationException("LibreGuard generated an unexpected VPN profile name; refusing to modify network settings.");
            }
            await InstallProfileAsync(profile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SetStatus(
                VpnConnectionState.Connecting,
                profile.ProfileName,
                $"Connecting to {server.DisplayName}...",
                connectedAt: null,
                clientPublicIp: resolvedClientIp,
                serverIp: server.ServerIp);
            await networkManager.ActivateAsync(profile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus(
                VpnConnectionState.Connected,
                profile.ProfileName,
                $"Connected to {server.DisplayName}",
                connectedAt: DateTimeOffset.UtcNow,
                clientPublicIp: resolvedClientIp,
                serverIp: server.ServerIp);
        }
        catch (OperationCanceledException)
        {
            await TryCleanupLibreGuardStateAsync(activeProfileName);
            SetStatus(VpnConnectionState.Disconnected, null, "Connection cancelled.", connectedAt: null, clientPublicIp: resolvedClientIp, serverIp: null);
            throw;
        }
        catch
        {
            await TryCleanupLibreGuardStateAsync(activeProfileName);
            SetStatus(
                VpnConnectionState.Disconnected,
                null,
                "Connection failed. Network settings were restored.",
                connectedAt: null,
                clientPublicIp: resolvedClientIp,
                serverIp: null);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        SetStatus(VpnConnectionState.Disconnecting, _status.ActiveProfile, "Disconnecting...");
        await CleanupLibreGuardStateAsync(cancellationToken);
        SetStatus(VpnConnectionState.Disconnected, null, "Disconnected");
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await CleanupLibreGuardStateAsync(cancellationToken);
        SetStatus(VpnConnectionState.Disconnected, null, "Disconnected");
    }

    public async Task<VpnProfile> ImportOrUpdateProfileAsync(VpnConfigResponse config, VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken)
    {
        var profileName = ProfileNames.For(server, protocol);
        try
        {
            var profile = await CreateProfileAsync(config, server, protocol, cancellationToken);
            if (!string.Equals(profile.ProfileName, profileName, StringComparison.Ordinal))
            {
                throw new VpnConfigurationException("LibreGuard generated an unexpected VPN profile name; refusing to modify network settings.");
            }

            await InstallProfileAsync(profile, cancellationToken);
            return profile;
        }
        catch
        {
            await TryCleanupLibreGuardStateAsync(profileName);
            throw;
        }
    }

    private async Task<VpnProfile> CreateProfileAsync(VpnConfigResponse config, VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken)
    {
        if (!_converters.TryGetValue(protocol, out var converter))
        {
            throw new VpnConfigurationException($"Unsupported VPN protocol: {protocol}");
        }

        return await converter.ConvertAsync(config, server, cancellationToken);
    }

    private async Task InstallProfileAsync(VpnProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Protocol == VpnProtocol.OpenVpn)
        {
            await networkManager.ImportOpenVpnAsync(profile, cancellationToken);
        }
        else
        {
            await networkManager.ImportIkeV2Async(profile, cancellationToken);
        }
    }

    private async Task CleanupLibreGuardStateAsync(CancellationToken cancellationToken)
    {
        await networkManager.EnsureAvailableAsync(cancellationToken);
        await networkManager.DisconnectLibreGuardProfilesAsync(cancellationToken);
        await networkManager.DeleteLibreGuardProfilesAsync(excludeProfileName: null, cancellationToken);
        await networkManager.CleanupLibreGuardArtifactsAsync(excludeProfileName: null, cancellationToken);
    }

    private async Task CleanupFailedProfileAsync(string? profileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            await CleanupLibreGuardStateAsync(cancellationToken);
            return;
        }

        await networkManager.DeactivateAsync(profileName, cancellationToken);
        await networkManager.DeleteLibreGuardProfileAsync(profileName, cancellationToken);
        await networkManager.CleanupLibreGuardProfileArtifactsAsync(profileName, cancellationToken);
    }

    private async Task TryCleanupLibreGuardStateAsync(string? profileName)
    {
        try
        {
            await CleanupFailedProfileAsync(profileName, CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task<VpnConfigResponse> LoadConfigWithCertificateFallbackAsync(VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken)
    {
        try
        {
            return await authSession.ExecuteAuthorizedAsync(
                token => backend.GetVpnConfigAsync(server.Id, protocol, token),
                cancellationToken);
        }
        catch (BackendApiException ex) when ((int)ex.StatusCode == 404)
        {
            var request = await authSession.ExecuteAuthorizedAsync(
                token => backend.RequestCertificateAsync(server.Id, protocol, token),
                cancellationToken);
            if (!request.Success || string.IsNullOrWhiteSpace(request.JobIdText))
            {
                throw new VpnConfigurationException(request.Message ?? "Could not request a VPN certificate.");
            }

            await WaitForCertificateAsync(request.JobIdText, cancellationToken);
            return await authSession.ExecuteAuthorizedAsync(
                token => backend.GetVpnConfigAsync(server.Id, protocol, token),
                cancellationToken);
        }
    }

    private async Task WaitForCertificateAsync(string jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var job = await authSession.ExecuteAuthorizedAsync(
                token => backend.GetCertificateJobAsync(jobId, token),
                cancellationToken);
            if (string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new VpnConfigurationException(job.Message ?? "Certificate generation failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new VpnConfigurationException("Certificate generation is taking longer than expected. Try again in a moment.");
    }

    private void SetStatus(VpnConnectionState state, string? profile, string? message)
    {
        SetStatus(state, profile, message, null, null, null);
    }


    private async Task<string?> ResolveClientPublicIpAsync(string? fallbackClientIp, CancellationToken cancellationToken)
    {
        var resolved = await publicIpResolver.ResolveAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(resolved) ? resolved : fallbackClientIp;
    }

    private void SetStatus(
        VpnConnectionState state,
        string? profile,
        string? message,
        DateTimeOffset? connectedAt,
        string? clientPublicIp,
        string? serverIp)
    {
        _status = new VpnStatus(state, profile, message, connectedAt, clientPublicIp, serverIp);
        StatusChanged?.Invoke(this, _status);
    }
}
