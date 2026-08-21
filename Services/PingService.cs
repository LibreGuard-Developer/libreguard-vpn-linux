using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class PingService : IServerLatencyService, IDisposable
{
    private const int DefaultPingPort = 5001;
    private const int PingTimeoutMs = 5000;
    private const int MaxResponseChars = 64 * 1024;
    private readonly HttpClient _httpClient;
    private readonly object _cacheLock = new();
    private Dictionary<string, int> _cachedLatencies = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PingService(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler();

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(PingTimeoutMs)
        };
    }

    public async Task<int> MeasureLatencyAsync(string hostname, int? customPort = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return -1;
        }

        var port = customPort ?? DefaultPingPort;
        var url = $"https://{hostname}:{port}/ping";

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return -1;
            }

            if (response.Content.Headers.ContentLength > MaxResponseChars)
            {
                return -1;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (content.Length > MaxResponseChars)
            {
                return -1;
            }

            var payload = JsonSerializer.Deserialize<PingResponse>(content, JsonOptions.Default);
            stopwatch.Stop();

            return payload is { Pong: true }
                ? (int)stopwatch.ElapsedMilliseconds
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<IReadOnlyDictionary<string, int>> MeasureLatenciesAsync(IReadOnlyList<VpnServer> servers, CancellationToken cancellationToken)
    {
        if (servers.Count == 0)
        {
            return GetCachedLatencies();
        }

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(10));

        var tasks = servers
            .Where(server => !string.IsNullOrWhiteSpace(server.ServerHostname))
            .Select(async server => (Server: server, Latency: await MeasureLatencyAsync(server.ServerHostname!, server.LatencyPingPort, overallCts.Token)))
            .ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
        }

        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            try
            {
                var (server, latency) = await task;
                if (string.IsNullOrWhiteSpace(server.ServerHostname))
                {
                    continue;
                }

                results[server.ServerHostname] = latency;
            }
            catch
            {
            }
        }

        lock (_cacheLock)
        {
            foreach (var item in results)
            {
                _cachedLatencies[item.Key] = item.Value;
            }
        }

        return results;
    }

    public IReadOnlyDictionary<string, int> GetCachedLatencies()
    {
        lock (_cacheLock)
        {
            return new Dictionary<string, int>(_cachedLatencies, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}
