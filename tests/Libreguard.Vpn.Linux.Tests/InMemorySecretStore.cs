using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
}
