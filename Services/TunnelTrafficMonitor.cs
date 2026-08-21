using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class TunnelTrafficMonitor(
    INetworkManagerClient networkManager,
    Func<string, bool>? fileExists = null,
    Func<string, CancellationToken, Task<string?>>? readFileAsync = null) : ITunnelTrafficMonitor
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, CancellationToken, Task<string?>> _readFileAsync = readFileAsync ?? ReadFileAsync;
    private string? _profileName;
    private string? _deviceName;
    private long? _baselineRxBytes;
    private long? _baselineTxBytes;
    private long? _lastRxBytes;
    private long? _lastTxBytes;
    private TunnelTrafficSnapshot _lastSnapshot = new(null, 0, 0, 0, 0, false, "Tunnel metrics unavailable.");

    public async Task<TunnelTrafficSnapshot> StartSessionAsync(string profileName, CancellationToken cancellationToken)
    {
        _profileName = profileName;
        _deviceName = null;
        _baselineRxBytes = null;
        _baselineTxBytes = null;
        _lastRxBytes = null;
        _lastTxBytes = null;
        _lastSnapshot = new(null, 0, 0, 0, 0, false, "Tunnel metrics unavailable.");

        return await RefreshAsync(cancellationToken);
    }

    public async Task<TunnelTrafficSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_profileName))
        {
            return _lastSnapshot;
        }

        _deviceName ??= await networkManager.GetActiveDeviceNameAsync(_profileName, cancellationToken);
        if (string.IsNullOrWhiteSpace(_deviceName))
        {
            return CreateUnavailableSnapshot("Tunnel device unavailable.");
        }

        var rxPath = $"/sys/class/net/{_deviceName}/statistics/rx_bytes";
        var txPath = $"/sys/class/net/{_deviceName}/statistics/tx_bytes";
        if (!_fileExists(rxPath) || !_fileExists(txPath))
        {
            return CreateUnavailableSnapshot("Tunnel statistics unavailable.");
        }

        var rxBytes = await ReadCounterAsync(rxPath, cancellationToken);
        var txBytes = await ReadCounterAsync(txPath, cancellationToken);
        if (!rxBytes.HasValue || !txBytes.HasValue)
        {
            return CreateUnavailableSnapshot("Tunnel statistics unavailable.");
        }

        if (!_baselineRxBytes.HasValue || !_baselineTxBytes.HasValue || !_lastRxBytes.HasValue || !_lastTxBytes.HasValue)
        {
            _baselineRxBytes = rxBytes.Value;
            _baselineTxBytes = txBytes.Value;
            _lastRxBytes = rxBytes.Value;
            _lastTxBytes = txBytes.Value;
            _lastSnapshot = new(_deviceName, 0, 0, 0, 0, true);
            return _lastSnapshot;
        }

        var downloadRate = rxBytes.Value >= _lastRxBytes.Value ? rxBytes.Value - _lastRxBytes.Value : 0;
        var uploadRate = txBytes.Value >= _lastTxBytes.Value ? txBytes.Value - _lastTxBytes.Value : 0;
        var sessionDownload = rxBytes.Value >= _baselineRxBytes.Value ? rxBytes.Value - _baselineRxBytes.Value : 0;
        var sessionUpload = txBytes.Value >= _baselineTxBytes.Value ? txBytes.Value - _baselineTxBytes.Value : 0;

        _lastRxBytes = rxBytes.Value;
        _lastTxBytes = txBytes.Value;
        _lastSnapshot = new(_deviceName, downloadRate, uploadRate, sessionDownload, sessionUpload, true);
        return _lastSnapshot;
    }

    public void Stop()
    {
        _profileName = null;
        _deviceName = null;
        _baselineRxBytes = null;
        _baselineTxBytes = null;
        _lastRxBytes = null;
        _lastTxBytes = null;
        _lastSnapshot = new(null, 0, 0, 0, 0, false, "Tunnel metrics unavailable.");
    }

    private TunnelTrafficSnapshot CreateUnavailableSnapshot(string message)
    {
        _lastSnapshot = _lastSnapshot with
        {
            DeviceName = _deviceName,
            DownloadBytesPerSecond = 0,
            UploadBytesPerSecond = 0,
            IsAvailable = false,
            Message = message
        };
        return _lastSnapshot;
    }

    private async Task<long?> ReadCounterAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var value = await _readFileAsync(path, cancellationToken);
            return long.TryParse(value?.Trim(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
        => await File.ReadAllTextAsync(path, cancellationToken);
}
