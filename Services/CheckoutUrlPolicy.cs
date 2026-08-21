namespace Libreguard.Vpn.Linux.Services;

internal static class CheckoutUrlPolicy
{
    private static readonly HashSet<string> AllowedCheckoutHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "checkout.stripe.com",
        "buy.stripe.com",
        "billing.stripe.com",
        "checkout.creem.io",
        "creem.io",
        "www.creem.io"
    };

    private static readonly HashSet<string> AllowedResourceHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "js.stripe.com",
        "m.stripe.network"
    };

    public static bool IsAllowed(Uri? uri)
        => uri is { } target
            && IsTrustedHttpsDnsUri(target)
            && AllowedCheckoutHosts.Contains(target.Host);

    public static bool IsAllowedResource(Uri? uri)
        => IsAllowed(uri) || IsAuxiliaryResource(uri);

    internal static bool IsAuxiliaryResource(Uri? uri)
        => IsSafeAboutBlank(uri)
            || (uri is { } target
                && IsTrustedHttpsDnsUri(target)
                && AllowedResourceHosts.Contains(target.Host));

    private static bool IsTrustedHttpsDnsUri(Uri? uri)
        => uri is { IsAbsoluteUri: true }
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.HostNameType == UriHostNameType.Dns;

    private static bool IsSafeAboutBlank(Uri? uri)
        => uri is { IsAbsoluteUri: true }
            && uri.OriginalString.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
}
