using Avalonia.Media;
using Avalonia.Media.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Libreguard.Vpn.Linux.Models;

public enum VpnProtocol
{
    Ikev2,
    OpenVpn
}

public enum VpnConnectionState
{
    Disconnected,
    Preparing,
    Connecting,
    Connected,
    Disconnecting,
    Error
}

public sealed record VpnServer(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("serverName")] string ServerName,
    [property: JsonPropertyName("serverIp")] string ServerIp,
    [property: JsonPropertyName("serverHostname")] string? ServerHostname,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("linkSpeed")] int LinkSpeed,
    [property: JsonPropertyName("pricingTier")] string? PricingTier,
    [property: JsonPropertyName("load")] int? Load,
    [property: JsonPropertyName("activeConnections")] int? ActiveConnections,
    [property: JsonPropertyName("latencyPingPort")] int? LatencyPingPort,
    [property: JsonPropertyName("loadDataFresh")] bool LoadDataFresh)
{
    private static readonly IBrush PingHealthyBrush = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush PingModerateBrush = new ImmutableSolidColorBrush(Color.Parse("#1570EF"));
    private static readonly IBrush PingMutedBrush = new ImmutableSolidColorBrush(Color.Parse("#64748B"));

    [JsonIgnore]
    public int PingMs { get; set; }

    [JsonIgnore]
    public string PingText => PingMs > 0
        ? $"{PingMs} ms"
        : PingMs == 0
            ? "Checking ping..."
            : "Ping unavailable";

    [JsonIgnore]
    public IBrush PingBrush => PingMs <= 100 && PingMs > 0
        ? PingHealthyBrush
        : PingMs <= 200 && PingMs > 100
            ? PingModerateBrush
            : PingMutedBrush;

    public string DisplayName => string.IsNullOrWhiteSpace(City) ? Country : $"{City}, {Country}";
    public string CountryFlag => CountryFlagResolver.FromCountry(Country);
    public bool IsPremium => string.Equals(PricingTier, "pro", StringComparison.OrdinalIgnoreCase)
        || string.Equals(PricingTier, "premium", StringComparison.OrdinalIgnoreCase);
    public string LinkSpeedText => $"{LinkSpeed} Mbps";
    public int LoadPercent => Math.Clamp(Load ?? 0, 0, 100);
    public string LoadText => Load.HasValue ? $"Load {LoadPercent}%" : "Load unavailable";
    public string ActiveConnectionsText => ActiveConnections.HasValue ? $"{ActiveConnections.Value} active" : "Active count unavailable";

}

