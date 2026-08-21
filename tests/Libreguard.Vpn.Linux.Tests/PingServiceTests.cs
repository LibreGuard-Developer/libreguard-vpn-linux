using System.Net;
using System.Net.Http;
using System.Text;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class PingServiceTests
{
    [Fact]
    public async Task MeasureLatenciesAsync_UsesPingEndpointAndCachesSuccessfulResults()
    {
        HttpRequestMessage? capturedRequest = null;
        var service = new PingService(new RecordingHandler(request =>
        {
            capturedRequest = request;
            return CreateJsonResponse(HttpStatusCode.OK, """{"pong":true,"timestamp":1703868000000}""");
        }));
        var servers = new[]
        {
            new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 5432, true)
        };

        var results = await service.MeasureLatenciesAsync(servers, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https", capturedRequest!.RequestUri!.Scheme);
        Assert.Equal("berlin.example", capturedRequest.RequestUri.Host);
        Assert.Equal(5432, capturedRequest.RequestUri.Port);
        Assert.Equal("/ping", capturedRequest.RequestUri.AbsolutePath);
        Assert.True(results.ContainsKey("berlin.example"));
        Assert.True(results["berlin.example"] >= 0);
        Assert.True(service.GetCachedLatencies().ContainsKey("berlin.example"));
    }

    [Fact]
    public async Task MeasureLatencyAsync_UsesDefaultPortWhenServerDoesNotSpecifyOne()
    {
        HttpRequestMessage? capturedRequest = null;
        var service = new PingService(new RecordingHandler(request =>
        {
            capturedRequest = request;
            return CreateJsonResponse(HttpStatusCode.OK, """{"pong":true,"timestamp":1703868000000}""");
        }));
        var servers = new[]
        {
            new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, null, true)
        };

        await service.MeasureLatenciesAsync(servers, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(5001, capturedRequest!.RequestUri!.Port);
    }

    [Fact]
    public async Task MeasureLatencyAsync_ReturnsMinusOneWhenRequestFails()
    {
        var service = new PingService(new RecordingHandler(_ => throw new HttpRequestException("boom")));

        var results = await service.MeasureLatenciesAsync(
            [new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true)],
            CancellationToken.None);

        Assert.Equal(-1, results["berlin.example"]);
    }

    [Fact]
    public async Task MeasureLatencyAsync_ReturnsMinusOneWhenPayloadIsInvalid()
    {
        var service = new PingService(new RecordingHandler(_ => CreateJsonResponse(HttpStatusCode.OK, """{"pong":"yes"}""")));

        var results = await service.MeasureLatenciesAsync(
            [new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true)],
            CancellationToken.None);

        Assert.Equal(-1, results["berlin.example"]);
    }

    [Fact]
    public async Task MeasureLatencyAsync_ReturnsMinusOneWhenPongIsFalse()
    {
        var service = new PingService(new RecordingHandler(_ => CreateJsonResponse(HttpStatusCode.OK, """{"pong":false,"timestamp":1703868000000}""")));

        var results = await service.MeasureLatenciesAsync(
            [new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true)],
            CancellationToken.None);

        Assert.Equal(-1, results["berlin.example"]);
    }

    [Fact]
    public async Task MeasureLatenciesAsync_ProcessesMultipleServers()
    {
        var requestedHosts = new List<string>();
        var service = new PingService(new RecordingHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.Host);
            return CreateJsonResponse(HttpStatusCode.OK, """{"pong":true,"timestamp":1703868000000}""");
        }));

        var servers = new[]
        {
            new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true),
            new VpnServer(2, "Paris", "10.0.0.2", "paris.example", "France", "Paris", 100, "free", 30, 1, 443, true)
        };

        var results = await service.MeasureLatenciesAsync(servers, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains("berlin.example", requestedHosts);
        Assert.Contains("paris.example", requestedHosts);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
