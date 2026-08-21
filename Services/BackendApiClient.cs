using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class BackendApiClient(HttpClient httpClient) : IBackendApiClient
{
    private const int MaxTextResponseChars = 4 * 1024 * 1024;
    private const int MaxBinaryResponseBytes = 32 * 1024 * 1024;

    public void SetBearerToken(string? token)
    {
        httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
        => PostAsync<RegisterRequest, RegisterResponse>("/api/register", request, cancellationToken);

    public Task<ApiMessage> ResendConfirmationAsync(string email, CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/register/resend-confirmation", new { email }, cancellationToken);

    public Task<EmailConfirmationStatus> CheckConfirmationAsync(string userId, CancellationToken cancellationToken)
        => GetAsync<EmailConfirmationStatus>($"/api/register/check-confirmation/{Uri.EscapeDataString(userId)}", cancellationToken);

    public Task<LoginResponse> LoginAsync(string email, string password, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<LoginRequest, LoginResponse>("/api/login", new LoginRequest(
            email,
            password,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<LoginResponse> VerifyTwoFactorAsync(string email, string code, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<TwoFactorVerifyRequest, LoginResponse>("/api/login/verify-2fa", new TwoFactorVerifyRequest(
            email,
            code,
            pendingLoginToken,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<LoginResponse> VerifyRecoveryCodeAsync(string email, string recoveryCode, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<RecoveryCodeVerifyRequest, LoginResponse>("/api/login/verify-recovery-code", new RecoveryCodeVerifyRequest(
            email,
            recoveryCode,
            pendingLoginToken,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<LoginResponse> RefreshAsync(string refreshToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<RefreshTokenRequest, LoginResponse>("/api/login/refresh", new RefreshTokenRequest(
            refreshToken,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<ApiMessage> LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/logout", new { refreshToken }, cancellationToken);

    public Task<TokenCheckResponse> CheckTokenAsync(CancellationToken cancellationToken)
        => GetAsync<TokenCheckResponse>("/api/token/check", cancellationToken);

    public Task<ApiMessage> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
        => PostAsync<ForgotPasswordRequest, ApiMessage>("/api/account/forgot-password", request, cancellationToken);

    public Task<ApiMessage> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
        => PostAsync<ResetPasswordRequest, ApiMessage>("/api/account/reset-password", request, cancellationToken);

    public Task<LoginResponse> LoginWithGoogleAsync(string token, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<GoogleLoginRequest, LoginResponse>("/api/login/google", new GoogleLoginRequest(
            token,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<LoginResponse> LoginWithGoogleCodeAsync(GoogleOAuthAuthorizationCode authorizationCode, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<GoogleCodeLoginRequest, LoginResponse>("/api/login/google/code", new GoogleCodeLoginRequest(
            authorizationCode.ClientId,
            authorizationCode.Code,
            authorizationCode.RedirectUri,
            authorizationCode.CodeVerifier,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<ApiMessage> RemovePreAuthDeviceAsync(string email, string password, int deviceId, CancellationToken cancellationToken)
        => PostAsync<PreAuthDeviceRemovalRequest, ApiMessage>(
            "/api/devices/pre-auth/remove",
            new PreAuthDeviceRemovalRequest(email, password, deviceId),
            cancellationToken);

    public Task<ApiMessage> RemovePreAuthOAuthDeviceAsync(string provider, string idToken, int deviceId, CancellationToken cancellationToken)
        => PostAsync<PreAuthOAuthDeviceRemovalRequest, ApiMessage>(
            "/api/devices/pre-auth/oauth/remove",
            new PreAuthOAuthDeviceRemovalRequest(provider, idToken, deviceId),
            cancellationToken);

    public Task<ApiMessage> RemovePreAuthOAuthDeviceWithCodeAsync(string provider, GoogleOAuthAuthorizationCode authorizationCode, int deviceId, CancellationToken cancellationToken)
        => PostAsync<PreAuthOAuthCodeDeviceRemovalRequest, ApiMessage>(
            "/api/devices/pre-auth/oauth/remove-code",
            new PreAuthOAuthCodeDeviceRemovalRequest(
                provider,
                authorizationCode.ClientId,
                authorizationCode.Code,
                authorizationCode.RedirectUri,
                authorizationCode.CodeVerifier,
                deviceId),
            cancellationToken);

    public Task<LoginResponse> ExchangeOAuthTokenAsync(string email, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<OAuthTokenRequest, LoginResponse>("/api/oauth/token", new OAuthTokenRequest(
            email,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<LoginResponse> CompleteOAuthAsync(string email, string provider, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<OAuthCompleteRequest, LoginResponse>("/api/oauth/complete", new OAuthCompleteRequest(
            email,
            provider,
            device.DeviceId,
            device.AppVersion,
            device.PublicKey,
            device.PublicKeyId,
            device.PublicKeyAlgorithm), cancellationToken);

    public Task<TwoFactorStatus> GetTwoFactorStatusAsync(CancellationToken cancellationToken)
        => GetAsync<TwoFactorStatus>("/api/2fa/status", cancellationToken);

    public Task<TwoFactorSetup> SetupTwoFactorAsync(CancellationToken cancellationToken)
        => PostAsync<object, TwoFactorSetup>("/api/2fa/setup", new { }, cancellationToken);

    public Task<ApiMessage> EnableTwoFactorAsync(string code, CancellationToken cancellationToken)
        => PostAsync<TwoFactorCodeRequest, ApiMessage>("/api/2fa/enable", new TwoFactorCodeRequest(code), cancellationToken);

    public Task<ApiMessage> DisableTwoFactorAsync(CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/2fa/disable", new { }, cancellationToken);

    public Task<ApiMessage> ResetTwoFactorAsync(CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/2fa/reset", new { }, cancellationToken);

    public Task<RecoveryCodesResponse> GenerateRecoveryCodesAsync(CancellationToken cancellationToken)
        => PostAsync<object, RecoveryCodesResponse>("/api/2fa/recovery-codes/generate", new { }, cancellationToken);

    public async Task<IReadOnlyList<VpnServer>> GetServersAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        StartupDiagnostics.Log("vpn-servers-request-start");
        HttpStatusCode? statusCode = null;

        try
        {
            using var response = await httpClient.GetAsync("/api/vpn/servers", cancellationToken);
            statusCode = response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                StartupDiagnostics.Log($"vpn-servers-request-response status={(int)response.StatusCode}");
            }

            await EnsureSuccessAsync(response, cancellationToken);
            var content = await ReadResponseTextAsync(response, cancellationToken);

            IReadOnlyList<VpnServer>? servers;
            try
            {
                var wrapped = JsonSerializer.Deserialize<VpnServersResponse>(content, JsonOptions.Default);
                servers = wrapped?.Servers ?? JsonSerializer.Deserialize<IReadOnlyList<VpnServer>>(content, JsonOptions.Default);
            }
            catch (JsonException ex)
            {
                StartupDiagnostics.Log($"vpn-servers-request-deserialization-failed {DescribeServerRequestFailure(ex)}");
                throw;
            }

            servers ??= [];
            StartupDiagnostics.Log($"vpn-servers-request-success status={(int)response.StatusCode} count={servers.Count}");
            return servers;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"vpn-servers-request-failed {DescribeServerRequestFailure(ex)}");
            throw;
        }
        finally
        {
            StartupDiagnostics.Log(
                $"vpn-servers-request-end elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F0} " +
                $"status={(statusCode.HasValue ? ((int)statusCode.Value).ToString() : "unavailable")}");
        }
    }

    internal static string DescribeServerRequestFailure(Exception exception)
        => exception is BackendApiException backendException
            ? $"type={backendException.GetType().Name} status={(int)backendException.StatusCode}"
            : $"type={exception.GetType().Name}";

    public Task<VpnConfigResponse> GetVpnConfigAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken)
        => PostAsync<VpnConfigRequest, VpnConfigResponse>("/api/vpn/config", new VpnConfigRequest(serverId, ProtocolToApi(protocol)), cancellationToken);

    public Task<VpnConfigResponse> GetVpnConfigQueryAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken)
        => GetAsync<VpnConfigResponse>($"/api/vpn/config?serverId={serverId}&protocol={Uri.EscapeDataString(ProtocolToApi(protocol))}", cancellationToken);

    public async Task<Stream> DownloadOpenVpnConfigAsync(int serverId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/vpn/config/openvpn/download",
            new OpenVpnConfigDownloadRequest(serverId),
            JsonOptions.Default,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadResponseStreamAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<UserCertificate>> GetCertificatesAsync(CancellationToken cancellationToken)
    {
        var response = await GetAsync<CertificateListResponse>("/api/certificates", cancellationToken);
        return response.Certificates ?? [];
    }

    public Task<CertificateRequestResponse> RequestCertificateAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken)
        => PostAsync<CertificateRequest, CertificateRequestResponse>("/api/certificates/request", new CertificateRequest(ProtocolToApi(protocol), serverId), cancellationToken);

    public Task<CertificateJob> GetCertificateJobAsync(string jobId, CancellationToken cancellationToken)
        => GetAsync<CertificateJob>($"/api/certificates/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);

    public Task<UsageQuota> GetUsageQuotaAsync(CancellationToken cancellationToken)
        => GetAsync<UsageQuota>("/api/usage/quota", cancellationToken);

    public Task<UsageQuota> CanConnectAsync(CancellationToken cancellationToken)
        => GetAsync<UsageQuota>("/api/usage/can-connect", cancellationToken);

    public Task<SubscriptionStatus> GetSubscriptionStatusAsync(CancellationToken cancellationToken)
        => GetAsync<SubscriptionStatus>("/api/subscription/status", cancellationToken);

    public Task<DnsPreferenceResponse> GetDnsPreferenceAsync(CancellationToken cancellationToken)
        => GetAsync<DnsPreferenceResponse>("/api/dns/settings", cancellationToken);

    public Task<DnsPreferenceResponse> UpdateDnsPreferenceAsync(bool enabled, CancellationToken cancellationToken)
        => PutAsync<UpdateDnsPreferenceRequest, DnsPreferenceResponse>(
            "/api/dns/settings",
            new UpdateDnsPreferenceRequest(enabled),
            cancellationToken);

    public Task<ServerAccessResponse> CanAccessServerAsync(int serverTier, CancellationToken cancellationToken)
        => GetAsync<ServerAccessResponse>($"/api/subscription/can-access-server/{serverTier}", cancellationToken);

    public Task<SubscriptionDeviceRegistrationResponse> RegisterSubscriptionDeviceAsync(DeviceRegistrationPayload device, CancellationToken cancellationToken)
        => PostAsync<SubscriptionDeviceRequest, SubscriptionDeviceRegistrationResponse>(
            "/api/subscription/register-device",
            new SubscriptionDeviceRequest(device.DeviceId, device.AppVersion),
            cancellationToken);

    public Task<ApiMessage> RemoveSubscriptionDeviceAsync(string deviceId, CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/subscription/remove-device", new { deviceId }, cancellationToken);

    public Task<CheckoutUrlResponse> GetCheckoutUrlAsync(string cycle, CancellationToken cancellationToken)
        => GetAsync<CheckoutUrlResponse>($"/api/subscription/checkout-url?cycle={Uri.EscapeDataString(cycle)}", cancellationToken);

    public Task<MoneroPriceResponse> GetMoneroPriceAsync(BillingCycle cycle, CancellationToken cancellationToken)
    {
        var query = cycle == BillingCycle.Yearly ? "?billingCycle=Yearly" : string.Empty;
        return GetAsync<MoneroPriceResponse>($"/api/monero/price{query}", cancellationToken);
    }

    public Task<MoneroInvoiceResponse> CreateMoneroInvoiceAsync(BillingCycle cycle, CancellationToken cancellationToken)
        => PostAsync<object, MoneroInvoiceResponse>("/api/monero/create-invoice", new { billingCycle = (int)cycle }, cancellationToken);

    public Task<MoneroStatusResponse> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken cancellationToken)
        => GetAsync<MoneroStatusResponse>($"/api/monero/status/{Uri.EscapeDataString(invoiceId)}", cancellationToken);

    public Task<MoneroInvoiceResponse> GetLatestMoneroInvoiceAsync(CancellationToken cancellationToken)
        => GetAsync<MoneroInvoiceResponse>("/api/monero/latest-invoice", cancellationToken);

    public Task<CardCheckoutResponse> CreateCardCheckoutAsync(BillingCycle cycle, CancellationToken cancellationToken)
        => PostAsync<object, CardCheckoutResponse>("/api/checkout/card", new { billingCycle = (int)cycle }, cancellationToken);

    public Task<CardPaymentStatusResponse> GetCardPaymentStatusAsync(string transactionId, CancellationToken cancellationToken)
        => GetAsync<CardPaymentStatusResponse>($"/api/payment/status/{Uri.EscapeDataString(transactionId)}", cancellationToken);

    public async Task<IReadOnlyList<UserDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("/api/devices", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var content = await ReadResponseTextAsync(response, cancellationToken);
        return JsonSerializer.Deserialize<DeviceListEnvelope>(content, JsonOptions.Default)?.Devices
            ?? JsonSerializer.Deserialize<IReadOnlyList<UserDevice>>(content, JsonOptions.Default)
            ?? [];
    }

    public Task<ApiMessage> RemoveDeviceAsync(int id, CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>($"/api/devices/remove/{id}", new { }, cancellationToken);

    public Task<ApiMessage> DeleteDeviceAsync(int id, CancellationToken cancellationToken)
        => DeleteAsync<ApiMessage>($"/api/devices/{id}", cancellationToken);

    public Task<ApiMessage> RemoveAllOtherDevicesAsync(CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/devices/remove-all-others", new { }, cancellationToken);

    public Task<ApiMessage> RemoveAllInactiveDevicesAsync(CancellationToken cancellationToken)
        => PostAsync<object, ApiMessage>("/api/devices/remove-all-inactive", new { }, cancellationToken);

    public async Task<Stream> DownloadCertificateConfigAsync(int certificateId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/api/user-certificates/{certificateId}/download/config", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadResponseStreamAsync(response, cancellationToken);
    }

    public async Task<Stream> DownloadCertificateAsync(int certificateId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/api/user-certificates/{certificateId}/download/certificate", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadResponseStreamAsync(response, cancellationToken);
    }

    private static string ProtocolToApi(VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.OpenVpn => "OpenVPN",
        _ => "IKEv2"
    };

    private async Task<TResponse> GetAsync<TResponse>(string route, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(route, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<TResponse>(response, cancellationToken);
    }

    private async Task<TResponse> DeleteAsync<TResponse>(string route, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync(route, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<TResponse>(response, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string route, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(route, request, JsonOptions.Default, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<TResponse>(response, cancellationToken);
    }

    private async Task<TResponse> PutAsync<TRequest, TResponse>(string route, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(route, request, JsonOptions.Default, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<TResponse>(response, cancellationToken);
    }

    private static async Task<TResponse> ReadJsonAsync<TResponse>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await ReadResponseTextAsync(response, cancellationToken);
        var value = JsonSerializer.Deserialize<TResponse>(content, JsonOptions.Default);
        return value ?? throw new BackendApiException("Backend returned an empty response.", response.StatusCode);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadResponseTextAsync(response, cancellationToken);
        throw new BackendApiException(GetSafeErrorMessage(response.StatusCode), response.StatusCode, body);
    }

    private static async Task<string> ReadResponseTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength > MaxTextResponseChars)
            {
                throw new ResponseLimitExceededException();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var builder = new System.Text.StringBuilder();
            var buffer = new char[8192];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (builder.Length + read > MaxTextResponseChars)
                {
                    throw new ResponseLimitExceededException();
                }

                builder.Append(buffer, 0, read);
            }

            return builder.ToString();
        }
        catch (ResponseLimitExceededException)
        {
            throw new BackendApiException("The backend response was too large.", response.StatusCode);
        }
    }

    private static async Task<Stream> ReadResponseStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength > MaxBinaryResponseBytes)
            {
                throw new ResponseLimitExceededException();
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var memory = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (memory.Length + read > MaxBinaryResponseBytes)
                {
                    memory.Dispose();
                    throw new ResponseLimitExceededException();
                }

                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            memory.Position = 0;
            return memory;
        }
        catch (ResponseLimitExceededException)
        {
            throw new BackendApiException("The backend response was too large.", response.StatusCode);
        }
    }

    private static string GetSafeErrorMessage(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized => "Authentication failed. Please sign in again.",
            HttpStatusCode.Forbidden => "You are not authorized to perform this action.",
            HttpStatusCode.NotFound => "The requested resource was not found.",
            HttpStatusCode.Conflict => "The request could not be completed.",
            _ when (int)statusCode >= 500 => "The backend service is temporarily unavailable.",
            _ => "The request could not be completed."
        };

    private sealed record DeviceListEnvelope(IReadOnlyList<UserDevice> Devices);

    private sealed class ResponseLimitExceededException : Exception;
}

public sealed class BackendApiException(string message, HttpStatusCode statusCode, string? responseBody = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
}