public static class CountryFlagResolver
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CountryCodeLookup = new(BuildCountryCodeLookup);
    private static readonly IReadOnlyDictionary<string, string> CountryAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["united states"] = "US",
        ["united states of america"] = "US",
        ["usa"] = "US",
        ["uk"] = "GB",
        ["u k"] = "GB",
        ["south korea"] = "KR",
        ["republic of korea"] = "KR",
        ["north korea"] = "KP",
        ["czech republic"] = "CZ",
        ["ivory coast"] = "CI",
        ["taiwan"] = "TW",
        ["russia"] = "RU",
        ["iran"] = "IR",
        ["syria"] = "SY",
        ["bolivia"] = "BO",
        ["tanzania"] = "TZ"
    };
    private static readonly IReadOnlyDictionary<string, string> ServerPrefixAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // LibreGuard's server naming scheme uses FL for Finland; FI is the ISO code.
        ["FL"] = "FI"
    };

    public static string FromCountry(string? country)
    {
        return TryResolveCountryCode(country) is { Length: 2 } code ? CreateFlagEmoji(code) : "🌐";
    }

    public static string FromServerName(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return "🌐";
        }

        var prefix = serverName.Trim().Split(['-', '_'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (prefix.Length != 2 || !prefix.All(char.IsLetter))
        {
            return FromCountry(serverName);
        }

        var isoCode = ServerPrefixAliases.TryGetValue(prefix, out var alias) ? alias : prefix;
        return CreateFlagEmoji(isoCode);
    }

    private static string CreateFlagEmoji(string iso2)
    {
        var upper = iso2.ToUpperInvariant();
        if (upper.Any(ch => ch is < 'A' or > 'Z'))
        {
            return "🌐";
        }

        return string.Concat(upper.Select(ch => char.ConvertFromUtf32(0x1F1E6 + (ch - 'A'))));
    }

    private static string? TryResolveCountryCode(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        var trimmed = country.Trim();
        if (trimmed.Length == 2 && trimmed.All(char.IsLetter))
        {
            return trimmed;
        }

        var normalized = NormalizeCountryName(trimmed);
        if (CountryAliases.TryGetValue(normalized, out var alias))
        {
            return alias;
        }

        return CountryCodeLookup.Value.TryGetValue(normalized, out var code) ? code : null;
    }

    private static IReadOnlyDictionary<string, string> BuildCountryCodeLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                AddCountryName(lookup, region.EnglishName, region.TwoLetterISORegionName);
                AddCountryName(lookup, region.NativeName, region.TwoLetterISORegionName);
                AddCountryName(lookup, region.DisplayName, region.TwoLetterISORegionName);
            }
            catch
            {
            }
        }

        AddCountryName(lookup, "Spain", "ES");
        AddCountryName(lookup, "Switzerland", "CH");
        AddCountryName(lookup, "Finland", "FI");

        return lookup;
    }

    private static void AddCountryName(IDictionary<string, string> lookup, string? countryName, string isoCode)
    {
        if (string.IsNullOrWhiteSpace(countryName))
        {
            return;
        }

        lookup[NormalizeCountryName(countryName)] = isoCode;
    }

    private static string NormalizeCountryName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return new string(builder.ToString().Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray()).Trim();
    }
}

public sealed record VpnServersResponse(
    [property: JsonPropertyName("servers")] IReadOnlyList<VpnServer>? Servers,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message);

public sealed record PingResponse(
    [property: JsonPropertyName("pong")] bool Pong,
    [property: JsonPropertyName("timestamp")] long Timestamp);

public sealed record VpnConfigRequest(
    [property: JsonPropertyName("serverId")] int ServerId,
    [property: JsonPropertyName("protocol")] string Protocol);

public sealed record OpenVpnConfigDownloadRequest([property: JsonPropertyName("serverId")] int ServerId);

public sealed record VpnConfigResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("serverName")] string? ServerName,
    [property: JsonPropertyName("serverIp")] string? ServerIp,
    [property: JsonPropertyName("certificateName")] string? CertificateName,
    [property: JsonPropertyName("configContent")] string? ConfigContent,
    [property: JsonPropertyName("encryptedPassphrase")] EncryptedPassphrase? EncryptedPassphrase,
    [property: JsonPropertyName("clientIp")] string? ClientIp,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("message")] string? Message);

public sealed record CertificateRequest(
    [property: JsonPropertyName("vpnType")] string VpnType,
    [property: JsonPropertyName("serverId")] int ServerId);

public sealed class CertificateRequestResponse
{
    [JsonPropertyName("success")]
    public bool? SuccessRaw { get; init; }

    [JsonPropertyName("jobId")]
    public int? JobId { get; init; }

    [JsonPropertyName("requestedName")]
    public string? RequestedName { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public bool Success => SuccessRaw ?? JobId.HasValue;

    [JsonIgnore]
    public string? JobIdText => JobId?.ToString();
}

public sealed class CertificateJob
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("jobType")]
    public string? JobType { get; init; }

    [JsonPropertyName("requestedName")]
    public string? RequestedName { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("message")]
    public string? MessageRaw { get; init; }

    [JsonPropertyName("outputCertificateId")]
    public int? OutputCertificateId { get; init; }

    [JsonPropertyName("certificateId")]
    public int? CertificateIdRaw { get; init; }

    [JsonIgnore]
    public string? Message => MessageRaw ?? ErrorMessage;

    [JsonIgnore]
    public int? CertificateId => CertificateIdRaw ?? OutputCertificateId;
}

