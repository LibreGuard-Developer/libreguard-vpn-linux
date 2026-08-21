using System.Net;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class AuthSessionService(
    IBackendApiClient backend,
    IDeviceIdentityService deviceIdentity,
    ISecretStore secretStore) : IAuthSessionService
{
    private const string TokenKey = "jwt-token";
    private const string RefreshTokenKey = "refresh-token";
    private const string EmailKey = "account-email";
    private const string PlanTypeKey = "plan-type";
    private const string ReportedAppVersionKey = "reported-app-version";
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _appVersionReportAttempted;

    public AuthSession? CurrentSession { get; private set; }

    public async Task SetSessionAsync(AuthSession session, CancellationToken cancellationToken)
    {
        var device = await deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
        await PersistSessionAsync(session, device.AppVersion, cancellationToken);
    }

    private async Task PersistSessionAsync(AuthSession session, string appVersion, CancellationToken cancellationToken)
    {
        CurrentSession = session;
        await secretStore.SetAsync(TokenKey, session.Token, cancellationToken);
        await secretStore.SetAsync(RefreshTokenKey, session.RefreshToken, cancellationToken);
        await secretStore.SetAsync(EmailKey, session.Email, cancellationToken);
        await secretStore.SetAsync(PlanTypeKey, session.PlanType, cancellationToken);
        await secretStore.SetAsync(ReportedAppVersionKey, appVersion, cancellationToken);
        _appVersionReportAttempted = appVersion;
        backend.SetBearerToken(session.Token);
    }

    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken)
    {
        StartupDiagnostics.Log("auth-session-restore-start");
        var token = await secretStore.GetAsync(TokenKey, cancellationToken);
        var refreshToken = await secretStore.GetAsync(RefreshTokenKey, cancellationToken);
        var email = await secretStore.GetAsync(EmailKey, cancellationToken);
        var planType = await secretStore.GetAsync(PlanTypeKey, cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                await ClearSessionAsync(cancellationToken);
                StartupDiagnostics.Log("auth-session-restore-result result=no-session");
                return false;
            }

            StartupDiagnostics.Log("auth-session-restore-result result=refresh-required");
            return await TryRefreshSessionAsync(cancellationToken);
        }

        backend.SetBearerToken(token);

        try
        {
            var check = await backend.CheckTokenAsync(cancellationToken);
            if (check.Valid)
            {
                UpdateCurrentSession(token, refreshToken, check.Email ?? email, null, null, planType);
                await ReportCurrentAppVersionIfNeededAsync(cancellationToken);
                StartupDiagnostics.Log("auth-session-restore-result result=token-valid");
                return true;
            }

            StartupDiagnostics.Log("auth-session-restore-result result=token-invalid");
        }
        catch (BackendApiException ex) when (IsAuthFailure(ex))
        {
            StartupDiagnostics.Log($"auth-session-restore-token-check-failed type={ex.GetType().Name} status={(int)ex.StatusCode}");
            return await TryRefreshSessionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"auth-session-restore-token-check-failed type={ex.GetType().Name}");
        }

        StartupDiagnostics.Log("auth-session-restore-result result=refresh-required");
        return await TryRefreshSessionAsync(cancellationToken);
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (await TryRestoreSessionAsync(cancellationToken))
        {
            return;
        }

        throw new SessionExpiredException("Please sign in again.");
    }

    public async Task<bool> TryRefreshSessionAsync(CancellationToken cancellationToken)
    {
        StartupDiagnostics.Log("auth-session-refresh-start");
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var currentToken = await secretStore.GetAsync(TokenKey, cancellationToken);
            var persistedPlanType = await secretStore.GetAsync(PlanTypeKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(currentToken))
            {
                backend.SetBearerToken(currentToken);
                try
                {
                    var currentCheck = await backend.CheckTokenAsync(cancellationToken);
                    if (currentCheck.Valid)
                    {
                        var currentRefresh = await secretStore.GetAsync(RefreshTokenKey, cancellationToken);
                        UpdateCurrentSession(currentToken, currentRefresh, currentCheck.Email, null, null, persistedPlanType);
                        await ReportCurrentAppVersionIfNeededAsync(cancellationToken);
                        StartupDiagnostics.Log("auth-session-refresh-result result=token-valid");
                        return true;
                    }
                }
                catch (BackendApiException ex) when (IsAuthFailure(ex))
                {
                    StartupDiagnostics.Log($"auth-session-refresh-token-check-failed type={ex.GetType().Name} status={(int)ex.StatusCode}");
                }
            }

            var refreshToken = await secretStore.GetAsync(RefreshTokenKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                await ClearSessionAsync(cancellationToken);
                StartupDiagnostics.Log("auth-session-refresh-result result=no-refresh-token");
                return false;
            }

            try
            {
                var device = await deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
                var refreshed = await backend.RefreshAsync(refreshToken, device, cancellationToken);
                if (!refreshed.Success || string.IsNullOrWhiteSpace(refreshed.Token) || string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                {
                    await ClearSessionAsync(cancellationToken);
                    StartupDiagnostics.Log("auth-session-refresh-result result=rejected");
                    return false;
                }

                var activeDevices = refreshed.ActiveDevices != 0 ? refreshed.ActiveDevices : CurrentSession?.ActiveDevices ?? 0;
                var maxDevices = refreshed.MaxDevices != 0 ? refreshed.MaxDevices : CurrentSession?.MaxDevices ?? 0;
                var planType = refreshed.PlanType ?? persistedPlanType ?? "Free";
                var session = new AuthSession(
                    refreshed.Token,
                    refreshed.RefreshToken,
                    refreshed.Email ?? CurrentSession?.Email ?? string.Empty,
                    refreshed.UserId ?? CurrentSession?.UserId ?? string.Empty,
                    refreshed.DeviceId ?? CurrentSession?.DeviceId ?? device.DeviceId,
                    activeDevices,
                    maxDevices,
                    planType);

                await SetSessionAsync(session, cancellationToken);
                StartupDiagnostics.Log("auth-session-refresh-result result=success");
                return true;
            }
            catch (BackendApiException ex) when (RequiresReauthentication(ex))
            {
                await ClearSessionAsync(cancellationToken);
                StartupDiagnostics.Log($"auth-session-refresh-result result=reauthentication-required status={(int)ex.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"auth-session-refresh-failed type={ex.GetType().Name}");
                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        CurrentSession = null;
        backend.SetBearerToken(null);

        Exception? firstFailure = null;
        foreach (var key in new[] { TokenKey, RefreshTokenKey, EmailKey, PlanTypeKey })
        {
            try
            {
                await secretStore.DeleteAsync(key, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
        {
            throw new InvalidOperationException("One or more persisted session values could not be deleted.", firstFailure);
        }
    }

    public Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken)
        => secretStore.GetAsync(RefreshTokenKey, cancellationToken);

    public async Task<T> ExecuteAuthorizedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            return await operation(cancellationToken);
        }
        catch (BackendApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (!await TryRefreshSessionAsync(cancellationToken))
            {
                throw new SessionExpiredException("Please sign in again.");
            }
        }

        try
        {
            return await operation(cancellationToken);
        }
        catch (BackendApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            await ClearSessionAsync(cancellationToken);
            throw new SessionExpiredException("Please sign in again.");
        }
    }

    public async Task ExecuteAuthorizedAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteAuthorizedAsync(async token =>
        {
            await operation(token);
            return true;
        }, cancellationToken);
    }

    private void UpdateCurrentSession(string token, string? refreshToken, string? email, string? userId, string? deviceId, string? planType)
    {
        CurrentSession = new AuthSession(
            token,
            refreshToken ?? CurrentSession?.RefreshToken ?? string.Empty,
            email ?? CurrentSession?.Email ?? string.Empty,
            userId ?? CurrentSession?.UserId ?? string.Empty,
            deviceId ?? CurrentSession?.DeviceId ?? string.Empty,
            CurrentSession?.ActiveDevices ?? 0,
            CurrentSession?.MaxDevices ?? 0,
            planType ?? "Free");
    }

    private async Task ReportCurrentAppVersionIfNeededAsync(CancellationToken cancellationToken)
    {
        try
        {
            var device = await deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(device.AppVersion)
                || string.Equals(_appVersionReportAttempted, device.AppVersion, StringComparison.Ordinal))
            {
                return;
            }

            var reportedAppVersion = await secretStore.GetAsync(ReportedAppVersionKey, cancellationToken);
            if (string.Equals(reportedAppVersion, device.AppVersion, StringComparison.Ordinal))
            {
                _appVersionReportAttempted = device.AppVersion;
                return;
            }

            // A valid persisted session can survive an app upgrade, so /api/token/check
            // alone is not enough to update the backend's active app-version aggregate.
            // Reuse the authenticated device-registration route to report the new version
            // without rotating an otherwise healthy token.
            _appVersionReportAttempted = device.AppVersion;
            await backend.RegisterSubscriptionDeviceAsync(device, cancellationToken);
            await secretStore.SetAsync(ReportedAppVersionKey, device.AppVersion, cancellationToken);
            StartupDiagnostics.Log($"auth-session-app-version-report result=success app_version=\"{device.AppVersion}\"");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackendApiException ex)
        {
            StartupDiagnostics.Log($"auth-session-app-version-report result=failed type={ex.GetType().Name} status={(int)ex.StatusCode}");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"auth-session-app-version-report result=failed type={ex.GetType().Name}");
        }
    }

    private static bool IsAuthFailure(BackendApiException ex)
        => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool RequiresReauthentication(BackendApiException ex)
        => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
