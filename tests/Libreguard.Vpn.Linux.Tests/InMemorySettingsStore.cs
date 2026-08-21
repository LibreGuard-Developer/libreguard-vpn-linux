using System.Text.Json;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

internal sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        if (_values.TryGetValue(key, out var value))
        {
            return Task.FromResult(value.Deserialize<T>());
        }

        return Task.FromResult(default(T));
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        _values[key] = JsonSerializer.SerializeToElement(value);
        return Task.CompletedTask;
    }
}