public sealed class UserCertificate
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("vpnType")]
    public string VpnType { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("issueDate")]
    public DateTimeOffset? IssueDate { get; init; }

    [JsonPropertyName("expirationDate")]
    public DateTimeOffset? ExpirationDate { get; init; }

    [JsonPropertyName("isRevoked")]
    public bool IsRevoked { get; init; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; init; }

    [JsonPropertyName("serverIp")]
    public string? ServerIp { get; init; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(ServerName)
        ? $"{Name} ({VpnType})"
        : $"{ServerName} - {VpnType}";

    [JsonIgnore]
    public string CountryFlag => CountryFlagResolver.FromServerName(ServerName);
}

public sealed record CertificateListResponse([property: JsonPropertyName("certificates")] IReadOnlyList<UserCertificate>? Certificates);

public sealed class UsageQuota
{
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    [JsonPropertyName("canConnect")]
    public bool? CanConnectRaw { get; init; }

    [JsonPropertyName("allowed")]
    public bool? Allowed { get; init; }

    [JsonPropertyName("bytesUsed")]
    public long BytesUsed { get; init; }

    [JsonPropertyName("bytesLimit")]
    public long? BytesLimit { get; init; }

    [JsonPropertyName("bytesRemaining")]
    public long? BytesRemaining { get; init; }

    [JsonPropertyName("usagePercentage")]
    public double? UsagePercentage { get; init; }

    [JsonPropertyName("isUnlimited")]
    public bool IsUnlimited { get; init; }

    [JsonPropertyName("isOverLimit")]
    public bool IsOverLimit { get; init; }

    [JsonPropertyName("resetDate")]
    public DateTimeOffset? ResetDate { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public bool CanConnect => CanConnectRaw ?? Allowed ?? !IsOverLimit;
}

public sealed record SubscriptionStatus(
    [property: JsonPropertyName("plan")] string? Plan,
    [property: JsonPropertyName("isPro")] bool IsPro,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("currentPeriodEnd")] DateTimeOffset? CurrentPeriodEnd,
    [property: JsonPropertyName("billingCycle")] string? BillingCycle,
    [property: JsonPropertyName("activeDevices")] int ActiveDevices,
    [property: JsonPropertyName("maxDevices")] int MaxDevices,
    [property: JsonPropertyName("canAddDevice")] bool CanAddDevice,
    [property: JsonPropertyName("message")] string? Message)
{
    [JsonIgnore]
    public string PlanType => Plan ?? (IsPro ? "Pro" : "Free");

    [JsonIgnore]
    public bool IsActive => string.IsNullOrWhiteSpace(Status)
        || string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "Trialing", StringComparison.OrdinalIgnoreCase);
}

public sealed record UpdateDnsPreferenceRequest(
    [property: JsonPropertyName("adBlockingEnabled")] bool AdBlockingEnabled);

public sealed record DnsPreferenceResponse(
    [property: JsonPropertyName("requestedEnabled")] bool RequestedEnabled,
    [property: JsonPropertyName("canUseAdBlocking")] bool CanUseAdBlocking,
    [property: JsonPropertyName("effectiveEnabled")] bool EffectiveEnabled,
    [property: JsonPropertyName("effectiveMode")] string? EffectiveMode,
    [property: JsonPropertyName("propagationSeconds")] int PropagationSeconds = 15);

public sealed record ServerAccessResponse(
    [property: JsonPropertyName("canAccess")] bool CanAccess,
    [property: JsonPropertyName("serverTier")] string? ServerTier,
    [property: JsonPropertyName("requiresPro")] bool RequiresPro,
    [property: JsonPropertyName("message")] string? Message);

public sealed record SubscriptionDeviceRequest(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string? AppVersion);

public sealed class SubscriptionDeviceRegistrationResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("deviceIdHash")]
    public string? DeviceIdHash { get; init; }

    [JsonPropertyName("isNewDevice")]
    public bool IsNewDevice { get; init; }
}

