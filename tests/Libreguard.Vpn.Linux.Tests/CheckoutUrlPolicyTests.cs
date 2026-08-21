using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class CheckoutUrlPolicyTests
{
    [Theory]
    [InlineData("https://checkout.stripe.com/c/pay/session")]
    [InlineData("https://buy.stripe.com/test/session")]
    [InlineData("https://billing.stripe.com/p/session")]
    [InlineData("https://checkout.creem.io/ch_session")]
    [InlineData("https://creem.io/payment/prod_session")]
    [InlineData("https://www.creem.io/payment/prod_session")]
    public void AllowsConfiguredCheckoutHosts(string value)
    {
        Assert.True(CheckoutUrlPolicy.IsAllowed(new Uri(value)));
    }

    [Theory]
    [InlineData("http://checkout.stripe.com/session")]
    [InlineData("https://checkout.example/session")]
    [InlineData("https://checkout.stripe.com:8443/session")]
    [InlineData("https://checkout.stripe.com@evil.example/session")]
    [InlineData("https://127.0.0.1/session")]
    public void RejectsUntrustedCheckoutUrls(string value)
    {
        Assert.False(CheckoutUrlPolicy.IsAllowed(new Uri(value)));
    }

    [Fact]
    public void AllowsKnownCheckoutResourcesButNotTopLevelResourceRedirects()
    {
        Assert.True(CheckoutUrlPolicy.IsAllowedResource(new Uri("https://js.stripe.com/v3/")));
        Assert.True(CheckoutUrlPolicy.IsAllowedResource(new Uri("https://m.stripe.network/inner.html")));
        Assert.True(CheckoutUrlPolicy.IsAllowedResource(new Uri("about:blank")));
        Assert.False(CheckoutUrlPolicy.IsAllowed(new Uri("https://m.stripe.network/inner.html")));
        Assert.False(CheckoutUrlPolicy.IsAllowedResource(new Uri("http://js.stripe.com/v3/")));
        Assert.False(CheckoutUrlPolicy.IsAllowedResource(new Uri("https://evil.example/script.js")));
    }
}
