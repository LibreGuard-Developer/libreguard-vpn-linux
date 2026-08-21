using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class TunnelTrafficMonitorTests
{
    [Fact]
    public async Task RefreshAsync_ComputesSpeedAndSessionTotals()
    {
        var networkManager = new FakeNetworkManager();
        var counters = new Dictionary<string, Queue<string>>(StringComparer.Ordinal)
        {
            ["/sys/class/net/lgvpn0/statistics/rx_bytes"] = new Queue<string>(["1000", "2500"]),
            ["/sys/class/net/lgvpn0/statistics/tx_bytes"] = new Queue<string>(["2000", "3200"])
        };
        var monitor = new TunnelTrafficMonitor(
            networkManager,
            path => counters.ContainsKey(path),
            (path, _) => Task.FromResult<string?>(counters[path].Dequeue()));

        var start = await monitor.StartSessionAsync("profile", CancellationToken.None);
        var refresh = await monitor.RefreshAsync(CancellationToken.None);

        Assert.True(start.IsAvailable);
        Assert.Equal(0, start.SessionTotalBytes);
        Assert.True(refresh.IsAvailable);
        Assert.Equal(1500, refresh.DownloadBytesPerSecond);
        Assert.Equal(1200, refresh.UploadBytesPerSecond);
        Assert.Equal(1500, refresh.SessionDownloadBytes);
        Assert.Equal(1200, refresh.SessionUploadBytes);
        Assert.Equal(2700, refresh.SessionTotalBytes);
    }

    [Fact]
    public async Task RefreshAsync_ClampsCounterResetsToZero()
    {
        var networkManager = new FakeNetworkManager();
        var counters = new Dictionary<string, Queue<string>>(StringComparer.Ordinal)
        {
            ["/sys/class/net/lgvpn0/statistics/rx_bytes"] = new Queue<string>(["2000", "1500"]),
            ["/sys/class/net/lgvpn0/statistics/tx_bytes"] = new Queue<string>(["3000", "2500"])
        };
        var monitor = new TunnelTrafficMonitor(
            networkManager,
            path => counters.ContainsKey(path),
            (path, _) => Task.FromResult<string?>(counters[path].Dequeue()));

        await monitor.StartSessionAsync("profile", CancellationToken.None);
        var refresh = await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, refresh.DownloadBytesPerSecond);
        Assert.Equal(0, refresh.UploadBytesPerSecond);
        Assert.Equal(0, refresh.SessionDownloadBytes);
        Assert.Equal(0, refresh.SessionUploadBytes);
    }

    [Fact]
    public async Task RefreshAsync_ReturnsPlaceholderWhenStatisticsAreMissing()
    {
        var networkManager = new FakeNetworkManager();
        var monitor = new TunnelTrafficMonitor(
            networkManager,
            _ => false,
            (_, _) => Task.FromResult<string?>("0"));

        var snapshot = await monitor.StartSessionAsync("profile", CancellationToken.None);

        Assert.False(snapshot.IsAvailable);
        Assert.Equal(0, snapshot.DownloadBytesPerSecond);
        Assert.Equal(0, snapshot.UploadBytesPerSecond);
        Assert.Equal(0, snapshot.SessionTotalBytes);
    }

    private sealed class FakeNetworkManager : INetworkManagerClient
    {
        public Task EnsureAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ImportOpenVpnAsync(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ImportIkeV2Async(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ActivateAsync(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeactivateAsync(string profileName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetActiveLibreGuardProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetLibreGuardProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task DisconnectLibreGuardProfilesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteLibreGuardProfilesAsync(string? excludeProfileName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupLibreGuardArtifactsAsync(string? excludeProfileName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteLibreGuardProfileAsync(string profileName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupLibreGuardProfileArtifactsAsync(string profileName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetActiveDeviceNameAsync(string profileName, CancellationToken cancellationToken) => Task.FromResult<string?>("lgvpn0");
    }
}
