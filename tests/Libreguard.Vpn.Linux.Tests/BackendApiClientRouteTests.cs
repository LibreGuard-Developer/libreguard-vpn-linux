using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class BackendApiClientRouteTests
{
    [Fact]
    public async Task AccountAndDeviceMethods_UseBackendControllerRoutes()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        await client.ResendConfirmationAsync("user@example.com", CancellationToken.None);
        await client.SetupTwoFactorAsync(CancellationToken.None);
        await client.DisableTwoFactorAsync(CancellationToken.None);
        await client.RemoveDeviceAsync(42, CancellationToken.None);
        await client.RemoveAllOtherDevicesAsync(CancellationToken.None);
        await client.RemoveAllInactiveDevicesAsync(CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/register/resend-confirmation");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/2fa/setup");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/2fa/disable");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/devices/remove/42");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/devices/remove-all-others");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/devices/remove-all-inactive");
    }

    [Fact]
    public async Task AuthMethods_UseBackendControllerRoutesAndPayloads()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });
        var device = new DeviceRegistrationPayload("device-1", "1.0.0", "public-key", "key-id", "RSA-OAEP-256");

        await client.LoginAsync("user@example.com", "pass", device, CancellationToken.None);
        await client.VerifyTwoFactorAsync("user@example.com", "123456", "pending-login-token", device, CancellationToken.None);
        await client.VerifyRecoveryCodeAsync("user@example.com", "abcd-efgh", "pending-login-token", device, CancellationToken.None);
        await client.LoginWithGoogleAsync("google-id-token", device, CancellationToken.None);
        await client.LoginWithGoogleCodeAsync(new GoogleOAuthAuthorizationCode(
            "google-client-id.apps.googleusercontent.com",
            "authorization-code",
            "http://127.0.0.1:54321/callback",
            "code-verifier"), device, CancellationToken.None);
        await client.RemovePreAuthDeviceAsync("user@example.com", "pass", 42, CancellationToken.None);
        await client.RemovePreAuthOAuthDeviceAsync("Google", "google-id-token", 42, CancellationToken.None);
        await client.RemovePreAuthOAuthDeviceWithCodeAsync("Google", new GoogleOAuthAuthorizationCode(
            "google-client-id.apps.googleusercontent.com",
            "removal-authorization-code",
            "http://127.0.0.1:54322/callback",
            "removal-code-verifier"), 43, CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/login" && request.Body.Contains("\"appVersion\":\"1.0.0\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/login/verify-2fa" && request.Body.Contains("\"twoFactorCode\":\"123456\"") && request.Body.Contains("\"pendingLoginToken\":\"pending-login-token\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/login/verify-recovery-code" && request.Body.Contains("\"recoveryCode\":\"abcd-efgh\"") && request.Body.Contains("\"pendingLoginToken\":\"pending-login-token\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/login/google" && request.Body.Contains("\"idToken\":\"google-id-token\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/login/google/code" && request.Body.Contains("\"clientId\":\"google-client-id.apps.googleusercontent.com\"") && request.Body.Contains("\"code\":\"authorization-code\"") && request.Body.Contains("\"codeVerifier\":\"code-verifier\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/devices/pre-auth/remove" && request.Body.Contains("\"email\":\"user@example.com\"") && request.Body.Contains("\"password\":\"pass\"") && request.Body.Contains("\"deviceIdToRemove\":42"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/devices/pre-auth/oauth/remove" && request.Body.Contains("\"provider\":\"Google\"") && request.Body.Contains("\"idToken\":\"google-id-token\"") && request.Body.Contains("\"deviceIdToRemove\":42"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/devices/pre-auth/oauth/remove-code" && request.Body.Contains("\"provider\":\"Google\"") && request.Body.Contains("\"code\":\"removal-authorization-code\"") && request.Body.Contains("\"codeVerifier\":\"removal-code-verifier\"") && request.Body.Contains("\"deviceIdToRemove\":43"));
    }

    [Fact]
    public async Task CertificateAndUsageMethods_UseBackendControllerRoutes()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var certificates = await client.GetCertificatesAsync(CancellationToken.None);
        var quota = await client.CanConnectAsync(CancellationToken.None);
        await using var config = await client.DownloadCertificateConfigAsync(7, CancellationToken.None);
        await using var cert = await client.DownloadCertificateAsync(7, CancellationToken.None);

        Assert.Single(certificates);
        Assert.True(quota.CanConnect);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/certificates");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/usage/can-connect");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/user-certificates/7/download/config");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/user-certificates/7/download/certificate");
    }

    [Fact]
    public async Task SubscriptionMethods_UseBackendControllerRoutes()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });
        var device = new DeviceRegistrationPayload("device-1", "1.0.0", "public-key", "key-id", "RSA-OAEP-256");

        var access = await client.CanAccessServerAsync(1, CancellationToken.None);
        var registered = await client.RegisterSubscriptionDeviceAsync(device, CancellationToken.None);
        var removed = await client.RemoveSubscriptionDeviceAsync("device-1", CancellationToken.None);

        Assert.True(access.CanAccess);
        Assert.Equal("abcd...", registered.DeviceIdHash);
        Assert.True(removed.Success);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/subscription/can-access-server/1");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/subscription/register-device" && request.Body.Contains("\"deviceId\":\"device-1\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/subscription/remove-device" && request.Body.Contains("\"deviceId\":\"device-1\""));
    }

    [Fact]
    public async Task DnsPreferenceMethods_UseExactAuthenticatedRouteAndPayload()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });
        client.SetBearerToken("access-token");

        var current = await client.GetDnsPreferenceAsync(CancellationToken.None);
        var updated = await client.UpdateDnsPreferenceAsync(false, CancellationToken.None);

        Assert.False(current.RequestedEnabled);
        Assert.True(current.CanUseAdBlocking);
        Assert.False(current.EffectiveEnabled);
        Assert.Equal("Standard", current.EffectiveMode);
        Assert.Equal(23, current.PropagationSeconds);
        Assert.Equal(current, updated);

        var getRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/dns/settings");
        Assert.Equal(string.Empty, getRequest.Query);
        Assert.Equal(string.Empty, getRequest.Body);
        Assert.Equal("Bearer access-token", getRequest.Authorization);

        var putRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.Path == "/api/dns/settings");
        Assert.Equal(string.Empty, putRequest.Query);
        Assert.Equal("{\"adBlockingEnabled\":false}", putRequest.Body);
        Assert.Equal("Bearer access-token", putRequest.Authorization);
    }

    [Fact]
    public async Task UpdateDnsPreference_PropagatesForbiddenStatusAndResponseBody()
    {
        const string responseBody = "{\"errorCode\":\"PRO_REQUIRED\",\"message\":\"A Pro subscription is required.\",\"settings\":{\"requestedEnabled\":false,\"canUseAdBlocking\":false,\"effectiveEnabled\":false,\"effectiveMode\":\"Regular\",\"propagationSeconds\":15}}";
        var client = new BackendApiClient(new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(responseBody)
        }))
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var exception = await Assert.ThrowsAsync<BackendApiException>(
            () => client.UpdateDnsPreferenceAsync(true, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("You are not authorized to perform this action.", exception.Message);
        Assert.Equal(responseBody, exception.ResponseBody);
    }

    [Fact]
    public async Task PaymentMethods_UseWindowsCompatibleRoutes()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var price = await client.GetMoneroPriceAsync(BillingCycle.Yearly, CancellationToken.None);
        var invoice = await client.CreateMoneroInvoiceAsync(BillingCycle.Yearly, CancellationToken.None);
        var status = await client.GetMoneroPaymentStatusAsync("invoice-1", CancellationToken.None);
        var latest = await client.GetLatestMoneroInvoiceAsync(CancellationToken.None);
        var checkout = await client.CreateCardCheckoutAsync(BillingCycle.Monthly, CancellationToken.None);
        var cardStatus = await client.GetCardPaymentStatusAsync("ch_123/with space", CancellationToken.None);

        Assert.Equal(0.04m, price.XmrAmount);
        Assert.Equal("invoice-1", invoice.InvoiceId);
        Assert.Equal("Pending", status.Status);
        Assert.Equal("invoice-1", latest.InvoiceId);
        Assert.Equal("https://checkout.example/pro", checkout.CheckoutUrl);
        Assert.Equal("ch_123", checkout.TransactionId);
        Assert.Equal("Paid", cardStatus.Status);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/monero/price" && request.Query.Contains("billingCycle=Yearly"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/monero/create-invoice" && request.Body.Contains("\"billingCycle\":1"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/monero/status/invoice-1");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/monero/latest-invoice");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/checkout/card" && request.Body.Contains("\"billingCycle\":0"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/payment/status/ch_123%2Fwith%20space");
    }

    [Fact]
    public async Task VpnServersMethod_UsesControllerRouteAndServerEnvelope()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var servers = await client.GetServersAsync(CancellationToken.None);

        Assert.Single(servers);
        Assert.Equal("Amsterdam, Netherlands", servers[0].DisplayName);
        Assert.True(servers[0].IsPremium);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/vpn/servers");
    }

    [Fact]
    public async Task VpnServersMethod_PropagatesMalformedPayload()
    {
        var client = new BackendApiClient(new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json")
        }))
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        await Assert.ThrowsAsync<JsonException>(() => client.GetServersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VpnServersMethod_PropagatesNonSuccessStatusWithoutLoggingResponseContent()
    {
        var client = new BackendApiClient(new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("access-token=must-not-appear-in-diagnostics")
        }))
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var exception = await Assert.ThrowsAsync<BackendApiException>(() => client.GetServersAsync(CancellationToken.None));
        var diagnostic = BackendApiClient.DescribeServerRequestFailure(exception);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("The backend service is temporarily unavailable.", exception.Message);
        Assert.Contains("access-token", exception.ResponseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access-token", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-appear", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status=503", diagnostic);
    }

    [Fact]
    public async Task VpnServersMethod_RejectsOversizedResponse()
    {
        var client = new BackendApiClient(new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 4 * 1024 * 1024 + 1))
        }))
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var exception = await Assert.ThrowsAsync<BackendApiException>(() => client.GetServersAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("The backend response was too large.", exception.Message);
        Assert.Null(exception.ResponseBody);
    }

    [Fact]
    public async Task VpnConfigMethods_UseControllerRoutes()
    {
        var handler = new RecordingHandler();
        var client = new BackendApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://management.libreguard.test")
        });

        var postConfig = await client.GetVpnConfigAsync(9, VpnProtocol.OpenVpn, CancellationToken.None);
        var getConfig = await client.GetVpnConfigQueryAsync(9, VpnProtocol.Ikev2, CancellationToken.None);
        await using var openVpn = await client.DownloadOpenVpnConfigAsync(9, CancellationToken.None);

        using var reader = new StreamReader(openVpn);
        Assert.Equal("OpenVPN", postConfig.Protocol);
        Assert.Equal("IKEV2", getConfig.Protocol);
        Assert.Contains("client", await reader.ReadToEndAsync(CancellationToken.None));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/vpn/config" && request.Body.Contains("\"protocol\":\"OpenVPN\""));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/api/vpn/config" && request.Query.Contains("protocol=IKEv2"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/vpn/config/openvpn/download" && request.Body.Contains("\"serverId\":9"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestRecord(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.RequestUri?.Query ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString()));

            if (request.RequestUri?.AbsolutePath == "/api/vpn/config/openvpn/download")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("client\nremote vpn.example.com 1194\n")
                };
            }

            object payload = request.RequestUri?.AbsolutePath switch
            {
                "/api/2fa/setup" => new { sharedKey = "aaaa bbbb", authenticatorUri = "otpauth://totp/test", manualEntryKey = "aaaabbbb" },
                "/api/certificates" => new { certificates = new[] { new { id = 7, vpnType = "OpenVPN", name = "OVPN_client7", serverName = "NL", serverIp = "10.0.0.1", isRevoked = false } } },
                "/api/usage/can-connect" => new { allowed = true, bytesUsed = 1024, bytesLimit = 2048 },
                "/api/subscription/can-access-server/1" => new { canAccess = true, serverTier = "Premium", requiresPro = true },
                "/api/subscription/register-device" => new { message = "Device registered successfully", deviceIdHash = "abcd...", isNewDevice = true },
                "/api/dns/settings" => new { requestedEnabled = false, canUseAdBlocking = true, effectiveEnabled = false, effectiveMode = "Standard", propagationSeconds = 23 },
                "/api/monero/price" => new { xmrAmount = 0.04m, usdAmount = 5.99m, xmrPriceUsd = 149.75m, currency = "XMR", product = "LibreGuard Pro" },
                "/api/monero/create-invoice" => new { invoiceId = "invoice-1", paymentAddress = "xmr-address", amount = 0.04m, currency = "XMR", status = "Pending", description = "LibreGuard Pro", createdAt = DateTimeOffset.UtcNow, billingCycle = "Yearly" },
                "/api/monero/status/invoice-1" => new { invoiceId = "invoice-1", status = "Pending", amountRequired = 0.04m, amountReceived = 0.01m, confirmations = 2, requiredConfirmations = 10, createdAt = DateTimeOffset.UtcNow, expiresAt = DateTimeOffset.UtcNow.AddHours(24), billingCycle = "Yearly" },
                "/api/monero/latest-invoice" => new { invoiceId = "invoice-1", paymentAddress = "xmr-address", amount = 0.04m, currency = "XMR", status = "Pending", description = "LibreGuard Pro", createdAt = DateTimeOffset.UtcNow, billingCycle = "Yearly" },
                "/api/checkout/card" => new { checkoutUrl = "https://checkout.example/pro", transactionId = "ch_123", localTransactionId = 42, billingCycle = "Monthly", amountUsd = 5.99m, currency = "USD", productId = "prod_monthly", customerEmail = "user@example.com", requestId = "card-42" },
                var path when path?.StartsWith("/api/payment/status/", StringComparison.Ordinal) == true => new { transactionId = "ch_123", status = "Paid", amountRequired = 5.99m, amountReceived = 5.99m, confirmedAt = "2026-07-10T12:00:00Z", expiresAt = (string?)null, serverTime = "2026-07-10T12:00:01Z" },
                "/api/vpn/servers" => new { servers = new[] { new { id = 1, serverName = "NL", serverIp = "10.0.0.1", serverHostname = "nl.example", country = "Netherlands", city = "Amsterdam", linkSpeed = 100, pricingTier = "Premium", load = 10, activeConnections = 2, latencyPingPort = 5001, loadDataFresh = true } } },
                "/api/vpn/config" => new { success = true, protocol = request.Method == HttpMethod.Get ? "IKEV2" : "OpenVPN", serverName = "NL", serverIp = "10.0.0.1", certificateName = "OVPN_client7", configContent = "client" },
                _ => new { success = true, message = "ok" }
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload, options: JsonOptions.Default)
            };
            await Task.CompletedTask;
            return response;
        }
    }

    private sealed class StaticResponseHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory());
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Query, string Body, string? Authorization);
}