public sealed record UserDevice(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("deviceIdHash")] string? DeviceIdHash,
    [property: JsonPropertyName("deviceNickname")] string? DeviceNickname,
    [property: JsonPropertyName("appVersion")] string? AppVersion,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset? FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent,
    [property: JsonPropertyName("daysSinceLastSeen")] int DaysSinceLastSeen)
{
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(DeviceNickname)
        ? (IsCurrent ? "This device" : $"Device #{Id}")
        : DeviceNickname;

    [JsonIgnore]
    public string DisplayIdentifier => string.IsNullOrWhiteSpace(DeviceIdHash)
        ? "Device identity hidden"
        : DeviceIdHash;
}

public sealed record CheckoutUrlResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("checkoutUrl")] string? CheckoutUrl,
    [property: JsonPropertyName("message")] string? Message)
{
    [JsonIgnore]
    public string? EffectiveUrl => CheckoutUrl ?? Url;
}

public enum BillingCycle
{
    Monthly = 0,
    Yearly = 1
}

public sealed record MoneroPriceResponse(
    [property: JsonPropertyName("xmrAmount")] decimal XmrAmount,
    [property: JsonPropertyName("usdAmount")] decimal UsdAmount,
    [property: JsonPropertyName("xmrPriceUsd")] decimal XmrPriceUsd,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("product")] string? Product);

public sealed record MoneroInvoiceResponse(
    [property: JsonPropertyName("invoiceId")] string? InvoiceId,
    [property: JsonPropertyName("paymentAddress")] string? PaymentAddress,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("billingCycle")] string? BillingCycle);

public sealed record MoneroStatusResponse(
    [property: JsonPropertyName("invoiceId")] string? InvoiceId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("amountRequired")] decimal AmountRequired,
    [property: JsonPropertyName("amountReceived")] decimal AmountReceived,
    [property: JsonPropertyName("confirmations")] int Confirmations,
    [property: JsonPropertyName("requiredConfirmations")] int RequiredConfirmations,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("billingCycle")] string? BillingCycle);

public sealed record CardCheckoutResponse(
    [property: JsonPropertyName("checkoutUrl")] string? CheckoutUrl,
    [property: JsonPropertyName("transactionId")] string? TransactionId,
    [property: JsonPropertyName("localTransactionId")] int LocalTransactionId,
    [property: JsonPropertyName("billingCycle")] string? BillingCycle,
    [property: JsonPropertyName("amountUsd")] decimal AmountUsd,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("productId")] string? ProductId,
    [property: JsonPropertyName("customerEmail")] string? CustomerEmail,
    [property: JsonPropertyName("requestId")] string? RequestId);

public sealed record CardPaymentStatusResponse(
    [property: JsonPropertyName("transactionId")] string? TransactionId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("amountRequired")] decimal AmountRequired,
    [property: JsonPropertyName("amountReceived")] decimal AmountReceived,
    [property: JsonPropertyName("confirmedAt")] DateTimeOffset? ConfirmedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime);

public sealed record VpnProfile(
    VpnProtocol Protocol,
    string ProfileName,
    string ConfigPath,
    string? SecretPath,
    string? NetworkManagerVpnData,
    string OuterTransportAddress,
    IReadOnlyList<string>? Ikev2GatewayCertificatePaths = null,
    bool Ikev2AllowPinnedGatewayRootFallback = false,
    string? Ikev2RemoteAddress = null,
    IReadOnlyList<string>? Ikev2CredentialPaths = null);

public sealed record TunnelTrafficSnapshot(
    string? DeviceName,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    long SessionDownloadBytes,
    long SessionUploadBytes,
    bool IsAvailable,
    string? Message = null)
{
    public long SessionTotalBytes => SessionDownloadBytes + SessionUploadBytes;
}

public sealed record VpnStatus(
    VpnConnectionState State,
    string? ActiveProfile,
    string? Message,
    DateTimeOffset? ConnectedAt = null,
    string? ClientPublicIp = null,
    string? ServerIp = null);
