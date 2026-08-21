using System.Net;
using System.Net.Http;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class PublicIpResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsValidIpv4Address()
    {
        var resolver = CreateResolver(request =>
            request.RequestUri!.AbsoluteUri.Contains("api.ipify.org", StringComparison.OrdinalIgnoreCase)
                ? Ok("203.0.113.42")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("203.0.113.42", result);
    }

    [Fact]
    public async Task ResolveAsync_ParsesIpv6Address()
    {
        var resolver = CreateResolver(request =>
            request.RequestUri!.AbsoluteUri.Contains("api.ipify.org", StringComparison.OrdinalIgnoreCase)
                ? Ok("2001:db8::8a2e:370:7334")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("2001:db8::8a2e:370:7334", result);
    }

    [Fact]
    public async Task ResolveAsync_IgnoresInvalidBodiesAndFallsBack()
    {
        var resolver = CreateResolver(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("api.ipify.org", StringComparison.OrdinalIgnoreCase))
            {
                return Ok("not an ip");
            }

            return uri.Contains("icanhazip.com", StringComparison.OrdinalIgnoreCase)
                ? Ok("198.51.100.15")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("198.51.100.15", result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenAllEndpointsFail()
    {
        var resolver = CreateResolver(_ => throw new HttpRequestException("offline"));

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Null(result);
    }

    private static PublicIpResolver CreateResolver(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new TestHandler(handler)));

    private static HttpResponseMessage Ok(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
