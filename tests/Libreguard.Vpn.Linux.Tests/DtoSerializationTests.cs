using System.Text.Json;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class DtoSerializationTests
{
    [Fact]
    public void LoginRequest_SerializesDeviceKeyMetadata()
    {
        var request = new LoginRequest(
            "user@example.com",
            "password",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256");

        var json = JsonSerializer.Serialize(request, JsonOptions.Default);

        Assert.Contains("\"deviceId\":\"device-1\"", json);
        Assert.Contains("\"devicePublicKey\":\"public-key\"", json);
        Assert.Contains("\"devicePublicKeyId\":\"key-id\"", json);
        Assert.Contains("\"devicePublicKeyAlgorithm\":\"RSA-OAEP-256\"", json);
    }

    [Fact]
    public void RegisterRequest_SerializesAppVersion()
    {
        var request = new RegisterRequest(
            "user@example.com",
            "password",
            "password",
            "Linux/1.1.32");

        var json = JsonSerializer.Serialize(request, JsonOptions.Default);

        Assert.Contains("\"appVersion\":\"Linux/1.1.32\"", json);
    }

    [Fact]
    public void VpnServer_DisplayName_UsesCityAndCountry()
    {
        var server = new VpnServer(1, "NL", "10.0.0.1", "nl.example", "Netherlands", "Amsterdam", 100, "free", 10, 1, 443, true);

        Assert.Equal("Amsterdam, Netherlands", server.DisplayName);
        Assert.False(server.IsPremium);
        Assert.Equal("🇳🇱", server.CountryFlag);
        Assert.Equal("100 Mbps", server.LinkSpeedText);
    }

    [Fact]
    public void BackendResponses_DeserializeCurrentControllerShapes()
    {
        var login = JsonSerializer.Deserialize<LoginResponse>(
            """{"requiresTwoFactor":false,"token":"jwt","refreshToken":"refresh","email":"user@example.com","userId":"u1","deviceId":"device-1","pendingLoginToken":"pending"}""",
            JsonOptions.Default);
        var login2fa = JsonSerializer.Deserialize<LoginResponse>(
            """{"requiresTwoFactor":true,"email":"user@example.com","userId":"u1","deviceId":"device-1","pendingLoginToken":"pending","message":"Two-factor authentication required."}""",
            JsonOptions.Default);
        var confirmation = JsonSerializer.Deserialize<EmailConfirmationStatus>(
            """{"emailConfirmed":true,"message":"Email has been confirmed!","email":"user@example.com","userId":"u1"}""",
            JsonOptions.Default);
        var serverList = JsonSerializer.Deserialize<VpnServersResponse>(
            """{"servers":[{"id":1,"serverName":"Frankfurt-1","serverIp":"10.0.0.1","serverHostname":"de-1.example","country":"Germany","city":"Frankfurt","linkSpeed":1000,"pricingTier":"Premium","load":null,"activeConnections":null,"latencyPingPort":5001,"loadDataFresh":false}]}""",
            JsonOptions.Default);
        var twoFactor = JsonSerializer.Deserialize<TwoFactorStatus>(
            """{"is2faEnabled":true,"hasAuthenticator":true,"recoveryCodesLeft":8}""",
            JsonOptions.Default);
        var subscription = JsonSerializer.Deserialize<SubscriptionStatus>(
            """{"plan":"Pro","isPro":true,"status":"Active","billingCycle":"Monthly","activeDevices":2,"maxDevices":3,"canAddDevice":true}""",
            JsonOptions.Default);
        var devices = JsonSerializer.Deserialize<DeviceEnvelope>(
            """{"devices":[{"id":5,"deviceIdHash":"abcdef","deviceNickname":"Laptop","appVersion":"1.0.0","isActive":true,"isCurrent":false,"daysSinceLastSeen":0}]}""",
            JsonOptions.Default);
        var reset = JsonSerializer.Serialize(new ResetPasswordRequest("user@example.com", "token", "new-pass"), JsonOptions.Default);
        var tokenCheck = JsonSerializer.Deserialize<TokenCheckResponse>(
            """{"isValid":true,"message":"Token is valid"}""",
            JsonOptions.Default);
        var canConnect = JsonSerializer.Deserialize<UsageQuota>(
            """{"allowed":true,"bytesUsed":10,"bytesLimit":100,"resetDate":"2026-07-01T00:00:00Z"}""",
            JsonOptions.Default);
        var certJob = JsonSerializer.Deserialize<CertificateRequestResponse>(
            """{"jobId":12,"requestedName":"IKEV2_client12","status":"Pending"}""",
            JsonOptions.Default);
        var messageOnly = JsonSerializer.Deserialize<ApiMessage>(
            """{"message":"Device removed successfully"}""",
            JsonOptions.Default);
        var moneroPrice = JsonSerializer.Deserialize<MoneroPriceResponse>(
            """{"xmrAmount":0.04,"usdAmount":5.99,"xmrPriceUsd":149.75,"currency":"XMR","product":"LibreGuard Pro"}""",
            JsonOptions.Default);
        var moneroInvoice = JsonSerializer.Deserialize<MoneroInvoiceResponse>(
            """{"invoiceId":"invoice-1","paymentAddress":"xmr-address","amount":0.04,"currency":"XMR","status":"Pending","description":"LibreGuard Pro","createdAt":"2026-07-09T12:00:00Z","billingCycle":"Monthly"}""",
            JsonOptions.Default);
        var moneroStatus = JsonSerializer.Deserialize<MoneroStatusResponse>(
            """{"invoiceId":"invoice-1","status":"Pending","amountRequired":0.04,"amountReceived":0.01,"confirmations":2,"requiredConfirmations":10,"createdAt":"2026-07-09T12:00:00Z","expiresAt":"2026-07-10T12:00:00Z","billingCycle":"Monthly"}""",
            JsonOptions.Default);
        var cardCheckout = JsonSerializer.Deserialize<CardCheckoutResponse>(
            """{"checkoutUrl":"https://checkout.example/pro","transactionId":"ch_123","localTransactionId":42,"billingCycle":"Monthly","amountUsd":5.99,"currency":"USD","productId":"prod_monthly","customerEmail":"user@example.com","requestId":"card-42"}""",
            JsonOptions.Default);
        var cardStatus = JsonSerializer.Deserialize<CardPaymentStatusResponse>(
            """{"transactionId":"ch_123","status":"Paid","amountRequired":5.99,"amountReceived":5.99,"confirmedAt":"2026-07-10T12:00:00Z","expiresAt":null,"serverTime":"2026-07-10T12:00:01Z"}""",
            JsonOptions.Default);

        Assert.True(login?.Success);
        Assert.True(login2fa?.Success);
        Assert.True(login2fa?.RequiresTwoFactor);
        Assert.Equal("pending", login2fa?.PendingLoginToken);
        Assert.True(confirmation?.EmailConfirmed);
        Assert.Equal(0, serverList?.Servers?[0].LoadPercent);
        Assert.Equal("Load unavailable", serverList?.Servers?[0].LoadText);
        Assert.True(serverList?.Servers?[0].IsPremium);
        Assert.Equal("Frankfurt, Germany", serverList?.Servers?[0].DisplayName);
        Assert.True(twoFactor?.Is2faEnabled);
        Assert.Equal("Pro", subscription?.PlanType);
        Assert.True(subscription?.IsActive);
        Assert.Equal("Laptop", devices?.Devices[0].DisplayName);
        Assert.Contains("\"newPassword\":\"new-pass\"", reset);
        Assert.True(tokenCheck?.Valid);
        Assert.True(canConnect?.CanConnect);
        Assert.Equal("12", certJob?.JobIdText);
        Assert.True(certJob?.Success);
        Assert.True(messageOnly?.Success);
        Assert.Equal(0.04m, moneroPrice?.XmrAmount);
        Assert.Equal("xmr-address", moneroInvoice?.PaymentAddress);
        Assert.Equal(2, moneroStatus?.Confirmations);
        Assert.Equal("https://checkout.example/pro", cardCheckout?.CheckoutUrl);
        Assert.Equal("ch_123", cardCheckout?.TransactionId);
        Assert.Equal(42, cardCheckout?.LocalTransactionId);
        Assert.Equal("prod_monthly", cardCheckout?.ProductId);
        Assert.Equal("Paid", cardStatus?.Status);
        Assert.Equal(5.99m, cardStatus?.AmountReceived);
        Assert.NotNull(cardStatus?.ConfirmedAt);
    }

    [Fact]
    public void AuthRequests_SerializeControllerPropertyNames()
    {
        var twoFactor = JsonSerializer.Serialize(new TwoFactorVerifyRequest(
            "user@example.com",
            "123456",
            "pending-login-token",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256"), JsonOptions.Default);
        var recovery = JsonSerializer.Serialize(new RecoveryCodeVerifyRequest(
            "user@example.com",
            "abcd-efgh",
            "pending-login-token",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256"), JsonOptions.Default);
        var google = JsonSerializer.Serialize(new GoogleLoginRequest(
            "google-id-token",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256"), JsonOptions.Default);
        var googleCode = JsonSerializer.Serialize(new GoogleCodeLoginRequest(
            "google-client-id.apps.googleusercontent.com",
            "authorization-code",
            "http://127.0.0.1:54321/callback",
            "code-verifier",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256"), JsonOptions.Default);
        var oauthToken = JsonSerializer.Serialize(new OAuthTokenRequest(
            "user@example.com",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256"), JsonOptions.Default);
        var oauthComplete = JsonSerializer.Serialize(new OAuthCompleteRequest(
            "user@example.com",
            "Google",
            "device-1",
            "1.0.0",
            "public-key",
            "key-id",
            "RSA-OAEP-256"), JsonOptions.Default);

        Assert.Contains("\"twoFactorCode\":\"123456\"", twoFactor);
        Assert.Contains("\"pendingLoginToken\":\"pending-login-token\"", twoFactor);
        Assert.DoesNotContain("\"code\"", twoFactor);
        Assert.Contains("\"recoveryCode\":\"abcd-efgh\"", recovery);
        Assert.Contains("\"pendingLoginToken\":\"pending-login-token\"", recovery);
        Assert.Contains("\"idToken\":\"google-id-token\"", google);
        Assert.Contains("\"clientId\":\"google-client-id.apps.googleusercontent.com\"", googleCode);
        Assert.Contains("\"code\":\"authorization-code\"", googleCode);
        Assert.Contains("\"redirectUri\":\"http://127.0.0.1:54321/callback\"", googleCode);
        Assert.Contains("\"codeVerifier\":\"code-verifier\"", googleCode);
        Assert.Contains("\"email\":\"user@example.com\"", oauthToken);
        Assert.Contains("\"provider\":\"Google\"", oauthComplete);
    }

    [Fact]
    public void DnsPreferenceDtos_UseExactBackendWireShapeAndDefaults()
    {
        var request = JsonSerializer.Serialize(new UpdateDnsPreferenceRequest(false), JsonOptions.Default);
        var serializedResponse = JsonSerializer.Serialize(
            new DnsPreferenceResponse(false, true, false, "Standard", 23),
            JsonOptions.Default);
        var response = JsonSerializer.Deserialize<DnsPreferenceResponse>(serializedResponse, JsonOptions.Default);
        var responseWithoutPropagation = JsonSerializer.Deserialize<DnsPreferenceResponse>(
            """{"requestedEnabled":false,"canUseAdBlocking":false,"effectiveEnabled":false,"effectiveMode":"Paused"}""",
            JsonOptions.Default);

        Assert.Equal("{\"adBlockingEnabled\":false}", request);
        Assert.Equal(
            "{\"requestedEnabled\":false,\"canUseAdBlocking\":true,\"effectiveEnabled\":false,\"effectiveMode\":\"Standard\",\"propagationSeconds\":23}",
            serializedResponse);
        Assert.NotNull(response);
        Assert.False(response.RequestedEnabled);
        Assert.True(response.CanUseAdBlocking);
        Assert.False(response.EffectiveEnabled);
        Assert.Equal("Standard", response.EffectiveMode);
        Assert.Equal(23, response.PropagationSeconds);
        Assert.NotNull(responseWithoutPropagation);
        Assert.False(responseWithoutPropagation.CanUseAdBlocking);
        Assert.Equal(15, responseWithoutPropagation.PropagationSeconds);
    }

    private sealed record DeviceEnvelope(IReadOnlyList<UserDevice> Devices);
}
